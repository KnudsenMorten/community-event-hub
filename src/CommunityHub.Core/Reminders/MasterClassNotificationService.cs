using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §383 — Master Class landing-page notifications: who is subscribed, and the two mails.
///
/// <para><b>Audience:</b> everyone with a stake in one Master Class — the SPEAKERS linked to it and
/// the ATTENDEES signed up for it (confirmed seats and waitlist places alike; someone queueing for a
/// class cares that its instructions changed).</para>
///
/// <para><b>Subscription is the default.</b> A <see cref="MasterClassSubscription"/> row means
/// UNSUBSCRIBED. That inversion is deliberate: the operator asked for the toggles to be ON by
/// default, and "no row = subscribed" makes that true for people who join later without any
/// backfill. A default-true column would need a row per member and would silently mute anyone
/// missed.</para>
///
/// <para><b>Never notify the actor about their own action</b> — the first bug this kind of feature
/// always ships with.</para>
/// </summary>
public sealed class MasterClassNotificationService
{
    /// <summary>The feature key both mails are tagged with (ring-gated: this is OUTREACH — it is
    /// triggered by SOMEONE ELSE's action, not by the recipient pressing a button, so §326by's
    /// participant-clicked exemption does NOT apply).</summary>
    public const string FeatureKey = "masterclass-notifications";

    private const string Category = "masterclass-notify";

    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _sender;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailContextAccessor _context;

    public MasterClassNotificationService(
        CommunityHubDbContext db,
        IEmailSender sender,
        EmailTemplateProvider templates,
        IEmailContextAccessor context)
    {
        _db = db;
        _sender = sender;
        _templates = templates;
        _context = context;
    }

    /// <summary>One person to notify, resolved to the address we actually send to.</summary>
    public sealed record Recipient(
        string Email, string DisplayName, int? ParticipantId, int? AttendeeId);

    /// <summary>
    /// Is this person subscribed to <paramref name="kind"/> for this class? True unless an opt-out
    /// row exists — see the class note on why absence is the default.
    /// </summary>
    public async Task<bool> IsSubscribedAsync(
        int eventId, int sessionId, MasterClassNotificationKind kind,
        int? participantId, int? attendeeId, CancellationToken ct = default)
    {
        if (participantId is null && attendeeId is null) return false;

        return !await _db.MasterClassSubscriptions.AnyAsync(
            s => s.EventId == eventId
                 && s.SessionId == sessionId
                 && s.Kind == kind
                 && ((participantId != null && s.ParticipantId == participantId)
                     || (attendeeId != null && s.AttendeeId == attendeeId)),
            ct);
    }

