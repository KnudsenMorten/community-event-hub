using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer page to import speakers from the Sessionize v2 view API. The pull
/// upserts Participant rows (role Speaker), matched by email. Organizer-only.
/// (The legacy Excel/.xlsx upload path was removed — §82, API-only now.)
/// </summary>
[Authorize]
public class SessionizeImportModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SessionizeApiImportService _apiImport;
    private readonly SessionizeImportPreviewService _preview;
    private readonly CommunityHub.Core.Integrations.SessionizeApiOptions _apiOptions;
    private readonly CommunityHub.Core.Settings.FeatureGateService _gate;
    private readonly CommunityHub.Core.Settings.RingResolver _rings;

    public SessionizeImportModel(
        ICurrentParticipantAccessor participant,
        SessionizeApiImportService apiImport,
        SessionizeImportPreviewService preview,
        CommunityHub.Core.Integrations.SessionizeApiOptions apiOptions,
        CommunityHub.Core.Settings.FeatureGateService gate,
        CommunityHub.Core.Settings.RingResolver rings)
    {
        _participant = participant;
        _apiImport = apiImport;
        _preview = preview;
        _apiOptions = apiOptions;
        _gate = gate;
        _rings = rings;
    }

    public bool AccessDenied { get; private set; }
    public SessionizeImportResult? Result { get; private set; }
    public string? ValidationError { get; private set; }

    /// <summary>
    /// The result of a DRY-RUN preview (REQUIREMENTS §21): created / updated /
    /// skipped counts + which speaker bios would be overwritten, computed WITHOUT
    /// writing. Shown so the organizer can confirm before committing a real import.
    /// Null until a Preview button is pressed.
    /// </summary>
    public CommunityHub.Core.Reminders.SessionizeImportPreviewResult? Preview { get; private set; }

    /// <summary>
    /// True when the last import that produced <see cref="Result"/> was a FULL
    /// refresh (the "Full import from Sessionize" override), so the result
    /// banner can say so. False = the default delta pull.
    /// </summary>
    public bool ResultWasFullImport { get; private set; }

    /// <summary>True when the Sessionize v2 view API is configured + enabled, so
    /// the "Pull from Sessionize API" button is shown.</summary>
    public bool ApiEnabled => _apiOptions.Enabled
        && !string.IsNullOrWhiteSpace(_apiOptions.EndpointId);

    public IActionResult OnGet()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer)
        {
            AccessDenied = true;
        }
        return Page();
    }

    /// <summary>
    /// DRY-RUN the configured Sessionize API pull (no writes): show the organizer
    /// created / updated / skipped counts AND which speaker bios would be overwritten
    /// before they commit. Defaults to FULL mode — that is the destructive path whose
    /// overwrites the organizer most needs to see; the Delta preview is offered too.
    /// Organizer-only.
    /// </summary>
    public Task<IActionResult> OnPostPreviewApiAsync(CancellationToken ct) =>
        RunApiPreviewAsync(SessionizeImportMode.Full, ct);

    /// <summary>Dry-run the DELTA API pull (additive — overwrites nothing). Organizer-only.</summary>
    public Task<IActionResult> OnPostPreviewApiDeltaAsync(CancellationToken ct) =>
        RunApiPreviewAsync(SessionizeImportMode.Delta, ct);

    private async Task<IActionResult> RunApiPreviewAsync(
        SessionizeImportMode mode, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me))
        {
            AccessDenied = true;
            return Page();
        }

        if (!ApiEnabled)
        {
            ValidationError = "The Sessionize API integration is not configured.";
            return Page();
        }

        Preview = await _preview.PreviewApiAsync(me.EventId, mode, ct);
        if (Preview.Error is not null)
        {
            ValidationError = Preview.Error;
        }
        return Page();
    }

    /// <summary>
    /// Pull speakers straight from the configured Sessionize v2 view API
    /// (no file), DELTA mode: add new speakers + fill empty/untouched bio
    /// fields, never flush a speaker's own edits. sendWelcome:false — the
    /// organizer-driven pull never emails anyone. Organizer-only.
    /// </summary>
    public Task<IActionResult> OnPostApiAsync(CancellationToken ct) =>
        RunApiImportAsync(SessionizeImportMode.Delta, ct);

    /// <summary>
    /// FULL import override: pull ALL accepted speakers + ALL fields and
    /// force-refresh every bio field from Sessionize, clearing each speaker's
    /// "edited" set (a complete re-seed). Use this when the organizer
    /// deliberately wants the latest from Sessionize to win over hub edits.
    /// Organizer-only; never emails.
    /// </summary>
    public Task<IActionResult> OnPostApiFullAsync(CancellationToken ct) =>
        RunApiImportAsync(SessionizeImportMode.Full, ct);

    private async Task<IActionResult> RunApiImportAsync(
        SessionizeImportMode mode, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me))
        {
            AccessDenied = true;
            return Page();
        }

        if (!ApiEnabled)
        {
            ValidationError = "The Sessionize API integration is not configured.";
            return Page();
        }

        // GATE (REQUIREMENTS §23): same ring-aware 'sessionize-import' gate as the
        // scheduled pull. Disabled / not released to the organizer's ring ⇒ no-op
        // with a clear "feature disabled" message.
        if (!await IsSessionizeImportActiveForMeAsync(me, ct))
        {
            ValidationError = "Sessionize import is turned off for this event. "
                + "Enable it in Settings to pull speakers.";
            return Page();
        }

        ResultWasFullImport = mode == SessionizeImportMode.Full;
        Result = await _apiImport.ImportAsync(
            me.EventId, ct, sendWelcome: false, mode: mode);
        return Page();
    }

    /// <summary>
    /// The §23 ring-aware gate for the current organizer: the feature is active
    /// for them only when it is enabled (not killed) AND their effective ring is
    /// ≤ the 'sessionize-import' released ring. With nothing ring-assigned the
    /// organizer is Ring3 and the feature is released to Ring3, so this reduces to
    /// the plain on/off switch — behaviour unchanged.
    /// </summary>
    private Task<bool> IsSessionizeImportActiveForMeAsync(
        CurrentParticipant me, CancellationToken ct) =>
        _gate.IsFeatureActiveForParticipantAsync(
            "sessionize-import", me.EventId, me.ParticipantId, _rings, ct);
}
