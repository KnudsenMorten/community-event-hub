using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>The kind of outreach a timeline item is — drives the icon / label / link.</summary>
public enum CommsChannel
{
    /// <summary>An outbound email recorded in <see cref="EmailLog"/>.</summary>
    Email = 0,

    /// <summary>A scheduled / published LinkedIn company-page post (<see cref="SoMePost"/>).</summary>
    SoMe = 1,
}

/// <summary>
/// The honest, real outcome of one piece of outreach — never optimistic. For
/// email it is read straight from the <see cref="EmailLog"/> row (after the DEV
/// redirect / PROD allowlist gate); for SoMe it is the post's lifecycle status.
/// </summary>
public enum CommsOutcome
{
    /// <summary>Future-dated and not yet attempted (a Queued, Active SoMe post).</summary>
    Scheduled = 0,

    /// <summary>Delivered: the email send completed, or the post published.</summary>
    Sent = 1,

    /// <summary>The send attempt failed / the post failed to publish (error recorded).</summary>
    Failed = 2,

    /// <summary>
    /// The email was deliberately NOT delivered by the allowlist / a disabled
    /// post — i.e. dropped-by-policy, distinct from a hard failure. The hub
    /// reports this honestly rather than counting it as sent.
    /// </summary>
    Dropped = 3,
}

/// <summary>
/// One row on the unified comms timeline (REQUIREMENTS §20 "Comms cockpit"). A
/// single shape over both email (<see cref="EmailLog"/>) and SoMe
/// (<see cref="SoMePost"/>) so the cockpit shows one chronological queue/feed of
/// "what went out / what is going out" with the real outcome of each.
/// </summary>
/// <param name="When">The timeline time — the send time for email, the scheduled
/// (or published) time for SoMe.</param>
/// <param name="Channel">Email or SoMe.</param>
/// <param name="Outcome">The real, non-optimistic outcome.</param>
/// <param name="Title">A human title (the email subject, or the SoMe post type + a snippet).</param>
/// <param name="Recipient">Who it was for, when known (recipient name/email; null for a broadcast SoMe post).</param>
/// <param name="Category">The email category / SoMe post type, for grouping + filtering.</param>
/// <param name="IsFuture">True when this is still ahead of "now" (a scheduled post).</param>
/// <param name="ParticipantId">The participant this item is about, when known (enables resend).</param>
/// <param name="Error">The failure / drop reason when the outcome is Failed/Dropped.</param>
public sealed record CommsTimelineItem(
    DateTimeOffset When,
    CommsChannel Channel,
    CommsOutcome Outcome,
    string Title,
    string? Recipient,
    string Category,
    bool IsFuture,
    int? ParticipantId,
    string? Error);

/// <summary>
/// One person's "who-got-what" line: every email outcome the hub has for one
/// address, sourced from the real <see cref="EmailLog"/> so the counts reflect
/// actual delivery (sent / dropped-by-allowlist / failed), never an optimistic
/// "we tried to send" tally.
/// </summary>
/// <param name="Email">The address the mail was intended for (<see cref="EmailLog.ToEmail"/>).</param>
/// <param name="Name">The recipient's display name when known.</param>
/// <param name="ParticipantId">The linked participant, when known (enables a one-click resend).</param>
/// <param name="Sent">Count of delivered emails.</param>
/// <param name="Dropped">Count dropped by the allowlist (delivered to nobody, on purpose).</param>
/// <param name="Failed">Count that failed to send.</param>
/// <param name="LastAt">The most recent send time for this address.</param>
/// <param name="LastSubject">The most recent subject for this address.</param>
public sealed record WhoGotWhatRow(
    string Email,
    string? Name,
    int? ParticipantId,
    int Sent,
    int Dropped,
    int Failed,
    DateTimeOffset LastAt,
    string? LastSubject)
{
    /// <summary>Total emails the hub has on record for this address.</summary>
    public int Total => Sent + Dropped + Failed;

    /// <summary>True when at least one email to this address did NOT reach them
    /// (dropped or failed) — the cockpit surfaces these as resend candidates.</summary>
    public bool HasUndelivered => Dropped > 0 || Failed > 0;
}

/// <summary>
/// One campaign/category line: how a whole category of outreach actually landed,
/// from the real <see cref="EmailLog"/>. "Welcome", "broadcast", "task-deadline",
/// etc. — sent / dropped / failed per category so the organizer sees, per
/// campaign, what truly reached people.
/// </summary>
public sealed record CampaignRow(
    string Category,
    int Sent,
    int Dropped,
    int Failed,
    DateTimeOffset LastAt)
{
    public int Total => Sent + Dropped + Failed;
    public bool HasUndelivered => Dropped > 0 || Failed > 0;
}