    /// <summary>
    /// Turn a subscription on or off. Idempotent in both directions: subscribing deletes any
    /// opt-out row (including duplicates a race could have created), unsubscribing writes one only
    /// when none exists.
    /// </summary>
    public async Task SetSubscribedAsync(
        int eventId, int sessionId, MasterClassNotificationKind kind,
        int? participantId, int? attendeeId, bool subscribed, CancellationToken ct = default)
    {
        if (participantId is null && attendeeId is null) return;

        var existing = await _db.MasterClassSubscriptions
            .Where(s => s.EventId == eventId
                        && s.SessionId == sessionId
                        && s.Kind == kind
                        && ((participantId != null && s.ParticipantId == participantId)
                            || (attendeeId != null && s.AttendeeId == attendeeId)))
            .ToListAsync(ct);

        if (subscribed)
        {
            if (existing.Count == 0) return;
            _db.MasterClassSubscriptions.RemoveRange(existing);
        }
        else
        {
            if (existing.Count > 0) return;
            _db.MasterClassSubscriptions.Add(new MasterClassSubscription
            {
                EventId = eventId,
                SessionId = sessionId,
                Kind = kind,
                ParticipantId = participantId,
                AttendeeId = attendeeId,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Everyone who should hear about <paramref name="kind"/> for this class, minus the opt-outs and
    /// minus the actor. Speakers are matched through <see cref="SessionSpeaker"/>; attendees through
    /// their signup (confirmed OR waitlisted).
    /// </summary>
    public async Task<IReadOnlyList<Recipient>> ResolveAudienceAsync(
        int eventId, int sessionId, MasterClassNotificationKind kind,
        int? actingParticipantId, int? actingAttendeeId, CancellationToken ct = default)
    {
        var speakers = await _db.SessionSpeakers
            .Where(ss => ss.SessionId == sessionId && ss.Participant.EventId == eventId)
            .Select(ss => new { ss.Participant.Id, ss.Participant.Email, ss.Participant.FullName })
            .ToListAsync(ct);

        var attendees = await _db.MasterClassSignups
            .Where(s => s.EventId == eventId
                        && s.SessionId == sessionId
                        && (s.Status == MasterClassSignupStatus.Confirmed
                            || s.Status == MasterClassSignupStatus.Waitlisted))
            .Select(s => new { s.Attendee.Id, s.Attendee.Email, s.Attendee.FullName })
            .ToListAsync(ct);

        // The opt-out set for this class + kind, pulled once rather than queried per person.
        var optedOut = await _db.MasterClassSubscriptions
            .Where(s => s.EventId == eventId && s.SessionId == sessionId && s.Kind == kind)
            .Select(s => new { s.ParticipantId, s.AttendeeId })
            .ToListAsync(ct);
        var outParticipants = optedOut.Where(x => x.ParticipantId != null)
            .Select(x => x.ParticipantId!.Value).ToHashSet();
        var outAttendees = optedOut.Where(x => x.AttendeeId != null)
            .Select(x => x.AttendeeId!.Value).ToHashSet();

        var list = new List<Recipient>();

        foreach (var s in speakers)
        {
            if (s.Id == actingParticipantId) continue;          // never mail the actor
            if (outParticipants.Contains(s.Id)) continue;
            if (string.IsNullOrWhiteSpace(s.Email)) continue;
            list.Add(new Recipient(s.Email, s.FullName ?? s.Email, s.Id, null));
        }

        foreach (var a in attendees)
        {
            if (a.Id == actingAttendeeId) continue;
            if (outAttendees.Contains(a.Id)) continue;
            if (string.IsNullOrWhiteSpace(a.Email)) continue;
            list.Add(new Recipient(a.Email, a.FullName ?? a.Email, null, a.Id));
        }

        // A speaker who is also an attendee would otherwise be mailed twice.
        return list
            .GroupBy(r => r.Email.Trim().ToLowerInvariant())
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// §705.14 — the MAIL IDENTITIES for the two notifications. Each is a mail in its own right, with
    /// its own subject, internal name and ring, so it appears on /Organizer/Settings like every other
    /// mail. Operator's rule: *"everything targetting one of the roles with an email has a subject +
    /// internal name and must be defined in settings, no exception."*
    /// </summary>
    /// <remarks>
    /// 🔒 These MUST match the <c>EmailTemplateCatalog.Map</c> keys, or the ring lookup finds nothing.
    /// Since §705.2 made a registered mail with no ring FAIL CLOSED, a mismatch here does not fall back
    /// to a feature ring — it silences the mail. `EmailRegistryCompletenessTests` pins the pairing.
    /// </remarks>
    public const string QandAMailKey = "masterclass-question-posted";
    public const string InstructionsMailKey = "masterclass-instructions-updated";

    /// <summary>Somebody posted to the Q&amp;A — tell the class (box 3).</summary>
    public Task<int> NotifyQandAAsync(
        int eventId, int sessionId, string authorName, string excerpt, string baseUrl,
        int? actingParticipantId, int? actingAttendeeId, CancellationToken ct = default) =>
        SendAsync(
            eventId, sessionId, MasterClassNotificationKind.QandA,
            actingParticipantId, actingAttendeeId, baseUrl,
            mailKey: QandAMailKey,
            subjectFor: title => $"New question in {title}",
            introFor: title =>
                $"<strong>{Enc(authorName)}</strong> posted in the Q&amp;A for <strong>{Enc(title)}</strong>:",
            bodyFor: _ => $"<blockquote style=\"margin:12px 0;padding:8px 14px;border-left:4px solid #dde4ee;color:#3a4250;\">{Enc(Trim(excerpt))}</blockquote>",
            ct);

    /// <summary>A speaker changed the instructions / what-to-bring — tell the class (box 2).</summary>
    public Task<int> NotifyInstructionsUpdatedAsync(
        int eventId, int sessionId, string editorName, string baseUrl,
        int? actingParticipantId, CancellationToken ct = default) =>
        SendAsync(
            eventId, sessionId, MasterClassNotificationKind.SpeakerInstructions,
            actingParticipantId, null, baseUrl,
            mailKey: InstructionsMailKey,
            subjectFor: title => $"Updated instructions for {title}",
            introFor: title =>
                $"<strong>{Enc(editorName)}</strong> updated the preparation instructions for <strong>{Enc(title)}</strong>.",
            bodyFor: _ => "<p>Open the Master Class page to see what changed.</p>",
            ct);

    private async Task<int> SendAsync(
        int eventId, int sessionId, MasterClassNotificationKind kind,
        int? actingParticipantId, int? actingAttendeeId, string baseUrl,
        string mailKey,
        Func<string, string> subjectFor,
        Func<string, string> introFor,
        Func<string, string> bodyFor,
        CancellationToken ct)
    {
        var title = await _db.Sessions
            .Where(s => s.Id == sessionId && s.EventId == eventId)
            .Select(s => s.Title)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(title)) return 0;

        var audience = await ResolveAudienceAsync(
            eventId, sessionId, kind, actingParticipantId, actingAttendeeId, ct);
        if (audience.Count == 0) return 0;

        var url = $"{baseUrl.TrimEnd('/')}/MasterClassPage/{sessionId}";
        var subject = subjectFor(title!);
        var sent = 0;

        foreach (var r in audience)
        {
            var html =
                $"<p>Hi {Enc(First(r.DisplayName))},</p>" +
                $"<p>{introFor(title!)}</p>" +
                bodyFor(title!) +
                $"<p><a href=\"{Enc(url)}\">Open the Master Class page</a></p>" +
                "<p style=\"color:#6b7280;font-size:13px;\">You can turn these notifications off on that page.</p>";

            // §705.14 — TemplateName carries the MAIL IDENTITY, which is what the ring lookup keys off.
            // Without it these two mails had no identity at all: invisible on the Settings page and
            // unable to hold a ring of their own (§705.3b resolves per (mail × role)).
            using (_context.Set(new EmailContext(
                Category, eventId, r.ParticipantId, r.DisplayName,
                TemplateName: mailKey, FeatureKey: FeatureKey)))
            {
                try
                {
                    await _sender.SendAsync(r.Email, subject, html, ct);
                    sent++;
                }
                catch
                {
                    // One bad address must not stop the rest of the class being told.
                }
            }
        }

        return sent;
    }

    private static string First(string name) =>
        string.IsNullOrWhiteSpace(name) ? "there" : name.Split(' ')[0];

    private static string Trim(string s) =>
        s.Length <= 300 ? s : s[..300] + "…";

    private static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
}
