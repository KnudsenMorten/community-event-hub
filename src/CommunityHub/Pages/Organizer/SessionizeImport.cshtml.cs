using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer page to import speakers from a Sessionize Excel export
/// (CONTEXT.md / DESIGN_NOTES). The organizer downloads the speaker list from
/// Sessionize as .xlsx and uploads it here; the import upserts Participant
/// rows (role Speaker). Organizer-only.
/// </summary>
[Authorize]
public class SessionizeImportModel : PageModel
{
    // Accept only spreadsheet uploads, and cap the size - a speaker list is small.
    private const long MaxUploadBytes = 5 * 1024 * 1024; // 5 MB
    private static readonly string[] AllowedExtensions = { ".xlsx", ".xlsm" };

    private readonly ICurrentParticipantAccessor _participant;
    private readonly SessionizeImportService _import;
    private readonly SessionizeApiImportService _apiImport;
    private readonly SessionizeImportPreviewService _preview;
    private readonly CommunityHub.Core.Integrations.SessionizeApiOptions _apiOptions;

    public SessionizeImportModel(
        ICurrentParticipantAccessor participant,
        SessionizeImportService import,
        SessionizeApiImportService apiImport,
        SessionizeImportPreviewService preview,
        CommunityHub.Core.Integrations.SessionizeApiOptions apiOptions)
    {
        _participant = participant;
        _import = import;
        _apiImport = apiImport;
        _preview = preview;
        _apiOptions = apiOptions;
    }

    [BindProperty]
    public IFormFile? UploadFile { get; set; }

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

    public async Task<IActionResult> OnPostImportAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer)
        {
            AccessDenied = true;
            return Page();
        }

        if (!ValidateUpload())
        {
            return Page();
        }

        await using var stream = UploadFile!.OpenReadStream();
        // sendWelcome:false -- this organizer-driven button never emails anyone.
        // Welcome emails are sent manually from the participants page when ready.
        Result = await _import.ImportAsync(me.EventId, stream, ct, sendWelcome: false);
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
        if (me.Role != ParticipantRole.Organizer)
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
    /// DRY-RUN an uploaded Excel export in FULL mode (no writes): the same
    /// counts + would-overwrite preview as the API path, for the file source.
    /// Organizer-only.
    /// </summary>
    public async Task<IActionResult> OnPostPreviewUploadAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer)
        {
            AccessDenied = true;
            return Page();
        }

        if (!ValidateUpload())
        {
            return Page();
        }

        await using var stream = UploadFile!.OpenReadStream();
        Preview = await _preview.PreviewExcelAsync(
            me.EventId, stream, SessionizeImportMode.Full, ct);
        if (Preview.Error is not null)
        {
            ValidationError = Preview.Error;
        }
        return Page();
    }

    /// <summary>
    /// Validate the uploaded file (presence, size, extension). Sets
    /// <see cref="ValidationError"/> and returns false on failure.
    /// </summary>
    private bool ValidateUpload()
    {
        if (UploadFile is null || UploadFile.Length == 0)
        {
            ValidationError = "Please choose a file to upload.";
            return false;
        }
        if (UploadFile.Length > MaxUploadBytes)
        {
            ValidationError = "The file is too large (limit 5 MB).";
            return false;
        }
        var ext = Path.GetExtension(UploadFile.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
        {
            ValidationError = "Please upload an Excel file (.xlsx).";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Pull speakers straight from the configured Sessionize v2 view API
    /// (no file), DELTA mode: add new speakers + fill empty/untouched bio
    /// fields, never flush a speaker's own edits. Same sendWelcome:false safety
    /// as the upload path. Organizer-only.
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
        if (me.Role != ParticipantRole.Organizer)
        {
            AccessDenied = true;
            return Page();
        }

        if (!ApiEnabled)
        {
            ValidationError = "The Sessionize API integration is not configured.";
            return Page();
        }

        ResultWasFullImport = mode == SessionizeImportMode.Full;
        Result = await _apiImport.ImportAsync(
            me.EventId, ct, sendWelcome: false, mode: mode);
        return Page();
    }
}
