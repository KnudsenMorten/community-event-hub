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

    public SessionizeImportModel(
        ICurrentParticipantAccessor participant,
        SessionizeImportService import)
    {
        _participant = participant;
        _import = import;
    }

    [BindProperty]
    public IFormFile? UploadFile { get; set; }

    public bool AccessDenied { get; private set; }
    public SessionizeImportResult? Result { get; private set; }
    public string? ValidationError { get; private set; }

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

        // Validate the upload before touching it.
        if (UploadFile is null || UploadFile.Length == 0)
        {
            ValidationError = "Please choose a file to upload.";
            return Page();
        }
        if (UploadFile.Length > MaxUploadBytes)
        {
            ValidationError = "The file is too large (limit 5 MB).";
            return Page();
        }
        var ext = Path.GetExtension(UploadFile.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
        {
            ValidationError = "Please upload an Excel file (.xlsx).";
            return Page();
        }

        await using var stream = UploadFile.OpenReadStream();
        // sendWelcome:false -- this organizer-driven button never emails anyone.
        // Welcome emails are sent manually from the participants page when ready.
        Result = await _import.ImportAsync(me.EventId, stream, ct, sendWelcome: false);
        return Page();
    }
}
