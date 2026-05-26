using CommunityHub.Core.Email;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>Settings for the Backstage exhibitor sync.</summary>
public sealed class BackstageSyncOptions
{
    public const string SectionName = "BackstageSync";

    /// <summary>Whether the sync runs at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The live event-coordinator address that missing-exhibitor
    /// notifications go to when NOT in TESTMODE.
    /// </summary>
    public string CoordinatorEmail { get; set; } = string.Empty;
}

/// <summary>The outcome of a full sync run.</summary>
public sealed record BackstageSyncResult(
    int Examined,
    int AlreadyExists,
    int Created,
    int WouldCreate,
    int Failed,
    IReadOnlyList<ExhibitorSyncItem> Items);

/// <summary>
/// Syncs sponsors / exhibitors into Zoho Backstage (CONTEXT.md - Backstage
/// exhibitor sync). For each sponsor company it: checks whether the exhibitor
/// already exists in Backstage; if missing, creates it (when the API supports
/// that) or records WouldCreate; and emails the event coordinator so a human
/// is always informed of a missing/added exhibitor.
///
/// TESTMODE (TestModeOptions.Enabled): no real Backstage calls happen and the
/// coordinator email is routed only to the test coordinator address. The
/// flow - lookup, decision, notification - runs identically, so it can be
/// fully exercised with the test sponsor before going live.
/// </summary>
public sealed class BackstageSyncService
{
    private readonly IBackstageExhibitorApi _backstage;
    private readonly IEmailSender _emailSender;
    private readonly EmailTemplateProvider _templates;
    private readonly BackstageSyncOptions _options;
    private readonly TestModeOptions _testMode;
    private readonly ILogger<BackstageSyncService> _log;

    public BackstageSyncService(
        IBackstageExhibitorApi backstage,
        IEmailSender emailSender,
        EmailTemplateProvider templates,
        BackstageSyncOptions options,
        TestModeOptions testMode,
        ILogger<BackstageSyncService> log)
    {
        _backstage = backstage;
        _emailSender = emailSender;
        _templates = templates;
        _options = options;
        _testMode = testMode;
        _log = log;
    }

    /// <summary>
    /// Sync the supplied exhibitors. The caller derives the list (e.g. from
    /// the WooCommerce orders already pulled). Returns per-exhibitor outcomes.
    /// </summary>
    public async Task<BackstageSyncResult> SyncAsync(
        IReadOnlyList<ExhibitorRecord> exhibitors,
        CancellationToken ct = default)
    {
        var items = new List<ExhibitorSyncItem>();

        foreach (var exhibitor in exhibitors)
        {
            ExhibitorSyncItem item;
            try
            {
                var exists = await _backstage.ExistsAsync(exhibitor, ct);
                if (exists)
                {
                    item = new ExhibitorSyncItem(
                        exhibitor, ExhibitorSyncOutcome.AlreadyExists, null);
                }
                else if (_backstage.CanCreate)
                {
                    await _backstage.CreateAsync(exhibitor, ct);
                    item = new ExhibitorSyncItem(
                        exhibitor, ExhibitorSyncOutcome.Created,
                        "Created in Backstage.");
                    await NotifyCoordinatorAsync(exhibitor, true, ct);
                }
                else
                {
                    // Missing, but this API cannot create (TESTMODE, or the
                    // live endpoint is not yet confirmed). Record it and tell
                    // the coordinator so a human can act.
                    item = new ExhibitorSyncItem(
                        exhibitor, ExhibitorSyncOutcome.WouldCreate,
                        "Missing in Backstage - coordinator notified.");
                    await NotifyCoordinatorAsync(exhibitor, false, ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Backstage sync failed for '{Company}'.",
                    exhibitor.CompanyName);
                item = new ExhibitorSyncItem(
                    exhibitor, ExhibitorSyncOutcome.Failed, ex.Message);
            }
            items.Add(item);
        }

        var result = new BackstageSyncResult(
            Examined: items.Count,
            AlreadyExists: items.Count(i => i.Outcome == ExhibitorSyncOutcome.AlreadyExists),
            Created: items.Count(i => i.Outcome == ExhibitorSyncOutcome.Created),
            WouldCreate: items.Count(i => i.Outcome == ExhibitorSyncOutcome.WouldCreate),
            Failed: items.Count(i => i.Outcome == ExhibitorSyncOutcome.Failed),
            Items: items);

        _log.LogInformation(
            "Backstage sync: {Examined} examined, {Exists} exist, {Created} "
            + "created, {Would} would-create, {Failed} failed. TESTMODE={Test}.",
            result.Examined, result.AlreadyExists, result.Created,
            result.WouldCreate, result.Failed, _testMode.Enabled);

        return result;
    }

    /// <summary>
    /// Email the event coordinator that an exhibitor was created, or needs to
    /// be created. In TESTMODE the recipient is forced to the test coordinator
    /// address; otherwise it is the configured coordinator.
    /// </summary>
    private async Task NotifyCoordinatorAsync(
        ExhibitorRecord exhibitor, bool wasCreated, CancellationToken ct)
    {
        var recipient = _testMode.Enabled
            ? _testMode.TestCoordinatorEmail
            : _options.CoordinatorEmail;

        if (string.IsNullOrWhiteSpace(recipient))
        {
            _log.LogWarning(
                "Backstage sync: no coordinator email configured; "
                + "notification for '{Company}' not sent.",
                exhibitor.CompanyName);
            return;
        }

        var action = wasCreated
            ? "has been created in"
            : "is missing from and should be added to";
        var prefix = _testMode.Enabled ? "[TESTMODE] " : string.Empty;
        var subject =
            $"{prefix}Backstage exhibitor: {exhibitor.CompanyName}";
        var body =
            $"<p>The sponsor/exhibitor <strong>{exhibitor.CompanyName}</strong> "
            + $"(company id {exhibitor.CompanyId}) {action} Zoho Backstage.</p>"
            + (string.IsNullOrWhiteSpace(exhibitor.ContactEmail)
                ? string.Empty
                : $"<p>Contact: {exhibitor.ContactEmail}</p>")
            + (_testMode.Enabled
                ? "<p><em>TESTMODE is on - no change was made in Backstage "
                  + "and this message was routed to the test coordinator.</em></p>"
                : string.Empty);

        await _emailSender.SendAsync(recipient, subject, body, ct);
        _log.LogInformation(
            "Backstage sync: coordinator notified ({Recipient}) about '{Company}'.",
            recipient, exhibitor.CompanyName);
    }
}
