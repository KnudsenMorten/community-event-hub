using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

/// <summary>
/// The participant's own profile page (REQUIREMENTS §1): every signed-in role
/// can view and edit their own basics — display name and phone — and see the
/// read-only facts that identify them (email = login identity; role = admin-set).
///
/// No schema change: this reuses the existing <see cref="Participant"/> fields
/// (<c>FullName</c>, <c>Phone</c>). Editing is scoped to the signed-in
/// participant's own row only — a participant can never load or save another
/// person's profile.
/// </summary>
[Authorize]
public class ProfileModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;

    public ProfileModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant)
    {
        _db = db;
        _participant = participant;
    }

    // --- Editable basics ----------------------------------------------------
    [BindProperty] public string? FullName { get; set; }
    [BindProperty] public string? Phone { get; set; }

    /// <summary>Optional extra address; when set, mail to this person is also
    /// CC'd here (10a-5). Blank clears it.</summary>
    [BindProperty] public string? SecondaryEmail { get; set; }

    // --- Read-only identity facts (shown, not editable) ---------------------
    public string Email { get; private set; } = string.Empty;
    public ParticipantRole Role { get; private set; }

    public string? Message { get; private set; }
    public bool Saved { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var p = await _db.Participants
            .Where(p => p.Id == me.ParticipantId && p.EventId == me.EventId)
            .Select(p => new { p.FullName, p.Phone, p.Email, p.Role, p.SecondaryEmail })
            .FirstOrDefaultAsync(ct);
        if (p is null) return RedirectToPage("/Login");

        FullName = p.FullName;
        Phone = p.Phone;
        SecondaryEmail = p.SecondaryEmail;
        Email = p.Email;
        Role = p.Role;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Always scope the write to the signed-in participant's OWN row.
        var p = await _db.Participants.FirstOrDefaultAsync(
            x => x.Id == me.ParticipantId && x.EventId == me.EventId, ct);
        if (p is null) return RedirectToPage("/Login");

        // Read-only facts for the redisplay regardless of validation outcome.
        Email = p.Email;
        Role = p.Role;

        var trimmedName = (FullName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            Message = "Please enter your name.";
            FullName = p.FullName; // restore so the field is not blanked
            Phone = p.Phone;
            return Page();
        }
        if (trimmedName.Length > 200)
        {
            Message = "That name is too long (max 200 characters).";
            return Page();
        }

        var trimmedPhone = string.IsNullOrWhiteSpace(Phone) ? null : Phone.Trim();
        if (trimmedPhone is { Length: > 40 })
        {
            Message = "That phone number is too long (max 40 characters).";
            return Page();
        }

        var trimmedSecondary = string.IsNullOrWhiteSpace(SecondaryEmail)
            ? null : SecondaryEmail.Trim();
        if (trimmedSecondary is not null)
        {
            if (trimmedSecondary.Length > 320)
            {
                Message = "That secondary email is too long (max 320 characters).";
                return Page();
            }
            // Cheap shape check: one @ with text either side, no spaces.
            var at = trimmedSecondary.IndexOf('@');
            if (at <= 0 || at >= trimmedSecondary.Length - 1
                || trimmedSecondary.Contains(' '))
            {
                Message = "Please enter a valid secondary email (or leave it blank).";
                FullName = p.FullName;
                Phone = p.Phone;
                return Page();
            }
        }

        p.FullName = trimmedName;
        p.Phone = trimmedPhone;
        p.SecondaryEmail = trimmedSecondary;
        await _db.SaveChangesAsync(ct);

        FullName = p.FullName;
        Phone = p.Phone;
        SecondaryEmail = p.SecondaryEmail;
        Saved = true;
        // The header greeting reads the name from the session cookie, which is
        // stamped at login — so a changed name shows everywhere after the next
        // sign-in. The profile page itself always shows the saved value.
        Message = "Your profile has been saved.";
        return Page();
    }
}
