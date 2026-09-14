using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>What one planning pass did.</summary>
/// <param name="Created">Posts written to the queue, all HELD for approval.</param>
/// <param name="AlreadyPlanned">Subjects that already had their posts — the steady state.</param>
/// <param name="NoRoom">
/// Subjects that could not be placed before the event. Reported, never silently dropped.
/// </param>
/// <param name="GuardRemoved">
/// §1178 — planned posts the queue guard removed on this run because their subject is test data or
/// excluded from announcements. Trailing with a default so no existing caller changes.
/// </param>
public sealed record SoMeScheduleRunResult(
    int Created, int AlreadyPlanned, IReadOnlyList<string> NoRoom, string Message,
    int GuardRemoved = 0);

/// <summary>§1144/§1205 — what one overdue-push pass did.</summary>
/// <param name="Moved">Posts re-dated onto the next free slot.</param>
/// <param name="HeldBack">
/// How many of <paramref name="Moved"/> were still waiting on a dependency — the ones §1205 exists
/// for. A subset of the count, not an addition to it.
/// </param>
/// <param name="Locked">
/// Overdue posts left exactly where they are because he ACCEPTED that date (§848.2). Named, not
/// counted (§854): these are the only ones he has to move himself.
/// </param>
/// <param name="Full">
/// Overdue posts with nowhere left to go — every remaining weekday is at its posts-per-day ceiling.
/// The capacity wall (§1199), named where he reads the run.
/// </param>
public sealed record OverduePush(
    int Moved, int HeldBack, IReadOnlyList<string> Locked, IReadOnlyList<string> Full)
{
    public static readonly OverduePush None =
        new(0, 0, Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// §824.21 — turns CEH's data into a queue of held, scheduled posts.
/// </summary>
/// <remarks>
/// <para>Collects the announceable subjects (tracks, sessions by type, sponsor tiers, sponsors),
/// asks <see cref="SoMeSchedulePlanner"/> when each should go out, composes the body from the
/// edition's template, and writes the rows.</para>
///
/// <para>🔒 <b>EVERY post is created INACTIVE — held for his approval (§824.8 Q2:</b> <i>"generate +
/// schedule automatically, publish only after your approval"</i>). An inactive queued post is never
/// published by the dispatcher, so the automation can run freely without anything reaching the
/// company page until a human turns a row on. That is the entire safety model, and it is one field.</para>
///
/// <para>⚠️ <b>Nothing is ever re-planned or rewritten.</b> A subject whose posts exist is skipped
/// whole — so an approval, a manual edit or a reschedule he made survives the next tick untouched.
/// The scheduler adds; it does not curate.</para>
/// </remarks>
public sealed class SoMeScheduleService
{
    private readonly CommunityHubDbContext _db;
    private readonly SoMeTemplateService _templates;
    private readonly SoMeVariableResolver _variables;
    private readonly TimeProvider _clock;
    private readonly ILogger<SoMeScheduleService>? _log;

    /// <summary>
    /// §824.2D — writes <c>{IntroText}</c>. Optional: without it every post is composed without an
    /// intro, which the template closes over. The campaign is planned either way.
    /// </summary>
    private readonly SoMeIntroGenerator? _intro;

    /// <summary>
    /// §911 — how many teasers ONE run may generate. The rest arrive on later ticks.
    /// </summary>
    /// <remarks>
    /// 🔒 The number is chosen against the failure it prevents: the intro client is bounded to 15s
    /// (§906), so 12 is a worst case of ~3 minutes inside a run that also has to plan ~100 posts.
    /// Unbounded is what died on 2026-08-06. With the timer at 5 minutes a backlog of 78 fills in
    /// well under an hour, and every run in between is a normal, fast run.
    /// </remarks>
    private const int MaxIntroGenerationsPerRun = 12;

    /// <summary>
    /// §925 — how long a track must receive NO new session before it counts as settled.
    /// </summary>
    /// <remarks>
    /// 🔑 Chosen against the shape of the arrival, not picked round: the Call for Speakers closes and
    /// its sessions sync in a batch, so a week of quiet after the last arrival means the batch has
    /// landed. Shorter would announce a track mid-import; much longer would delay every track for a
    /// single late addition.
    /// <para>⚠️ It is a FLOOR, never a ceiling — a track that settles early still waits for
    /// <see cref="Domain.SoMeSettings.SpeakerAnnouncementFrom"/> if he has set one.</para>
    /// </remarks>
    private static readonly TimeSpan TrackSettlePeriod = TimeSpan.FromDays(7);

    /// <summary>§911 — teasers already written, keyed by (subject, occurrence). Per-run.</summary>
    private Dictionary<(string SubjectKey, int Occurrence), string> _introReuse = new();

    /// <summary>§911 — what is left of this run's generation budget.</summary>
    private int _introBudget;

    public SoMeScheduleService(
        CommunityHubDbContext db,
        SoMeTemplateService templates,
        SoMeVariableResolver variables,
        TimeProvider clock,
        ILogger<SoMeScheduleService>? log = null,
        SoMeIntroGenerator? intro = null)
    {
        _db = db;
        _templates = templates;
        _variables = variables;
        _clock = clock;
        _log = log;
        _intro = intro;
    }

    /// <summary>
    /// 🔴 §906.2 — RUN THROUGH THE EXECUTION STRATEGY, because §906.1's transaction cannot exist
    /// without it.
    /// </summary>
    /// <remarks>
    /// <para>Both hosts configure <c>EnableRetryOnFailure</c> (Azure SQL Serverless cold-starts,
    /// error 40613). EF then <b>refuses a user-initiated transaction outright</b>: "the configured
    /// execution strategy 'SqlServerRetryingExecutionStrategy' does not support user-initiated
    /// transactions". So §906.1's `BeginTransactionAsync` threw on the FIRST tick after deploy and
    /// the planner stopped running entirely — 22 minutes with no run before the gap in
    /// <c>MAX(CreatedAt)</c> gave it away.</para>
    ///
    /// <para>🔒 Nothing was lost: it throws BEFORE the discard, so the queue simply froze intact.
    /// That is the right side of the failure to be on, and it is the reason §906.1 put the
    /// transaction first.</para>
    ///
    /// <para>⚠️ <b>The tests could not have caught it</b>, and that is the lesson worth keeping.
    /// They run on the in-memory provider, where the <c>IsRelational()</c> guard makes the
    /// transaction a no-op — so the one line that only executes against SQL Server was the one line
    /// with no coverage. A guard that says "skip this in tests" is a guard that says "this is
    /// untested".</para>
    /// </remarks>
    public Task<SoMeScheduleRunResult> RunAsync(int eventId, CancellationToken ct = default) =>
        _db.Database.CreateExecutionStrategy()
            .ExecuteAsync(() => RunCoreAsync(eventId, ct));

    private async Task<SoMeScheduleRunResult> RunCoreAsync(int eventId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        var ev = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.StartDate })
            .FirstOrDefaultAsync(ct);
        if (ev is null)
        {
            return new SoMeScheduleRunResult(0, 0, Array.Empty<string>(), "No such edition.");
        }

        var eventStartUtc = SoMeSchedulePlanner.ToUtc(ev.StartDate, new TimeOnly(0, 0));
        if (eventStartUtc <= now)
        {
            // Announcing an event that has started is worse than not announcing it.
            return new SoMeScheduleRunResult(
                0, 0, Array.Empty<string>(), "The edition has already started — nothing to announce.");
        }

        // 🔴 §824.23 — REFUSE TO COMPOSE UNTIL THE POST FOOTER EXISTS.
        //
        // The scheduler ADDS and never re-composes (§824.21a) — that is what protects his approvals
        // and edits. The cost of that rule is this: whatever a post says when it is created is what
        // it says forever. So running before the edition has its {EventSystemUrl} / {EventTags} /
        // {OrganizerLinkedInUrls} would permanently bake a footerless post into the queue, and the
        // gap would close so cleanly (§824.15) that it would read as finished.
        //
        // ⚠️ Silence would be the worst outcome, so the reason is the returned message and the job
        // logs it. Planning nothing today is recoverable in five minutes on the settings page;
        // forty posts missing their tag block are not.
        var footer = await _db.SoMeSettings
            .Where(s => s.EventId == eventId)
            // §842.7 — MaxPostsPerDay rides along on the row this already reads, rather than a
            // second query for one integer.
            .Select(s => new
            {
                s.EventSystemUrl, s.EventTags, s.OrganizerCredits,
                s.MaxPostsPerDay, s.ExceptionPostsPerDay,
                // §1181 — the Type 5 window's closing day rides the row this already reads.
                s.EventPostWindowEndsOn,
            })
            .FirstOrDefaultAsync(ct);

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(footer?.EventSystemUrl)) missing.Add("event link");
        if (string.IsNullOrWhiteSpace(footer?.EventTags)) missing.Add("hashtags");
        if (string.IsNullOrWhiteSpace(footer?.OrganizerCredits)) missing.Add("organizer credit");

        if (missing.Count > 0)
        {
            return new SoMeScheduleRunResult(0, 0, Array.Empty<string>(),
                $"Nothing was scheduled: this edition has no {string.Join(", no ", missing)} yet, and "
                + "posts are composed once and never rewritten — so they would be queued permanently "
                + "without part of their footer. Fill it in on /Organizer/SoMeSettings.");
        }

        // §842.7/§843.3 — the everyday RHYTHM, and the ceiling reserved for what will not otherwise
        // fit. Both clamped to the number of preferred times: a further post would have to share a
        // minute with another or break his 08:00–16:00 rule, and silently doing either is worse than
        // refusing the extra slot.
        //
        // ⚠️ Read HERE rather than just before planning, because §928's re-time runs first and has to
        // honour the same ceiling — a window that packed master classes four to a day would move his
        // announcements and break §843.3 in the same stroke.
        var slots = SoMeSchedulePlanner.PreferredTimes.Length;
        var normalPerDay = Math.Clamp(footer?.MaxPostsPerDay ?? 2, 1, slots);
        var exceptionPerDay = Math.Clamp(footer?.ExceptionPostsPerDay ?? normalPerDay, normalPerDay, slots);

        // 🔴 §1178 — THE GUARD RUNS BEFORE ANYTHING IS PLANNED. Operator 2026-09-12: *"guard needed.
        // no test sessions or test sponsor or excluded can exist in some planner"*.
        //
        // 🔑 Placed FIRST on purpose. `CollectSubjectsAsync` skips a subject whose posts already
        // exist (§824.21a — "nothing is ever re-planned"), so a post for a subject that has since
        // become excluded would both survive the run AND make the planner believe that subject is
        // handled. Removing it first is what lets the rest of this method see the truth.
        //
        // ⚠️ It cannot revive an excluded subject: the tombstone it writes is exactly what
        // `CollectSubjectsAsync` reads as "do not propose this again".
        var queueGuard = new SoMeQueueGuard(_db);
        var guard = await queueGuard.EnforceAsync(eventId, ct);

        // 🔴 §1203 — and ONE POST PER SUBJECT PER ROUND. Operator 2026-09-12: *"and cleaned up, so we
        // have only 1 per post"*. Runs beside the exclusion guard and for the same reason: the
        // planner's model is one post per (subject, occurrence), so a second one is always wrong and
        // nothing was removing it.
        // ⚠️ The same subject on several DATES is not a duplicate — those are different rounds, and
        // for Type 5 they are the dated runs he wrote himself.
        var dupes = await queueGuard.RemoveDuplicatesAsync(eventId, ct);

        // §908 — the planner's two clocks travel with it: "now" for round 1's floor, the event start
        // for round 2's ("one month out"), so neither is re-derived and neither can drift.
        var subjects = await CollectSubjectsAsync(eventId, now, eventStartUtc, ct);

        // 🔑 §848.2 — DISCARD THE OLD PROPOSALS AND PLAN AGAIN.
        //
        // Operator 2026-08-05: "initialy the planner proposes a schedule, then i decide - and then
        // things are locked down". A Proposed post is the planner's own guess, so re-planning it is
        // not destruction — it is the planner doing its job with better information (a new sponsor,
        // a graphic that has just been built). This is what makes §848.1's whole-period spread
        // possible at all: without it, the first run's front-loaded placement would be permanent.
        //
        // 🔒 §824.21a IS NOT REPEALED, it is narrowed to where it belongs. Anything he has ACCEPTED
        // (Scheduled), EDITED (a manual override), or that has already PUBLISHED is untouchable —
        // and now provably so, because "locked" is a column rather than a promise.
        // 🔴 §906 — DISCARD AND RE-PLAN MUST BE ONE UNIT OF WORK.
        //
        // The delete below is committed immediately, and the replacements are composed afterwards.
        // Anything that throws in between leaves the queue EMPTIED — which is not a theory: on
        // 2026-08-06 an AI intro call timed out mid-compose and his queue went from 83 posts to 5
        // (§906 in REQUIREMENTS). The planner rebuilds proposals on the next tick, so nothing is
        // lost for ever, but an empty SoMe queue is alarming and the window is real.
        //
        // 🔒 One transaction around discard + re-plan makes the failure mode "nothing changed"
        // instead of "everything gone". Null on a non-relational provider — the in-memory provider
        // the tests use has no transactions, and the behaviour under test is the planning, not the
        // atomicity.
        await using var tx = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;

        // 🔴 §911 — READ THE EXISTING TEASERS BEFORE THE DISCARD BELOW WIPES THE ROWS THAT HOLD
        // THEM. This is the whole reuse mechanism: the teaser is keyed by (subject, occurrence), so
        // it outlives the row being deleted and re-created, and the next run spends no AI call on a
        // subject that already has one.
        //
        // ⚠️ Deleted posts are included on purpose. A tombstone (§853) still carries the words that
        // were written for that subject, and re-generating them because the row was retired would
        // pay twice for the same paragraph.
        _introReuse = (await _db.SoMePosts
                .AsNoTracking()
                .Where(p => p.EventId == eventId
                            && p.IntroText != null && p.IntroText != ""
                            && p.SubjectKey != null && p.Occurrence != null)
                .Select(p => new { p.SubjectKey, p.Occurrence, p.IntroText })
                .ToListAsync(ct))
            .GroupBy(p => (p.SubjectKey!, p.Occurrence!.Value))
            .ToDictionary(g => g.Key, g => g.First().IntroText!);

        _introBudget = MaxIntroGenerationsPerRun;

        var stale = await _db.SoMePosts
            .Where(p => p.EventId == eventId
                        // 🔒 §853 — a DELETED post is never discarded: it is the record that says
                        // "do not propose this again". Sweeping it away would let the planner
                        // re-create the very post he deleted, on the next tick.
                        && !p.IsDeleted
                        && p.PlanState == SoMePostPlanState.Proposed
                        // 🔴 APPROVING IS A DECISION TOO. A caught bug: filtering only on PlanState
                        // deleted posts he had APPROVED but not formally accepted, because approval
                        // and acceptance are different columns. Any signal of a human decision —
                        // accepted, approved, edited, or already sent — makes a post untouchable.
                        // §846.3's lesson again: a new feature must never outrank a correctness rule.
                        && !p.IsActive
                        && p.Status == SoMePostStatus.Queued
                        && p.PublishedAtUtc == null
                        && p.ManualTextOverride == null
                        && p.TemplateKind != null
                        // 🔴 §1201 — TYPE 5 IS NEVER DISCARDED, because re-planning it achieves
                        // NOTHING. Operator 2026-09-12: *"something is wrong with event post 5. i
                        // dont recall having added so many"*.
                        //
                        // §834.4: an event post's date comes from his deck and is used AS WRITTEN.
                        // So the discard deleted every un-approved Type 5 post and re-created it on
                        // the same date, every ten minutes, for no gain at all — the planner's own
                        // log read "created 48 (32 event posts)" on run after run, which is how this
                        // was visible at all.
                        //
                        // ⚠️ And it was not merely wasteful. The post got a NEW ID each time, so
                        // every link already sent — the 24-hour alert's "Open it →", a URL he had
                        // pasted somewhere — pointed at a row that no longer existed. The ids
                        // climbing past 321,000 in an edition with a few dozen posts is the churn
                        // made visible.
                        //
                        // 🔑 The discard exists so the SPREAD can be recomputed as subjects arrive
                        // (§848.2). A type whose dates are fixed input has nothing to recompute.
                        && p.TemplateKind != SoMeTemplateKind.EventPost)
            .ToListAsync(ct);

        if (stale.Count > 0)
        {
            _db.SoMePosts.RemoveRange(stale);
            await _db.SaveChangesAsync(ct);
            _log?.LogInformation(
                "§848.2: discarded {Count} un-accepted proposal(s) before re-planning.", stale.Count);
        }

        // 🔴 §928 — MOVE THE ROWS, THEN PLAN. Both halves, in this order, or the data and the rule
        // disagree (§901): re-timing after planning would place new posts around slots that are
        // about to be vacated, and the next tick would find a queue it did not predict.
        //
        // 🔒 AFTER the discard above, so the slots held by proposals that were just thrown away are
        // free for the master classes to move into rather than being obstacles that no longer exist.
        var retimed = await RetimeMasterClassesAsync(eventId, now, eventStartUtc, normalPerDay, ct);

        // §1144 — and now the posts whose slot went by while they were still waiting. Runs in the
        // same place and for the same reason as the retime above: after the discard, so slots freed
        // this tick are available rather than phantom obstacles.
        var pushed = await PushOverduePostsAsync(eventId, now, eventStartUtc, normalPerDay, ct);

        // 🔴 §1183 — the announcement dates, applied to posts that ALREADY EXIST. Operator
        // 2026-09-12: *"planner must obey to the update date changes, even though they are planned or
        // approved related to type 1-4"*. Before the blackout pass, so a post this moves is then
        // checked against the holidays like any other.
        var (movedIntoWindow, lockedBeforeWindow) = await MoveOutOfWindowAsync(
            eventId, now, eventStartUtc, normalPerDay,
            await AnnouncementWindowsAsync(eventId, eventStartUtc, ct), ct);

        // 🔴 §1179 — and off the holidays. Runs in the same place and for the same reason as the two
        // above: after the discard, so slots freed this tick are real rather than phantom obstacles.
        // 🔒 LAST of the passes, so a post it moves is placed around the master classes, the pushed
        // overdue ones and the window moves rather than into a slot one of them is about to take.
        var (movedOffHoliday, lockedOnHoliday) =
            await MoveOutOfBlackoutAsync(eventId, now, eventStartUtc, normalPerDay, ct);

        // §853 — the subject+occurrences he has DELETED. They are excluded from the plan afterwards
        // rather than reserving a slot: a deleted post must not keep occupying the day it had.
        var suppressed = await _db.SoMePosts
            .Where(p => p.EventId == eventId && p.IsDeleted
                        && p.SubjectKey != null && p.Occurrence != null)
            .Select(p => new { p.SubjectKey, p.Occurrence })
            .ToListAsync(ct);

        var suppressedKeys = suppressed
            .Select(s => (s.SubjectKey!, s.Occurrence!.Value))
            .ToHashSet();

        var existing = await _db.SoMePosts
            .Where(p => p.EventId == eventId && !p.IsDeleted
                        && p.SubjectKey != null && p.Occurrence != null)
            .Select(p => new { p.SubjectKey, p.Occurrence, p.ScheduledAtUtc })
            .ToListAsync(ct);

        var existingTuples = existing
            .Select(e => (e.SubjectKey!, e.Occurrence!.Value, e.ScheduledAtUtc))
            .ToList();

        var plan = SoMeSchedulePlanner.Plan(
            subjects, existingTuples, now, eventStartUtc, normalPerDay, exceptionPerDay);

        // 🔒 §853 — drop what he deleted. Done AFTER planning rather than by removing the subject,
        // because a subject's other occurrences must still be planned: deleting a sponsor's SECOND
        // post must not silently cancel their first.
        if (suppressedKeys.Count > 0)
        {
            plan = plan
                .Where(p => !suppressedKeys.Contains((p.SubjectKey, p.Occurrence)))
                .ToList();
        }

        // Anything wanted but not placed had no room before the event — named, not swallowed.
        var placed = plan.Select(p => (p.SubjectKey, p.Occurrence)).ToHashSet();
        var already = existingTuples.Select(e => (e.Item1, e.Item2)).ToHashSet();
        var noRoom = subjects
            .SelectMany(s => Enumerable.Range(1, s.Occurrences).Select(n => (s.SubjectKey, n)))
            .Where(x => !placed.Contains(x) && !already.Contains(x))
            .Select(x => $"{x.SubjectKey} #{x.n}")
            .ToList();

        var editionValues = await _variables.EditionValuesAsync(eventId, ct);

        // 🔴 §915 — PRE-STAGE THE PICTURE. Operator 2026-08-06: *"you need to pre-stage the linking
        // + picture, so i dont have to do that"*.
        //
        // The post already knew its SUBJECT (that is what SubjectKey is, and what fills the
        // variables) — but only Type 5 carried an ImageRef, so for every track, session, tier and
        // sponsor post he had to open the editor and pick the graphic by hand from a dropdown.
        //
        // 🔑 The planner ALREADY looks these graphics up: §846/§854 gate announceability on the
        // graphic EXISTING. It read the date and threw the file name away. Now it keeps it.
        // §917 — shared with the PUBLISHER, which re-resolves the same map at send time so a
        // rebuilt graphic reaches a post that has not gone out. What is stamped here is a default
        // for the editor to show, not the answer.
        var graphicFiles = await new SoMeSubjectGraphic(_db).FileNamesAsync(eventId, ct);

        foreach (var p in plan)
        {
            var composed = await ComposeAsync(eventId, p, editionValues, ct);

            _db.SoMePosts.Add(new SoMePost
            {
                EventId = eventId,
                // The legacy discriminator, kept meaningful for the pages that still read it.
                Type = p.Kind == SoMeTemplateKind.Sponsor || p.Kind == SoMeTemplateKind.SponsorCategory
                    ? SoMePostType.Sponsor
                    : p.Kind == SoMeTemplateKind.EventPost ? SoMePostType.AdHoc : SoMePostType.Speaker,
                TemplateKind = p.Kind,
                SubjectKey = p.SubjectKey,
                Occurrence = p.Occurrence,
                ScheduledAtUtc = p.ScheduledAtUtc,
                Status = SoMePostStatus.Queued,
                // 🔒 HELD. §824.8 Q2 — nothing reaches the company page until he turns it on.
                IsActive = false,
                // §848.2 — a PROPOSAL until he accepts it; re-plannable on any later run.
                PlanState = SoMePostPlanState.Proposed,
                AutoGenerated = true,
                // 🔒 §901 — the TEMPLATE. Resolved at preview and at publish, never here.
                AutoText = composed.Template,
                IntroText = composed.Intro,
                // §915 — the subject's own graphic, attached at birth. Null when none exists yet,
                // which stays an ordinary state: §846 already refuses to announce most subjects
                // before their graphic is built, and the editor still lets him override.
                ImageRef = SoMeSubjectGraphic.Lookup(graphicFiles, p.SubjectKey),
                CreatedAt = now,
            });
        }

        // §834.4 — Type 5 alongside the other four, so "the engine autobuilds everything" is true
        // for all five types rather than four of five.
        var eventPostCount = await PlanEventPostsAsync(
            eventId, editionValues, now, eventStartUtc, footer?.EventPostWindowEndsOn, ct);

        await _db.SaveChangesAsync(ct);

        // §906 — the discard and its replacements land together, or neither does.
        if (tx is not null) await tx.CommitAsync(ct);

        var created = plan.Count + eventPostCount;

        var msg = created == 0
            ? $"Nothing new to schedule ({already.Count} post(s) already planned)."
            : $"{created} post(s) scheduled and HELD for approval.";
        if (eventPostCount > 0) msg += $" {eventPostCount} of them are your own event posts.";

        // 🔒 §928 — SAID OUT LOUD, because this is the one thing the run does to posts he has already
        // approved. A date silently changing under an accepted post is exactly the surprise §824.21a
        // exists to prevent; moving it and not mentioning it would be worse than not moving it.
        if (retimed > 0)
        {
            msg += $" {retimed} already-approved master-class announcement(s) were MOVED into your "
                 + "master-class window — their wording and approval are unchanged, only the date.";
        }
        // §1144 — say it too. A post moving date is a visible change to the plan he reads, and a
        // silent count is the §335 trap: it would look identical to a run that pushed nothing.
        if (pushed.Moved > 0)
        {
            msg += $" {pushed.Moved} post(s) had missed their slot while waiting and were pushed to "
                 + "the next free one";
            // 🔑 §1205 — the held-back ones are the answer to "what are these 19 waiting on": they
            // are no longer stranded in the past, they are walking forward until they are ready.
            if (pushed.HeldBack > 0)
            {
                msg += $", {pushed.HeldBack} of them still held back on a missing logo, text or "
                     + "graphic — they keep moving forward until it arrives";
            }
            msg += " (dates you have ACCEPTED are never moved).";
        }

        // 🛑 §1205 — the two outcomes the push could not deliver, named rather than counted.
        if (pushed.Locked.Count > 0)
        {
            msg += $" ⚠️ {pushed.Locked.Count} overdue post(s) keep a date the planner is not allowed "
                 + "to change: " + string.Join("; ", pushed.Locked.Take(10))
                 + ". Withdraw the acceptance, or edit the date yourself, to move them.";
        }

        if (pushed.Full.Count > 0)
        {
            msg += $" 🛑 {pushed.Full.Count} overdue post(s) could NOT be pushed — every remaining "
                 + "weekday is already at its posts-per-day limit: "
                 + string.Join("; ", pushed.Full.Take(10))
                 + ". Raise the posts per day in SoMe settings, or the campaign loses these.";
        }
        if (noRoom.Count > 0) msg += $" {noRoom.Count} could not fit before the event.";

        // 🔴 §842.5 — A SPONSOR POST THAT DOES NOT FIT IS A CONTRACT BREACH, not a cosmetic miss.
        // Every sponsor must be announced twice. Buried in a flat "N could not fit" line that would
        // read as tidying; it is called out by name and logged as an error so it cannot pass quietly.
        var sponsorNoRoom = noRoom
            .Where(x => x.StartsWith("sponsor:", StringComparison.OrdinalIgnoreCase)
                        || x.StartsWith("tier:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 🔴 §854.1 — sponsors that could not be planned AT ALL because they have no graphic yet.
        // Named in the run message so he can chase the missing logos; §842.5 makes it his problem
        // long before it is theirs.
        if (NotPlannableSponsors.Count > 0)
        {
            msg += $" ⚠️ {NotPlannableSponsors.Count} sponsor(s) could NOT be planned — no logo/graphic "
                 + "yet, so there is nothing to announce. They must be chased: every sponsor is owed "
                 + "two announcements.";

            _log?.LogWarning(
                "§854 {Count} sponsor(s) not plannable (no graphic): {Companies}.",
                NotPlannableSponsors.Count, string.Join(", ", NotPlannableSponsors));
        }

        if (sponsorNoRoom.Count > 0)
        {
            msg += $" ⚠️ {sponsorNoRoom.Count} of those are SPONSOR posts — every sponsor must be "
                 + "announced twice, so this is a contractual problem and needs fixing now.";

            _log?.LogError(
                "§842.5 SPONSOR OBLIGATION AT RISK: {Count} sponsor/tier announcement(s) found no "
                + "room before the event — {Keys}.",
                sponsorNoRoom.Count, string.Join(", ", sponsorNoRoom));
        }

        // 🔑 §1183 — a post that MOVED must say so. He reads this queue daily; a date changing under
        // him with no explanation is indistinguishable from a bug, which is §854's rule applied to a
        // re-time rather than to a refusal.
        if (movedIntoWindow > 0)
        {
            msg += $" 📅 {movedIntoWindow} planned/approved post(s) were moved forward into their "
                 + "type's announcement window.";
        }

        if (lockedBeforeWindow.Count > 0)
        {
            // 🛑 Named, not counted: these are the ones only he can move, so "3 posts" would be a
            // dead end. §848.2 — an accepted slot is his decision and the planner does not overrule it.
            msg += $" ⚠️ {lockedBeforeWindow.Count} post(s) sit BEFORE their window but you accepted "
                 + "those dates, so they were left alone: "
                 + string.Join("; ", lockedBeforeWindow.Take(10))
                 + ". Move them by hand, or withdraw the acceptance to let the planner re-date them.";
        }

        if (movedOffHoliday > 0)
        {
            msg += $" 🎄 {movedOffHoliday} post(s) were moved off the {SoMeBlackout.Description} "
                 + "blackout.";
        }

        if (lockedOnHoliday.Count > 0)
        {
            msg += $" ⚠️ {lockedOnHoliday.Count} accepted post(s) remain inside the holiday blackout: "
                 + string.Join("; ", lockedOnHoliday.Take(10)) + ".";
        }

        if (dupes.Removed > 0)
        {
            msg += $" 🧹 {dupes.Removed} duplicate post(s) removed (same subject and round): "
                 + string.Join("; ", dupes.Reasons.Take(10)) + ".";
        }

        // §1178 — stated in the message he reads, not only in the log: a post disappearing from the
        // queue with no explanation is the thing that sends him looking for a bug.
        if (guard.Removed > 0)
        {
            msg += $" 🛡 {guard.Removed} planned post(s) were removed because their subject must not "
                 + "be announced (test data, or excluded from announcements): "
                 + string.Join("; ", guard.Reasons.Take(10))
                 + ". Clear the flag and press Restore in the editor to bring one back.";
        }

        _log?.LogInformation(
            "§824.21 SoMe schedule: created {Created} ({EventPosts} event posts), already planned "
            + "{Already}, no room {NoRoom}, guard removed {Removed}.",
            created, eventPostCount, already.Count, noRoom.Count, guard.Removed);

        return new SoMeScheduleRunResult(created, already.Count, noRoom, msg, guard.Removed);
    }

    /// <summary>
    /// §834.4 — plans TYPE 5 from the imported event-post deck (§828), one queued post per dated run.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>THE DECK'S DATES ARE USED AS WRITTEN — they are never re-planned.</b> Types 1–4
    /// have no date of their own, so <see cref="SoMeSchedulePlanner"/> chooses one for them. An event
    /// post's run already carries a <c>PostDate</c> he decided; overriding it would be the scheduler
    /// CURATING, which §824.21a forbids in the same words that protect his approvals and edits.</para>
    ///
    /// <para>🔒 Adds only. A slug+run that is already queued is skipped whole, so a re-run never
    /// duplicates a post and never rewrites one he has edited.</para>
    ///
    /// <para>⚠️ Runs whose date has already passed are skipped rather than fired late — publishing
    /// "tickets go on sale Tuesday" a month afterwards is worse than not publishing it.</para>
    /// </remarks>
    /// <summary>
    /// §847 — the last moment a Type 5 event post may be scheduled: <b>1 February of the edition's
    /// year</b>, end of day.
    /// </summary>
    /// <remarks>
    /// <para>🔑 Operator 2026-08-05: <i>"event post must run fom aug-feb 1"</i>. For ELDK27 (event
    /// 9–10 Feb) that closes the window 8 days before the event, which is deliberately tighter than
    /// the <c>eventStartUtc</c> boundary the other four types use.</para>
    ///
    /// <para>🔒 Derived from the edition's own start rather than hardcoded to 2027, so a later
    /// edition does not silently inherit ELDK27's calendar. ⚠️ It is a BOUNDARY, never a
    /// redistribution: §834.4 keeps the deck's stated dates exactly as he wrote them, and this only
    /// declines one that falls outside his window.</para>
    /// </remarks>
    /// <summary>
    /// §854.1 — sponsors that could not be planned because they have no graphic yet (no logo).
    /// </summary>
    /// <remarks>
    /// 🔒 Surfaced so "not planned" is a state he can SEE. §842.5 makes two announcements per sponsor
    /// contractual, so a sponsor missing from the campaign because they never sent a logo is a breach
    /// waiting to happen — and the person who can chase them is the one who would not otherwise
    /// notice. Same rule as §850.2's hidden-count: hidden must never mean forgotten.
    /// </remarks>
    public IReadOnlyList<string> NotPlannableSponsors { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// §847 — the last moment a Type 5 event post may be scheduled.
    /// </summary>
    /// <remarks>
    /// <para>🔴 §1181 — <b>THIS WAS A LITERAL 1 FEBRUARY</b>
    /// (<c>new DateTimeOffset(eventStartUtc.Year, 2, 1, …)</c>), which is §847's instruction for
    /// ELDK27 frozen as a month and a day. Operator 2026-09-12: <i>"verify code so we dont have any
    /// static values dateswise"</i> — this was the one that mattered.</para>
    ///
    /// <para>⚠️ For an edition not held in February it is silently destructive, not merely wrong: a
    /// June event computes 1 February of the same year, months BEFORE the event, and every post in
    /// the deck is dropped by a <c>continue</c> with no message anywhere.</para>
    ///
    /// <para>🔒 His setting wins; the fallback is eight days before the event, which reproduces
    /// 1 February exactly for ELDK27's 9 February start and means something for every other edition.
    /// </para>
    /// </remarks>
    private static DateTimeOffset EventPostWindowEnd(
        DateTimeOffset eventStartUtc, DateOnly? configured) =>
        configured is { } d
            ? SoMeSchedulePlanner.ToUtc(d, new TimeOnly(23, 59, 59))
            : eventStartUtc.AddDays(-8);

    private async Task<int> PlanEventPostsAsync(
        int eventId,
        IReadOnlyDictionary<string, string?> editionValues,
        DateTimeOffset now,
        DateTimeOffset eventStartUtc,
        DateOnly? eventPostWindowEndsOn,   // §1181 — his setting, or null for the derived fallback
        CancellationToken ct)
    {
        var runs = await _db.EventSoMePostOccurrences
            .Where(o => o.Post.EventId == eventId)
            .Select(o => new
            {
                o.Post.Slug,
                o.Sequence,
                o.PostDate,
                o.GraphicFileName,
            })
            .ToListAsync(ct);

        if (runs.Count == 0) return 0;

        var existing = await _db.SoMePosts
            .Where(p => p.EventId == eventId
                        && p.TemplateKind == SoMeTemplateKind.EventPost
                        && p.SubjectKey != null && p.Occurrence != null)
            .Select(p => new { p.SubjectKey, p.Occurrence })
            .ToListAsync(ct);

        var already = existing
            .Select(e => (Key: e.SubjectKey!, Occ: e.Occurrence!.Value))
            .ToHashSet();

        var created = 0;

        foreach (var run in runs.OrderBy(r => r.PostDate).ThenBy(r => r.Sequence))
        {
            var key = SoMeAnnouncementQuery.EventPostPrefix + run.Slug;
            if (already.Contains((key, run.Sequence))) continue;

            // His stated DAY — never re-planned. But the TIME is ours to choose, and §842.4 asks to
            // "mix times".
            //
            // 🔴 This used to be PreferredTimes[0] for every event post, which measured out as 45 of
            // the 112 planned posts all at 09:00. The hour is now picked from the preferred times by
            // a stable hash of the slug + run, so the deck spreads across the day and stays
            // deterministic — the same post always lands at the same hour.
            var hour = SoMeSchedulePlanner.PreferredTimes[
                (int)(SoMeSchedulePlanner.StableHash($"{run.Slug}|{run.Sequence}")
                      % (uint)SoMeSchedulePlanner.PreferredTimes.Length)];

            var scheduled = SoMeSchedulePlanner.ToUtc(run.PostDate, hour);

            // 🔒 §847 — THE TYPE 5 WINDOW CLOSES BEFORE THE EVENT, not at it.
            // Operator 2026-08-05: "event post must run fom aug-feb 1". That is 8 days tighter than
            // the eventStartUtc boundary every other type uses, so it is applied here explicitly
            // rather than inherited.
            // §1181 — the day is now HIS setting, falling back to the derived eight days; it used to
            // be a literal 1 February, which only happened to be right for this edition.
            if (scheduled <= now || scheduled >= eventStartUtc
                || scheduled > EventPostWindowEnd(eventStartUtc, eventPostWindowEndsOn))
            {
                continue;
            }

            var planned = new PlannedSoMePost(
                SoMeTemplateKind.EventPost, key, run.Sequence, scheduled);

            // §901 — Type 5 never has an intro (§834.5: its copy is his own), so this is the
            // template alone. It goes through the same call so there is one seeding path, not two.
            var composed = await ComposeAsync(eventId, planned, editionValues, ct);

            _db.SoMePosts.Add(new SoMePost
            {
                EventId = eventId,
                Type = SoMePostType.AdHoc,
                TemplateKind = SoMeTemplateKind.EventPost,
                SubjectKey = key,
                Occurrence = run.Sequence,
                ScheduledAtUtc = scheduled,
                Status = SoMePostStatus.Queued,
                // 🔒 HELD, exactly like the other four. §824.8 Q2.
                IsActive = false,
                // §848.2 — a proposal. ⚠️ Its DATE is his (from the deck, §834.4), but the post
                // itself is still the planner's until he accepts it.
                PlanState = SoMePostPlanState.Proposed,
                AutoGenerated = true,
                AutoText = composed.Template,
                IntroText = composed.Intro,
                // §828.7 — the run names its own graphic, so the post carries it from the start.
                ImageRef = run.GraphicFileName,
                CreatedAt = now,
            });

            created++;
        }

        return created;
    }

    /// <summary>
    /// Everything announceable, with the occurrence counts he specified in §824.1.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Type 5 (event posts) is absent HERE, but no longer unplanned.</b> It is built by
    /// <see cref="PlanEventPostsAsync"/> instead, because it is the one type whose DATES are already
    /// decided — the deck states them (§828.7), so it must not go through the date planner at all.
    /// <para>(This used to read "there is no subject CEH can derive one from". §828 supplied that
    /// subject: the imported post's slug. §834.4 closed the gap.)</para>
    /// </remarks>
    private async Task<List<SoMeSubject>> CollectSubjectsAsync(
        int eventId, DateTimeOffset now, DateTimeOffset eventStartUtc, CancellationToken ct)
    {
        var subjects = new List<SoMeSubject>();

        // 🔴 §1207 — the old per-type cadence table is NO LONGER READ HERE. §842.2's row is still
        // what §1195 SEEDS a new rule from, so an edition that had customised its frequency keeps
        // that choice — but once the rule exists, the rule is the answer. Reading both was two
        // sources of truth for "how many rounds", and the stale one silently won.

        // 🔴 §1194 — THE RULES: rounds, start and end, per category, from the one table.
        // Operator 2026-09-12: *"basically we define the rules like start date, end date, cadence
        // inside the some settings and the some planner must recalculate if they are changed"*.
        //
        // 🔑 Every ad-hoc floor below now comes from here instead of from its own column, so adding
        // a round on the settings page adds a round to the plan — *"when a some post has more rounds,
        // it should be added into the planner and planned"* — with nothing else to change.
        var ruleSvc = new SoMeCategoryRules(_db);
        var rules = await ruleSvc.GetAllAsync(eventId, ct);
        // §1195 — the rounds he has dated, so a named round keeps its exact day.
        var roundStarts = await ruleSvc.RoundStartsAsync(eventId, ct);

        // The rounds a category is set to, and where each of them opens and closes.
        // 🔴 §1207 — "if i define 3 rounds, we must plan 3 rounds" (operator 2026-09-12). A round he
        // has DATED counts even when the saved number is lower: §1195 seeded that number from the old
        // posting-frequency table, so an edition whose frequency page still said 2 got a rule with
        // Rounds = 2 AND a dated round 3 — a date entered, stored, shown, and never planned.
        // 🔑 The same function `Windows` uses, so the count and the windows cannot disagree.
        int RoundsOf(SoMeAnnouncementCategory c) =>
            rules.TryGetValue(c, out var r)
                ? SoMeCategoryRules.EffectiveRounds(
                    r, roundStarts.TryGetValue(c, out var named) ? named : null)
                : 0;

        IReadOnlyDictionary<int, SoMeRoundWindow> WindowsOf(SoMeAnnouncementCategory c) =>
            rules.TryGetValue(c, out var r)
                ? SoMeCategoryRules.Windows(
                    r, eventStartUtc,
                    roundStarts.TryGetValue(c, out var named) ? named : null)
                : new Dictionary<int, SoMeRoundWindow>();

        // The per-round OPENING dates, in the shape SoMeSubject wants. A round with no opening is
        // omitted: absent means "as soon as the subject is ready", which is not the same as "now".
        Dictionary<int, DateTimeOffset> OpensOf(SoMeAnnouncementCategory c)
        {
            var map = new Dictionary<int, DateTimeOffset>();
            foreach (var (round, w) in WindowsOf(c))
            {
                if (w.OpensUtc is { } o) map[round] = o;
            }
            return map;
        }

        // The category's closing instant — the ceiling §1194 added to the planner.
        DateTimeOffset? ClosesOf(SoMeAnnouncementCategory c) =>
            rules.TryGetValue(c, out var r) && r.EndsOn is not null
                ? SoMeSchedulePlanner.ToUtc(r.EndsOn.Value, new TimeOnly(23, 59, 59))
                : null;

        // 🔴 §920 — THE DEPENDENCY IS THE DATA, NOT A DATE. Operator 2026-08-06: *"remove the 7th
        // sept blocker. we will change the dependency as only active sessions in ceh can be planned.
        // and since the sessions in cfs will not sync until after 5th sept, it comes in
        // automatically and will be active"* … *"this principle change will also allow me to start
        // schedule for example master class session which are approved"*.
        //
        // 🔑 §851 put a hard 7 Sep floor under EVERY track and session because the CfS decision was
        // not made yet. That is a date standing in for a fact, and it was wrong in both directions:
        // it blocked the NINE master classes that are already confirmed, and it would have expired
        // on 7 Sep whether or not the CfS had actually synced.
        //
        // ⇒ A SESSION is announceable because it EXISTS in CEH. Nothing syncs before it is decided,
        // so the arrival of the row IS the decision — no gate needed, and the master classes become
        // schedulable today.
        //
        // ⚠️ A TRACK is different and keeps the floor. A track post lists the speakers of a whole
        // track, so it is only complete once that track's sessions have arrived — and today the 9
        // master classes are a fraction of the ~65 sessions expected. Un-gating tracks too would
        // publish a track post naming a handful of a hundred speakers, which is the one thing he
        // said he wanted to avoid when asked (§908). §901's late resolution does not save it: once
        // PUBLISHED, a post cannot pick anyone up.
        var gateSettings = await _db.SoMeSettings
            .Where(s => s.EventId == eventId)
            .Select(s => new
            {
                s.SpeakerAnnouncementFrom,
                s.ExcludedSessionTitlePatterns,
                s.MasterClassAnnouncementFrom,
                s.CallForSpeakersClosesOn,
                s.SponsorAnnouncementFrom,   // §1179
                s.SponsorRound2From,         // §1184
                s.SponsorCategoryRound1From, // §1181
                s.SponsorCategoryRound2From,
                s.SpeakerTracksRound2From,
                s.SpeakerTracksRound3From,   // §1185
                s.SessionAnnouncementFrom,   // §1186
                s.EventPostWindowEndsOn,
            })
            .FirstOrDefaultAsync(ct);

        var speakerGateDate = gateSettings?.SpeakerAnnouncementFrom;

        // 🔴 §1179 — the sponsor floor. Operator 2026-09-12: *"i wants sponsors to start from
        // oct 15"*. A FLOOR like the track one, not a window: he kept §848.1's spread, so this says
        // "not before" and lets the spread go on choosing the day.
        DateTimeOffset? sponsorFloor = gateSettings?.SponsorAnnouncementFrom is { } sf
            ? SoMeSchedulePlanner.ToUtc(sf, SoMeSchedulePlanner.PreferredTimes[0])
            : null;

        // §1184 — round 2's own date. Null = round 1's floor, so nothing changes for an edition that
        // has not set it.
        DateTimeOffset? sponsorRound2 = gateSettings?.SponsorRound2From is { } sr2
            ? SoMeSchedulePlanner.ToUtc(sr2, SoMeSchedulePlanner.PreferredTimes[0])
            : null;

        DateTimeOffset? speakerGate = speakerGateDate is { } d
            ? SoMeSchedulePlanner.ToUtc(d, SoMeSchedulePlanner.PreferredTimes[0])
            : null;

        // 🔒 §920 — TRACKS ONLY now. Combine the track floor with a subject's own readiness: the
        // LATER of the two wins, because both are "not before this" and neither excuses the other.
        DateTimeOffset? GatedBySpeakers(DateTimeOffset? subjectEarliest) =>
            speakerGate is null ? subjectEarliest
            : subjectEarliest is null ? speakerGate
            : (subjectEarliest > speakerGate ? subjectEarliest : speakerGate);

        // 🔴 §905 — TEST DATA IS NOT ANNOUNCEABLE. Operator 2026-08-06: *"same with SoMe publishing
        // service, it must not include test users"* / *"sponsor categories and sponsor individual of
        // type test should not be included"*.
        //
        // ⚠️ This was NOT hypothetical: two posts announcing "Test Exhibitor Session Preday" and
        // "…MainDay" were already queued for 18 Jan and 1 Feb 2027. They were held (IsActive=false),
        // so the approval gate was the only thing standing between a test fixture and the company
        // page — and a gate a human has to remember is not a control.
        var testCompanies = await TestDataScope.TestSponsorCompanyIdsAsync(_db, eventId, ct);

        // 🔴 §1178 — ONE EXCLUSION RULE, ASKED IN ONE PLACE. Operator 2026-09-12: *"guard needed. no
        // test sessions or test sponsor or excluded can exist in some planner"*.
        //
        // 🔑 This block used to carry §909's flag+inference and §927's title filter inline, and it was
        // MISSING two exclusions that other services already enforced:
        //   • `ExcludeFromSoMeAnnouncements` (§1060(h)) — the gate blocked APPROVAL and auto-approve
        //     WITHDREW, but the planner went on PROPOSING them, so they piled up in the queue.
        //   • `UsedForTesting` (§299) — the button on the Sessions page that literally reads
        //     "Mark as TEST session", which the SoMe engine never read at all.
        // Four services, four different definitions of "excluded" (see SoMeSubjectScope's table).
        //
        // ⇒ SoMeSubjectScope is now the single answer, shared with the GRAPHICS sweep and the §1178
        // guard, so a session can no longer be out of the campaign and inside its own artwork.
        var excludedSessions = await new SoMeSubjectScope(_db).ExcludedSessionsAsync(eventId, ct);
        var testSessionIds = excludedSessions.Select(x => x.SessionId).ToHashSet();

        if (excludedSessions.Count > 0)
        {
            // NAMED with the reason, not counted — §854: he has to see WHY a session left the
            // campaign, and "12 sessions excluded" is not something anyone can act on.
            _log?.LogInformation(
                "§1178 SoMe planner: {Count} session(s) excluded from the campaign: {Sessions}.",
                excludedSessions.Count,
                string.Join(" | ", excludedSessions.Take(30)
                    .Select(x => $"#{x.SessionId} {x.Title} ({x.Reason})")));
        }

        // --- Type 1: one per TRACK, twice --------------------------------------------------
        // 🔒 A track is derived from its sessions, so a track that exists ONLY because of a test
        // session is not a track worth announcing.
        var tracks = await _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession && s.Track != null && s.Track != ""
                        && !testSessionIds.Contains(s.Id))
            .Select(s => s.Track!)
            .Distinct()
            .ToListAsync(ct);
        // §851 — a track post lists its speakers, so it waits for the CfS decision.
        //
        // 🔑 §908 — TWO ROUNDS, NOT A FREQUENCY. Operator 2026-08-06: *"build post for each
        // speakertracks so they run 2 times; now and jan 2027"*.
        //
        // The generic spread distributes a subject's occurrences evenly, which put the eight tracks
        // one per month from September to February — announced once and then not mentioned again for
        // weeks. He wants every track announced as soon as it CAN be, and every track repeated in
        // the run-up to the event.
        //
        // ⚠️ "Now" is honoured as "as soon as the gate allows", his choice when asked: a track post
        // lists {Speakers}, the CfS closes 31 Aug and selection completes 7 Sep (§851), so posting
        // before the gate would announce a half-finished line-up that the post can never correct.
        //
        // 🔒 Round 2 is derived from the EVENT, not hard-coded to January — one month out. ELDK27
        // starts 9 Feb 2027, so this reads "jan 2027" as asked, and the next edition computes its
        // own without anyone editing code.
        // 🔴 §925 — A TRACK IS READY WHEN ITS LINE-UP HAS SETTLED, not on a date.
        //
        // §920 made a SESSION's readiness data-driven (it exists ⇒ it was decided) and left tracks
        // on a hand-set date, because a track post lists a WHOLE track's speakers and is incomplete
        // until that track's sessions have arrived. That date was the last thing standing in for a
        // fact, and it has the §851 flaw in miniature: it expires whether or not the sessions came.
        //
        // 🔑 The data already says it. A track whose newest session arrived N days ago has stopped
        // growing; one still receiving sessions has not. So readiness = the newest session in THAT
        // track + a settle period — per track, and self-adjusting: if the CfS sync slips a week, the
        // track's readiness slips with it, with nobody editing anything.
        //
        // ⚠️ His SpeakerAnnouncementFrom still applies as a FLOOR where he has set one, so this can
        // only ever make a track wait LONGER than he asked, never publish earlier than he allowed.
        // 🔒 §1178 — the SAME excluded set as everything else in this method. This filtered on
        // `IsTestData` alone, a column nothing has ever written, so a test session's arrival date
        // could push a real track's readiness out — a session that is not announceable must not get
        // a vote on WHEN its track is.
        var trackNewestSession = (await _db.Sessions
                .Where(s => s.EventId == eventId && !s.IsServiceSession
                            && !testSessionIds.Contains(s.Id)
                            && s.Track != null && s.Track != "")
                .Select(s => new { s.Track, s.CreatedAt })
                .ToListAsync(ct))
            .GroupBy(s => s.Track!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(x => x.CreatedAt), StringComparer.OrdinalIgnoreCase);

        // 🔴 §925.2 — A QUIET TRACK IS NOT NECESSARILY A FINISHED ONE.
        //
        // §925.1, measured on PROD the day §925 shipped and the only reason it was caught: all eight
        // tracks scored as SETTLED, because each held only its one or two confirmed master classes
        // from 24–26 June. "Nothing new for six weeks" was read as *the line-up has finished* when it
        // actually meant *the intake has not started* — and from inside a single track those two are
        // indistinguishable. The rule was right in SHAPE and thin in SIGNAL.
        //
        // 🔑 THE MISSING FACT IS EDITION-WIDE, AND NO TRACK CAN KNOW IT. Whether the Call for
        // Speakers has landed is a property of the whole import, so readiness now measures the newest
        // session in the EDITION as well as in the track, and neither may sit before the CfS close.
        //
        // ⇒ settle base = the LATEST of: this track's newest session, the edition's newest session,
        //   and the day the CfS closes. Before the intake the third term binds and nothing is
        //   announceable early; after it the data governs again and the date stops mattering.
        //
        // 🔒 §925's PER-TRACK behaviour is refined, not replaced. Once the wave is over, a track that
        // keeps receiving stragglers pushes its own readiness out further than one that has gone
        // quiet — which is the self-adjusting property §925 was built for, now with a floor under it.
        //
        // 🔒 NOT "has the import job run since the CfS closed", which is what §925.1 sketched. A run
        // that imported NOTHING is not evidence that the line-up arrived — it is a rumour of it. The
        // arrival of the sessions is the fact, and it is already in the table being read here.
        //
        // ⚠️ THE HONEST RESIDUAL LIMIT: this makes the campaign wait for the intake, and it cannot
        // make a broken import produce one. With the sync dead the tracks still become announceable a
        // settle period after the CfS closes, naming whatever CEH already had. That is a SILENT JOB,
        // which the job-silence alerting exists to catch; no scheduling rule can see it from inside.
        var editionNewestSession = trackNewestSession.Count > 0
            ? trackNewestSession.Values.Max()
            : (DateTimeOffset?)null;

        var cfsClosesUtc = gateSettings?.CallForSpeakersClosesOn is { } cfs
            ? SoMeSchedulePlanner.ToUtc(cfs, SoMeSchedulePlanner.PreferredTimes[0])
            : (DateTimeOffset?)null;

        // §1181 — HIS SETTING WINS; §908's "one month before the event" is now the fallback rather
        // than the rule. The derivation is still evergreen, but it was not something he could change
        // without a deploy — *"make sure that values here wins, so we dont have static values in the
        // code"*.
        var round2 = gateSettings?.SpeakerTracksRound2From is { } t2
            ? SoMeSchedulePlanner.ToUtc(t2, SoMeSchedulePlanner.PreferredTimes[0])
            : eventStartUtc.AddMonths(-1);

        // 🔴 §1185 — A THIRD ROUND. Operator 2026-09-12: *"speaker tracks must have 3 rounds in the
        // some post, where the first runs in sept as now. second runs in early dec and third runs
        // from mid jan 27"*.
        // ⚠️ The DATE is only half of it: `Times()` reads the edition's saved cadence row first, so a
        // posting-frequency page still saying 2 means round 3 is never planned and this governs
        // nothing. The default is raised to 3 to match, which covers an edition that has never saved
        // one.
        DateTimeOffset? round3 = gateSettings?.SpeakerTracksRound3From is { } t3
            ? SoMeSchedulePlanner.ToUtc(t3, SoMeSchedulePlanner.PreferredTimes[0])
            : null;

        subjects.AddRange(tracks.Select(t =>
        {
            // Settled = nothing new in this track — NOR anywhere in the edition — for
            // TrackSettlePeriod, and never counted from before the intake could have finished.
            var settleBase = trackNewestSession.TryGetValue(t, out var newest)
                ? (DateTimeOffset?)newest
                : null;

            if (editionNewestSession is { } ed && (settleBase is null || ed > settleBase))
                settleBase = ed;
            if (cfsClosesUtc is { } close && (settleBase is null || close > settleBase))
                settleBase = close;

            var settled = settleBase is { } b ? b + TrackSettlePeriod : now;

            // The later of "settled" and his own floor — both are "not before this".
            var round1 = GatedBySpeakers(settled) ?? settled;
            if (round1 < now) round1 = now;

            // 🔴 §1194 — the rounds and their windows come from the RULE now, not from three
            // columns. A round added on the settings page is planned on the next run.
            // 🔒 Round 1 still takes the LATER of the rule's opening and the track's own settle
            // date: a rule may only ever delay, never announce a line-up that has not settled (§925).
            var opens = OpensOf(SoMeAnnouncementCategory.SpeakerTracks);
            var byOccurrence = new Dictionary<int, DateTimeOffset>();
            var floor = round1;

            foreach (var round in opens.Keys.Order())
            {
                // Monotonic: a later round can never open before an earlier one (§908's guard,
                // generalised to however many rounds he has asked for).
                floor = Later(opens[round], floor);
                byOccurrence[round] = floor;
            }

            if (byOccurrence.Count == 0) byOccurrence[1] = round1;

            return new SoMeSubject(
                SoMeTemplateKind.SpeakerTracks, $"track:{t}",
                RoundsOf(SoMeAnnouncementCategory.SpeakerTracks),
                EarliestUtc: round1,
                EarliestByOccurrence: byOccurrence,
                LatestUtc: ClosesOf(SoMeAnnouncementCategory.SpeakerTracks));
        }));

        // §846 — when each session's graphic was built: the moment it became announceable.
        var sessionGraphicReady = await _db.GraphicAssets
            .Where(g => g.EventId == eventId
                        && g.Type == Domain.GraphicAssetType.Session
                        && g.SessionId != null)
            .GroupBy(g => g.SessionId!.Value)
            .Select(g => new { SessionId = g.Key, ReadyAt = g.Min(x => x.CreatedAt) })
            .ToDictionaryAsync(x => x.SessionId, x => x.ReadyAt, ct);

        // --- Type 2: one per SESSION ---------------------------------------------------------
        // His order — master classes, then technical, then panels — is carried by the ORDER the
        // subjects are added, which the planner preserves within a type.
        //
        // 🔑 §912 — WHICH SESSION KINDS ARE ANNOUNCED, AND HOW OFTEN (operator 2026-08-06, reading
        // his own §824.1 back to me):
        //   • Keynote                  — 1 post, 2 × ("key 1 post x 2 times")
        //   • Master class / technical — 1 ×
        //   • Panel discussion         — 1 × ("panels is not 2 but 1" — §845.1's ELDK26 history
        //                                showed 5+5, but the TABLE is the rule, not the history)
        //   • Sponsor speaker session  — 2 × (below; it is not a Sessions row at all)
        //   • Ask the Experts          — NOT announced individually: "ask the experts comes as
        //                                type 5 (not individual)", i.e. his own event-post deck
        //                                covers it. Its absence here is now a DECISION, not an
        //                                oversight.
        // §1207 — `sessionTimes` is gone with the legacy cadence table: every count in this method
        // now comes from `RoundsOf`, i.e. from the rules on the SoMe settings page. Two numbers for
        // "how many rounds" is what this section spent the day being wrong about.

        // 🔴 §928 — THE MASTER CLASSES GO OUT TOGETHER, IN ONE NAMED WEEK.
        //
        // Operator 2026-08-06: *"reschedule master classes to last week august"*, after *"i expect
        // some master classes to be moved up earlier"*. §920 made them schedulable the moment their
        // rows existed, and §848.1 then did what it is supposed to do — spread them evenly across
        // the six months to the event. Nine master classes arrived one at a time from August to
        // February, which is the opposite of an announcement.
        //
        // 🔑 THE DATE IS AN INSTRUCTION, NOT A FLOOR, so this is §908's explicit round rather than
        // §851's gate: a round listed in EarliestByOccurrence is placed from its own window and is
        // NOT spread. Nine posts then fill forward from the Monday at MaxPostsPerDay, which is the
        // week he asked for — whereas a plain EarliestUtc would only have said "not before 24 Aug"
        // and left the spread free to scatter them into December all over again.
        //
        // ⚠️ Round 1 only. If he ever sets the session frequency to 2, the repeat is an ordinary
        // spread post — the window describes when a master class is ANNOUNCED, not how often it is
        // mentioned afterwards.
        var masterClassWindow = gateSettings?.MasterClassAnnouncementFrom is { } mcFrom
            ? Later(SoMeSchedulePlanner.ToUtc(mcFrom, SoMeSchedulePlanner.PreferredTimes[0]), now)
            : (DateTimeOffset?)null;

        // §1186 — the floor for every OTHER session type (keynote, technical session, panel). Master
        // classes keep their own, earlier window above.
        DateTimeOffset? sessionFloor = gateSettings?.SessionAnnouncementFrom is { } sFrom
            ? SoMeSchedulePlanner.ToUtc(sFrom, SoMeSchedulePlanner.PreferredTimes[0])
            : null;

        foreach (var type in new[]
                 {
                     SessionType.Keynote, SessionType.MasterClass,
                     SessionType.TechnicalSession, SessionType.PanelDiscussion,
                 })
        {
            // 🔒 A disabled type (0) stays disabled for every kind — his on/off switch (§842.2)
            // outranks a per-kind minimum, or turning Type 2 off would leave keynotes posting.
            // 🔴 §1194 — master classes and everything else are two CATEGORIES with two rules, so the
            // rounds come from whichever one this session type belongs to.
            var category = type == SessionType.MasterClass
                ? SoMeAnnouncementCategory.MasterClasses
                : SoMeAnnouncementCategory.TechnicalSessions;

            var categoryRounds = RoundsOf(category);

            // §912 — a keynote is announced twice where an ordinary session runs once. Kept as a
            // floor over the category's own number so raising the category still raises the keynote.
            var times = type == SessionType.Keynote && categoryRounds > 0
                ? Math.Max(categoryRounds, 2)
                : categoryRounds;

            var categoryOpens = OpensOf(category);
            var categoryCloses = ClosesOf(category);

            var ids = (await _db.Sessions
                    .Where(s => s.EventId == eventId && !s.IsServiceSession && s.Type == type)
                    .OrderBy(s => s.Id)
                    .Select(s => s.Id)
                    .ToListAsync(ct))
                // 🔒 §1178 — the FULL exclusion set, for every session type in this loop (keynote,
                // master class, TECHNICAL SESSION, panel): marked TEST on the Sessions page
                // (`UsedForTesting`), excluded from announcements (§1060(h)), matched by one of his
                // title patterns (§927), flagged `IsTestData`, or a line-up that is entirely test
                // accounts (§905 — which is all this used to check).
                .Where(id => !testSessionIds.Contains(id))
                .ToList();

            subjects.AddRange(ids.Select(id => new SoMeSubject(
                SoMeTemplateKind.Session, $"session:{id}", times,
                // 🔴 §846 — A SESSION IS ANNOUNCEABLE WHEN ITS GRAPHIC EXISTS, not when the session
                // row does. Operator 2026-08-05: "there are some eligle steps that are required
                // befoe a speaker og sponsor can be considered eligble in the panning, ikke speaker
                // photos or sponsor logo. so the planner have dependencies."
                //
                // His example is the hard one: a sponsor who only decides mid-January who is
                // speaking, then uploads that speaker's photo. The session becomes eligible three
                // weeks before the event — legitimately — and announcing it earlier was never
                // possible, because the graphic could not have been built.
                //
                // ⚠️ Null when no graphic exists yet ⇒ "as soon as the plan allows", which is the
                // pre-§846 behaviour and the right fallback: a session with no graphic still gets
                // announced rather than silently dropped from the campaign.
                // 🔑 §920 — NO SPEAKER GATE ON A SESSION. It is announceable because it EXISTS in
                // CEH: a session only syncs from Sessionize once it has been decided, so the row's
                // arrival IS the decision (operator: *"active is also when they are synced from
                // sessionize and exist in ceh"*). Its own speakers are complete by definition —
                // they are the people on THAT session, not a track-wide list still being filled.
                // ⇒ The nine confirmed master classes become schedulable today instead of waiting
                // for a date that described a CfS deadline rather than their own readiness.
                // 🔴 §1186 — AND HIS FLOOR FOR NON-MASTER-CLASS SESSIONS. Operator 2026-09-12:
                // *"master class start date is a category of technical sessions. they runs fist
                // starting from 14. sept. and other technical sessions (excluding ask the experts)
                // runs from 28. sept."* Type 2 was treated as one thing and it is two: master classes
                // had a window and everything else had nothing at all.
                // 🔒 The LATER of the session's own readiness and the floor — the floor must not
                // announce a session whose graphic does not exist (§846), and readiness must not jump
                // the date he set. Master classes are excluded: their own window governs them.
                EarliestUtc: type == SessionType.MasterClass
                    ? (sessionGraphicReady.TryGetValue(id, out var mcReady) ? mcReady : null)
                    : LaterOrNull(
                        sessionGraphicReady.TryGetValue(id, out var ready) ? ready : null,
                        sessionFloor),
                // 🔒 The session's OWN graphic date is the prompt signal — the speaker gate is a
                // type-wide floor and must not hurry every session at once (§851).
                PromptFromUtc: sessionGraphicReady.TryGetValue(id, out var sessionReady)
                    ? sessionReady : null,
                // 🔴 §1194 — every round's opening, from the category's rule. §928's master-class
                // window is now just this category's round 1, and a second or third round added on
                // the settings page is planned with no code change.
                EarliestByOccurrence: categoryOpens.Count > 0 ? categoryOpens : null,
                LatestUtc: categoryCloses)));
        }

        // --- Type 2b: SPONSOR SPEAKER SESSIONS, twice (§824.1, built in §912) -----------------
        //
        // 🔴 These were never announced AT ALL, and the reason is structural rather than a wrong
        // number: a sponsor speaker session is a SponsorSession row (§292 — entered in the sponsor's
        // wizard, pushed one-way to the Backstage agenda later), and the planner reads Sessions. It
        // could not see them.
        //
        // §824.1: *"sponsor speaker sessions 2 × — when available (depends on when the sponsor has
        // assigned a person to their session) + 14–21 days before the event"*. Both halves are
        // honoured below.
        // 🔴 §1207 — GATED ON ITS OWN CATEGORY, not on Type 2's legacy cadence row. Sponsor speaker
        // sessions are a category with their own switch and their own round count (§1187 Type 2c);
        // asking the OLD posting-frequency table whether ordinary sessions are enabled meant turning
        // Type 2 off there silently cancelled a category that has nothing to do with it — and the
        // category's own Enabled switch governed nothing. Same family as the round-count bug above.
        if (RoundsOf(SoMeAnnouncementCategory.SponsorSpeakerSessions) > 0)
        {
            var sponsorSessions = await _db.SponsorSessions
                .Where(x => x.EventId == eventId
                            // 🔒 "WHEN AVAILABLE" IS A REAL GATE: a session with no speaker assigned
                            // yet has nobody to announce, and {Speakers} would render empty into a
                            // post that can never repair itself (§901).
                            && x.Speakers.Any(sp => sp.ParticipantId != null
                                                    && sp.Participant!.IsActive
                                                    && !sp.Participant.IsTestUser)
                            // §905 — and never a test company's session.
                            && !testCompanies.Contains(x.SponsorCompanyId))
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(ct);

            // 🔴 §1194 — ROUND 2'S WINDOW IS NOW HIS, AND THE 14-DAY LIMIT IS FINALLY REAL.
            //
            // This was `eventStartUtc.AddDays(-21)` with a comment claiming the 14-day edge was
            // "respected because the event start is its ceiling". It was not: the ceiling WAS the
            // event start, so nothing stopped round 2 landing in the final week. §1194 gave the
            // planner a real `LatestUtc`, and this category's rule now carries both ends.
            //
            // 🔒 The shipped fallbacks reproduce the old behaviour exactly — opens 21 days out,
            // closes 14 — so an edition that sets nothing keeps what §824.1 asked for, and now
            // actually gets it.
            var scOpens = OpensOf(SoMeAnnouncementCategory.SponsorSpeakerSessions);
            var scCloses = ClosesOf(SoMeAnnouncementCategory.SponsorSpeakerSessions)
                           ?? eventStartUtc.AddDays(-14);

            if (!scOpens.ContainsKey(2))
            {
                var fallback = eventStartUtc.AddDays(-21);
                scOpens[2] = fallback > now ? fallback : now;
            }

            subjects.AddRange(sponsorSessions.Select(id => new SoMeSubject(
                SoMeTemplateKind.Session,
                SoMeSponsorSessionKey.For(id),
                RoundsOf(SoMeAnnouncementCategory.SponsorSpeakerSessions),
                // 🔑 §920 — NO SPEAKER GATE, same as any other session. Its readiness is its OWN
                // speaker being assigned, which the query above already requires — a CfS deadline
                // has nothing to do with a sponsor naming someone from their own company.
                EarliestUtc: null,
                // 🔑 §843.6 — the sponsor naming their speaker IS an individual arrival, so round 1
                // goes PROMPTLY rather than being spread across the campaign.
                PromptFromUtc: now,
                EarliestByOccurrence: scOpens,
                LatestUtc: scCloses)));
        }

        // --- Type 3: one per sponsor TIER that actually has sponsors, twice -------------------
        // §905 — a tier is announceable because REAL companies are in it. A tier whose only member
        // is a test company must not get a post, and the tier list itself is filtered in
        // SoMeVariableResolver.SponsorTierValuesAsync so the two agree.
        var tiers = (await _db.SponsorInfos
                .Where(s => s.EventId == eventId)
                .Select(s => new { s.SponsorPackage, s.SponsorCompanyId })
                .ToListAsync(ct))
            .Where(s => s.SponsorCompanyId == null || !testCompanies.Contains(s.SponsorCompanyId))
            .Select(s => s.SponsorPackage)
            .Distinct()
            .ToList();
        // 🔴 §1181 — THE TIER ROUNDS ARE NAMED, SO THEY ARE §908 WINDOWS AND NOT A FLOOR. Operator
        // 2026-09-12: *"i would like to run sponsor category some posts, so round 1 runs from dec 15
        // and round 2 runs from jan 15"*.
        //
        // 🔑 A floor lets the spread pick the day, which suits a category that trickles in as sponsors
        // sign. Tiers are a handful of posts and he is naming WHEN EACH ROUND HAPPENS — so an
        // occurrence listed here is placed from its own window and is deliberately NOT spread, and the
        // tier posts land together from the date.
        //
        // ⚠️ This splits tiers off from the §1179 sponsor floor, which covered both types. That was
        // right until he gave tiers their own dates; the more specific instruction wins now. 🔒 The
        // fallback keeps §1179's promise: with the tier dates blank, a tier still obeys the sponsor
        // floor exactly as before, so nothing changes for an edition that has not set them.
        var tierRounds = new Dictionary<int, DateTimeOffset>();
        if (gateSettings?.SponsorCategoryRound1From is { } r1)
        {
            tierRounds[1] = SoMeSchedulePlanner.ToUtc(r1, SoMeSchedulePlanner.PreferredTimes[0]);
        }
        if (gateSettings?.SponsorCategoryRound2From is { } r2)
        {
            tierRounds[2] = SoMeSchedulePlanner.ToUtc(r2, SoMeSchedulePlanner.PreferredTimes[0]);
        }

        // 🔴 §1194 — from the rule. Any number of rounds, each with its own opening, plus a real end.
        var tierOpens = OpensOf(SoMeAnnouncementCategory.SponsorTiers);

        subjects.AddRange(tiers.Select(t => new SoMeSubject(
            SoMeTemplateKind.SponsorCategory, $"tier:{t}",
            RoundsOf(SoMeAnnouncementCategory.SponsorTiers),
            EarliestUtc: sponsorFloor,
            EarliestByOccurrence: tierOpens.Count > 0 ? tierOpens : null,
            LatestUtc: ClosesOf(SoMeAnnouncementCategory.SponsorTiers))));

        // --- Type 4: one per SPONSOR, twice ---------------------------------------------------
        //
        // 🔴 §843.6 — A SPONSOR IS ANNOUNCEABLE WHEN THEIR GRAPHIC EXISTS, NOT WHEN THEY SIGNED.
        //
        // Operator 2026-08-05: "technically there are no delays after a sponsor signed, got
        // onboarded, uploaded logos, graphics are build - then some publish can happen immediately
        // after". The chain is sign → onboard → upload logo → graphic BUILT → announceable.
        //
        // ⚠️ This CORRECTS §842.8, which used SponsorInfo.CreatedAt (the signup date). That
        // scheduled a post for a company whose artwork did not exist yet — the same mistake as
        // announcing them before they signed, only later in the chain. The signup date survives
        // solely as a fallback for a sponsor whose graphic is not built yet, so they are not simply
        // dropped from the plan.
        var sponsorRows = (await _db.SponsorInfos
                .Where(s => s.EventId == eventId && s.SponsorCompanyId != null && s.SponsorCompanyId != "")
                .Select(s => new { s.SponsorCompanyId, s.CreatedAt })
                .ToListAsync(ct))
            // §905 — no Type 4 post for a test company. 🔒 Excluded HERE rather than at the
            // NotPlannableSponsors split below, so a test company is not reported as a sponsor
            // "owed two announcements" (§842.5) — that alert is a contractual warning and must
            // never fire for a fixture.
            .Where(s => !testCompanies.Contains(s.SponsorCompanyId!))
            .ToList();

        // When the sponsor's graphic was built — the moment they became announceable.
        // 🔴 §1143 — A ROW IS NOT A GRAPHIC. `FileName != null` is the difference.
        //
        // Operator 2026-08-28, after deleting a file by hand: *"if i delete a sharepoint file, it
        // must detect it is gone and reset the state"*.
        //
        // ⚠️ This gate used to accept ANY row, so a sponsor stayed "announceable" after their
        // artwork was deleted — the post was planned and would publish with no image, or fail at
        // dispatch. §854 established that a sponsor with no graphic is not planned at all; that rule
        // was only ever enforced against the row's EXISTENCE, never against the file still being
        // there. `SoMeBundleBuildService` clears these fields when it finds the file gone, so the
        // sponsor drops out of the plan (and into `NotPlannableSponsors`, where he is told) until
        // the sweep rebuilds it.
        var graphicReady = await _db.GraphicAssets
            .Where(g => g.EventId == eventId
                        && g.Type == Domain.GraphicAssetType.Sponsor
                        && g.SponsorCompanyId != null
                        && g.FileName != null)
            .GroupBy(g => g.SponsorCompanyId!)
            .Select(g => new { CompanyId = g.Key, ReadyAt = g.Min(x => x.CreatedAt) })
            .ToDictionaryAsync(x => x.CompanyId, x => x.ReadyAt, ct);

        var sponsors = sponsorRows
            .GroupBy(s => s.SponsorCompanyId!)
            // Earliest wins when a company has two rows: the later date would delay a sponsor who
            // has in fact been ready since the earlier one.
            .Select(g => new { CompanyId = g.Key, SignedAt = g.Min(x => x.CreatedAt) })
            .ToList();

        // 🔴 §854 — A SPONSOR WITH NO GRAPHIC IS NOT PLANNED AT ALL.
        //
        // Operator 2026-08-05: "silver sponsor apento is shown in the list even though they have not
        // uploaded logo yet". This USED to fall back to the signup date when no graphic existed,
        // which I justified as "so a sponsor without artwork is not dropped" — and that defeated
        // §846's whole gate, because a signup date in the past makes them look ready from day one.
        //
        // The chain is signed → onboarded → LOGO UPLOADED → graphic built ⇒ eligible. Falling back
        // past the middle of it announces a company whose artwork does not exist.
        var plannable = sponsors.Where(s => graphicReady.ContainsKey(s.CompanyId)).ToList();

        // ⚠️ …but they must not become INVISIBLE. §842.5 is contractual, and a sponsor silently
        // absent because they never sent a logo is a breach waiting to happen. Named, not dropped.
        NotPlannableSponsors = sponsors
            .Where(s => !graphicReady.ContainsKey(s.CompanyId))
            .Select(s => s.CompanyId)
            .ToList();

        // 🔴 §1194 — from the rule, like every other category.
        var sponsorOpens = OpensOf(SoMeAnnouncementCategory.Sponsors);

        subjects.AddRange(plannable.Select(s => new SoMeSubject(
            SoMeTemplateKind.Sponsor,
            $"sponsor:{s.CompanyId}",
            RoundsOf(SoMeAnnouncementCategory.Sponsors),
            // 🔴 §1179 — the LATER of the sponsor's own readiness and his floor. Both are "not before
            // this" and neither excuses the other: the floor must not announce a sponsor whose
            // artwork does not exist yet (§854), and readiness must not jump the date he set.
            // Exactly the shape §920 uses for the track floor.
            EarliestUtc: LaterOrNull(graphicReady[s.CompanyId], sponsorFloor),
            // §851 — a sponsor becoming ready is an INDIVIDUAL arrival, so it is announced promptly
            // (§843.6) rather than spread across the window.
            //
            // 🔴 §1179 — THE FLOOR MUST NOT TOUCH THIS, and my first attempt had it raising the
            // prompt date too. `SoMeSubject.PromptFromUtc` says it in as many words: *"a type-wide
            // floor spreads; an individual arrival hurries"*. Raising it to 15 October tells the
            // planner every sponsor "just became ready" that morning, so all of them hurry and the
            // floor silently becomes a WINDOW — §842.4's clustering, through a different door, and
            // the exact opposite of *"lets keep the current design"*.
            // ⚠️ Caught by `Sponsors_still_spread_across_the_run_up_after_the_floor`, which exists
            // for this and would fail again the moment someone re-applies the floor here.
            PromptFromUtc: graphicReady[s.CompanyId],
            EarliestByOccurrence: sponsorOpens.Count > 0 ? sponsorOpens : null,
            LatestUtc: ClosesOf(SoMeAnnouncementCategory.Sponsors))));

        // 🔒 §842.2 — a DISABLED type contributes no subjects, so it plans nothing new. It does NOT
        // remove what that type has already produced: the scheduler adds and never curates
        // (§824.21a), and deleting posts he may have approved or edited would be exactly the loss
        // that rule exists to prevent.
        return subjects.Where(s => s.Occurrences > 0).ToList();
    }

    /// <summary>
    /// The later of two instants — used wherever a configured date has to be clamped to "not in the
    /// past", because a window whose Monday has already gone by is simply "now".
    /// </summary>
    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>
    /// §1185 — the three track rounds, each guaranteed not to precede the one before it.
    /// </summary>
    /// <remarks>
    /// 🔒 The monotonic clamp is §908's guard extended: a track that only settles inside the final
    /// month would otherwise have round 2 dated BEFORE round 1, and now round 3 before round 2. A
    /// reminder that arrives before the announcement is worse than no reminder.
    /// <para>⚠️ Round 3 is omitted entirely when he has set no date for it, rather than defaulted to
    /// something invented — an un-named round spreads like any other occurrence, which is the
    /// behaviour every other type already has.</para>
    /// </remarks>
    private static Dictionary<int, DateTimeOffset> TrackRounds(
        DateTimeOffset round1, DateTimeOffset round2, DateTimeOffset? round3)
    {
        var two = Later(round2, round1);
        var rounds = new Dictionary<int, DateTimeOffset> { [1] = round1, [2] = two };
        if (round3 is { } three) rounds[3] = Later(three, two);
        return rounds;
    }

    /// <summary>
    /// §1179 — the later of two "not before" dates, either of which may be absent.
    /// </summary>
    /// <remarks>
    /// 🔑 Both arguments are floors, so the answer is the LATER one and a missing floor is simply no
    /// opinion — never a reason to drop the other. Same rule §920's <c>GatedBySpeakers</c> applies to
    /// tracks, written once so the two cannot drift.
    /// </remarks>
    private static DateTimeOffset? LaterOrNull(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : Later(a.Value, b.Value);

    /// <summary>
    /// §928 — move the master-class posts that ALREADY EXIST into his announcement window.
    /// Returns how many actually moved.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>This is the half that re-planning cannot do.</b> Operator 2026-08-06:
    /// <i>"reschedule master classes to last week august"</i>. §918's auto-approval had accepted the
    /// whole open queue, and §848.2 only discards proposals he has NOT accepted — so every post he
    /// wanted moved was frozen against the planner. Steering only NEW posts would have left the rule
    /// and the queue disagreeing, which is §901's shape exactly.</para>
    ///
    /// <para>🔒 <b>Round 1 only, and unpublished only.</b> The window says when a master class is
    /// ANNOUNCED. A post that has already gone out is history and is never touched; a second
    /// occurrence, if his cadence ever asks for one, is an ordinary spread post.</para>
    ///
    /// <para>🔒 Nothing but <c>ScheduledAtUtc</c> changes — not the words, not the approval, not the
    /// plan state. A re-time is not a re-plan.</para>
    /// </remarks>
    private async Task<int> RetimeMasterClassesAsync(
        int eventId, DateTimeOffset now, DateTimeOffset eventStartUtc, int maxPerDay,
        CancellationToken ct)
    {
        var from = await _db.SoMeSettings
            .Where(s => s.EventId == eventId)
            .Select(s => s.MasterClassAnnouncementFrom)
            .FirstOrDefaultAsync(ct);

        // ⚠️ No window set = no opinion. Master classes spread like any other session, which is the
        // pre-§928 behaviour and the right thing for an edition that has not chosen a week.
        if (from is null) return 0;

        var windowStart = Later(
            SoMeSchedulePlanner.ToUtc(from.Value, SoMeSchedulePlanner.PreferredTimes[0]), now);
        if (windowStart >= eventStartUtc) return 0;

        // 🔒 §1178 — the SAME exclusion set as the rest of the engine. This had no test/excluded
        // filter at all, so a test master class would be gathered into the window with the real ones.
        // The guard's tombstone already keeps it out of `rows` below; this keeps the two from
        // disagreeing in the first place, which is the whole point of one shared rule.
        var excludedSessionIds = await new SoMeSubjectScope(_db).ExcludedSessionIdsAsync(eventId, ct);

        var masterClassKeys = (await _db.Sessions
                .Where(s => s.EventId == eventId
                            && s.Type == SessionType.MasterClass
                            && !s.IsServiceSession)
                .Select(s => s.Id)
                .ToListAsync(ct))
            .Where(id => !excludedSessionIds.Contains(id))
            .Select(id => $"session:{id}")
            .ToHashSet(StringComparer.Ordinal);

        if (masterClassKeys.Count == 0) return 0;

        var rows = await _db.SoMePosts
            .Where(p => p.EventId == eventId && !p.IsDeleted
                        && p.SubjectKey != null && p.Occurrence != null)
            .Select(p => new
            {
                p.Id, p.SubjectKey, p.Occurrence, p.ScheduledAtUtc, p.Status, p.PublishedAtUtc,
            })
            .ToListAsync(ct);

        var movable = rows
            .Where(p => masterClassKeys.Contains(p.SubjectKey!)
                        && p.Occurrence == 1
                        && p.PublishedAtUtc == null
                        && p.Status == SoMePostStatus.Queued)
            .Select(p => (p.Id, p.SubjectKey!, p.ScheduledAtUtc))
            .ToList();

        if (movable.Count == 0) return 0;

        // 🔒 Every OTHER post is an obstacle, including the ones already published: their day is
        // spent whether or not the post is still pending.
        var movableIds = movable.Select(m => m.Id).ToHashSet();
        var otherOccupied = rows
            .Where(p => !movableIds.Contains(p.Id))
            .Select(p => p.ScheduledAtUtc)
            .ToList();

        var moves = SoMeSchedulePlanner.RetimeIntoWindow(
            movable, otherOccupied, windowStart, eventStartUtc, maxPerDay);

        if (moves.Count == 0) return 0;

        var byId = moves.ToDictionary(m => m.PostId, m => m.ScheduledAtUtc);
        var ids = byId.Keys.ToList();

        var posts = await _db.SoMePosts.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        foreach (var p in posts) p.ScheduledAtUtc = byId[p.Id];

        await _db.SaveChangesAsync(ct);

        _log?.LogInformation(
            "§928: moved {Count} master-class announcement(s) into the window opening {Window:yyyy-MM-dd}.",
            moves.Count, windowStart);

        return moves.Count;
    }

    /// <summary>
    /// §1144 — move a post whose slot has PASSED while it was still waiting, onto the next free one.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-28: <i>"planning service must adjust if a pending planned will not be
    /// met, so it pushes the schedule"</i>.</para>
    ///
    /// <para>🔴 <b>The gap this closes.</b> The planner ADDS and never curates (§824.21a), and the
    /// dispatcher publishes <c>Queued &amp;&amp; ScheduledAtUtc &lt;= now</c>. So a post blocked on
    /// its artwork simply sat there with a date in the past: it was not rescheduled, it was not
    /// dropped, and when the blocker finally cleared it published immediately and OUT OF ORDER
    /// relative to everything planned after it. The campaign's shape silently degraded, and nothing
    /// in the plan said so.</para>
    ///
    /// <para>🔒 <b>PROPOSED ONLY — an accepted post is never moved.</b> Operator's decision
    /// 2026-08-28, and it is §848.2's rule: once he has accepted a slot the planner does not own it
    /// any more. An accepted post that slips keeps its date and publishes late; that is his call to
    /// change, not the engine's.</para>
    ///
    /// <para>🔒 <b>Only the slipped post moves.</b> His decision over cascading the whole tail: the
    /// posts after it keep their dates, so a single missed slot cannot re-date a campaign he has
    /// been reading all week. `RetimeIntoWindow` treats every other post as an obstacle, so the
    /// per-day rhythm (§843.3) is still respected — the moved post lands in a genuinely free slot
    /// rather than doubling one up.</para>
    ///
    /// <para>⚠️ Published posts are never touched (§1077 stage 5: a published time never moves), and
    /// they remain obstacles — their day is spent whether or not anything is still pending on it.</para>
    ///
    /// <para>🔴 <b>§1205 — AND THE HELD-BACK POSTS, WHICH ARE THE ONES THIS WAS WRITTEN FOR.</b>
    /// Operator 2026-09-12: <i>"anyone that is held-back should be planned. if they dont make the
    /// planned timeslot, then the planner must push to new date until they meet the requirement. but
    /// i prefer to see them inside the plan, as i can then also see capacity"</i>.</para>
    ///
    /// <para>⚠️ <b>The filter was the exact inverse of the paragraph above it.</b> It required
    /// <c>IsActive</c> — i.e. ALREADY APPROVED — so the only posts it ever moved were the ones the
    /// dispatcher was about to publish anyway, and <b>"a post blocked on its artwork" was excluded by
    /// construction</b>: a blocked post never passes <see cref="SoMeApprovalGate"/>, so it is never
    /// <c>IsActive</c>, so it was never movable. The 19 posts held back on a missing logo or social
    /// text sat in the past for ever — still in the plan, still holding a seat in a month that had
    /// already gone. <c>[[a-quiet-fix-must-not-become-a-silent-one]]</c></para>
    ///
    /// <para>🔑 <b>Approval is not the question here; capacity is.</b> An overdue post that cannot
    /// publish has a date that is a lie, and a lie in the plan is a seat mis-sold — which is the
    /// airplane rule §1199 measures. So the rule is simply: overdue and still the planner's to move
    /// ⇒ move it. It walks forward one free slot per run until the dependency lands, and then
    /// publishes at a date he can actually read.</para>
    ///
    /// <para>🔒 <b>Still PROPOSED-only, and what it may not move is now NAMED</b> (§854/§1183). Two
    /// dates are not the planner's: one he ACCEPTED (§848.2), and a Type 5 date, which comes from his
    /// deck and is used as written (§834.4) — re-dating those here would reintroduce §1201's damage
    /// through a different door. Both are reported WITH THE REASON, because the remedy differs.
    /// Same for the ones with nowhere left to go: that is the capacity wall, and it belongs in the
    /// run message rather than in a silent zero.</para>
    ///
    /// <para>⚠️ <b>What this pass actually sees, and why that is not most of the queue.</b> §848.2's
    /// discard runs FIRST and throws away every un-accepted, un-approved, template-built proposal, so
    /// those are re-planned from <c>now</c> on the same tick and can never be overdue. What survives
    /// to reach this pass is the rest: a post he EDITED, a post he APPROVED that then slipped, an
    /// accepted one, and Type 5. Those had no mechanism at all before — the discard skips them by
    /// design and the push skipped them by accident.</para>
    /// </remarks>
    private async Task<OverduePush> PushOverduePostsAsync(
        int eventId, DateTimeOffset now, DateTimeOffset eventStartUtc, int maxPerDay,
        CancellationToken ct)
    {
        var rows = await _db.SoMePosts
            .Where(p => p.EventId == eventId && !p.IsDeleted && p.SubjectKey != null)
            .Select(p => new
            {
                p.Id, p.SubjectKey, p.ScheduledAtUtc, p.Status, p.PublishedAtUtc, p.PlanState,
                p.IsActive, p.TemplateKind,
            })
            .ToListAsync(ct);

        // Overdue = its moment came and went while it was still sitting in the queue.
        // 🔴 §1205 — NO `IsActive` CONDITION. Whether he has approved it yet is a different
        // question from whether its date is still true.
        var overdue = rows
            .Where(p => p.Status == SoMePostStatus.Queued
                        && p.PublishedAtUtc == null
                        && p.ScheduledAtUtc < now)
            .ToList();

        if (overdue.Count == 0) return OverduePush.None;

        // 🛑 The two dates the planner is NOT allowed to touch, named with the reason — because
        // "3 posts were left behind" sends him looking for a bug, and the remedy differs per reason.
        static string? WhyNotMine(Domain.SoMePostPlanState plan, SoMeTemplateKind? kind) =>
            // §848.2 — once he accepts a slot, the planner does not own it any more.
            plan != Domain.SoMePostPlanState.Proposed ? "you accepted this date"
            // 🔴 §834.4 — an event post's date IS his input. §1201 has just finished undoing the
            // damage of re-planning these every ten minutes; re-dating them here would reintroduce
            // it through a different door.
            : kind == SoMeTemplateKind.EventPost ? "the date comes from your event-post deck"
            : null;

        var locked = overdue
            .Select(p => new { Row = p, Why = WhyNotMine(p.PlanState, p.TemplateKind) })
            .Where(x => x.Why is not null)
            .Select(x => $"#{x.Row.Id} {x.Row.TemplateKind?.ToString() ?? "ad-hoc"} was due "
                       + $"{SoMeDisplayTime.ToDanish(x.Row.ScheduledAtUtc):dd-MM-yyyy} ({x.Why})")
            .ToList();

        var movable = overdue
            .Where(p => WhyNotMine(p.PlanState, p.TemplateKind) is null)
            .Select(p => (p.Id, p.SubjectKey!, p.ScheduledAtUtc))
            .ToList();

        if (movable.Count == 0) return new OverduePush(0, 0, locked, Array.Empty<string>());

        var movableIds = movable.Select(m => m.Id).ToHashSet();
        var otherOccupied = rows
            .Where(p => !movableIds.Contains(p.Id))
            .Select(p => p.ScheduledAtUtc)
            .ToList();

        // The window opens NOW: the earliest honest slot for something already late.
        var moves = SoMeSchedulePlanner.RetimeIntoWindow(
            movable, otherOccupied, now, eventStartUtc, maxPerDay);

        var placed = moves.Select(m => m.PostId).ToHashSet();

        // ⚠️ §1205 — every remaining weekday is full at the current posts-per-day, so there is
        // nowhere honest to put these. That is the capacity wall, and it must be visible.
        var full = overdue
            .Where(p => movableIds.Contains(p.Id) && !placed.Contains(p.Id))
            .Select(p => $"#{p.Id} {p.TemplateKind?.ToString() ?? "ad-hoc"} (due "
                       + $"{SoMeDisplayTime.ToDanish(p.ScheduledAtUtc):dd-MM-yyyy})")
            .ToList();

        if (moves.Count == 0) return new OverduePush(0, 0, locked, full);

        // How many of the moved ones were still WAITING on something — the number his question was
        // about. Counted before the save, while `IsActive` still describes the row he asked about.
        var heldBack = overdue.Count(p => placed.Contains(p.Id) && !p.IsActive);

        var byId = moves.ToDictionary(m => m.PostId, m => m.ScheduledAtUtc);
        var ids = byId.Keys.ToList();
        var posts = await _db.SoMePosts.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        foreach (var p in posts) p.ScheduledAtUtc = byId[p.Id];

        await _db.SaveChangesAsync(ct);

        _log?.LogInformation(
            "§1144/§1205: pushed {Count} overdue post(s) onto the next free slot ({HeldBack} of them "
            + "still held back on a dependency); {Locked} accepted and left alone, {Full} with no "
            + "room left before the event.",
            moves.Count, heldBack, locked.Count, full.Count);

        return new OverduePush(moves.Count, heldBack, locked, full);
    }

    /// <summary>
    /// §1183 — the configured earliest date for each (type, round), read from ONE place.
    /// </summary>
    /// <remarks>
    /// <para>🔑 The same values <c>CollectSubjectsAsync</c> plans with, so the re-time and the plan
    /// cannot disagree about when a type opens — which is the whole failure §1178 spent the morning
    /// on, in a different corner of the same engine.</para>
    ///
    /// <para>⚠️ <b>Type 2 is absent on purpose.</b> Master classes already have their own re-time
    /// (§928's <c>RetimeMasterClassesAsync</c>, which moves accepted posts too, deliberately), and
    /// ordinary sessions have no date at all — a session is announceable because it EXISTS (§920).
    /// Adding them here would either duplicate that pass or invent a rule nobody asked for.</para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<(SoMeTemplateKind, int), DateTimeOffset>>
        AnnouncementWindowsAsync(int eventId, DateTimeOffset eventStartUtc, CancellationToken ct)
    {
        // 🔴 §1194 — FROM THE RULES, so "the planner must recalculate if they are changed" holds for
        // every category and every round, including ones he adds later. This used to enumerate the
        // ten columns by hand, which meant an eleventh date was also an eleventh place to remember.
        var ruleSvc = new SoMeCategoryRules(_db);
        var rules = await ruleSvc.GetAllAsync(eventId, ct);
        // §1195 — the rounds he has dated, so a named round keeps its exact day.
        var roundStarts = await ruleSvc.RoundStartsAsync(eventId, ct);
        var map = new Dictionary<(SoMeTemplateKind, int), DateTimeOffset>();

        // ⚠️ The re-time works on the post's TemplateKind, which is coarser than the category — the
        // three session categories all write Type 2. Sessions are therefore left out here rather than
        // moved against the wrong category's dates: master classes already have §928's own re-time,
        // and moving a technical session by a master-class window would be worse than not moving it.
        var byKind = new (SoMeAnnouncementCategory Category, SoMeTemplateKind Kind)[]
        {
            (SoMeAnnouncementCategory.SpeakerTracks, SoMeTemplateKind.SpeakerTracks),
            (SoMeAnnouncementCategory.SponsorTiers, SoMeTemplateKind.SponsorCategory),
            (SoMeAnnouncementCategory.Sponsors, SoMeTemplateKind.Sponsor),
        };

        foreach (var (category, kind) in byKind)
        {
            if (!rules.TryGetValue(category, out var rule)) continue;

            var named = roundStarts.TryGetValue(category, out var n) ? n : null;

            foreach (var (round, window) in SoMeCategoryRules.Windows(rule, eventStartUtc, named))
            {
                if (window.OpensUtc is { } opens) map[(kind, round)] = opens;
            }
        }

        return map;
    }

    /// <summary>
    /// 🔴 §1183 — THE ANNOUNCEMENT DATES GOVERN THE POSTS THAT ALREADY EXIST, NOT ONLY NEW ONES.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"decission update: planner must obey to the update date changes,
    /// even though they are planned or approved related to type 1-4. we need to streamline this and
    /// move the current wrong planned/approved some posts"</i>.</para>
    ///
    /// <para>🔑 <b>Until now a new date only steered NEW posts.</b> §848.2's discard re-plans an
    /// un-approved proposal, so a held post followed a changed date by accident — but an AUTO-APPROVED
    /// one (§918) is excluded from that discard on the principle that approving is a decision, so it
    /// kept whatever date it was born with. Setting "sponsors from 15 October" therefore left every
    /// already-approved sponsor post sitting in September, and the queue disagreed with the rule that
    /// produced it — DESIGN §928 named that failure for the master-class window and it applies to
    /// every type.</para>
    ///
    /// <para>🔒 <b>Approved posts MOVE; accepted posts do not.</b> He named "planned or approved",
    /// which is §848.2's <c>Proposed</c> state with and without <c>IsActive</c>. A post he has
    /// ACCEPTED (<c>PlanState.Scheduled</c>) is the one thing §848.2 makes inviolable — he chose that
    /// slot — so those are counted and NAMED for him to move, never moved for him. 🛑 Published posts
    /// are never touched.</para>
    ///
    /// <para>⚠️ <b>Only ever FORWARD, out of a window the post is too early for.</b> A post already
    /// late enough is left alone: the dates are floors, and dragging a December sponsor post back to
    /// October because "the window opened" would re-plan a schedule he has been reading all week.</para>
    ///
    /// <para>🔑 A re-time is not a re-plan: this writes <c>ScheduledAtUtc</c> and nothing else — the
    /// words, the picture, the approval and the plan state all survive.</para>
    /// </remarks>
    /// <remarks>
    /// 🔴 <b>§1213 — <paramref name="now"/> IS THE FLOOR. The one remaining path that could write a
    /// date in the past, closed.</b>
    ///
    /// <para>⚠️ <b>Honest about what this is and is not.</b> It was found while investigating
    /// <i>"and it also planned a post in the past"</i> ("Surveil auto-approved for Thu 10 Sep 13:00",
    /// reported on the 12th) and it is <b>NOT</b> that defect's cause — this pass did not exist when
    /// that post was dated (§1183 shipped the same day as the report), and a test that removes the
    /// clamp still passes, because §1144's push runs BEFORE this and re-dates an overdue proposal
    /// first. What the PROD evidence shows now is <c>"No due posts"</c> on every tick: nothing is
    /// backdated any more. See REQUIREMENTS §1213 for what is still unexplained.</para>
    ///
    /// <para>🔑 <b>The hole is real even though it is not that hole.</b> A category's window can open
    /// on a day that has already passed — a round he dated last week, or simply the run-up moving on.
    /// This pass then calls <c>RetimeIntoWindow</c> from the WINDOW's opening day, and that loop packs
    /// from <c>windowStartUtc</c>: it compares against the window and the event, never against today.
    /// An accepted-but-unmovable post, or a proposal the push could not place, reaches here and is
    /// moved BACKWARDS into a day that is gone. ⇒ Clamped, because <b>every other re-time in this
    /// class takes <c>now</c> — §928's master classes, §1144's overdue push, §1179's blackout — and
    /// this one was the only one that did not.</b></para>
    ///
    /// <para>🔒 A window that has opened is still OPEN; it is not an instruction to publish in the
    /// past. Clamping keeps "not before the window" true while making "not before today" true as
    /// well, and a future window is still obeyed exactly.</para>
    /// </remarks>
    private async Task<(int Moved, IReadOnlyList<string> Locked)> MoveOutOfWindowAsync(
        int eventId, DateTimeOffset now, DateTimeOffset eventStartUtc, int maxPerDay,
        IReadOnlyDictionary<(SoMeTemplateKind Kind, int Occurrence), DateTimeOffset> windows,
        CancellationToken ct)
    {
        if (windows.Count == 0) return (0, Array.Empty<string>());

        var rows = await _db.SoMePosts
            .Where(p => p.EventId == eventId && !p.IsDeleted && p.SubjectKey != null
                        && p.TemplateKind != null && p.Occurrence != null)
            .Select(p => new
            {
                p.Id, p.SubjectKey, p.ScheduledAtUtc, p.Status, p.PublishedAtUtc, p.PlanState,
                p.TemplateKind, p.Occurrence,
            })
            .ToListAsync(ct);

        var tooEarly = rows
            .Where(p => p.Status == SoMePostStatus.Queued && p.PublishedAtUtc == null)
            .Select(p => new
            {
                Row = p,
                Window = windows.TryGetValue((p.TemplateKind!.Value, p.Occurrence!.Value), out var w)
                    ? (DateTimeOffset?)w : null,
            })
            .Where(x => x.Window is { } w && x.Row.ScheduledAtUtc < w)
            .ToList();

        if (tooEarly.Count == 0) return (0, Array.Empty<string>());

        // 🛑 His own accepted slots: reported, never moved.
        var locked = tooEarly
            .Where(x => x.Row.PlanState == Domain.SoMePostPlanState.Scheduled)
            .Select(x => $"#{x.Row.Id} {x.Row.TemplateKind} on "
                       + $"{SoMeDisplayTime.ToDanish(x.Row.ScheduledAtUtc):dd-MM-yyyy}")
            .ToList();

        var movableIds = tooEarly
            .Where(x => x.Row.PlanState == Domain.SoMePostPlanState.Proposed)
            .Select(x => x.Row.Id)
            .ToHashSet();

        if (movableIds.Count == 0) return (0, locked);

        var moved = 0;

        // One pass per WINDOW: `RetimeIntoWindow` packs from a single start, so two types opening on
        // different dates cannot share a call without one of them being placed from the other's date.
        foreach (var group in tooEarly
                     .Where(x => movableIds.Contains(x.Row.Id))
                     .GroupBy(x => x.Window!.Value))
        {
            // 🔴 §1213 — NEVER BEFORE TODAY. A window that opened last week is still open; it is not
            // an instruction to place a post last week. Without this clamp `RetimeIntoWindow` packs
            // from the window's own opening day and compares only against the window and the event.
            var windowStart = Later(group.Key, now);
            if (windowStart >= eventStartUtc) continue;   // no room left before the event

            var movable = group
                .Select(x => (x.Row.Id, x.Row.SubjectKey!, x.Row.ScheduledAtUtc))
                .ToList();

            var ids = movable.Select(m => m.Id).ToHashSet();

            // ⚠️ Every OTHER post is an obstacle, including the ones this run has already moved —
            // otherwise two windows would both pack onto the same free days.
            var occupied = rows
                .Where(p => !ids.Contains(p.Id))
                .Select(p => p.ScheduledAtUtc)
                .ToList();

            var moves = SoMeSchedulePlanner.RetimeIntoWindow(
                movable, occupied, windowStart, eventStartUtc, maxPerDay);

            if (moves.Count == 0) continue;

            var byId = moves.ToDictionary(m => m.PostId, m => m.ScheduledAtUtc);
            var idList = byId.Keys.ToList();
            var posts = await _db.SoMePosts.Where(p => idList.Contains(p.Id)).ToListAsync(ct);
            foreach (var p in posts)
            {
                p.ScheduledAtUtc = byId[p.Id];
                // Keep the in-memory obstacle list honest for the next window in this loop.
                var row = rows.First(r => r.Id == p.Id);
                rows[rows.IndexOf(row)] = row with { ScheduledAtUtc = byId[p.Id] };
            }

            await _db.SaveChangesAsync(ct);
            moved += moves.Count;
        }

        if (moved > 0 || locked.Count > 0)
        {
            _log?.LogInformation(
                "§1183: moved {Moved} planned/approved post(s) forward into their type's announcement "
                + "window; {Locked} accepted post(s) left for the operator.", moved, locked.Count);
        }

        return (moved, locked);
    }

    /// <summary>
    /// 🔴 §1179 — MOVE THE POSTS THAT ARE ALREADY SITTING ON THE HOLIDAYS.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"dates are all over the place, even dec 30 and jan 1"</i> ·
    /// <i>"lets keep the current design, but make blockout between dec 23 - jan 3 due to
    /// holidays"</i>.</para>
    ///
    /// <para>🔑 <b>Teaching the planner about holidays only fixes the NEXT post.</b> The ones he is
    /// looking at are already placed AND already auto-approved — the mails naming 30 Dec 14:30 and
    /// 01 Jan 11:00 have been sent — so without this pass they would publish on those days no matter
    /// what the placement rule now says. This is §928's own lesson, restated: <i>"a window that
    /// steered only NEW posts would leave the queue disagreeing with the rule that produced it"</i>.</para>
    ///
    /// <para>🔒 <b>Forward, to the first free slots after the holiday.</b> Displaced posts pack from
    /// 4 January at the normal posts-per-day, around whatever already holds a slot. Moving them
    /// BACKWARDS into the week before Christmas was the alternative and is worse: that week is the
    /// one people are already checking out of, and it would bunch the posts against the very break
    /// he is avoiding.</para>
    ///
    /// <para>🛑 <b>A post he has ACCEPTED is never moved.</b> §848.2 — a Scheduled slot is his, and a
    /// holiday is not a good enough reason to overrule a date he chose deliberately. Those are
    /// COUNTED and named in the run message instead, so he can move them himself. Published posts are
    /// never touched at all.</para>
    ///
    /// <para>⚠️ A re-time is not a re-plan (DESIGN §928): this writes <c>ScheduledAtUtc</c> and
    /// nothing else — the words, the picture, the approval and the plan state all survive.</para>
    /// </remarks>
    private async Task<(int Moved, IReadOnlyList<string> Locked)> MoveOutOfBlackoutAsync(
        int eventId, DateTimeOffset now, DateTimeOffset eventStartUtc, int maxPerDay,
        CancellationToken ct)
    {
        var rows = await _db.SoMePosts
            .Where(p => p.EventId == eventId && !p.IsDeleted && p.SubjectKey != null)
            .Select(p => new
            {
                p.Id, p.SubjectKey, p.ScheduledAtUtc, p.Status, p.PublishedAtUtc, p.PlanState,
            })
            .ToListAsync(ct);

        static DateOnly DanishDay(DateTimeOffset utc) => DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(utc, SoMeSchedulePlanner.DanishTime).DateTime);

        var onHoliday = rows
            .Where(p => p.Status == SoMePostStatus.Queued
                        && p.PublishedAtUtc == null
                        && SoMeBlackout.IsBlackedOut(DanishDay(p.ScheduledAtUtc)))
            .ToList();

        if (onHoliday.Count == 0) return (0, Array.Empty<string>());

        // 🛑 His accepted dates are reported, never moved.
        var locked = onHoliday
            .Where(p => p.PlanState == Domain.SoMePostPlanState.Scheduled)
            .Select(p => $"#{p.Id} on {DanishDay(p.ScheduledAtUtc):dd-MM-yyyy}")
            .ToList();

        var movable = onHoliday
            .Where(p => p.PlanState == Domain.SoMePostPlanState.Proposed)
            .Select(p => (p.Id, p.SubjectKey!, p.ScheduledAtUtc))
            .ToList();

        if (movable.Count == 0) return (0, locked);

        var movableIds = movable.Select(m => m.Id).ToHashSet();
        var otherOccupied = rows
            .Where(p => !movableIds.Contains(p.Id))
            .Select(p => p.ScheduledAtUtc)
            .ToList();

        // The window opens on the first day the holiday is over — or now, if the break has already
        // passed and these posts are simply stale.
        var reopensOn = SoMeBlackout.NextAllowedDay(DanishDay(now));
        var windowStart = Later(
            SoMeSchedulePlanner.ToUtc(reopensOn, SoMeSchedulePlanner.PreferredTimes[0]), now);

        // ⚠️ The whole holiday can sit AFTER the event for a late edition, in which case there is no
        // window to move into and the posts are left where they are rather than shoved past the event.
        if (windowStart >= eventStartUtc) return (0, locked);

        var moves = SoMeSchedulePlanner.RetimeIntoWindow(
            movable, otherOccupied, windowStart, eventStartUtc, maxPerDay);

        if (moves.Count == 0) return (0, locked);

        var byId = moves.ToDictionary(m => m.PostId, m => m.ScheduledAtUtc);
        var ids = byId.Keys.ToList();
        var posts = await _db.SoMePosts.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        foreach (var p in posts) p.ScheduledAtUtc = byId[p.Id];

        await _db.SaveChangesAsync(ct);

        _log?.LogInformation(
            "§1179: moved {Count} post(s) off the {Window} blackout; {Locked} accepted post(s) left "
            + "for the operator.", moves.Count, SoMeBlackout.Description, locked.Count);

        return (moves.Count, locked);
    }

    /// <summary>
    /// What a newly planned post is seeded with: the edition's TEMPLATE, and the one value that has
    /// to be drawn now rather than at publish time.
    /// </summary>
    /// <param name="Template">
    /// 🔴 §901 — the raw token body, stored VERBATIM as <see cref="SoMePost.AutoText"/>.
    /// </param>
    /// <param name="Intro">The AI opening line (§824.2D), or null — see <see cref="SoMePost.IntroText"/>.</param>
    private readonly record struct PlannedBody(string Template, string? Intro);

    /// <summary>
    /// §901 — build the body for one planned post: the TEMPLATE plus its plan-time intro.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>This used to RENDER the template and hand back the result, which the planner stored.</b>
    /// That froze every variable at plan time (operator 2026-08-06: <i>"why do you make {organizers}
    /// differently than {speakers}"</i> — the answer was that CEH froze BOTH, and every other token
    /// too). A post planned in August published August's speaker list in January; a post whose
    /// speaker list was empty when it was planned said <i>"Meet our tech legends:"</i> above a blank
    /// line for ever, and <b>could not repair itself</b> — which is the exact failure §864's late
    /// resolution exists to prevent. It was also silently undoing §892, which had just converted 82
    /// bodies to variables for ELDK28 reuse: the two changes were fighting.
    ///
    /// <para>🔑 <b>The resolution below is still needed — but as INPUT TO THE INTRO, not as the body.</b>
    /// The model is told what the post is about (a sponsor's name, a session's abstract), which is
    /// why the values are gathered here at all. What gets persisted is the template they were
    /// gathered for, and <see cref="SoMePostComposer"/> resolves it again at preview and at publish.</para>
    /// </remarks>
    private async Task<PlannedBody> ComposeAsync(
        int eventId, PlannedSoMePost p,
        IReadOnlyDictionary<string, string?> editionValues, CancellationToken ct)
    {
        var values = new Dictionary<string, string?>(editionValues, StringComparer.OrdinalIgnoreCase);

        var id = p.SubjectKey.Contains(':') ? p.SubjectKey[(p.SubjectKey.IndexOf(':') + 1)..] : p.SubjectKey;

        switch (p.Kind)
        {
            case SoMeTemplateKind.SpeakerTracks:
                Merge(values, await _variables.TrackValuesAsync(eventId, id, ct));
                break;
            // 🔒 §912 — the sponsor-session prefix is tested FIRST. Both keys end in a number, and a
            // bare int.TryParse would resolve sponsorsession:1 as Sessions row 1 — a different talk.
            case SoMeTemplateKind.Session
                when SoMeSponsorSessionKey.TryParse(p.SubjectKey, out var sponsorSessionId):
                Merge(values, await _variables.SponsorSessionValuesAsync(eventId, sponsorSessionId, ct));
                break;
            case SoMeTemplateKind.Session when int.TryParse(id, out var sessionId):
                Merge(values, await _variables.SessionValuesAsync(sessionId, ct));
                break;
            case SoMeTemplateKind.SponsorCategory when Enum.TryParse<SponsorPackage>(id, out var tier):
                Merge(values, await _variables.SponsorTierValuesAsync(eventId, tier, ct));
                break;
            case SoMeTemplateKind.Sponsor:
                Merge(values, await _variables.SponsorValuesAsync(eventId, id, ct));
                break;
            // §834.4 — Type 5's copy is HIS, imported from the deck and keyed by slug (§828).
            case SoMeTemplateKind.EventPost:
                Merge(values, await _variables.EventPostValuesAsync(eventId, id, ct));
                break;
        }

        // §824.2D — the AI intro. 🔒 Every failure path here (not configured, endpoint busy, a
        // reply that broke the house rules) resolves to NULL, and the renderer then closes the gap:
        // the post is composed without its opening line and still queues for approval. A missing
        // paragraph is a small loss; a scheduler that stops planning because an AI endpoint had a bad
        // minute is an absence nobody notices.
        //
        // 🔒 §834.5 — NOT FOR TYPE 5. Its body is his own finished copy, so generating an opening
        // line for it would put a machine sentence above what he wrote. The Type 5 template does not
        // reference {IntroText} at all; skipping the call also saves a pointless AI round-trip.
        // 🔴 §911 — GENERATED ONCE PER SUBJECT, THEN REUSED. This is the fix that makes the feature
        // affordable at all.
        //
        // The planner discards and re-plans its un-accepted proposals on EVERY tick (§848.2, every
        // 5 minutes). Generating here unconditionally therefore meant ~78 AI calls per run, forever
        // — which is what killed the run on 2026-08-06 (§906) and emptied the queue. The endpoint
        // was never the problem: it answers in well under a second. The COUNT was.
        //
        // 🔒 A teaser belongs to (subject, occurrence), not to a row id, so it survives the row
        // being discarded and re-created. Steady state is ZERO calls.
        // 🔑 §923 — PICK THE BODY FIRST, so the next block can ask whether it even USES a teaser.
        // (This selection used to sit below the generation, which is why the generation could not
        // consult it.)
        var pool = await _db.SoMeBodySamples
            .Where(s => s.EventId == eventId && s.Kind == p.Kind)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
            .Select(s => s.Body)
            .ToListAsync(ct);

        var template = SoMeBodySampleCatalog.Pick(pool, p.SubjectKey, p.Occurrence)
                       ?? await _templates.BodyAsync(eventId, p.Kind, ct);

        // 🔴 §923 — DO NOT WRITE A TEASER NOBODY WILL READ.
        //
        // Measured on PROD 2026-08-06, minutes after the AI was switched on: Type 1 had 16 posts,
        // ZERO of whose bodies reference {IntroText} — and 11 teasers had already been generated
        // for them. His 10 track wordings simply do not use a teaser; the session wordings do.
        //
        // ⚠️ The cost is not only the wasted call. §911's budget is 12 generations per RUN, so a
        // teaser written for a track post is a slot NOT spent on a session post that needs one —
        // it delays exactly the work he was waiting for.
        var bodyUsesTeaser =
            template.Contains("{IntroText}", StringComparison.OrdinalIgnoreCase)
            || template.Contains("{SessionTeaserTextAI}", StringComparison.OrdinalIgnoreCase);

        string? intro = null;
        var reuseKey = (p.SubjectKey, p.Occurrence);

        if (p.Kind != SoMeTemplateKind.EventPost && bodyUsesTeaser)
        {
            if (_introReuse.TryGetValue(reuseKey, out var kept) && !string.IsNullOrWhiteSpace(kept))
            {
                intro = kept;
            }
            // ⚠️ A BUDGET PER RUN, because reuse alone does not save the FIRST run: with an empty
            // store every post is a miss, and 78 × up-to-15s is far past the function's execution
            // window. Bounded, the backlog simply fills in over successive ticks — a few posts a
            // run, every 5 minutes — and no single run can ever be the one that dies.
            else if (_intro is { IsConfigured: true } && _introBudget > 0)
            {
                _introBudget--;
                intro = await _intro.GenerateAsync(
                    new SoMeIntroRequest(p.Kind, IntroTitle(p.Kind, id, values), IntroDetail(p.Kind, values)),
                    ct);

                // Remembered immediately, so a second occurrence of the same subject in THIS run
                // reuses it rather than spending a second call on the same words.
                if (!string.IsNullOrWhiteSpace(intro)) _introReuse[reuseKey] = intro;
            }
        }
        // Kept in the dictionary because IntroTitle/IntroDetail above read from it, and because the
        // rendering below is what a caller wanting a PREVIEW would use. It is not what is stored.
        values["IntroText"] = intro;

        // 🔑 §908 — A POOL OF WORDINGS, WHERE HE HAS GIVEN ONE. Operator 2026-08-06: *"the
        // speakersession is a catalog of samples, which you can randomize to make new posts. this
        // way it will be a mix of many different wordings"* — 38 session wordings, 10 track ones.
        //
        // 🔒 The draw is deterministic (see SoMeBodySampleCatalog): varied across posts, identical
        // for the same post on every re-plan. An empty pool falls through to the single template,
        // which is exactly the pre-§908 behaviour, so a type he has written no samples for is
        // unaffected.
        // 🔴 §907 — TYPE 5 IS SEEDED WITH HIS COPY, NOT WITH A POINTER TO IT.
        //
        // Operator 2026-08-06: *"i have NOT asked for a eventPostBody variable - it makes NO sense"*.
        // He is right. {EventPostBody} is a hole in the Type 5 template for copy that is already
        // finished and already his (§834.4) — it is plumbing, not a variable he would ever want to
        // position or reuse. Left unresolved by §901 it became the entire post text: the editor
        // showed three lines of tokens where his post used to be ("all text is gone").
        //
        // 🔑 So it is substituted HERE, at plan time, and ONE LEVEL ONLY. What lands in the post is
        // the deck's text — which §904 tokenised, so it still carries {EventTags},
        // {EventSystemUrl}, {EventDates} and the rest. Nothing about §901 is given up: the values
        // that must stay live are still tokens, resolved at publish. The only thing resolved early
        // is WHICH TEXT this post is, and that was never a variable.
        if (p.Kind == SoMeTemplateKind.EventPost)
        {
            var deckBody = values.GetValueOrDefault("EventPostBody");
            if (!string.IsNullOrWhiteSpace(deckBody))
            {
                template = template.Replace("{EventPostBody}", deckBody);
            }
        }

        // 🔒 §901 — otherwise THE TEMPLATE, NOT `SoMeTemplateRenderer.Render(template, values)`.
        // That one call is the whole defect: its output is correct as a preview and wrong as a
        // stored body.
        return new PlannedBody(template, intro);
    }

    private static void Merge(
        Dictionary<string, string?> into, IReadOnlyDictionary<string, string?> from)
    {
        foreach (var (k, v) in from) into[k] = v;
    }

    /// <summary>What the intro is ABOUT — the already-resolved value, not the raw subject key.</summary>
    /// <remarks>
    /// Falls back to the key only if resolution found nothing, so the model is never handed
    /// <c>sponsor:co-26</c> and asked to be enthusiastic about it.
    /// </remarks>
    private static string IntroTitle(
        SoMeTemplateKind kind, string id, IReadOnlyDictionary<string, string?> values)
    {
        var resolved = kind switch
        {
            SoMeTemplateKind.SpeakerTracks => values.GetValueOrDefault("TrackName"),
            SoMeTemplateKind.Session => values.GetValueOrDefault("SessionTitle"),
            SoMeTemplateKind.SponsorCategory => values.GetValueOrDefault("SponsorTier"),
            SoMeTemplateKind.Sponsor => values.GetValueOrDefault("SponsorName"),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(resolved) ? id : resolved!;
    }

    /// <summary>
    /// The context the model writes from: a session's own abstract, or a sponsor's own description.
    /// </summary>
    /// <remarks>
    /// ⚠️ A sponsor's <c>SocialMediaIntro</c> is ALREADY printed in full by the Type 4 template. It is
    /// passed here as CONTEXT so the opening line is about the right company — and the system prompt
    /// forbids repeating what surrounds it, which is what stops the post saying the same thing twice.
    /// </remarks>
    private string? IntroDetail(SoMeTemplateKind kind, IReadOnlyDictionary<string, string?> values) =>
        kind switch
        {
            SoMeTemplateKind.Sponsor => values.GetValueOrDefault("SponsorSocialMediaCompanyDescription"),
            SoMeTemplateKind.SponsorCategory => values.GetValueOrDefault("SponsorList"),
            SoMeTemplateKind.SpeakerTracks => values.GetValueOrDefault("SpeakerNames"),
            // His words: the intro is generated "based on session title and session abstract".
            SoMeTemplateKind.Session => values.GetValueOrDefault("SessionAbstract"),
            _ => null,
        };
}
