using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §335 (closes the open item in §326cd) — surface a sponsor company that has been waiting too
/// long for its SharePoint upload folder.
///
/// The provisioning chain is fully automatic: <c>WooCommercePullJob</c> (every 30 min) →
/// <c>SponsorOrderPullService</c> → <c>EnsureFolderWithEditLinkAsync</c> → the 15-minute
/// <c>SponsorWelcomeReconcileJob</c> welcomes the company. Until the folder exists, a Gold+
/// booth company's welcome is deliberately BLOCKED so the exhibitor never lands on tasks whose
/// upload links are not ready.
///
/// That block is transient by design — but when provisioning actually FAILS (bad permissions,
/// wrong site, SharePoint unconfigured) the two <c>catch</c> blocks in the pull log a warning
/// and carry on, so the company is blocked **indefinitely and the only symptom is an absence**:
/// no error, no alert, just a sponsor who is never welcomed. Finding that meant log archaeology.
///
/// This detector turns the absence into a visible item in the Action queue.
/// </summary>
public sealed class SponsorProvisioningStallDetector
{
    /// <summary>Type PREFIX; the concrete type carries the company id so each stuck company
    /// keeps its own queue row (the same shape as <c>change-requested:{topic}</c>).</summary>
    public const string TypePrefix = "sponsor-upload-folder-stuck";

    /// <summary>
    /// How long a company may sit unprovisioned before it is treated as STUCK rather than
    /// merely in-flight. The pull runs every 30 minutes, so a full day is many chances to
    /// succeed — long enough that a healthy company never raises an item, short enough that a
    /// broken one is found in a day rather than at the event.
    /// </summary>
    public const int StuckAfterDays = 1;

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly OrganizerActionItemService _actions;

    public SponsorProvisioningStallDetector(
        CommunityHubDbContext db, TimeProvider clock, OrganizerActionItemService actions)
    {
        _db = db;
        _clock = clock;
        _actions = actions;
    }

    /// <param name="Stuck">Companies that raised (or refreshed) an action item this run.</param>
    /// <param name="Cleared">Companies whose folder appeared, so their item was resolved.</param>
    public sealed record StallResult(int Stuck, int Cleared, IReadOnlyList<string> StuckCompanies);

    /// <summary>
    /// Raise an action item for every Gold+ booth company that has been waiting longer than
    /// <see cref="StuckAfterDays"/>, and RESOLVE the item for any company whose folder has since
    /// appeared. Idempotent: re-running refreshes the summary (with the current wait) rather
    /// than piling up rows, and self-heals — the queue never keeps a stale "stuck" entry for a
    /// company that recovered on its own.
    /// </summary>
    public async Task<StallResult> RunAsync(int eventId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        // The SAME condition the welcome guard applies (SponsorWelcomeEmailService: package
        // >= Gold, and a SponsorUploadLocation carrying a non-empty edit link). Deliberately
        // duplicated in shape rather than approximated: if this drifted from the guard, the
        // queue would report companies that are not actually blocked, or — far worse — stay
        // silent about ones that are.
        var boothCompanies = await _db.SponsorInfos
            .Where(s => s.EventId == eventId && s.SponsorPackage >= SponsorPackage.Gold)
            .Select(s => new { s.SponsorCompanyId, s.CreatedAt })
            .ToListAsync(ct);

        // A readable name where one is mirrored LOCALLY. Deliberately not a Company Manager API
        // call: a background stall check must not be able to fail because a third-party API is
        // down — that would silence the very alert it exists to raise. ErpCustomerLink is keyed
        // on the same (EventId, SponsorCompanyId) and already holds a resolved name; where it is
        // absent the id is used, which still identifies the company unambiguously.
        var names = await _db.ErpCustomerLinks
            .Where(l => l.EventId == eventId && l.CompanyName != "")
            .Select(l => new { l.SponsorCompanyId, l.CompanyName })
            .ToListAsync(ct);
        var nameById = names
            .GroupBy(n => n.SponsorCompanyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().CompanyName, StringComparer.OrdinalIgnoreCase);

        var provisioned = await _db.SponsorUploadLocations
            .Where(l => l.EventId == eventId && l.EditLinkUrl != null && l.EditLinkUrl != "")
            .Select(l => l.SponsorCompanyId)
            .Distinct()
            .ToListAsync(ct);
        var provisionedSet = provisioned.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stuckCompanies = new List<string>();
        int cleared = 0;

        foreach (var c in boothCompanies)
        {
            if (string.IsNullOrWhiteSpace(c.SponsorCompanyId)) continue;
            var type = $"{TypePrefix}:{c.SponsorCompanyId}";

            if (provisionedSet.Contains(c.SponsorCompanyId))
            {
                // Recovered — close any open item so the queue reflects reality.
                var open = await _db.OrganizerActionItems.FirstOrDefaultAsync(
                    a => a.EventId == eventId && a.Type == type && a.ResolvedAt == null, ct);
                if (open is not null)
                {
                    await _actions.ResolveAsync(
                        eventId, open.Id,
                        "Upload folder was provisioned — cleared automatically.", ct);
                    cleared++;
                }
                continue;
            }

            var waitedDays = (int)Math.Floor((now - c.CreatedAt).TotalDays);
            if (waitedDays < StuckAfterDays) continue;   // still plausibly in flight

            var name = nameById.TryGetValue(c.SponsorCompanyId, out var known)
                ? $"{known} ({c.SponsorCompanyId})"
                : $"Company {c.SponsorCompanyId}";
            await _actions.UpsertOpenAsync(
                eventId, type, participantId: null,
                summary: $"{name} has been waiting {waitedDays} day(s) for its SharePoint upload "
                         + "folder, so its welcome e-mail is still blocked. The sponsor pull "
                         + "provisions this automatically every ~30 minutes — a wait this long "
                         + "means provisioning is failing or SharePoint is not configured. Check "
                         + "the pull logs for the site URL and permissions.",
                ct);
            stuckCompanies.Add(c.SponsorCompanyId);
        }

        return new StallResult(stuckCompanies.Count, cleared, stuckCompanies);
    }
}
