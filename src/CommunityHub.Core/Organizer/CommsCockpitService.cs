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
/// §784.2 — ONE MAIL, as the organizer needs to read it: subject, when, how it landed.
/// </summary>
/// <remarks>
/// 🔒 <b>This is a view of an ATTEMPT, not of a delivery.</b> <see cref="Outcome"/> is what CEH
/// observed at send time — the same classification the rest of the cockpit uses — and a
/// <c>Sent</c> row means the transport accepted it, not that a human read it. Reconciling with
/// Brevo remains the only way to answer that, and this list must never be presented as proof of
/// arrival.
/// </remarks>
/// <param name="SentAt">When the attempt was made.</param>
/// <param name="Subject">The subject line, as logged.</param>
/// <param name="Category">The campaign/category the mail belongs to.</param>
/// <param name="Outcome">Sent / Dropped / Failed, from the real log row.</param>
public sealed record WhoGotWhatMessage(
    DateTimeOffset SentAt,
    string? Subject,
    string Category,
    CommsOutcome Outcome);

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
/// <param name="Messages">
/// §784.2 — THE ACTUAL MAILS behind the counts, newest first. Operator 2026-08-03: *"i must have a
/// button to show all emails that was sent to people. i only see count. I need to see
/// subject,date,time"*. A count answers "how many", which is never the question being asked at this
/// page — the question is "did HER invitation go out, and when".
/// </param>
public sealed record WhoGotWhatRow(
    string Email,
    string? Name,
    int? ParticipantId,
    int Sent,
    int Dropped,
    int Failed,
    DateTimeOffset LastAt,
    string? LastSubject,
    IReadOnlyList<WhoGotWhatMessage> Messages)
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
    string? Error,
    // §644 — what the row will ACTUALLY do, rather than what it looks like it will do.
    string? Template = null,
    string? CurrentEmail = null,
    // §656 — this "failure" was followed by a successful send of the same message. Hidden by
    // default; when shown, the row says so instead of reading as undelivered.
    bool WasSuperseded = false)
{
    /// <summary>
    /// §644 — the template this row should resend: the one that FAILED, never a guess.
    /// </summary>
    /// <remarks>
    /// 🔒 The page offered a HARD-CODED list of two templates (<c>onboarding-getting-started</c>,
    /// <c>invitation</c>) whatever had actually failed — so a failed <c>calendar-invite</c> could
    /// not be resent at all, and pressing "Resend" sent a real speaker an unrelated onboarding
    /// mail. <b>A resend that cannot send the thing that failed is not a resend.</b>
    /// <see cref="Domain.EmailLog.TemplateName"/> is blank on older rows, so the Category — which
    /// IS always set — is the fallback.
    /// </remarks>
    public string? ResendTemplate =>
        !string.IsNullOrWhiteSpace(Template) ? Template!.Trim()
        : !string.IsNullOrWhiteSpace(Category) ? Category.Split(':')[0].Trim()
        : null;

    /// <summary>
    /// §644 — true when the person's CURRENT address differs from the one that failed, so the page
    /// can say where the resend is really going.
    /// </summary>
    /// <remarks>
    /// Per Larsen's failed mail was addressed to a historical <c>per@famlarsen.se</c> while his
    /// record now reads <c>per.larsen@microsoft.com</c>. The resend used the current address, which
    /// is CORRECT — but the row still showed the old one, so the confirmation named someone the
    /// operator had not seen on screen. Right behaviour, alarming presentation.
    /// </remarks>
    public bool GoesToADifferentAddress =>
        !string.IsNullOrWhiteSpace(CurrentEmail)
        && !string.Equals(CurrentEmail, Email, StringComparison.OrdinalIgnoreCase);
}

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

    /// <summary>
    /// §818 — ENGINE &amp; OPS MAIL: what the machine sent to a MAILBOX, not to a person.
    /// </summary>
    /// <remarks>
    /// <para>Job-failure alerts, hand-entry lists, invoice notices, ERP refusals. Operator 2026-08-04
    /// (§815): <i>"why can I not see the emails sent here in a logged form"</i> — 375 such rows existed
    /// in PROD and none of them rendered anywhere in the organizer UI, because they carry no edition
    /// stamp and every view on this page is edition-scoped.</para>
    ///
    /// <para>🔒 <b>Deliberately its own list, never merged into <see cref="Timeline"/> or
    /// <see cref="WhoGotWhat"/>.</b> §815.1: a participant's welcome and an ops alert answer different
    /// questions, and 252 engine alerts poured into the participant timeline would bury the mail this
    /// page exists to track — the same burial §650 already had to undo once for ring-drops.</para>
    /// </remarks>
    public List<CommsTimelineItem> OpsMail { get; set; } = new();

    // --- ops counters, kept apart from the participant ones for the same reason ----
    public int OpsMailSent { get; set; }
    public int OpsMailFailed { get; set; }

    /// <summary>Ops mail in the window BEFORE the display cap — the list may be shorter.</summary>
    public int OpsMailTotal { get; set; }

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

    /// <summary>§818 — most ops/engine mails listed before the section says it was cut short.</summary>
    private const int OpsMailCap = 100;

    /// <summary>§784.2 — most mails listed under ONE person before the list is cut short.</summary>
    public const int MessagesPerPersonCap = 100;

    /// <summary>
    /// §784.1 — a failed send stops being a RESEND CANDIDATE after this long.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-03: <i>"if mail has been in the 'Resend undelivered mail' queue for
    /// more than 1 month, then remove it from the resend queue"</i>.</para>
    ///
    /// <para>🔑 <b>Stated separately from <see cref="TimelineDays"/> on purpose.</b> The two happen
    /// to be the same number today, which is exactly why the rule was invisible — the queue aged out
    /// only as a SIDE EFFECT of how far back the timeline reads. Widen the timeline for any unrelated
    /// reason and stale rows silently reappear in the resend queue. The queue's own rule now lives
    /// where the queue is built.</para>
    ///
    /// <para>⚠️ A month-old undelivered mail is not something to re-send: the moment has passed, and
    /// re-sending it now would deliver a message whose content refers to a deadline that is gone.</para>
    /// </remarks>
    private const int ResendMaxAgeDays = 30;
    private const int SnippetLen = 60;

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public CommsCockpitService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// §784.1 — DISMISS one participant's row from the resend queue ("I have dealt with this").
    /// Returns the number of log rows hidden, or 0 when there was nothing to dismiss.
    /// </summary>
    /// <remarks>
    /// <para>The queue shows ONE row per participant (their most recent undelivered mail), but the
    /// same participant may have several undelivered rows inside the window. Dismissing only the
    /// newest would make the row reappear showing an OLDER failure — which reads as a new problem.
    /// So dismissal covers every undelivered row for that participant within the age window.</para>
    ///
    /// <para>🔒 <b>It changes nothing about delivery.</b> No <see cref="EmailLog.Error"/>, no
    /// <see cref="EmailLog.Success"/>, no resend. The mail is exactly as undelivered as it was; the
    /// organizer has simply said they are not going to act on it. Reversible by clearing the column.
    /// See <see cref="EmailLog.ResendDismissedAt"/> for why this must not spread.</para>
    /// </remarks>
    public async Task<int> DismissFromResendQueueAsync(
        int eventId, int participantId, string? byEmail, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var cutoff = now.AddDays(-ResendMaxAgeDays);

        var rows = await _db.EmailLogs
            .Where(e => e.EventId == eventId
                        && e.ParticipantId == participantId
                        && e.SentAt >= cutoff
                        && e.ResendDismissedAt == null)
            .ToListAsync(ct);

        // Only rows that are actually IN the queue (undelivered) are dismissed — a delivered row
        // was never shown, and stamping it would put dismissal data on healthy mail.
        var undelivered = rows.Where(e => OutcomeOf(e) != CommsOutcome.Sent).ToList();
        if (undelivered.Count == 0) return 0;

        foreach (var row in undelivered)
        {
            row.ResendDismissedAt = now;
            row.ResendDismissedByEmail = byEmail;
        }

        await _db.SaveChangesAsync(ct);
        return undelivered.Count;
    }

    /// <param name="includeDropped">
    /// §650 — include RING-DROPPED mail in the resend list. Defaults to <c>false</c>: a ring drop is
    /// the system obeying the operator, not a fault, and 1,242 of them buried the 2 real failures.
    /// </param>
    public async Task<CommsCockpitSnapshot> BuildAsync(
        int eventId, CancellationToken ct = default, bool includeDropped = false)
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

        // 🔑 §818 — THE MAIL WITH NO EDITION STAMP. `LoggingEmailSender` writes
        // `EventId = ctx?.EventId ?? 0`, so any send whose ambient EmailContext carries no edition
        // lands on event 0 — and every query above is edition-scoped, so it renders NOWHERE. In PROD
        // that was 375 rows (252 engine alerts, 111 ops notices, 11 speaker evaluation mails, 1
        // AiHelper intake) sent over six weeks, none of it visible to the organizer.
        //
        // 🔒 It is split by AUDIENCE, not by EventId — the whole point of §815.1 — and the audience
        // is read from the mail's own TEMPLATE IDENTITY (`EmailTemplateCatalog.IsInternalMailboxMail`),
        // never from who received it.
        //
        // ⚠️ THE RECIPIENT ADDRESS LOOKS LIKE THE OBVIOUS TEST AND IS WRONG. It was written that way
        // first — "unstamped mail to a known participant address is that person's mail" — every unit
        // test passed, and the first real render on DEV showed the section EMPTY beside 25 live
        // engine alerts: they go to `mok@`, which is also the organizer's own participant address, so
        // all 25 were filed as his personal mail. That is the exact burial §815.1 forbids, and no
        // test caught it because the fixtures used addresses nobody had registered.
        var unscoped = eventId > 0
            ? await _db.EmailLogs
                .Where(e => e.EventId == 0 && e.SentAt >= since)
                .OrderByDescending(e => e.SentAt)
                .ToListAsync(ct)
            : new List<EmailLog>();

        var opsMail = new List<EmailLog>();
        if (unscoped.Count > 0)
        {
            var participantMail = new List<EmailLog>();
            foreach (var row in unscoped)
            {
                var isOps = Email.EmailTemplateCatalog.IsInternalMailboxMail(row.TemplateName);
                (isOps ? opsMail : participantMail).Add(row);
            }

            if (participantMail.Count > 0)
            {
                // Fold in, newest first, exactly as if the stamp had been there — who-got-what
                // already groups by ADDRESS, so the mail lands on the right person's row.
                // ⚠️ These rows carry NO ParticipantId, so `BuildResendCandidates` — which requires
                // one — can never offer them for resend. Deliberate: the row says whose mail it is,
                // not who to send it to.
                emails = emails.Concat(participantMail)
                    .OrderByDescending(e => e.SentAt)
                    .ToList();
            }
        }

        BuildTimeline(snap, emails, posts, now);
        BuildWhoGotWhat(snap, emails);
        BuildCampaigns(snap, emails);
        BuildResendCandidates(snap, emails, includeDropped, now);

        // §644 — stamp each candidate with the person's CURRENT address, so the row can warn when
        // the resend will not go to the address printed above it (a historical address on an old
        // log row is common and the silent divergence is what alarmed the operator).
        if (snap.ResendCandidates.Count > 0)
        {
            var ids = snap.ResendCandidates.Select(c => c.ParticipantId).ToList();
            var current = await _db.Participants
                .Where(p => ids.Contains(p.Id))
                .Select(p => new { p.Id, p.Email })
                .ToDictionaryAsync(x => x.Id, x => x.Email, ct);

            snap.ResendCandidates = snap.ResendCandidates
                .Select(c => current.TryGetValue(c.ParticipantId, out var mail)
                    ? c with { CurrentEmail = mail }
                    : c)
                .ToList();
        }
        BuildCounters(snap, emails, posts);
        BuildOpsMail(snap, opsMail);

        return snap;
    }

    // --- ops / engine mail ----------------------------------------------------

    private static void BuildOpsMail(CommsCockpitSnapshot s, IReadOnlyList<EmailLog> opsMail)
    {
        s.OpsMailTotal = opsMail.Count;
        s.OpsMailSent = opsMail.Count(e => OutcomeOf(e) == CommsOutcome.Sent);
        // 🔒 Dropped and Failed are counted TOGETHER here, unlike the participant counters. A ring
        // drop is a meaningful, benign outcome for participant mail (§650) — but ops mail is
        // ring-EXEMPT by construction (EngineAlertSender sets RingExempt), so a drop on this side is
        // never the system obeying a ring. It is an alert that did not arrive, which is the one
        // failure mode this whole section exists to make visible.
        s.OpsMailFailed = opsMail.Count(e => OutcomeOf(e) != CommsOutcome.Sent);

        s.OpsMail = opsMail
            .OrderByDescending(e => e.SentAt)
            .Take(OpsMailCap)
            .Select(e => new CommsTimelineItem(
                When: e.SentAt,
                Channel: CommsChannel.Email,
                Outcome: OutcomeOf(e),
                Title: string.IsNullOrWhiteSpace(e.Subject) ? "(no subject)" : e.Subject,
                Recipient: e.ToEmail,
                Category: string.IsNullOrWhiteSpace(e.Category) ? "other" : e.Category,
                IsFuture: false,
                ParticipantId: null,
                Error: e.Success ? null : e.Error))
            .ToList();
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
                    LastSubject: latest.Subject,
                    // §784.2 — the mails themselves, newest first. Capped per person so one
                    // address with a thousand log rows cannot make the page unusable for the
                    // other 199; the cap is stated in the UI rather than silently truncating.
                    Messages: g.OrderByDescending(e => e.SentAt)
                        .Take(MessagesPerPersonCap)
                        .Select(e => new WhoGotWhatMessage(
                            e.SentAt,
                            e.Subject,
                            string.IsNullOrWhiteSpace(e.Category) ? "other" : e.Category,
                            OutcomeOf(e)))
                        .ToList());
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

    private static void BuildResendCandidates(
        CommsCockpitSnapshot s, IReadOnlyList<EmailLog> emails, bool includeDropped,
        // §784.1 — passed in rather than read from a clock field: this method is static and
        // deterministic, which is what makes the age rule testable without a real clock.
        DateTimeOffset now)
    {
        // Only participant-linked undelivered mail can be resent (a resend needs a
        // person to target via ParticipantEmailService). One candidate per
        // participant — the most recent undelivered item.
        // 🔒 §650 — RING-DROPPED MAIL IS NOT A PROBLEM TO TROUBLESHOOT. Operator 2026-07-29: *"it is
        // not relevant when you troubleshoot to see dropped mails due to ring-gates. i should be
        // able to enable it but by default it should not be shown"*. A ring drop is the system
        // obeying him, and 1,242 of them buried the 2 real failures completely.
        // §656 — a failure that was ALREADY SUPERSEDED by a successful send is not undelivered.
        // SendMaxAttempts writes one log row per attempt, so a message that failed and then
        // succeeded a second later leaves both rows — and the Failed one was being shown as though
        // the person never received it. Operator, on finding this: *"hide the superseded rows like
        // ring-drops"*. Same SHARED rule the retry job uses, so the two cannot disagree.
        var delivered = emails
            .Where(e => e.Error == null && e.ParticipantId != null)
            .Select(e => new Email.SupersededSendDetector.Delivery(e.ParticipantId, e.Category, e.SentAt))
            .ToList();

        // §784.1 — the queue's OWN age rule, not a by-product of the timeline window.
        var resendCutoff = now.AddDays(-ResendMaxAgeDays);

        s.ResendCandidates = emails
            .Where(e => e.SentAt >= resendCutoff)
            // §784.1 — a row the organizer has explicitly dismissed stays out of the queue.
            // 🔒 This is the ONLY place ResendDismissedAt may be read: it means "I have dealt with
            // this", not "this was delivered". Nothing about the mail's outcome changes.
            .Where(e => e.ResendDismissedAt == null)
            .Where(e => e.ParticipantId != null && OutcomeOf(e) != CommsOutcome.Sent)
            .Where(e => includeDropped || OutcomeOf(e) != CommsOutcome.Dropped)
            .Where(e => includeDropped
                        || !Email.SupersededSendDetector.IsSuperseded(
                               e.ParticipantId, e.Category, e.SentAt, delivered))
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
                Error: e.Error,
                // §644 — carry the template that FAILED so the row can resend that, not a guess.
                Template: e.TemplateName,
                // §656 — only ever true when the toggle is showing them; the label then explains
                // WHY the row is here rather than leaving it looking like a real failure.
                WasSuperseded: Email.SupersededSendDetector.IsSuperseded(
                    e.ParticipantId, e.Category, e.SentAt, delivered)))
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
