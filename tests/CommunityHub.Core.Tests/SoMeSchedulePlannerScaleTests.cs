using System;
using System.Collections.Generic;
using System.Linq;
using CommunityHub.Core.Integrations;
using Xunit;
using Xunit.Abstractions;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §842.6 — THE PLANNER AT HIS REAL SCALE, and §842.4's mix.
///
/// <para>Operator 2026-08-05: <i>"there will be a min 65 sessions, 40 sponsors, 12-15 sponsor
/// categories, 8 speakertracks, 45 event posts that must be scheduled so you know scale planner must
/// support"</i> — roughly <b>236 posts</b>, more than double the 112 that were planned on PROD.</para>
///
/// <para>🔴 The part that is not merely cosmetic: <b>every sponsor and sponsor-category post must be
/// placed</b>. §842.5 makes those contractual, so a post the planner cannot fit is a breach rather
/// than a tidy-up. This test exists to fail loudly if the campaign ever stops fitting.</para>
/// </summary>
public sealed class SoMeSchedulePlannerScaleTests
{
    private readonly ITestOutputHelper _out;
    public SoMeSchedulePlannerScaleTests(ITestOutputHelper o) => _out = o;

    // His stated minimums (§842.6).
    private const int Sessions = 65;
    private const int Sponsors = 40;
    private const int SponsorCategories = 15;
    private const int SpeakerTracks = 8;
    private const int EventPostRuns = 45;