/// <summary>
/// One resend candidate: an email that did NOT reach its participant (failed or
/// dropped) and is linked to a participant so the organizer can re-send it in one
/// click via the existing per-person send path (<c>ParticipantEmailService</c>).
/// Only participant-linked undelivered mail is offered — a resend needs a person
/// to target.
/// </summary>
public sealed record ResendCandidate(
    int ParticipantId,
    string Name,
    string Email,
    string Subject,
    string Category,
    CommsOutcome Outcome,
    DateTimeOffset At,
    string? Error);

/// <summary>
/// The whole Comms-cockpit snapshot for one edition (REQUIREMENTS §20 Organizer
/// "Comms cockpit"). One place that schedules / sends / tracks all email + SoMe:
/// a unified <see cref="Timeline"/> (the queue/feed of everything out + going
/// out), the per-recipient <see cref="WhoGotWhat"/> and per-campaign
/// <see cref="Campaigns"/> delivery views sourced from the real
/// <see cref="EmailLog"/> + SoMe queue, and the <see cref="ResendCandidates"/>
/// (undelivered participant mail to re-send) + <see cref="UpcomingScheduled"/>
/// (the next things due — reminders/posts) call-outs.
///
/// Every number is a read-only aggregate over entities that already exist; nothing
/// is persisted, so building it twice yields the same snapshot. Resend itself is
/// the only write, and it is handled by the existing per-person send path — this
/// service just identifies the candidates.
/// </summary>
public sealed class CommsCockpitSnapshot
{
    public string EventDisplayName { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }

    /// <summary>The unified email+SoMe timeline, newest first (future scheduled at the top).</summary>
    public List<CommsTimelineItem> Timeline { get; set; } = new();

    /// <summary>Per-recipient delivery view from the real EmailLog (worst-outcome first).</summary>
    public List<WhoGotWhatRow> WhoGotWhat { get; set; } = new();

    /// <summary>Per-campaign/category delivery view from the real EmailLog.</summary>
    public List<CampaignRow> Campaigns { get; set; } = new();

    /// <summary>Participant-linked undelivered mail offered for one-click resend.</summary>
    public List<ResendCandidate> ResendCandidates { get; set; } = new();

    /// <summary>The next scheduled SoMe posts that have not fired yet (the queue ahead).</summary>
    public List<CommsTimelineItem> UpcomingScheduled { get; set; } = new();

    // --- headline counters (all from the real outcome, never optimistic) -----
    public int TotalEmails { get; set; }
    public int EmailsSent { get; set; }
    public int EmailsDropped { get; set; }
    public int EmailsFailed { get; set; }
    public int SoMeScheduled { get; set; }
    public int SoMePublished { get; set; }
    public int SoMeFailed { get; set; }

    /// <summary>True when nothing failed or was dropped — a calm, honest "all delivered".</summary>
    public bool AllDelivered => EmailsDropped == 0 && EmailsFailed == 0 && SoMeFailed == 0;
}

/// <summary>
/// Builds the organizer <see cref="CommsCockpitSnapshot"/> (REQUIREMENTS §20
/// Organizer — "Comms cockpit"). Read-mostly aggregation, edition-scoped, never
/// writes. It consolidates fragmented outreach into one place by reusing existing
/// data only:
///   <list type="bullet">
///   <item><see cref="EmailLog"/> — the audit of every outbound email
///   (welcome / onboarding / reminders / broadcast / manual-resend), incl. the
///   honest dropped-by-allowlist / failed outcomes;</item>
///   <item><see cref="SoMePost"/> — the LinkedIn company-page scheduled-post queue
///   (REQUIREMENTS §19);</item>
///   <item><see cref="SentReminder"/> — the reminder/idempotency ledger, used only
///   to count the next things due.</item>
///   </list>
/// It does NOT send: the resend is performed by the page via the existing
/// per-person <c>ParticipantEmailService</c> path; this service only identifies
/// the candidates. Sibling of <see cref="CommandCenterService"/> — it extends the
/// command center's "what needs my attention" into the comms domain, never
/// duplicating the Email Center / Email Log / Broadcast pages it links to.
/// </summary>
public sealed class CommsCockpitService
{
    /// <summary>How far back the timeline + delivery views look (keeps the page light).</summary>
    private const int TimelineDays = 30;
    private const int TimelineCap = 100;
    private const int WhoGotWhatCap = 200;
    private const int ResendCap = 50;
    private const int SnippetLen = 60;

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public CommsCockpitService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<CommsCockpitSnapshot> BuildAsync(int eventId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var since = now.AddDays(-TimelineDays);

        var snap = new CommsCockpitSnapshot
        {
            GeneratedAtUtc = now,
            EventDisplayName = await _db.Events
                .Where(e => e.Id == eventId)
                .Select(e => e.DisplayName)
                .FirstOrDefaultAsync(ct) ?? string.Empty,
        };

        var emails = await _db.EmailLogs
            .Where(e => e.EventId == eventId && e.SentAt >= since)
            .OrderByDescending(e => e.SentAt)
            .ToListAsync(ct);

        var posts = await _db.SoMePosts
            .Where(p => p.EventId == eventId
                        && (p.ScheduledAtUtc >= since
                            || (p.PublishedAtUtc != null && p.PublishedAtUtc >= since)))
            .ToListAsync(ct);

        BuildTimeline(snap, emails, posts, now);
        BuildWhoGotWhat(snap, emails);
        BuildCampaigns(snap, emails);
        BuildResendCandidates(snap, emails);
        BuildCounters(snap, emails, posts);

        return snap;
    }

