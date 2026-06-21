using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer-gated settings page for the edition's Sessionize endpoint.
///
/// The organizer provides/edits the Sessionize <b>endpoint id</b> (the
/// <c>&lt;your-event-id&gt;</c> segment of the v2 view URL — ordinary operator
/// config, NOT a secret). When the endpoint actually CHANGES (the real driver is
/// the ELDK26→ELDK27 switch), the page prompts the organizer to choose how the
/// already-imported data is handled:
///  - <b>Replace</b> — replace existing data + re-import accepted speakers from the
///    new endpoint (production path). Maps to import mode <c>Full</c>.
///  - <b>Merge</b> — merge with existing data (testing only). Maps to import mode
///    <c>Delta</c> (never flushes a speaker's edits).
///
/// The page only BUILDS the trigger + confirmation flow: it persists the endpoint,
/// the last-change stamp and the chosen mode, then links the organizer to the
/// matching button on <c>/Organizer/SessionizeImport</c>. It NEVER runs an import.
/// </summary>
[Authorize]
public class SessionizeEndpointSettingsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SessionizeEndpointSettingsService _settings;

    public SessionizeEndpointSettingsModel(
        ICurrentParticipantAccessor participant,
        SessionizeEndpointSettingsService settings)
    {
        _participant = participant;
        _settings = settings;
    }

    [BindProperty]
    public string? EndpointId { get; set; }

    [BindProperty]
    public string? View { get; set; }

    /// <summary>The Replace/Merge choice posted from the change-handling prompt.</summary>
    [BindProperty]
    public SessionizeChangeMode ChosenMode { get; set; }

    public bool AccessDenied { get; private set; }
    public string? ValidationError { get; private set; }
    public string? SavedMessage { get; private set; }

    /// <summary>The persisted setting row for the edition (null until first save).</summary>
    public SessionizeEndpointSetting? Setting { get; private set; }

    /// <summary>The endpoint id currently in effect (saved row, else config default).</summary>
    public string EffectiveEndpointId { get; private set; } = string.Empty;

    /// <summary>True when an endpoint change is recorded but no Replace/Merge choice made yet.</summary>
    public bool AwaitingChoice { get; private set; }

    /// <summary>Set after a Replace/Merge choice is recorded — the import mode it maps to.</summary>
    public SessionizeImportMode? RecordedImportMode { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return GateResult();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var newId = (EndpointId ?? string.Empty).Trim();
        // The endpoint id is the <your-event-id> URL segment: a short token, no
        // scheme/slashes. Reject obvious paste-of-the-whole-URL mistakes.
        if (newId.Length > 0 &&
            (newId.Contains('/') || newId.Contains(' ') || newId.Contains("http", StringComparison.OrdinalIgnoreCase)))
        {
            ValidationError = "Enter only the endpoint id (the <your-event-id> segment of the "
                + "Sessionize v2 view URL), not the full URL.";
            await LoadAsync(ct);
            return Page();
        }

        var result = await _settings.SaveEndpointAsync(
            me.EventId, newId, View, me.Email, ct);

        await LoadAsync(ct);
        SavedMessage = result.EndpointChanged
            ? "Endpoint changed. Choose how to handle the already-imported speakers below."
            : "Sessionize endpoint settings saved.";
        return Page();
    }

    /// <summary>
    /// Record the organizer's Replace/Merge choice for the recorded endpoint change.
    /// Persists the CHOICE only and surfaces the import mode it maps to + a link to
    /// the matching import button. Does NOT run an import.
    /// </summary>
    public async Task<IActionResult> OnPostChooseModeAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        if (ChosenMode is not (SessionizeChangeMode.Replace or SessionizeChangeMode.Merge))
        {
            ValidationError = "Please choose Replace or Merge.";
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            RecordedImportMode = await _settings.RecordChangeChoiceAsync(me.EventId, ChosenMode, ct);
        }
        catch (InvalidOperationException ex)
        {
            ValidationError = ex.Message;
            await LoadAsync(ct);
            return Page();
        }

        await LoadAsync(ct);
        SavedMessage = ChosenMode == SessionizeChangeMode.Replace
            ? "Replace chosen — run \"Full import from Sessionize\" to re-import from the new endpoint."
            : "Merge chosen (testing) — run \"Sync new speakers (delta)\" to merge without flushing edits.";
        return Page();
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || me.Role != ParticipantRole.Organizer)
        {
            AccessDenied = me is not null && me.Role != ParticipantRole.Organizer;
            return false;
        }

        Setting = await _settings.LoadAsync(me.EventId, ct);
        EffectiveEndpointId = await _settings.GetEffectiveEndpointIdAsync(me.EventId, ct);
        AwaitingChoice = Setting?.AwaitingChangeChoice ?? false;

        // Prefill the form with the effective values on GET / re-render.
        EndpointId ??= Setting?.EndpointId ?? string.Empty;
        View ??= Setting?.View ?? "Speakers";
        return true;
    }

    private IActionResult GateResult() =>
        _participant.Current is null ? RedirectToPage("/Login") : Page();
}