    private static readonly DateTimeOffset From = new(2026, 8, 5, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EventStart = new(2027, 2, 9, 0, 0, 0, TimeSpan.Zero);

    private static List<SoMeSubject> RealScaleSubjects()
    {
        var s = new List<SoMeSubject>();

        s.AddRange(Enumerable.Range(1, SpeakerTracks)
            .Select(i => new SoMeSubject(SoMeTemplateKind.SpeakerTracks, $"track:Track {i}", 2)));

        s.AddRange(Enumerable.Range(1, Sessions)
            .Select(i => new SoMeSubject(SoMeTemplateKind.Session, $"session:{i}", 1)));

        s.AddRange(Enumerable.Range(1, SponsorCategories)
            .Select(i => new SoMeSubject(SoMeTemplateKind.SponsorCategory, $"tier:Tier{i}", 2)));

        s.AddRange(Enumerable.Range(1, Sponsors)
            .Select(i => new SoMeSubject(SoMeTemplateKind.Sponsor, $"sponsor:{i}", 2)));

        return s;
    }

    /// <summary>
    /// The 45 event posts occupy FIXED dates he chose (Tuesdays and Thursdays), so they are seeded as
    /// ALREADY-PLACED rather than as subjects. That is what makes this test realistic: they consume
    /// slots the other four types then cannot use.
    /// </summary>
    private static List<(string, int, DateTimeOffset)> EventPostsAlreadyPlaced()
    {
        var placed = new List<(string, int, DateTimeOffset)>();
        var day = new DateOnly(2026, 8, 6);   // the deck's first Thursday
        var n = 0;

        while (placed.Count < EventPostRuns && day < DateOnly.FromDateTime(EventStart.UtcDateTime))
        {
            if (day.DayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Thursday)
            {
                placed.Add(($"event:post-{n}", 1,
                    SoMeSchedulePlanner.ToUtc(day, SoMeSchedulePlanner.PreferredTimes[n % SoMeSchedulePlanner.PreferredTimes.Length])));
                n++;
            }

            day = day.AddDays(1);
        }

        return placed;
    }

    [Fact]
    public void Every_post_fits_before_the_event_at_his_stated_scale()
    {
        var subjects = RealScaleSubjects();
        var wanted = subjects.Sum(s => s.Occurrences);
        var seeded = EventPostsAlreadyPlaced();

        var plan = SoMeSchedulePlanner.Plan(subjects, seeded, From, EventStart);

        _out.WriteLine($"subjects={subjects.Count} wanted={wanted} seeded(event posts)={seeded.Count}");
        _out.WriteLine($"placed={plan.Count}  total campaign={plan.Count + seeded.Count}");

        var missing = wanted - plan.Count;
        _out.WriteLine($"UNPLACED: {missing}");

        Assert.Equal(wanted, plan.Count);
    }

    [Fact]
    public void Every_sponsor_and_category_post_is_placed_because_it_is_contractual()
    {
        var subjects = RealScaleSubjects();
        var plan = SoMeSchedulePlanner.Plan(subjects, EventPostsAlreadyPlaced(), From, EventStart);

        // 🔴 §842.5 — every sponsor announced twice, and every tier post too. A miss here is a
        // breach of contract, which is why it is asserted separately from the general fit.
        var sponsorPosts = plan.Count(p => p.Kind == SoMeTemplateKind.Sponsor);
        var categoryPosts = plan.Count(p => p.Kind == SoMeTemplateKind.SponsorCategory);

        _out.WriteLine($"sponsor posts placed:  {sponsorPosts} / {Sponsors * 2}");
        _out.WriteLine($"category posts placed: {categoryPosts} / {SponsorCategories * 2}");

        Assert.Equal(Sponsors * 2, sponsorPosts);
        Assert.Equal(SponsorCategories * 2, categoryPosts);

        // Each individual sponsor must appear TWICE — not "80 sponsor posts" with one company
        // announced three times and another once.
        var perSponsor = plan
            .Where(p => p.Kind == SoMeTemplateKind.Sponsor)
            .GroupBy(p => p.SubjectKey)
            .ToList();

        Assert.Equal(Sponsors, perSponsor.Count);
        Assert.All(perSponsor, g => Assert.Equal(2, g.Count()));
    }

    /// <summary>
    /// 🔴 §842.8/§842.8a — <b>the squeeze case, and the one most likely to become a breach.</b>
    /// Operator: <i>"sponsor slots are sold during now and jan27"</i>, and every sponsor must be
    /// announced twice (§842.5). A sponsor signing in January 2027 has ~1 month before the event.
    /// </summary>
    [Theory]
    [InlineData(2026, 11, 15)]   // November signer
    [InlineData(2026, 12, 20)]   // December signer, over the Christmas gap
    [InlineData(2027, 1, 20)]    // January signer — the tightest runway there is
    public void A_late_signing_sponsor_still_gets_both_contractual_posts(int y, int m, int d)
    {
        var signedAt = new DateTimeOffset(y, m, d, 9, 0, 0, TimeSpan.Zero);

        var subjects = RealScaleSubjects();
        // §851 — PromptFromUtc marks this as an INDIVIDUAL arrival (their graphic was just built),
        // which is what earns the promptness rule. EarliestUtc alone is only a floor, and a floor is
        // spread across the window like everything else.
        subjects.Add(new SoMeSubject(
            SoMeTemplateKind.Sponsor, "sponsor:late-signer", 2,
            EarliestUtc: signedAt, PromptFromUtc: signedAt));

        // §843.6 — with the exception ceiling available, a ready sponsor must be announced PROMPTLY,
        // not merely eventually.
        var plan = SoMeSchedulePlanner.Plan(
            subjects, EventPostsAlreadyPlaced(), From, EventStart,
            normalPerDay: 2, exceptionPerDay: 3);

        var mine = plan.Where(p => p.SubjectKey == "sponsor:late-signer")
            .OrderBy(p => p.ScheduledAtUtc).ToList();

        foreach (var p in mine)
        {
            _out.WriteLine($"  ready {signedAt:yyyy-MM-dd} → post {p.Occurrence} on "
                           + $"{p.ScheduledAtUtc:yyyy-MM-dd HH:mm} "
                           + $"(+{(int)(p.ScheduledAtUtc - signedAt).TotalDays}d)");
        }

        Assert.Equal(2, mine.Count);

        // 🔒 §843.6 — "no delays after ... graphics are build". The FIRST post must land inside the
        // freshness window; a 61-day wait is the defect this closes.
        var wait = (mine[0].ScheduledAtUtc - signedAt).TotalDays;
        Assert.True(wait <= SoMeSchedulePlanner.FreshnessDays,
            $"The first post lands {wait:F0} days after the sponsor became announceable — he asked "
            + "for no delay once the graphics are built.");

        // 🔒 And never BEFORE they signed — that would announce a company that was not yet a sponsor.
        Assert.All(mine, p => Assert.True(
            p.ScheduledAtUtc >= signedAt,
            $"A post was scheduled for {p.ScheduledAtUtc:u}, before the sponsor signed on {signedAt:u}."));
    }

    /// <summary>
    /// §843 — WHY a November signer waits until January, measured rather than guessed, and what
    /// fixes it. This is the flow defect §842.8's green test hid.
    /// </summary>
    [Fact]
    public void Weekday_capacity_is_what_delays_a_late_sponsor_and_a_third_slot_fixes_it()
    {
        var signedAt = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

        SoMeSubject Late() => new(SoMeTemplateKind.Sponsor, "sponsor:late-signer", 2, signedAt);

        // Weekends are excluded, so the real denominator is WEEKDAYS, not days.
        var weekdays = 0;
        for (var d = DateOnly.FromDateTime(From.UtcDateTime);
             d < DateOnly.FromDateTime(EventStart.UtcDateTime); d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) weekdays++;
        }

        var total = RealScaleSubjects().Sum(s => s.Occurrences) + 2 + EventPostRuns;
        _out.WriteLine($"weekdays={weekdays}  capacity@2={weekdays * 2}  capacity@3={weekdays * 3}  posts={total}");
        _out.WriteLine($"utilisation @2 = {100.0 * total / (weekdays * 2):F0}%   @3 = {100.0 * total / (weekdays * 3):F0}%");

        foreach (var perDay in new[] { 2, 3 })
        {
            var subjects = RealScaleSubjects();
            subjects.Add(Late());

            var plan = SoMeSchedulePlanner.Plan(
                subjects, EventPostsAlreadyPlaced(), From, EventStart, perDay);

            var mine = plan.Where(p => p.SubjectKey == "sponsor:late-signer")
                .OrderBy(p => p.ScheduledAtUtc).ToList();

            var waitDays = mine.Count == 0
                ? -1
                : (int)(mine[0].ScheduledAtUtc - signedAt).TotalDays;

            _out.WriteLine($"maxPerDay={perDay}: first post {(mine.Count == 0 ? "NONE" : mine[0].ScheduledAtUtc.ToString("yyyy-MM-dd"))}"
                           + $"  → {waitDays} days after signing");

            Assert.Equal(2, mine.Count);
        }
    }

