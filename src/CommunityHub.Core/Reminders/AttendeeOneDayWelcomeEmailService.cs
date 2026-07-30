using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §208 — the NEW welcome email for 1-day-ticket attendees (they get none today). It
/// introduces the hub and carries ONE task: the Party signup. The CTA is the participant's
/// personal 1-YEAR auto-login magic-link (§169) — the <c>welcome-attendee-1day</c> template's
/// <c>{{hubUrl}}</c> is rewritten to the magic link by <see cref="EmailTemplateProvider.NewTokenSet"/>.
///
/// <para><b>New-only, idempotent.</b> Like the 2-day provisioning, this only welcomes a
/// participant who has never been welcomed (<see cref="Participant.WelcomeWithLoginSentAt"/> is
/// null), so a re-run never re-emails and existing attendees are not back-filled. Gated upstream
/// by the <c>welcome-email</c> feature ring + the central email kill-switch via the
/// <see cref="EmailContext"/> FeatureKey.</para>
/// </summary>
public sealed class AttendeeOneDayWelcomeEmailService
{
    private const string TemplateName = "welcome-attendee-1day";

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailSender _emailSender;
    private readonly IEmailContextAccessor? _context;
    private readonly TimeProvider _clock;
    // §234: delivered-vs-dropped seam — the WelcomeWithLoginSentAt stamp is written
    // only when the transport actually delivered, so a ring-dropped welcome is
    // retried once rings widen. Null (legacy/test wiring) ⇒ old always-stamp.
    private readonly IEmailDeliveryOutcome? _outcome;

    public AttendeeOneDayWelcomeEmailService(
        CommunityHubDbContext db,
        EmailTemplateProvider templates,
        IEmailSender emailSender,
        TimeProvider clock,
        IEmailContextAccessor? context = null,
        IEmailDeliveryOutcome? outcome = null)
    {
        _db = db;
        _templates = templates;
        _emailSender = emailSender;
        _clock = clock;
        _context = context;
        _outcome = outcome;
    }

    /// <summary>
    /// RETIRED (§299 OPEN-26, operator 2026-07-23): 1-day attendees receive NO welcome mail.
    /// The send path below is kept compiling for historic re-renders but is inert — every call
    /// returns false without emailing or stamping. The 2-day welcome
    /// (<c>AttendeeWelcomeProvisioningService</c>) is unaffected. Delete the class once the
    /// <c>welcome-attendee-1day</c> template is retired from the catalog.
    /// </summary>
    public Task<bool> SendForProvisioningAsync(int participantId, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>The pre-retirement §208 send, kept only for reference (never called).</summary>
    private async Task<bool> RetiredSendForProvisioningAsync(int participantId, CancellationToken ct = default)
    {
        var p = await _db.Participants
            .Include(x => x.Event)
            .FirstOrDefaultAsync(x => x.Id == participantId, ct);
        if (p is null || !p.IsActive || p.Role != ParticipantRole.Attendee) return false;
        if (p.WelcomeWithLoginSentAt is not null) return false; // already welcomed — new-only

        var firstName = string.IsNullOrWhiteSpace(p.FullName)
            ? "there"
            : p.FullName.Split(' ')[0];

        var tokens = _templates.NewTokenSet(p.Id);   // §169: {{hubUrl}} → 1-year magic link
        tokens["firstName"] = firstName;
        tokens["communityName"] = p.Event.CommunityName;
        tokens["eventDisplayName"] = p.Event.DisplayName;
        tokens["eventCode"] = p.Event.Code;
        var rendered = _templates.Render(TemplateName, tokens);

        // §217 (F3): tag as an ATTENDEE WELCOME so the sender applies the dedicated
        // Email:AttendeeWelcomeMaxReleaseRing ceiling (default Ring1) on top of the
        // welcome-email feature gate — the ~1500 1-day attendees are released in phases.
        using (_context?.Set(new EmailContext(
            TemplateName, p.EventId, p.Id, p.FullName, FeatureKey: "welcome-email",
            AttendeeWelcome: true)))
        {
            await _emailSender.SendAsync(p.Email, rendered.Subject, rendered.HtmlBody, ct);
        }

        // §234: a gated (ring-dropped / kill-switched) send is NOT a welcome — do
        // not stamp, so the provisioning sync retries automatically once the
        // operator widens the ring / attendee-welcome cap.
        if (_outcome is not null && !_outcome.LastSendDelivered)
        {
            return false;
        }

        p.WelcomeWithLoginSentAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
