using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The anonymous Party RSVP service (<see cref="PartyRsvpService"/>): resolves the
/// active edition's party window, validates + upserts by email, and counts the
/// attending headcount (the Bella Center food-order figure). EF in-memory.
/// </summary>
public class PartyRsvpServiceTests
{
    private static async Task<int> SeedActiveEventAsync(CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "Test Community", DisplayName = "Test 2027", Code = "TC27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    [Fact]
    public async Task GetActiveParty_returns_preday_at_16_to_18()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedActiveEventAsync(db);
        var p = await new PartyRsvpService(db).GetActivePartyAsync();
        Assert.NotNull(p);
        Assert.Equal(new DateOnly(2027, 2, 9), p!.Date);
        Assert.Equal(16, p.StartHour);
        Assert.Equal(18, p.EndHour);
    }

    [Fact]
    public async Task Submit_validates_name_and_email()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedActiveEventAsync(db);
        var svc = new PartyRsvpService(db);

        Assert.False((await svc.SubmitAsync("", "a@b.dk", true, null)).Ok);        // no name
        Assert.False((await svc.SubmitAsync("Jane", "not-an-email", true, null)).Ok); // bad email
        Assert.True((await svc.SubmitAsync("Jane", "jane@x.dk", true, null)).Ok);
    }

    [Fact]
    public async Task Submit_upserts_by_email_and_counts_attending()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedActiveEventAsync(db);
        var svc = new PartyRsvpService(db);

        await svc.SubmitAsync("Jane", "jane@x.dk", true, null);
        await svc.SubmitAsync("John", "john@x.dk", true, null);
        await svc.SubmitAsync("Nope", "nope@x.dk", false, null);
        // Jane changes her mind — same email upserts, not a new row.
        await svc.SubmitAsync("Jane B", "jane@x.dk", false, null);

        var rows = await svc.GetAllAsync(ev);
        Assert.Equal(3, rows.Count);                         // 3 distinct emails
        Assert.Equal("Jane B", rows.Single(r => r.Email == "jane@x.dk").Name);
        var (total, attending) = await svc.CountsAsync(ev);
        Assert.Equal(3, total);
        Assert.Equal(1, attending);                          // only John still attending
    }

    [Fact]
    public async Task Submit_fails_cleanly_with_no_active_event()
    {
        using var db = ScenarioFixture.NewDb();   // no event seeded
        var r = await new PartyRsvpService(db).SubmitAsync("Jane", "jane@x.dk", true, null);
        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
    }
}
