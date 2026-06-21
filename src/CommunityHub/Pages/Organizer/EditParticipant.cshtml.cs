using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
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
    private readonly WelcomeEmailService _welcome;
    private readonly TimeProvider _clock;

    public EditParticipantModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant,
        WelcomeEmailService welcome, TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _welcome = welcome;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public bool IsNew { get; private set; }

    /// <summary>True once this participant has already had a welcome email sent.</summary>
    public bool WelcomeAlreadySent { get; private set; }

    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? message { get; set; }

    [BindProperty] public string Email { get; set; } = string.Empty;
    [BindProperty] public string FullName { get; set; } = string.Empty;
    [BindProperty] public string? Phone { get; set; }
    [BindProperty] public ParticipantRole Role { get; set; } = ParticipantRole.Speaker;
    [BindProperty] public bool IsActive { get; set; } = true;
    [BindProperty] public string? SponsorCompanyId { get; set; }

    /// <summary>
    /// When ticked on the create form, fire the welcome email to the new
    /// participant (idempotent via the SentReminder ledger). Defaults off so a
    /// bulk hand-add does not surprise anyone with mail.
    /// </summary>
    [BindProperty] public bool SendWelcome { get; set; }

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

        if (!string.IsNullOrWhiteSpace(message)) Message = message;

        var p = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == Id && x.EventId == me.EventId, ct);
        if (p is null) { Error = $"Participant #{Id} not found."; IsNew = true; return Page(); }

        IsNew = false;
        Email = p.Email;
        FullName = p.FullName;
        Phone = p.Phone;
        Role = p.Role;
        IsActive = p.IsActive;
        SponsorCompanyId = p.SponsorCompanyId;

        WelcomeAlreadySent = await _db.SentReminders.AnyAsync(
            s => s.EventId == p.EventId
                 && s.RecipientEmail == p.Email
                 && s.ReminderType == "welcome",
            ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

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
                // Organizer hand-add bypasses the pre-selection queue: the row is
                // activated immediately so the person can sign in right away.
                LifecycleState = ParticipantLifecycleState.Active,
                QueueSource = ParticipantQueueSource.Manual,
                SponsorCompanyId = string.IsNullOrWhiteSpace(SponsorCompanyId) ? null : SponsorCompanyId.Trim(),
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.Participants.Add(p);
            await _db.SaveChangesAsync(ct);

            // Manual-create welcome hook: parity with the Sessionize import path,
            // which welcomes new speakers. Fires only when the organizer ticked
            // "Send welcome email" and the person is active. Idempotent via the
            // SentReminder ledger inside WelcomeEmailService.
            var note = "Created.";
            if (SendWelcome && p.IsActive)
            {
                try
                {
                    var sent = await _welcome.SendWelcomeAsync(p.Id, ct);
                    note = sent ? "Created and welcome email sent." : "Created (welcome already sent earlier).";
                }
                catch
                {
                    note = "Created, but the welcome email could not be sent (check email config).";
                }
            }
            return RedirectToPage("/Organizer/EditParticipant",
                new { Id = p.Id, message = note });
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
        p.SponsorCompanyId = string.IsNullOrWhiteSpace(SponsorCompanyId) ? null : SponsorCompanyId.Trim();
        await _db.SaveChangesAsync(ct);

        Message = "Saved.";
        IsNew = false;
        WelcomeAlreadySent = await _db.SentReminders.AnyAsync(
            s => s.EventId == p.EventId && s.RecipientEmail == p.Email && s.ReminderType == "welcome", ct);
        return Page();
    }

    /// <summary>
    /// Manually send (or re-attempt) the welcome email to an existing
    /// participant. Idempotent: WelcomeEmailService no-ops if it has already
    /// been sent. Lets an organizer welcome someone who was added by hand or
    /// before the welcome hook existed.
    /// </summary>
    public async Task<IActionResult> OnPostSendWelcomeAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var p = Id is null ? null
            : await _db.Participants.FirstOrDefaultAsync(x => x.Id == Id && x.EventId == me.EventId, ct);
        if (p is null) { Error = "Participant not found."; IsNew = true; return Page(); }

        if (!p.IsActive)
        {
            Error = "Cannot send a welcome to a deactivated participant.";
        }
        else
        {
            try
            {
                var sent = await _welcome.SendWelcomeAsync(p.Id, ct);
                Message = sent ? "Welcome email sent." : "Welcome email was already sent earlier — nothing re-sent.";
            }
            catch (Exception ex)
            {
                Error = $"Could not send the welcome email: {ex.Message}";
            }
        }

        // Re-hydrate the form.
        IsNew = false;
        Email = p.Email; FullName = p.FullName; Phone = p.Phone;
        Role = p.Role; IsActive = p.IsActive; SponsorCompanyId = p.SponsorCompanyId;
        WelcomeAlreadySent = await _db.SentReminders.AnyAsync(
            s => s.EventId == p.EventId && s.RecipientEmail == p.Email && s.ReminderType == "welcome", ct);
        return Page();
    }
}