    // --- timeline ------------------------------------------------------------

    private static void BuildTimeline(
        CommsCockpitSnapshot s,
        IReadOnlyList<EmailLog> emails,
        IReadOnlyList<SoMePost> posts,
        DateTimeOffset now)
    {
        var items = new List<CommsTimelineItem>();

        foreach (var e in emails)
        {
            items.Add(new CommsTimelineItem(
                When: e.SentAt,
                Channel: CommsChannel.Email,
                Outcome: OutcomeOf(e),
                Title: string.IsNullOrWhiteSpace(e.Subject) ? "(no subject)" : e.Subject,
                Recipient: string.IsNullOrWhiteSpace(e.RecipientName) ? e.ToEmail : e.RecipientName,
                Category: string.IsNullOrWhiteSpace(e.Category) ? "other" : e.Category,
                IsFuture: false,
                ParticipantId: e.ParticipantId,
                Error: e.Success ? null : e.Error));
        }

        foreach (var p in posts)
        {
            var when = p.PublishedAtUtc ?? p.ScheduledAtUtc;
            var outcome = OutcomeOf(p, now);
            items.Add(new CommsTimelineItem(
                When: when,
                Channel: CommsChannel.SoMe,
                Outcome: outcome,
                Title: SoMeTitle(p),
                Recipient: null,
                Category: $"some:{p.Type.ToString().ToLowerInvariant()}",
                IsFuture: outcome == CommsOutcome.Scheduled && when > now,
                ParticipantId: p.ParticipantId,
                Error: p.Status == SoMePostStatus.Failed ? p.LastError : null));
        }

        // Newest first, but future-scheduled (the queue ahead) floats to the very
        // top so the organizer sees "what is about to go out" before the history.
        s.Timeline = items
            .OrderByDescending(i => i.IsFuture)
            .ThenByDescending(i => i.When)
            .Take(TimelineCap)
            .ToList();

        s.UpcomingScheduled = items
            .Where(i => i.IsFuture)
            .OrderBy(i => i.When)
            .ToList();
    }

    private static string SoMeTitle(SoMePost p)
    {
        var kind = p.Type switch
        {
            SoMePostType.Sponsor => "Sponsor post",
            SoMePostType.Speaker => "Speaker post",
            _ => "Ad-hoc post",
        };
        var text = p.EffectiveText;
        if (string.IsNullOrWhiteSpace(text)) return kind;
        var snippet = text.Trim().Replace('\n', ' ');
        if (snippet.Length > SnippetLen) snippet = snippet[..SnippetLen] + "…";
        return $"{kind}: {snippet}";
    }

    /// <summary>
    /// Map an email-log row to its REAL outcome. A successful row is Sent. A failed
    /// row whose error reads like an allowlist drop is Dropped (delivered to
    /// nobody, on purpose); any other failure is Failed. Never optimistic — a row
    /// the gate dropped is never reported as Sent.
    /// </summary>
    private static CommsOutcome OutcomeOf(EmailLog e)
    {
        if (e.Success) return CommsOutcome.Sent;
        var reason = e.Error ?? string.Empty;
        var dropped = reason.Contains("allowlist", StringComparison.OrdinalIgnoreCase)
                      || reason.Contains("dropped", StringComparison.OrdinalIgnoreCase)
                      || reason.Contains("not allowed", StringComparison.OrdinalIgnoreCase)
                      || reason.Contains("suppress", StringComparison.OrdinalIgnoreCase);
        return dropped ? CommsOutcome.Dropped : CommsOutcome.Failed;
    }

