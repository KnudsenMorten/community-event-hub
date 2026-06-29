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
    private readonly IEmailContextAccessor? _context;
    private readonly CommunityHub.Core.Settings.FeatureGateService? _gate;
    private readonly CommunityHub.Core.Settings.RingResolver? _rings;

    public WelcomeEmailService(
        CommunityHubDbContext db,
        EmailTemplateProvider templates,
        IEmailSender emailSender,
        TimeProvider clock,
        IEmailContextAccessor? context = null,
        CommunityHub.Core.Settings.FeatureGateService? gate = null,
        CommunityHub.Core.Settings.RingResolver? rings = null)
    {
        _db = db;
        _templates = templates;
        _emailSender = emailSender;
        _clock = clock;
        _context = context;
        _gate = gate;
        _rings = rings;
    }

    /// <summary>
    /// Send the welcome email to one participant if it has not been sent
    /// before (idempotent via the SentReminder ledger). Returns true if an
    /// email was actually sent. Pass <paramref name="force"/> = true for an
    /// organizer-initiated RESEND: the once-ever idempotency check is bypassed
    /// (the ring gate + email allowlist still apply), and no duplicate ledger
    /// row is written.
    /// </summary>
    public async Task<bool> SendWelcomeAsync(
        int participantId, CancellationToken ct = default, bool force = false)
    {
        var participant = await _db.Participants
            .Include(p => p.Event)
            .FirstOrDefaultAsync(p => p.Id == participantId, ct);
        if (participant is null || !participant.IsActive)
        {
            return false;
        }

        // Attendees get the per-role variant welcome (welcome-attendee) via the
        // WelcomeWithLoginEmailService provisioning path, NOT this legacy once-ever
        // welcome — so skip them here to avoid a double welcome (operator 2026-06-22).
        if (participant.Role == ParticipantRole.Attendee)
        {
            return false;
        }

        // Route to the speaker's effective address (override ?? Sessionize).
        // Non-speakers / no override resolve to the participant's own address.
        var overrideEmail = await _db.SpeakerProfiles
            .Where(sp => sp.ParticipantId == participant.Id)
            .Select(sp => sp.ContactEmailOverride)
            .FirstOrDefaultAsync(ct);
        var toEmail = Domain.SpeakerProfile.EffectiveEmailFor(
            participant.Email, overrideEmail);

        // Idempotency: one welcome per participant, ever. Keyed on the IDENTITY
        // address so changing the override later never re-welcomes the speaker.
        var occasionKey = $"welcome:{participant.Id}";
        var already = await _db.SentReminders.AnyAsync(
            s => s.EventId == participant.EventId
                 && s.RecipientEmail == participant.Email
                 && s.ReminderType == ReminderType
                 && s.OccasionKey == occasionKey,
            ct);
        if (already && !force)
        {
            return false;
        }

        // DESIRED-STATE RING GATE (operator 2026-06-23): if the recipient is OUTSIDE
        // the welcome-email feature's released ring, SKIP without recording — so a
        // reconcile re-sends automatically when the ring is later widened. Without
        // this, the send below would be ring-DROPPED by BrevoEmailSender yet still
        // recorded as welcomed, and the recipient would never get it. (Null gate in
        // legacy/test wiring ⇒ no pre-gate; the sender's ring drop still applies.)
        if (_gate is not null && _rings is not null
            && !await _gate.IsFeatureActiveForParticipantAsync(
                "welcome-email", participant.EventId, participant.Id, _rings, ct))
        {
            return false;
        }

        var firstName = string.IsNullOrWhiteSpace(participant.FullName)
            ? "there"
            : participant.FullName.Split(' ')[0];

        // §169: the welcome CTA becomes the recipient's personal auto-login link.
        var tokens = _templates.NewTokenSet(participant.Id);
        tokens["firstName"] = firstName;
        tokens["communityName"] = participant.Event.CommunityName;
        tokens["eventDisplayName"] = participant.Event.DisplayName;
        tokens["eventCode"] = participant.Event.Code;
        tokens["roleName"] = FriendlyRoleName(participant.Role);
        tokens["roleGuidance"] = RoleGuidance(participant.Role);
        tokens["roleLine"] = WelcomeWithLoginEmailService.RoleLine(participant.Role);
        // Sponsor variant: dynamic "you are receiving this in your role as …" line.
        tokens["sponsorRole"] = CommunityHub.Core.Email.WelcomeVariants.SponsorRoleLabel(
            participant.IsEventCoordinator, participant.IsSigner, participant.IsBoothMember);

        // Render the per-role VARIANT (welcome-sponsor / welcome-speaker / …) when
        // one exists for the role, so the polished role-specific copy is what goes
        // out; fall back to the generic welcome for roles without a variant.
        var templateKey =
            CommunityHub.Core.Email.WelcomeVariants.TemplateKeyFor(participant.Role) ?? TemplateName;
        var rendered = _templates.Render(templateKey, tokens);
        // Ring-governed by the welcome-email feature (operator 2026-06-22).
        using (_context?.Set(new EmailContext(
            ReminderType, participant.EventId, participant.Id, participant.FullName,
            FeatureKey: "welcome-email")))
        {
            await _emailSender.SendAsync(
                toEmail, rendered.Subject, rendered.HtmlBody, ct);
        }

        // Record it (first time) so a re-import does not re-send. A forced resend
        // re-sends an already-welcomed person without adding a duplicate ledger row.
        if (!already)
        {
            _db.SentReminders.Add(new SentReminder
            {
                EventId = participant.EventId,
                RecipientEmail = participant.Email,
                ReminderType = ReminderType,
                OccasionKey = occasionKey,
                SentAt = _clock.GetUtcNow(),
            });
            await _db.SaveChangesAsync(ct);
        }
        return true;
    }

    private static string FriendlyRoleName(ParticipantRole role) => role switch
    {
        ParticipantRole.Organizer => "organizer",
        ParticipantRole.Speaker => "speaker",
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
            "Start with the \"Get Started\" flow in the hub — it walks you "
            + "through everything you need to set up. Afterwards you can change "
            + "anything under Event Logistics if needed.",
        ParticipantRole.Volunteer =>
            "Start with the \"Get Started\" flow in the hub — it walks you "
            + "through everything you need to set up. Afterwards you can change "
            + "anything under Event Logistics if needed.",
        ParticipantRole.Sponsor =>
            "Your sponsor onboarding tasks and deadlines are in the hub. New "
            + "tasks appear as your order is processed.",
        ParticipantRole.Attendee =>
            "You can check your Master Class booking status in the hub.",
        _ => "Sign in to the hub to see what is relevant to you.",
    };
}
