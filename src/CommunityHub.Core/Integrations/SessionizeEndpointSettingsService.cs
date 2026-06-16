using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>The outcome of saving the Sessionize endpoint setting.</summary>
/// <param name="Setting">The persisted row (effective endpoint id + change bookkeeping).</param>
/// <param name="EndpointChanged">
/// True when this save CHANGED the effective endpoint id (vs the value the row held
/// before). When true the caller must prompt the organizer for a change-handling
/// choice (Replace / Merge) before any re-import.
/// </param>
public sealed record SaveEndpointResult(
    SessionizeEndpointSetting Setting,
    bool EndpointChanged);

/// <summary>
/// Loads/saves the per-edition Sessionize endpoint admin setting
/// (<see cref="SessionizeEndpointSetting"/>) and runs the endpoint-change handling
/// bookkeeping for the <c>/Organizer/SessionizeEndpointSettings</c> page.
///
/// Responsibilities:
///  - resolve the EFFECTIVE endpoint id (DB row overrides the config-bound default);
///  - on save, detect whether the endpoint id actually CHANGED, and if so stamp the
///    change (so the page can prompt Replace vs Merge) and reset any prior choice;
///  - record the organizer's chosen <see cref="SessionizeChangeMode"/> and expose
///    the <see cref="SessionizeImportMode"/> it maps to (Replace⇒Full, Merge⇒Delta);
///  - keep the in-process <see cref="SessionizeApiOptions"/> in sync so the live
///    <see cref="SessionizeApiClient"/> uses the new id WITHOUT a restart.
///
/// This service NEVER runs an import. The operator runs the import from the existing
/// <c>/Organizer/SessionizeImport</c> buttons (or the CLI / scheduled job), which use
/// the mode this service records.
/// </summary>
public sealed class SessionizeEndpointSettingsService
{
    private readonly CommunityHubDbContext _db;
    private readonly SessionizeApiOptions _options;
    private readonly TimeProvider _clock;

    public SessionizeEndpointSettingsService(
        CommunityHubDbContext db,
        SessionizeApiOptions options,
        TimeProvider clock)
    {
        _db = db;
        _options = options;
        _clock = clock;
    }

    /// <summary>
    /// Map an organizer's change-handling choice to the import mode the operator's
    /// import button must run. This is the single mapping point referenced by the
    /// UI and the tests: Replace ⇒ Full (re-seed), Merge ⇒ Delta (additive).
    /// <see cref="SessionizeChangeMode.None"/> defaults to Delta (the safe,
    /// edit-preserving mode) since no destructive choice was made.
    /// </summary>
    public static SessionizeImportMode ToImportMode(SessionizeChangeMode mode) => mode switch
    {
        SessionizeChangeMode.Replace => SessionizeImportMode.Full,
        SessionizeChangeMode.Merge => SessionizeImportMode.Delta,
        _ => SessionizeImportMode.Delta,
    };

    /// <summary>The config-bound default endpoint id (blank if none configured).</summary>
    public string ConfiguredDefaultEndpointId => _options.EndpointId ?? string.Empty;

    /// <summary>Load the edition's setting row, or null if none saved yet.</summary>
    public Task<SessionizeEndpointSetting?> LoadAsync(int eventId, CancellationToken ct = default) =>
        _db.SessionizeEndpointSettings
            .FirstOrDefaultAsync(s => s.EventId == eventId, ct);

    /// <summary>
    /// The endpoint id currently in EFFECT for the edition: the saved row's value
    /// when non-blank, otherwise the config-bound default.
    /// </summary>
    public async Task<string> GetEffectiveEndpointIdAsync(int eventId, CancellationToken ct = default)
    {
        var row = await LoadAsync(eventId, ct);
        return Effective(row?.EndpointId);
    }

