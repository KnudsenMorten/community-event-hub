using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Sends the welcome email to a participant (CONTEXT.md - email matrix). The
/// email is role-aware: one template (welcome.html), with a {{roleGuidance}}
/// token whose text depends on the participant's role, so a speaker, a
/// volunteer and a sponsor each get guidance relevant to them.
///
/// Sent on participant creation / import. Routed through SentReminder (via the
/// caller, or directly here) so a re-import does not re-welcome someone.
/// </summary>
public sealed class WelcomeEmailService
{
    private const string TemplateName = "welcome";
    private const string ReminderType = "welcome";

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailSender _emailSender;
    private readonly TimeProvider _clock;

    public WelcomeEmailService(
        CommunityHubDbContext db,
        EmailTemplateProvider templates,
        IEmailSender emailSender,
        TimeProvider clock)
    {
        _db = db;
        _templates = templates;
        _emailSender = emailSender;
        _clock = clock;
    }

    /// <summary>
    /// Send the welcome email to one participant if it has not been sent
    /// before (idempotent via the SentReminder ledger). Returns true if an
    /// email was actually sent.
    /// </summary>
    public async Task<bool> SendWelcomeAsync(
        int participantId, CancellationToken ct = default)
    {
        var participant = await _db.Participants
            .Include(p => p.Event)
            .FirstOrDefaultAsync(p => p.Id == participantId, ct);
        if (participant is null || !participant.IsActive)
        {
            return false;
        }

        // Idempotency: one welcome per participant, ever.
        var occasionKey = $"welcome:{participant.Id}";
        var already = await _db.SentReminders.AnyAsync(
            s => s.EventId == participant.EventId
                 && s.RecipientEmail == participant.Email
                 && s.ReminderType == ReminderType
                 && s.OccasionKey == occasionKey,
            ct);
        if (already)
        {
            return false;
        }

        var firstName = string.IsNullOrWhiteSpace(participant.FullName)
            ? "there"
            : participant.FullName.Split(' ')[0];

        var tokens = _templates.NewTokenSet();
        tokens["firstName"] = firstName;
        tokens["communityName"] = participant.Event.CommunityName;
        tokens["eventDisplayName"] = participant.Event.DisplayName;
        tokens["roleName"] = FriendlyRoleName(participant.Role);
        tokens["roleGuidance"] = RoleGuidance(participant.Role);

        var rendered = _templates.Render(TemplateName, tokens);
        await _emailSender.SendAsync(
            participant.Email, rendered.Subject, rendered.HtmlBody, ct);

        // Record it so a re-import does not re-send.
        _db.SentReminders.Add(new SentReminder
        {
            EventId = participant.EventId,
            RecipientEmail = participant.Email,
            ReminderType = ReminderType,
            OccasionKey = occasionKey,
            SentAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static string FriendlyRoleName(ParticipantRole role) => role switch
    {
        ParticipantRole.Organizer => "organizer",
        ParticipantRole.Speaker => "speaker",
        ParticipantRole.MasterclassSpeaker => "Master Class speaker",
        ParticipantRole.Volunteer => "volunteer",
        ParticipantRole.Sponsor => "sponsor contact",
        ParticipantRole.Attendee => "attendee",
        _ => "participant",
    };

    /// <summary>Role-specific guidance paragraph for the welcome email.</summary>
    private static string RoleGuidance(ParticipantRole role) => role switch
    {
        ParticipantRole.Organizer =>
            "You have full access: participant management, sponsor orders, and "
            + "attendee reconciliation.",
        ParticipantRole.Speaker =>
            "You will find your hotel and dinner forms and your session "
            + "deadlines in the hub. Please complete the forms before the "
            + "deadline.",
        ParticipantRole.MasterclassSpeaker =>
            "You will find your hotel and dinner forms, your session "
            + "deadlines, and pre-day information in the hub.",
        ParticipantRole.Volunteer =>
            "Please complete the volunteer sign-up in the hub to tell us which "
            + "shifts you can work. You will also find your hotel and dinner "
            + "forms there.",
        ParticipantRole.Sponsor =>
            "Your sponsor onboarding tasks and deadlines are in the hub. New "
            + "tasks appear as your order is processed.",
        ParticipantRole.Attendee =>
            "You can check your Master Class booking status in the hub.",
        _ => "Sign in to the hub to see what is relevant to you.",
    };
}
