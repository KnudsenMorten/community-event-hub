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
public sealed record SoMeScheduleRunResult(
    int Created, int AlreadyPlanned, IReadOnlyList<string> NoRoom, string Message);

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
                        && p.TemplateKind != null)
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
        var eventPostCount = await PlanEventPostsAsync(eventId, editionValues, now, eventStartUtc, ct);

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

        _log?.LogInformation(
            "§824.21 SoMe schedule: created {Created} ({EventPosts} event posts), already planned "
            + "{Already}, no room {NoRoom}.",
            created, eventPostCount, already.Count, noRoom.Count);

        return new SoMeScheduleRunResult(created, already.Count, noRoom, msg);
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

    private static DateTimeOffset EventPostWindowEnd(DateTimeOffset eventStartUtc) =>
        new DateTimeOffset(eventStartUtc.Year, 2, 1, 23, 59, 59, TimeSpan.Zero);

    private async Task<int> PlanEventPostsAsync(
        int eventId,
        IReadOnlyDictionary<string, string?> editionValues,
        DateTimeOffset now,
        DateTimeOffset eventStartUtc,
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

            // 🔒 §847 — THE TYPE 5 WINDOW CLOSES ON 1 FEBRUARY, not at the event.
            // Operator 2026-08-05: "event post must run fom aug-feb 1". That is 8 days tighter than
            // the eventStartUtc boundary every other type uses, so it is applied here explicitly
            // rather than inherited.
            if (scheduled <= now || scheduled >= eventStartUtc || scheduled > EventPostWindowEnd(eventStartUtc))
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

        // §842.2 — his per-type frequency, or the shipped §824.1 defaults where he has not set one.
        var cadence = await new SoMeCadenceService(_db).GetAllAsync(eventId, ct);

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
            })
            .FirstOrDefaultAsync(ct);

        var speakerGateDate = gateSettings?.SpeakerAnnouncementFrom;

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

        // 🔴 §909 — THE FLAG FIRST, the inference second. Operator 2026-08-06: *"remove test
        // sessions"*. §905's derived rule ("has speakers and every one is a test user") caught the
        // two exhibitor fixtures and missed "Test Master Class" and "Test Session" completely,
        // because those carry FOUR REAL SPEAKERS each. A session is test because of what it IS.
        var testSessionIds = (await _db.Sessions
                .Where(s => s.EventId == eventId && (s.IsTestData || s.SessionSpeakers.Any()))
                .Select(s => new
                {
                    s.Id,
                    s.IsTestData,
                    HasSpeakers = s.SessionSpeakers.Any(),
                    // ⚠️ An unresolvable participant is NOT test — so a session containing one is
                    // never classified as a fixture and dropped from the campaign.
                    AllTest = s.SessionSpeakers.All(ss => ss.Participant != null && ss.Participant.IsTestUser),
                })
                .ToListAsync(ct))
            // The derived half still applies, so fixtures seeded before the column keep working
            // with no data entry — same arrangement as TestDataScope for sponsors.
            .Where(x => x.IsTestData || (x.HasSpeakers && x.AllTest))
            .Select(x => x.Id)
            .ToHashSet();

        // 🔴 §927 — HIS OWN EXCLUSION LIST, matched on the session TITLE. Operator 2026-08-06:
        // *"exclude option with title filters must be build like ask the experts*"*.
        //
        // 🔑 Test data is decided by the system; this is decided by HIM. The Ask-the-Experts
        // sessions are a format rather than a talk — no abstract to announce, and he covers them in
        // one Type 5 post he writes himself. Same treatment either way: not a subject at all, so the
        // planner never proposes it and no held post accumulates waiting for an abstract that is
        // never coming (which is exactly what §926 would otherwise do to every one of them).
        var titlePatterns = SoMeTitleExclusions.Parse(gateSettings?.ExcludedSessionTitlePatterns);
        if (titlePatterns.Count > 0)
        {
            var excludedByTitle = (await _db.Sessions
                    .Where(s => s.EventId == eventId)
                    .Select(s => new { s.Id, s.Title })
                    .ToListAsync(ct))
                // ⚠️ Matched in memory: the wildcard is his syntax, not SQL's, and translating it
                // to LIKE would quietly change what `_` and `%` in a real title mean.
                .Where(s => SoMeTitleExclusions.IsExcluded(s.Title, titlePatterns))
                .Select(s => s.Id)
                .ToList();

            if (excludedByTitle.Count > 0)
            {
                _log?.LogInformation(
                    "SoMe planner: {Count} session(s) excluded by title filter ({Patterns}).",
                    excludedByTitle.Count, string.Join(" | ", titlePatterns));
                testSessionIds.UnionWith(excludedByTitle);
            }
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
        var trackNewestSession = (await _db.Sessions
                .Where(s => s.EventId == eventId && !s.IsServiceSession && !s.IsTestData
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

        var round2 = eventStartUtc.AddMonths(-1);

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

            return new SoMeSubject(
                SoMeTemplateKind.SpeakerTracks, $"track:{t}",
                Times(cadence, SoMeTemplateKind.SpeakerTracks),
                EarliestUtc: round1,
                EarliestByOccurrence: new Dictionary<int, DateTimeOffset>
                {
                    [1] = round1,
                    // Guard the degenerate case: a track that settles inside the final month must
                    // not have round 2 scheduled BEFORE round 1.
                    [2] = round2 > round1 ? round2 : round1,
                });
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
        var sessionTimes = Times(cadence, SoMeTemplateKind.Session);

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

        foreach (var type in new[]
                 {
                     SessionType.Keynote, SessionType.MasterClass,
                     SessionType.TechnicalSession, SessionType.PanelDiscussion,
                 })
        {
            // 🔒 A disabled type (0) stays disabled for every kind — his on/off switch (§842.2)
            // outranks a per-kind minimum, or turning Type 2 off would leave keynotes posting.
            var times = type == SessionType.Keynote && sessionTimes > 0
                ? Math.Max(sessionTimes, 2)
                : sessionTimes;

            var ids = (await _db.Sessions
                    .Where(s => s.EventId == eventId && !s.IsServiceSession && s.Type == type)
                    .OrderBy(s => s.Id)
                    .Select(s => s.Id)
                    .ToListAsync(ct))
                // §905 — no announcement for a session whose whole line-up is test accounts.
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
                EarliestUtc: sessionGraphicReady.TryGetValue(id, out var ready) ? ready : null,
                // 🔒 The session's OWN graphic date is the prompt signal — the speaker gate is a
                // type-wide floor and must not hurry every session at once (§851).
                PromptFromUtc: sessionGraphicReady.TryGetValue(id, out var sessionReady)
                    ? sessionReady : null,
                // §928 — master classes only, round 1 only. Null everywhere else, so every other
                // session type behaves exactly as it did.
                EarliestByOccurrence: type == SessionType.MasterClass && masterClassWindow is { } w
                    ? new Dictionary<int, DateTimeOffset> { [1] = w }
                    : null)));
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
        if (sessionTimes > 0)
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

            // Round 2 is a WINDOW, not a date: "14–21 days before the event". The window OPENS at
            // 21 days out and the planner places inside it; 14 days is where it must not slip past,
            // which the ordinary forward search respects because the event start is its ceiling.
            var sponsorSessionRound2 = eventStartUtc.AddDays(-21);

            subjects.AddRange(sponsorSessions.Select(id => new SoMeSubject(
                SoMeTemplateKind.Session,
                SoMeSponsorSessionKey.For(id),
                Math.Max(sessionTimes, 2),
                // 🔑 §920 — NO SPEAKER GATE, same as any other session. Its readiness is its OWN
                // speaker being assigned, which the query above already requires — a CfS deadline
                // has nothing to do with a sponsor naming someone from their own company.
                EarliestUtc: null,
                // 🔑 §843.6 — the sponsor naming their speaker IS an individual arrival, so round 1
                // goes PROMPTLY rather than being spread across the campaign.
                PromptFromUtc: now,
                EarliestByOccurrence: new Dictionary<int, DateTimeOffset>
                {
                    [2] = sponsorSessionRound2 > now ? sponsorSessionRound2 : now,
                })));
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
        subjects.AddRange(tiers.Select(t => new SoMeSubject(
            SoMeTemplateKind.SponsorCategory, $"tier:{t}", Times(cadence, SoMeTemplateKind.SponsorCategory))));

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
        var graphicReady = await _db.GraphicAssets
            .Where(g => g.EventId == eventId
                        && g.Type == Domain.GraphicAssetType.Sponsor
                        && g.SponsorCompanyId != null)
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

        subjects.AddRange(plannable.Select(s => new SoMeSubject(
            SoMeTemplateKind.Sponsor,
            $"sponsor:{s.CompanyId}",
            Times(cadence, SoMeTemplateKind.Sponsor),
            EarliestUtc: graphicReady[s.CompanyId],
            // §851 — a sponsor becoming ready is an INDIVIDUAL arrival, so it is announced promptly
            // (§843.6) rather than spread across the window.
            PromptFromUtc: graphicReady[s.CompanyId])));

        // 🔒 §842.2 — a DISABLED type contributes no subjects, so it plans nothing new. It does NOT
        // remove what that type has already produced: the scheduler adds and never curates
        // (§824.21a), and deleting posts he may have approved or edited would be exactly the loss
        // that rule exists to prevent.
        return subjects.Where(s => s.Occurrences > 0).ToList();
    }

    /// <summary>
    /// §842.2 — how many times this type is announced: his setting when he has one, else the shipped
    /// §824.1 default. A disabled type returns 0, which drops it from the plan entirely.
    /// </summary>
    private static int Times(IReadOnlyList<SoMeCadence> cadence, SoMeTemplateKind kind)
    {
        var c = cadence.FirstOrDefault(x => x.Kind == kind);
        if (c is null) return SoMeCadenceService.DefaultOccurrences(kind);
        return c.Enabled ? c.Occurrences : 0;
    }

    /// <summary>
    /// The later of two instants — used wherever a configured date has to be clamped to "not in the
    /// past", because a window whose Monday has already gone by is simply "now".
    /// </summary>
    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

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

        var masterClassKeys = (await _db.Sessions
                .Where(s => s.EventId == eventId
                            && s.Type == SessionType.MasterClass
                            && !s.IsServiceSession)
                .Select(s => s.Id)
                .ToListAsync(ct))
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
