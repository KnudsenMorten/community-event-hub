using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §972 — A MASTER CLASS FLAGGED <see cref="Session.UsedForTesting"/> IS NOT OFFERED TO ATTENDEES.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-09: <i>"i would like to not show my test master class anymore in prod,
/// what is the best way to turn it off for attendees"</i> → then, once the gap was confirmed:
/// <i>"add it for both the master class selection + waitlist functionality"</i>.</para>
///
/// <para>🔴 <b>The flag already existed and this query ignored it.</b> §299 4.5/b8 says a
/// <c>UsedForTesting</c> session <i>"never appears on any PUBLIC page"</i> and is hub-visible only to
/// ring 0/1, and the Backstage push enforces it in three places — but
/// <c>ListMasterClassesAsync</c> filtered <c>!IsServiceSession</c> and nothing else. So ticking the
/// flag hid the session from the public agenda and changed nothing on the screen the operator was
/// actually looking at.</para>
///
/// <para>🔒 <b>Opt-in, default unchanged</b> (operator: <i>"no changing of default behavior"</i>).
/// The organizer page still sees it — a row hidden from the very screen where its flag is set and
/// cleared is unreachable.</para>
/// </remarks>
public sealed class MasterClassTestFlagHidesItTests
{
    private static async Task<(int EventId, int RealId, int TestId)> SeedAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "T", Code = "T27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var real = new Session
        {
            EventId = ev.Id, SessionizeId = "mc-real", Title = "Purview in practice",
            Type = SessionType.MasterClass, MasterClassCapacity = 30,
        };
        var test = new Session
        {
            EventId = ev.Id, SessionizeId = "mc-test", Title = "Test Master Class",
            Type = SessionType.MasterClass, MasterClassCapacity = 30,
            UsedForTesting = true,
        };
        db.Sessions.AddRange(real, test);
        await db.SaveChangesAsync();
        return (ev.Id, real.Id, test.Id);
    }

    private static MasterClassSignupService NewService(CommunityHubDbContext db) => new(db);

    /// <summary>
    /// 🔒 THE DEFAULT IS UNCHANGED — this is what the organizer page and every untouched caller get.
    /// If this ever starts excluding the test class, a behaviour change has leaked in.
    /// </summary>
    [Fact]
    public async Task By_default_the_test_master_class_is_still_listed()
    {
        using var db = ScenarioFixture.NewDb();
        var (evId, _, testId) = await SeedAsync(db);

        var all = await NewService(db).ListMasterClassesAsync(evId);

        Assert.Contains(all, o => o.SessionId == testId);
        Assert.Equal(2, all.Count);
    }

    /// <summary>🔴 The attendee SELECTION + WAITLIST surfaces ask for the exclusion.</summary>
    [Fact]
    public async Task When_excluded_only_the_real_master_class_is_offered()
    {
        using var db = ScenarioFixture.NewDb();
        var (evId, realId, testId) = await SeedAsync(db);

        var offered = await NewService(db).ListMasterClassesAsync(evId, excludeTestSessions: true);

        Assert.DoesNotContain(offered, o => o.SessionId == testId);
        Assert.Contains(offered, o => o.SessionId == realId);
    }

    /// <summary>
    /// ⚠️ Clearing the flag brings it straight back — the hide is a property of the SESSION, not a
    /// deletion, so the operator can test with it again by un-ticking one box.
    /// </summary>
    [Fact]
    public async Task Clearing_the_flag_restores_it_for_attendees()
    {
        using var db = ScenarioFixture.NewDb();
        var (evId, _, testId) = await SeedAsync(db);

        var s = await db.Sessions.FindAsync(testId);
        s!.UsedForTesting = false;
        await db.SaveChangesAsync();

        var offered = await NewService(db).ListMasterClassesAsync(evId, excludeTestSessions: true);

        Assert.Contains(offered, o => o.SessionId == testId);
    }

    /// <summary>
    /// 🔒 The exclusion is keyed on <see cref="Session.UsedForTesting"/> ALONE. IsTestData (§909) is
    /// a CAMPAIGN property — "never announced on social media" — and borrowing it here would hide
    /// every session kept out of the SoMe queue, a much larger and different set.
    /// </summary>
    [Fact]
    public async Task IsTestData_alone_does_not_hide_a_master_class()
    {
        using var db = ScenarioFixture.NewDb();
        var (evId, realId, _) = await SeedAsync(db);

        var s = await db.Sessions.FindAsync(realId);
        s!.IsTestData = true;               // excluded from SoMe, still a real class attendees may pick
        await db.SaveChangesAsync();

        var offered = await NewService(db).ListMasterClassesAsync(evId, excludeTestSessions: true);

        Assert.Contains(offered, o => o.SessionId == realId);
    }
}
