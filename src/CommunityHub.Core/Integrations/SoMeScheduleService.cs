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

    public async Task<SoMeScheduleRunResult> RunAsync(int eventId, CancellationToken ct = default)
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

        var subjects = await CollectSubjectsAsync(eventId, ct);

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

        // §842.7/§843.3 — the everyday RHYTHM, and the ceiling reserved for what will not otherwise
        // fit. Both clamped to the number of preferred times: a further post would have to share a
        // minute with another or break his 08:00–16:00 rule, and silently doing either is worse than
        // refusing the extra slot.
        var slots = SoMeSchedulePlanner.PreferredTimes.Length;
        var normalPerDay = Math.Clamp(footer?.MaxPostsPerDay ?? 2, 1, slots);
        var exceptionPerDay = Math.Clamp(footer?.ExceptionPostsPerDay ?? normalPerDay, normalPerDay, slots);

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

        foreach (var p in plan)
        {
            var body = await ComposeAsync(eventId, p, editionValues, ct);

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
                AutoText = body,
                CreatedAt = now,
            });
        }

        // §834.4 — Type 5 alongside the other four, so "the engine autobuilds everything" is true
        // for all five types rather than four of five.
        var eventPostCount = await PlanEventPostsAsync(eventId, editionValues, now, eventStartUtc, ct);

        await _db.SaveChangesAsync(ct);

        var created = plan.Count + eventPostCount;

        var msg = created == 0
            ? $"Nothing new to schedule ({already.Count} post(s) already planned)."
            : $"{created} post(s) scheduled and HELD for approval.";
        if (eventPostCount > 0) msg += $" {eventPostCount} of them are your own event posts.";
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
                AutoText = await ComposeAsync(eventId, planned, editionValues, ct),
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
    private async Task<List<SoMeSubject>> CollectSubjectsAsync(int eventId, CancellationToken ct)
    {
        var subjects = new List<SoMeSubject>();

        // §842.2 — his per-type frequency, or the shipped §824.1 defaults where he has not set one.
        var cadence = await new SoMeCadenceService(_db).GetAllAsync(eventId, ct);

        // 🔴 §851 — SPEAKERS ARE NOT KNOWN UNTIL THE CALL FOR SPEAKERS IS DECIDED.
        //
        // Operator 2026-08-05: "call for speakers ends 31 aug 2026 and then we spend 1 week deciding
        // who is selected … we wont have the complete list of speakers until 7th of sept 2026".
        // A track post LISTS its speakers, so publishing one before the selection would announce a
        // line-up that does not exist. Gates Type 1 and Type 2 only.
        var speakerGateDate = await _db.SoMeSettings
            .Where(s => s.EventId == eventId)
            .Select(s => s.SpeakerAnnouncementFrom)
            .FirstOrDefaultAsync(ct);

        DateTimeOffset? speakerGate = speakerGateDate is { } d
            ? SoMeSchedulePlanner.ToUtc(d, SoMeSchedulePlanner.PreferredTimes[0])
            : null;

        // Combine the type gate with a subject's own readiness — the LATER of the two wins, because
        // both are "not before this" and neither excuses the other.
        DateTimeOffset? GatedBySpeakers(DateTimeOffset? subjectEarliest) =>
            speakerGate is null ? subjectEarliest
            : subjectEarliest is null ? speakerGate
            : (subjectEarliest > speakerGate ? subjectEarliest : speakerGate);

        // --- Type 1: one per TRACK, twice --------------------------------------------------
        var tracks = await _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession && s.Track != null && s.Track != "")
            .Select(s => s.Track!)
            .Distinct()
            .ToListAsync(ct);
        // §851 — a track post lists its speakers, so it waits for the CfS decision.
        subjects.AddRange(tracks.Select(t => new SoMeSubject(
            SoMeTemplateKind.SpeakerTracks, $"track:{t}", Times(cadence, SoMeTemplateKind.SpeakerTracks),
            EarliestUtc: GatedBySpeakers(null))));

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
        foreach (var type in new[] { SessionType.MasterClass, SessionType.TechnicalSession, SessionType.PanelDiscussion })
        {
            var ids = await _db.Sessions
                .Where(s => s.EventId == eventId && !s.IsServiceSession && s.Type == type)
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .ToListAsync(ct);

            subjects.AddRange(ids.Select(id => new SoMeSubject(
                SoMeTemplateKind.Session, $"session:{id}", Times(cadence, SoMeTemplateKind.Session),
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
                // §851 — and never before the speaker selection is complete, whichever is later.
                EarliestUtc: GatedBySpeakers(
                    sessionGraphicReady.TryGetValue(id, out var ready) ? ready : null),
                // 🔒 The session's OWN graphic date is the prompt signal — the speaker gate is a
                // type-wide floor and must not hurry every session at once (§851).
                PromptFromUtc: sessionGraphicReady.TryGetValue(id, out var sessionReady)
                    ? sessionReady : null)));
        }

        // --- Type 3: one per sponsor TIER that actually has sponsors, twice -------------------
        var tiers = await _db.SponsorInfos
            .Where(s => s.EventId == eventId)
            .Select(s => s.SponsorPackage)
            .Distinct()
            .ToListAsync(ct);
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
        var sponsorRows = await _db.SponsorInfos
            .Where(s => s.EventId == eventId && s.SponsorCompanyId != null && s.SponsorCompanyId != "")
            .Select(s => new { s.SponsorCompanyId, s.CreatedAt })
            .ToListAsync(ct);

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

    /// <summary>Render the edition's template for one planned post.</summary>
    private async Task<string> ComposeAsync(
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
        string? intro = null;
        if (p.Kind != SoMeTemplateKind.EventPost && _intro is { IsConfigured: true })
        {
            intro = await _intro.GenerateAsync(
                new SoMeIntroRequest(p.Kind, IntroTitle(p.Kind, id, values), IntroDetail(p.Kind, values)),
                ct);
        }
        values["IntroText"] = intro;

        var template = await _templates.BodyAsync(eventId, p.Kind, ct);
        return SoMeTemplateRenderer.Render(template, values);
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
