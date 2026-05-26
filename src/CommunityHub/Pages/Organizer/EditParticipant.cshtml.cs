using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

[Authorize]
public class EditParticipantModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;

    public EditParticipantModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant, TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public bool IsNew { get; private set; }

    [BindProperty(SupportsGet = true)] public int? Id { get; set; }

    [BindProperty] public string Email { get; set; } = string.Empty;
    [BindProperty] public string FullName { get; set; } = string.Empty;
    [BindProperty] public string? Phone { get; set; }
    [BindProperty] public ParticipantRole Role { get; set; } = ParticipantRole.Speaker;
    [BindProperty] public bool IsActive { get; set; } = true;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        if (Id is null)
        {
            IsNew = true;
            return Page();
        }

        var p = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == Id && x.EventId == me.EventId, ct);
        if (p is null) { Error = $"Participant #{Id} not found."; IsNew = true; return Page(); }

        IsNew = false;
        Email = p.Email;
        FullName = p.FullName;
        Phone = p.Phone;
        Role = p.Role;
        IsActive = p.IsActive;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var emailNorm = (Email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(emailNorm) || !emailNorm.Contains('@'))
        {
            Error = "A valid email is required."; IsNew = (Id is null); return Page();
        }
        if (string.IsNullOrWhiteSpace(FullName))
        {
            Error = "Full name is required."; IsNew = (Id is null); return Page();
        }

        Participant? p = Id is null
            ? null
            : await _db.Participants
                .FirstOrDefaultAsync(x => x.Id == Id && x.EventId == me.EventId, ct);

        if (p is null)
        {
            // Reject if email already taken in this edition.
            var clash = await _db.Participants.AnyAsync(
                x => x.EventId == me.EventId && x.Email == emailNorm, ct);
            if (clash)
            {
                Error = $"A participant with email {emailNorm} already exists in this edition.";
                IsNew = true; return Page();
            }

            p = new Participant
            {
                EventId = me.EventId,
                Email = emailNorm,
                FullName = FullName.Trim(),
                Phone = string.IsNullOrWhiteSpace(Phone) ? null : Phone.Trim(),
                Role = Role,
                IsActive = IsActive,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.Participants.Add(p);
            await _db.SaveChangesAsync(ct);
            return RedirectToPage("/Organizer/EditParticipant",
                new { Id = p.Id, message = "Created." });
        }

        // Reject email change that collides with another row.
        if (!string.Equals(p.Email, emailNorm, StringComparison.OrdinalIgnoreCase))
        {
            var clash = await _db.Participants.AnyAsync(
                x => x.EventId == me.EventId && x.Email == emailNorm && x.Id != p.Id, ct);
            if (clash)
            {
                Error = $"Email {emailNorm} is already used by another participant in this edition.";
                IsNew = false; return Page();
            }
            p.Email = emailNorm;
        }

        p.FullName = FullName.Trim();
        p.Phone = string.IsNullOrWhiteSpace(Phone) ? null : Phone.Trim();
        p.Role = Role;
        p.IsActive = IsActive;
        await _db.SaveChangesAsync(ct);

        Message = "Saved.";
        IsNew = false;
        return Page();
    }
}
