using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1178 — ONE exclusion rule, and it must answer the same way for the planner and the graphics.
///
/// <para>Operator 2026-09-12: <i>"the master class with the 4 organizers should be marked as test and
/// the graphics for security track is wrong as no test session speakers must be included"</i> ·
/// <i>"guard needed. no test sessions or test sponsor or excluded can exist in some planner"</i>.</para>
///
/// <para>🔴 <b>The defect these pin is a flag with readers and no writer.</b>
/// <c>Session.IsTestData</c> (§909) and <c>Session.ExcludeFromSoMeAnnouncements</c> (§1060(h)) were
/// both read by services and set by NOTHING — no UI, no service, no seed, no migration. Meanwhile the
/// button he actually presses says "Mark as TEST session" and writes <c>UsedForTesting</c>, which the
/// SoMe engine did not read. So the four organizers on the test Master Class stayed in the Security
/// track GIF no matter what he ticked.</para>
/// </summary>
public sealed class SoMeSubjectScopeTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"some-scope-{Guid.NewGuid():N}").Options);

    private static Participant Speaker(int id, bool isTest = false) => new()
    {
        Id = id,
        EventId = EventId,
        Email = $"speaker{id}@example.test",
        FullName = $"Speaker {id}",
        Role = ParticipantRole.Speaker,
        IsTestUser = isTest,
    };

    private static Session SessionWith(
        int id, string title, string? track = "Security",
        bool usedForTesting = false, bool isTestData = false, bool excludeFromSoMe = false,
        bool isService = false) => new()
    {
        Id = id,
        EventId = EventId,
        Title = title,
        Track = track,
        UsedForTesting = usedForTesting,
        IsTestData = isTestData,
        ExcludeFromSoMeAnnouncements = excludeFromSoMe,
        IsServiceSession = isService,
    };

    private static async Task SeedAsync(
        CommunityHubDbContext db, Session session, params int[] speakerIds)
    {
        db.Sessions.Add(session);
        foreach (var pid in speakerIds)
        {
            if (!await db.Participants.AnyAsync(p => p.Id == pid)) db.Participants.Add(Speaker(pid));
            db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = pid });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 🔴 THE REPORTED CASE. A session flagged with the button he actually has — "Mark as TEST
    /// session", which writes <c>UsedForTesting</c> — must leave the campaign.
    /// </summary>
    /// <remarks>
    /// ⚠️ This is the assertion that was impossible before §1178: the SoMe engine read
    /// <c>IsTestData</c> only, and nothing wrote it, so a test session was indistinguishable from a
    /// real one everywhere the campaign looked.
    /// </remarks>
    [Fact]
    public async Task A_session_marked_TEST_on_the_sessions_page_leaves_the_campaign()
    {
        using var db = NewDb();
        await SeedAsync(db, SessionWith(10, "Test Master Class", usedForTesting: true), 1, 2, 3, 4);

        var excluded = await new SoMeSubjectScope(db).ExcludedSessionsAsync(EventId);

        var row = Assert.Single(excluded);
        Assert.Equal(10, row.SessionId);
        Assert.Contains("UsedForTesting", row.Reason);
    }

    /// <summary>§1060(h)'s flag, which also had no writer until §1178 gave it a button.</summary>
    [Fact]
    public async Task A_session_excluded_from_announcements_leaves_the_campaign()
    {
        using var db = NewDb();
        await SeedAsync(db, SessionWith(11, "ELDK27 Welcome", excludeFromSoMe: true), 1);

        var excluded = await new SoMeSubjectScope(db).ExcludedSessionsAsync(EventId);

        Assert.Equal(11, Assert.Single(excluded).SessionId);
    }

    /// <summary>§909's flag still works — it is kept, not replaced.</summary>
    [Fact]
    public async Task The_original_IsTestData_flag_still_excludes()
    {
        using var db = NewDb();
        await SeedAsync(db, SessionWith(12, "Test Session", isTestData: true), 1);

        Assert.Equal(12, Assert.Single(await new SoMeSubjectScope(db).ExcludedSessionsAsync(EventId)).SessionId);
    }

    /// <summary>
    /// 🔒 A REAL session is not excluded — the failure that actually costs something.
    /// </summary>
    /// <remarks>
    /// ⚠️ Widening an exclusion silently DROPS content, and nobody notices an absence (§909). This is
    /// the assertion that keeps every rule here opt-in.
    /// </remarks>
    [Fact]
    public async Task A_real_session_is_never_excluded()
    {
        using var db = NewDb();
        await SeedAsync(db, SessionWith(13, "Zero Trust in Practice"), 1, 2);

        Assert.Empty(await new SoMeSubjectScope(db).ExcludedSessionsAsync(EventId));
    }

    /// <summary>
    /// §905's derived rule survives: every speaker a test account ⇒ the session is a fixture.
    /// </summary>
    [Fact]
    public async Task A_session_whose_every_speaker_is_a_test_user_is_excluded()
    {
        using var db = NewDb();
        db.Participants.Add(Speaker(20, isTest: true));
        db.Participants.Add(Speaker(21, isTest: true));
        await db.SaveChangesAsync();
        await SeedAsync(db, SessionWith(14, "Test Exhibitor Session"), 20, 21);

        Assert.Equal(14, Assert.Single(await new SoMeSubjectScope(db).ExcludedSessionsAsync(EventId)).SessionId);
    }

    /// <summary>
    /// 🔒 §905's null-tolerance, and the reason it is load-bearing: ONE real speaker keeps the session.
    /// </summary>
    /// <remarks>
    /// Measured on PROD 2026-08-06 for sponsors (2linkIT: 6 test contacts beside 6 real ones). The
    /// same shape applies to a session, and "has any test speaker" would delete a real talk.
    /// </remarks>
    [Fact]
    public async Task A_mixed_session_keeps_its_place_in_the_campaign()
    {
        using var db = NewDb();
        db.Participants.Add(Speaker(30, isTest: true));
        await db.SaveChangesAsync();
        await SeedAsync(db, SessionWith(15, "Identity Deep Dive"), 30, 31);

        Assert.Empty(await new SoMeSubjectScope(db).ExcludedSessionsAsync(EventId));
    }

    /// <summary>
    /// §927 — his own title filter is part of the same answer, so the graphics sweep honours it too.
    /// </summary>
    [Fact]
    public async Task A_title_pattern_he_set_excludes_the_session_for_graphics_as_well()
    {
        using var db = NewDb();
        await SeedAsync(db, SessionWith(16, "Ask the Experts — Identity"), 1);
        db.SoMeSettings.Add(new SoMeSettings
        {
            EventId = EventId,
            ExcludedSessionTitlePatterns = "ask the experts*",
        });
        await db.SaveChangesAsync();

        var row = Assert.Single(await new SoMeSubjectScope(db).ExcludedSessionsAsync(EventId));
        Assert.Equal(16, row.SessionId);
        Assert.Contains("title pattern", row.Reason);
    }

    /// <summary>
    /// ⚠️ A SERVICE session (break/lunch) is NOT in this set, deliberately — every caller filters it
    /// as "not a session at all", and folding it in would make the set mean two things.
    /// </summary>
    [Fact]
    public async Task A_service_session_is_not_reported_as_an_exclusion()
    {
        using var db = NewDb();
        await SeedAsync(db, SessionWith(17, "Lunch", track: null, isService: true));

        Assert.Empty(await new SoMeSubjectScope(db).ExcludedSessionsAsync(EventId));
    }

    /// <summary>
    /// The subject KEYS the guard works from: an excluded session and a test sponsor company.
    /// </summary>
    [Fact]
    public async Task Excluded_subject_keys_cover_sessions_and_test_sponsors()
    {
        using var db = NewDb();
        await SeedAsync(db, SessionWith(18, "Test Master Class", usedForTesting: true), 1);
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId,
            SponsorCompanyId = "99",
            CompanyName = "Test-Silver",
            IsTestData = true,
        });
        await db.SaveChangesAsync();

        var keys = await new SoMeSubjectScope(db).ExcludedSubjectKeysAsync(EventId);

        Assert.Contains("session:18", keys.Keys);
        Assert.Contains("sponsor:99", keys.Keys);
    }

    /// <summary>
    /// 🔑 A TIER key is never excluded, even when a test company sits in that tier — the tier is real
    /// and its content already leaves test companies out.
    /// </summary>
    [Fact]
    public async Task A_tier_is_not_excluded_because_one_test_company_is_in_it()
    {
        using var db = NewDb();
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId,
            SponsorCompanyId = "99",
            CompanyName = "Test-Silver",
            SponsorPackage = SponsorPackage.Silver,
            IsTestData = true,
        });
        await db.SaveChangesAsync();

        var keys = await new SoMeSubjectScope(db).ExcludedSubjectKeysAsync(EventId);

        Assert.DoesNotContain("tier:Silver", keys.Keys);
    }
}
