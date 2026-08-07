using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §925.2 — A QUIET TRACK IS NOT NECESSARILY A FINISHED ONE.
/// </summary>
/// <remarks>
/// <para>🔴 <b>§925.1, measured on PROD the day §925 shipped.</b> All eight tracks scored as SETTLED,
/// because each held only its one or two confirmed master classes from 24–26 June. "Nothing new for
/// six weeks" was read as *the line-up has finished* when it meant *the intake has not started* — and
/// from inside a single track those two are indistinguishable.</para>
///
/// <para>🔑 The missing fact is EDITION-WIDE. These tests pin both halves of the correction: the CfS
/// close date as a floor under the settle base, and the edition's newest session alongside the
/// track's own — because a track cannot tell on its own whether the import is still running.</para>
///
/// <para>🔒 Every fixture here deliberately leaves <c>SpeakerAnnouncementFrom</c> NULL. That date is
/// what was really protecting the campaign (§925.1) and it would mask the signal under test; the
/// point of the § is that the rule must hold without it.</para>
/// </remarks>
public sealed class SoMeTrackSettleSignalTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

    /// <summary>His real CfS deadline for ELDK27.</summary>
    private static readonly DateOnly CfsCloses = new(2026, 8, 31);

    /// <summary>The confirmed master classes arrived here — before the CfS closed.</summary>
    private static readonly DateTimeOffset ArrivedInJune =
        new(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 🔴 THE MEASURED DEFECT, PINNED: eight quiet tracks must not read as eight finished ones.
    /// </summary>
    /// <remarks>
    /// Before §925.2 every one of these tracks was announceable immediately — its newest session was
    /// six weeks old, so §925's quiet period had long since elapsed. Each would have gone out naming
    /// one or two speakers out of an expected hundred.
    /// </remarks>
    [Fact]
    public async Task A_track_holding_only_pre_CfS_sessions_is_not_settled_while_the_CfS_is_open()
    {
        using var db = NewDb();
        await SeedAsync(db, cfsCloses: CfsCloses);
        await AddTrackAsync(db, "AI", ArrivedInJune);
        await AddTrackAsync(db, "Security", ArrivedInJune);

        await Svc(db).RunAsync(EventId);

        // The CfS closes 31 Aug; nothing counts as settled until a quiet period after that.
        var earliestHonest = new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);

        foreach (var p in await Round1Async(db))
        {
            Assert.True(
                p.ScheduledAtUtc >= earliestHonest,
                $"{p.SubjectKey} was announced {p.ScheduledAtUtc:u} — that is the §925.1 defect: a "
                + "track that had not yet RECEIVED its sessions read as one that had finished "
                + "receiving them.");
        }
    }

    /// <summary>
    /// 🔑 NO TRACK CAN KNOW THIS ALONE — a quiet track waits while the EDITION is still filling.
    /// </summary>
    /// <remarks>
    /// The CfS date is deliberately in the PAST here, so it cannot be what holds the quiet track
    /// back. What holds it back is the other track still receiving sessions: whether the intake has
    /// landed is a property of the whole import, and "my own corner has gone quiet" is not evidence
    /// about it. Without the edition-wide term the quiet track is announceable today.
    /// </remarks>
    [Fact]
    public async Task A_quiet_track_waits_while_another_track_is_still_receiving_sessions()
    {
        using var db = NewDb();
        await SeedAsync(db, cfsCloses: new DateOnly(2026, 7, 1));   // long past — cannot bind
        await AddTrackAsync(db, "Quiet", ArrivedInJune);
        await AddTrackAsync(db, "Filling", Now);                     // still arriving today

        await Svc(db).RunAsync(EventId);

        // The edition's newest session is TODAY, so nothing is settled for a quiet period yet.
        var settleEnds = Now.AddDays(7);

        var quiet = (await Round1Async(db)).Single(p => p.SubjectKey == "track:Quiet");
        Assert.True(
            quiet.ScheduledAtUtc >= settleEnds,
            $"the quiet track was announced {quiet.ScheduledAtUtc:u}, while the edition was still "
            + "receiving sessions — its own silence was treated as evidence about the whole import");
    }

    /// <summary>
    /// ✅ ONCE THE INTAKE HAS LANDED, THE DATA GOVERNS AGAIN and the date stops mattering.
    /// </summary>
    /// <remarks>
    /// This is §925's whole point, preserved: the CfS date is a floor under the signal, not a
    /// replacement for it. If the sync slips a week the announcements slip with it, with nobody
    /// editing anything — which a hand-set announcement date could never do.
    /// </remarks>
    [Fact]
    public async Task After_the_intake_lands_readiness_follows_the_sessions_not_the_date()
    {
        using var db = NewDb();
        await SeedAsync(db, cfsCloses: CfsCloses);

        // The CfS wave landed LATE — a fortnight after the deadline.
        var waveLanded = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);
        await AddTrackAsync(db, "AI", ArrivedInJune);
        await AddTrackAsync(db, "AI", waveLanded);

        await Svc(db).RunAsync(EventId);

        var post = (await Round1Async(db)).Single(p => p.SubjectKey == "track:AI");

        // Not the CfS date + settle (7 Sep) — the ARRIVAL + settle (21 Sep). The slip carried.
        Assert.True(
            post.ScheduledAtUtc >= waveLanded.AddDays(7),
            $"announced {post.ScheduledAtUtc:u} — the settle period did not follow the late arrival");
    }

    /// <summary>
    /// 🔒 §925'S PER-TRACK BEHAVIOUR SURVIVES: a straggler delays ITS track, not every track.
    /// </summary>
    /// <remarks>
    /// The correction adds a floor and an edition-wide term; it must not flatten the campaign into
    /// "every track becomes ready on the same day", which would throw away the reason §925 exists.
    /// </remarks>
    [Fact]
    public async Task A_late_addition_delays_its_own_track_and_not_the_others()
    {
        using var db = NewDb();
        await SeedAsync(db, cfsCloses: CfsCloses);

        var wave = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await AddTrackAsync(db, "Done", wave);
        await AddTrackAsync(db, "Straggling", wave);
        // One track keeps growing well after the wave.
        await AddTrackAsync(db, "Straggling", new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));

        await Svc(db).RunAsync(EventId);

        var round1 = await Round1Async(db);
        var done = round1.Single(p => p.SubjectKey == "track:Done");
        var straggling = round1.Single(p => p.SubjectKey == "track:Straggling");

        Assert.True(
            straggling.ScheduledAtUtc > done.ScheduledAtUtc,
            $"the straggling track ({straggling.ScheduledAtUtc:u}) should wait longer than the "
            + $"finished one ({done.ScheduledAtUtc:u}) — the per-track signal was flattened away");
    }

    /// <summary>
    /// ⚠️ NO CfS DATE ⇒ §925'S BEHAVIOUR, UNCHANGED — including its blind spot, stated honestly.
    /// </summary>
    /// <remarks>
    /// An edition that has not told the hub when its Call for Speakers closes cannot have the intake
    /// modelled for it, so the signal falls back to session arrivals alone. Pinned rather than left
    /// implicit: this is the exact configuration in which §925.1's misreading is still possible, and
    /// the settings page says so where the field is empty.
    /// </remarks>
    [Fact]
    public async Task With_no_CfS_date_the_signal_falls_back_to_session_arrivals_alone()
    {
        using var db = NewDb();
        await SeedAsync(db, cfsCloses: null);
        await AddTrackAsync(db, "AI", ArrivedInJune);

        await Svc(db).RunAsync(EventId);

        var post = (await Round1Async(db)).Single(p => p.SubjectKey == "track:AI");

        // Quiet since June ⇒ settled long ago ⇒ schedulable from now. §925, verbatim.
        Assert.True(
            post.ScheduledAtUtc < new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero),
            $"announced {post.ScheduledAtUtc:u} — with no CfS date nothing should be holding it back");
    }

    // ---------------------------------------------------------------------------------------

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"some-settle-{Guid.NewGuid():N}").Options);

    private static SoMeScheduleService Svc(CommunityHubDbContext db)
    {
        var clock = new FixedClock(Now);
        return new SoMeScheduleService(
            db, new SoMeTemplateService(db, clock), new SoMeVariableResolver(db), clock);
    }

    private static async Task SeedAsync(CommunityHubDbContext db, DateOnly? cfsCloses)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            VenueName = "Bella Center", StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.SoMeSettings.Add(new SoMeSettings
        {
            EventId = EventId, Enabled = true,
            EventSystemUrl = "https://eldk27.expertslive.dk",
            EventTags = "#ELDK27 #ExpertsLiveDK",
            OrganizerCredits = "Organizer One | Organizer Two",
            // 🔒 NULL on purpose — see the class remarks. The rule must hold without the floor that
            // was really doing the work.
            SpeakerAnnouncementFrom = null,
            CallForSpeakersClosesOn = cfsCloses,
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddTrackAsync(
        CommunityHubDbContext db, string track, DateTimeOffset arrivedAt)
    {
        db.Sessions.Add(new Session
        {
            EventId = EventId,
            SessionizeId = $"{track}-{Guid.NewGuid():N}",
            Title = $"{track} session",
            Track = track,
            Type = SessionType.TechnicalSession,
            CreatedAt = arrivedAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<List<SoMePost>> Round1Async(CommunityHubDbContext db) =>
        await db.SoMePosts
            .Where(p => p.TemplateKind == SoMeTemplateKind.SpeakerTracks && p.Occurrence == 1)
            .ToListAsync();
}