    /// <summary>
    /// Save the endpoint id (+ optional view) for the edition. Detects whether the
    /// effective endpoint id CHANGED: if so it stamps the change (PreviousEndpointId,
    /// EndpointLastChangedAt) and RESETS any prior change-handling choice back to
    /// <see cref="SessionizeChangeMode.None"/>, so the page re-prompts Replace/Merge.
    /// Also updates the in-process <see cref="SessionizeApiOptions"/> so the live
    /// client uses the new id immediately. Does NOT run an import.
    /// </summary>
    public async Task<SaveEndpointResult> SaveEndpointAsync(
        int eventId,
        string? endpointId,
        string? view = null,
        string? byEmail = null,
        CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var newId = (endpointId ?? string.Empty).Trim();

        var row = await LoadAsync(eventId, ct);
        if (row is null)
        {
            row = new SessionizeEndpointSetting
            {
                EventId = eventId,
                CreatedAt = now,
            };
            _db.SessionizeEndpointSettings.Add(row);
        }

        // Compare against the EFFECTIVE id the edition had before this save. A
        // "change" requires a real, different PREVIOUS endpoint to migrate away
        // from: setting the endpoint for the FIRST time (old effective was blank)
        // is not a change — there is no already-imported data tied to a prior
        // endpoint to Replace/Merge. Only an old non-blank id → a different non-blank
        // id triggers the Replace/Merge prompt (the ELDK26→ELDK27 switch).
        var oldEffective = Effective(row.EndpointId);
        var newEffective = Effective(newId);
        var changed = !string.IsNullOrEmpty(oldEffective)
                      && !string.IsNullOrEmpty(newEffective)
                      && !string.Equals(oldEffective, newEffective, StringComparison.Ordinal);

        row.EndpointId = string.IsNullOrEmpty(newId) ? null : newId;
        if (!string.IsNullOrWhiteSpace(view)) row.View = view.Trim();
        row.LastUpdatedByEmail = byEmail;
        row.UpdatedAt = now;

        if (changed)
        {
            row.PreviousEndpointId = string.IsNullOrEmpty(oldEffective) ? null : oldEffective;
            row.EndpointLastChangedAt = now;
            // A new endpoint means the prior re-import choice no longer applies.
            row.PendingChangeMode = SessionizeChangeMode.None;
            row.ChangeModeChosenAt = null;
        }

        await _db.SaveChangesAsync(ct);

        // Keep the live client in sync (no restart needed). Operator config only.
        _options.EndpointId = newEffective;
        if (!string.IsNullOrWhiteSpace(row.View)
            && Enum.TryParse<SessionizeView>(row.View, ignoreCase: true, out var parsedView))
        {
            _options.View = parsedView;
        }

        return new SaveEndpointResult(row, changed);
    }

    /// <summary>
    /// Record the organizer's change-handling choice (Replace / Merge) for the most
    /// recent endpoint change. This persists the CHOICE only — it never runs an
    /// import. Returns the import mode the operator's import button maps it to.
    /// Throws if there is no recorded endpoint change awaiting a choice.
    /// </summary>
    public async Task<SessionizeImportMode> RecordChangeChoiceAsync(
        int eventId,
        SessionizeChangeMode mode,
        CancellationToken ct = default)
    {
        if (mode == SessionizeChangeMode.None)
            throw new ArgumentException("A real change mode (Replace/Merge) is required.", nameof(mode));

        var row = await LoadAsync(eventId, ct)
            ?? throw new InvalidOperationException("No Sessionize endpoint setting exists for this edition.");
        if (row.EndpointLastChangedAt is null)
            throw new InvalidOperationException("No endpoint change is awaiting a re-import choice.");

        row.PendingChangeMode = mode;
        row.ChangeModeChosenAt = _clock.GetUtcNow();
        row.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        return ToImportMode(mode);
    }

    private string Effective(string? rowEndpointId) =>
        string.IsNullOrWhiteSpace(rowEndpointId)
            ? (_options.EndpointId ?? string.Empty).Trim()
            : rowEndpointId.Trim();
}