    private static CommsOutcome OutcomeOf(SoMePost p, DateTimeOffset now) => p.Status switch
    {
        SoMePostStatus.Published => CommsOutcome.Sent,
        SoMePostStatus.Failed => CommsOutcome.Failed,
        // Queued: an Inactive post will never fire -> Dropped (won't be delivered);
        // an Active queued post ahead of now is Scheduled; an Active queued post
        // already past its time is still Scheduled (the dispatcher will pick it up).
        _ => p.IsActive ? CommsOutcome.Scheduled : CommsOutcome.Dropped,
    };

    // --- who-got-what (per recipient) ----------------------------------------

    private static void BuildWhoGotWhat(CommsCockpitSnapshot s, IReadOnlyList<EmailLog> emails)
    {
        s.WhoGotWhat = emails
            .Where(e => !string.IsNullOrWhiteSpace(e.ToEmail))
            .GroupBy(e => e.ToEmail.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.OrderByDescending(e => e.SentAt).First();
                return new WhoGotWhatRow(
                    Email: g.Key,
                    Name: latest.RecipientName,
                    ParticipantId: g.Select(e => e.ParticipantId).FirstOrDefault(id => id != null),
                    Sent: g.Count(e => OutcomeOf(e) == CommsOutcome.Sent),
                    Dropped: g.Count(e => OutcomeOf(e) == CommsOutcome.Dropped),
                    Failed: g.Count(e => OutcomeOf(e) == CommsOutcome.Failed),
                    LastAt: latest.SentAt,
                    LastSubject: latest.Subject);
            })
            // Anyone with undelivered mail first (most undelivered first), then by recency.
            .OrderByDescending(r => r.HasUndelivered)
            .ThenByDescending(r => r.Dropped + r.Failed)
            .ThenByDescending(r => r.LastAt)
            .Take(WhoGotWhatCap)
            .ToList();
    }

    // --- per-campaign (per category) -----------------------------------------

    private static void BuildCampaigns(CommsCockpitSnapshot s, IReadOnlyList<EmailLog> emails)
    {
        s.Campaigns = emails
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Category) ? "other" : e.Category)
            .Select(g => new CampaignRow(
                Category: g.Key,
                Sent: g.Count(e => OutcomeOf(e) == CommsOutcome.Sent),
                Dropped: g.Count(e => OutcomeOf(e) == CommsOutcome.Dropped),
                Failed: g.Count(e => OutcomeOf(e) == CommsOutcome.Failed),
                LastAt: g.Max(e => e.SentAt)))
            .OrderByDescending(c => c.HasUndelivered)
            .ThenByDescending(c => c.LastAt)
            .ToList();
    }

    // --- resend candidates ---------------------------------------------------

    private static void BuildResendCandidates(CommsCockpitSnapshot s, IReadOnlyList<EmailLog> emails)
    {
        // Only participant-linked undelivered mail can be resent (a resend needs a
        // person to target via ParticipantEmailService). One candidate per
        // participant — the most recent undelivered item.
        s.ResendCandidates = emails
            .Where(e => e.ParticipantId != null && OutcomeOf(e) != CommsOutcome.Sent)
            .GroupBy(e => e.ParticipantId!.Value)
            .Select(g => g.OrderByDescending(e => e.SentAt).First())
            .OrderByDescending(e => e.SentAt)
            .Take(ResendCap)
            .Select(e => new ResendCandidate(
                ParticipantId: e.ParticipantId!.Value,
                Name: string.IsNullOrWhiteSpace(e.RecipientName) ? e.ToEmail : e.RecipientName!,
                Email: e.ToEmail,
                Subject: string.IsNullOrWhiteSpace(e.Subject) ? "(no subject)" : e.Subject,
                Category: string.IsNullOrWhiteSpace(e.Category) ? "other" : e.Category,
                Outcome: OutcomeOf(e),
                At: e.SentAt,
                Error: e.Error))
            .ToList();
    }

    // --- headline counters ---------------------------------------------------

    private static void BuildCounters(
        CommsCockpitSnapshot s,
        IReadOnlyList<EmailLog> emails,
        IReadOnlyList<SoMePost> posts)
    {
        s.TotalEmails = emails.Count;
        s.EmailsSent = emails.Count(e => OutcomeOf(e) == CommsOutcome.Sent);
        s.EmailsDropped = emails.Count(e => OutcomeOf(e) == CommsOutcome.Dropped);
        s.EmailsFailed = emails.Count(e => OutcomeOf(e) == CommsOutcome.Failed);

        s.SoMeScheduled = posts.Count(p => p.Status == SoMePostStatus.Queued && p.IsActive);
        s.SoMePublished = posts.Count(p => p.Status == SoMePostStatus.Published);
        s.SoMeFailed = posts.Count(p => p.Status == SoMePostStatus.Failed);
    }
}