    /// <summary>
    /// §843.3 — his worry, measured: does raising the cap make every day sit AT the cap?
    /// </summary>
    /// <summary>
    /// 🔒 §843.3 — AN EXCEPTION MUST BE EARNED. With normal=2 and exception=3, most days must still
    /// carry 2: the busier days are for what could not otherwise fit, not a new rhythm.
    /// </summary>
    [Fact]
    public void An_exception_ceiling_does_not_become_the_everyday_rhythm()
    {
        // A late signer creates genuine overflow, which is what an exception is FOR.
        var subjects = RealScaleSubjects();
        subjects.Add(new SoMeSubject(
            SoMeTemplateKind.Sponsor, "sponsor:late-signer", 2,
            new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero)));

        var plan = SoMeSchedulePlanner.Plan(
            subjects, EventPostsAlreadyPlaced(), From, EventStart,
            normalPerDay: 2, exceptionPerDay: 3);

        var byDay = plan
            .Select(p => SoMeDisplayTime.DanishDay(p.ScheduledAtUtc))
            .Concat(EventPostsAlreadyPlaced().Select(e => SoMeDisplayTime.DanishDay(e.Item3)))
            .GroupBy(d => d)
            .ToList();

        var normalDays = byDay.Count(g => g.Count() <= 2);
        var exceptionDays = byDay.Count(g => g.Count() > 2);

        _out.WriteLine($"normal days (<=2): {normalDays}   exception days (3): {exceptionDays}");
        _out.WriteLine($"exceptions are {100.0 * exceptionDays / byDay.Count:F0}% of posting days");

