using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Org-admin console for the per-ROOM session-evaluation QR codes (REQUIREMENTS §124).
/// Lists the QR files an operator keeps in the configured SharePoint folder and lets an
/// organizer UPLOAD / REPLACE / DELETE them. Speakers then download the QR for their
/// session's room (Session Evaluation page + My Sessions). Organizer-only; INERT (a
/// friendly "not configured" notice) when the QR folder is not wired. Mobile-first.
/// </summary>
[Authorize]
public class SessionEvalsQrModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SessionEvalsQrService _qr;

    public SessionEvalsQrModel(ICurrentParticipantAccessor participant, SessionEvalsQrService qr)
    {
        _participant = participant;
        _qr = qr;
    }

    public bool AccessDenied { get; private set; }
    public bool Configured { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<SessionEvalQrFile> Files { get; private set; } = Array.Empty<SessionEvalQrFile>();

    [BindProperty] public IFormFile? Upload { get; set; }
    [BindProperty] public string? FileName { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        if (!_qr.CanManage)
        {
            Error = "The session-evaluation QR SharePoint folder is not configured yet — nothing was uploaded.";
            await LoadAsync(ct);
            return Page();
        }
        if (Upload is null || Upload.Length == 0)
        {
            Error = "Choose a QR image file to upload.";
            await LoadAsync(ct);
            return Page();
        }

        using var ms = new MemoryStream();
        await Upload.CopyToAsync(ms, ct);
        var name = Path.GetFileName(Upload.FileName);
        await _qr.UploadAsync(name, ms.ToArray(), Upload.ContentType ?? "application/octet-stream", ct);
        Message = $"Uploaded \"{name}\" (room: {SessionEvalsQrService.RoomDisplayFromFileName(name)}).";
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        if (_qr.CanManage && !string.IsNullOrWhiteSpace(FileName))
        {
            await _qr.DeleteAsync(Path.GetFileName(FileName), ct);
            Message = $"Deleted \"{FileName}\".";
        }
        await LoadAsync(ct);
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Configured = _qr.CanRead;
        Files = await _qr.ListAllAsync(ct);
    }
}