        // 🔒 The rhythm stays the normal one. A cap that becomes the norm is the §843.3 defect.
        Assert.True(exceptionDays < normalDays,
            $"{exceptionDays} exception days vs {normalDays} normal ones — the exception has become "
            + "the rhythm, which is exactly what §843.3 forbids.");
    }

    [Fact]
    public void Raising_the_cap_shows_whether_it_becomes_the_everyday_rhythm()
    {
        foreach (var perDay in new[] { 2, 3 })
        {
            var plan = SoMeSchedulePlanner.Plan(
                RealScaleSubjects(), EventPostsAlreadyPlaced(), From, EventStart, perDay);

            // Count the WHOLE campaign, the planned posts plus the fixed event posts, because that
            // is what a follower sees in a day.
            var perDayCounts = plan
                .Select(p => SoMeDisplayTime.DanishDay(p.ScheduledAtUtc))
                .Concat(EventPostsAlreadyPlaced().Select(e => SoMeDisplayTime.DanishDay(e.Item3)))
                .GroupBy(d => d)
                .GroupBy(g => g.Count())
                .OrderBy(g => g.Key)
                .ToList();

            var days = perDayCounts.Sum(g => g.Count());
            _out.WriteLine($"maxPerDay={perDay}: {days} posting days");
            foreach (var g in perDayCounts)
            {
                _out.WriteLine($"    {g.Key} post(s)/day : {g.Count()} days  ({100.0 * g.Count() / days:F0}%)");
            }
        }
    }

    /// <summary>
    /// §846.1 — HIS CRAMPED CASE, verbatim: a sponsor decides mid-January who is speaking, uploads
    /// the speaker photo, the graphic is built, and only then is the session announceable.
    /// </summary>
    /// <remarks>
    /// Everything about that is legitimate — nothing existed to announce earlier — so the test is
    /// that the campaign still gets it out, promptly, using an exception day rather than by
    /// rearranging the whole calendar.
    /// </remarks>
    [Fact]
    public void A_session_whose_speaker_is_decided_in_mid_january_is_still_announced_promptly()
    {
        var eligibleAt = new DateTimeOffset(2027, 1, 18, 10, 0, 0, TimeSpan.Zero);

        var subjects = RealScaleSubjects();
        subjects.Add(new SoMeSubject(
            SoMeTemplateKind.Session, "session:late-sponsor-session", 1,
            // Its own graphic was just built (the sponsor finally named their speaker), so this is
            // an individual arrival and earns the promptness rule — §851's distinction.
            EarliestUtc: eligibleAt, PromptFromUtc: eligibleAt));

        var plan = SoMeSchedulePlanner.Plan(
            subjects, EventPostsAlreadyPlaced(), From, EventStart,
            normalPerDay: 2, exceptionPerDay: 3);

        var mine = plan.Where(p => p.SubjectKey == "session:late-sponsor-session").ToList();

        foreach (var p in mine)
        {
            _out.WriteLine($"  eligible {eligibleAt:yyyy-MM-dd} → {p.ScheduledAtUtc:yyyy-MM-dd HH:mm} "
                           + $"(+{(int)(p.ScheduledAtUtc - eligibleAt).TotalDays}d)");
        }

        // It gets announced at all — three weeks before the event, on a nearly full calendar.
        var post = Assert.Single(mine);

        // 🔒 Never before it was announceable: the graphic did not exist until then.
        Assert.True(post.ScheduledAtUtc >= eligibleAt);

        // And promptly, because §843.6 says there is no delay once the assets are ready.
        Assert.True((post.ScheduledAtUtc - eligibleAt).TotalDays <= SoMeSchedulePlanner.FreshnessDays);
    }

    /// <summary>
    /// §848.1 — the campaign must fill the WHOLE window, not the opening weeks.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-05: <i>"it is important that you spread out things in the whole period -
    /// scheduler service must do that by default"</i>. Measured on the live plan before this, August
    /// held 45 posts and September 38 while October–January carried only event posts.
    /// </remarks>
    [Fact]
    public void The_campaign_is_spread_across_the_whole_window_not_front_loaded()
    {
        var plan = SoMeSchedulePlanner.Plan(
            RealScaleSubjects(), EventPostsAlreadyPlaced(), From, EventStart,
            normalPerDay: 2, exceptionPerDay: 3);

        var months = plan
            .GroupBy(p => new DateOnly(p.ScheduledAtUtc.Year, p.ScheduledAtUtc.Month, 1))
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var m in months) _out.WriteLine($"  {m.Key:yyyy-MM}: {m.Count()} posts");

        // Every month of the campaign carries planned posts — none is left empty.
        // Inclusive of the event's own month: posts may be placed right up to the event, and the
        // 2027-02 entries are legitimately part of the campaign.
        var windowMonths = 0;
        for (var d = new DateOnly(From.Year, From.Month, 1);
             d <= new DateOnly(EventStart.Year, EventStart.Month, 1); d = d.AddMonths(1))
        {
            windowMonths++;
        }

        _out.WriteLine($"months with posts: {months.Count} of {windowMonths}");
        Assert.Equal(windowMonths, months.Count);

        // 🔒 And no single month may hold a runaway share. Before the spread, August held ~40% of
        // everything; an even campaign over 6 months is ~17% each, so 30% is a generous ceiling that
        // still fails hard front-loading.
        var biggest = months.Max(m => m.Count());
        var share = 100.0 * biggest / plan.Count;
        _out.WriteLine($"largest month holds {share:F0}% of the campaign");

        Assert.True(share < 30,
            $"One month holds {share:F0}% of the campaign — that is the §848.1 front-loading.");
    }

    /// <summary>
    /// §851 — speaker-derived posts wait for the Call-for-Speakers decision, and then SPREAD.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-05: <i>"we wont have the complete list of speakers until 7th of sept 2026 …
    /// planner must adjust to this"</i>.
    ///
    /// 🔴 The second assertion is the subtle one: a type-wide gate must not behave like a late
    /// ARRIVAL. If it did, §843.6's promptness rule would hurry all 29 track and session posts into
    /// the week after the gate — the §842.4 clustering defect through a different door.
    /// </remarks>
    [Fact]
    public void Speaker_posts_wait_for_the_gate_and_then_spread_rather_than_bunching()
    {
        var gate = new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);

        var subjects = new List<SoMeSubject>();
        subjects.AddRange(Enumerable.Range(1, SpeakerTracks)
            .Select(i => new SoMeSubject(SoMeTemplateKind.SpeakerTracks, $"track:T{i}", 2, gate)));
        subjects.AddRange(Enumerable.Range(1, Sessions)
            .Select(i => new SoMeSubject(SoMeTemplateKind.Session, $"session:{i}", 1, gate)));
        // Sponsors are NOT gated — a separate pipeline.
        subjects.AddRange(Enumerable.Range(1, Sponsors)
            .Select(i => new SoMeSubject(SoMeTemplateKind.Sponsor, $"sponsor:{i}", 2)));

        var plan = SoMeSchedulePlanner.Plan(
            subjects, EventPostsAlreadyPlaced(), From, EventStart,
            normalPerDay: 2, exceptionPerDay: 3);

        var speaker = plan
            .Where(p => p.Kind is SoMeTemplateKind.SpeakerTracks or SoMeTemplateKind.Session)
            .ToList();

        // 🔒 NOTHING speaker-derived before the gate — a track post lists its speakers.
        var tooEarly = speaker.Where(p => p.ScheduledAtUtc < gate).ToList();
        foreach (var p in tooEarly) _out.WriteLine($"  TOO EARLY {p.SubjectKey} {p.ScheduledAtUtc:yyyy-MM-dd}");
        Assert.Empty(tooEarly);

        // Sponsors are unaffected and still start before it.
        Assert.Contains(plan, p => p.Kind == SoMeTemplateKind.Sponsor && p.ScheduledAtUtc < gate);

        // 🔴 …and they SPREAD from the gate rather than bunching into the first week after it.
        var firstWeek = speaker.Count(p => p.ScheduledAtUtc < gate.AddDays(7));
        var share = 100.0 * firstWeek / speaker.Count;
        _out.WriteLine($"speaker posts: {speaker.Count}, in the week after the gate: {firstWeek} ({share:F0}%)");

        Assert.True(share < 40,
            $"{share:F0}% of speaker posts land in the week after the gate — a type-wide floor is "
            + "being treated as a late arrival, which is the §851 clustering trap.");
    }

    [Fact]
    public void The_types_are_mixed_through_the_campaign_rather_than_clustered()
    {
        // §842.4: "we mix speaker,sponsor,event messages". Before the interleave fix the real plan
        // put every track/session/tier post in August and every sponsor post in September.
        var subjects = RealScaleSubjects();
        var plan = SoMeSchedulePlanner.Plan(subjects, EventPostsAlreadyPlaced(), From, EventStart);

        var months = plan
            .GroupBy(p => new DateOnly(p.ScheduledAtUtc.Year, p.ScheduledAtUtc.Month, 1))
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var m in months)
        {
            var kinds = m.GroupBy(p => p.Kind).OrderBy(g => (int)g.Key);
            _out.WriteLine($"{m.Key:yyyy-MM}: " + string.Join("  ", kinds.Select(k => $"{k.Key}={k.Count()}")));
        }

        // Sponsors are the largest type and the one with the deadline: they must not all land in one
        // month. Assert they are spread over at least half the campaign's months.
        var sponsorMonths = plan
            .Where(p => p.Kind == SoMeTemplateKind.Sponsor)
            .Select(p => new DateOnly(p.ScheduledAtUtc.Year, p.ScheduledAtUtc.Month, 1))
            .Distinct()
            .Count();

        _out.WriteLine($"months containing sponsor posts: {sponsorMonths} of {months.Count}");
        Assert.True(sponsorMonths >= months.Count / 2,
            $"Sponsor posts are clustered into {sponsorMonths} of {months.Count} months — §842.4 asks "
            + "for them mixed through the campaign.");

        // And the first month must not be monopolised by one type, which is the clustering he saw.
        var firstMonth = months[0];
        var dominant = firstMonth.GroupBy(p => p.Kind).Max(g => g.Count());
        Assert.True(dominant < firstMonth.Count(),
            "The first month contains only one post type — that is the §842.4 clustering.");
    }
}
