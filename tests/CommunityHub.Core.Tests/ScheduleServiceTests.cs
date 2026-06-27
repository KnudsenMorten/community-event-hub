using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The role-tagged event SCHEDULE service (<see cref="ScheduleService"/>) — the
/// single source of truth behind the Key-dates panel, the iCal feed and the
/// organizer editor. Asserts the derived default, role filtering, effective
/// (persisted-or-default) resolution and idempotent seeding. EF in-memory.
/// </summary>
public class ScheduleServiceTests
{
    // ELDK27: pre-day/master-class 9 Feb, main day 10 Feb 2027.
    private static readonly DateOnly Start = new(2027, 2, 9);
    private static readonly DateOnly End = new(2027, 2, 10);

    private static async Task<int> SeedEventAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "Test Community",
            DisplayName = "Test Community 2027",
            Code = "TC27",
            IsActive = true,
            StartDate = Start,
            EndDate = End,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    [Fact]
    public void BuildDefault_has_the_six_day_matrix_plus_three_pre_day_events()
    {
        var d = ScheduleService.BuildDefault(1, Start, End);

        Assert.Equal(10, d.Count);

        // §122: a SPONSOR-only "booth photos" key date on the pre-day, 11:00–14:00.
        var booth = Assert.Single(d, e => e.Title.Contains("booth photos"));
        Assert.Equal("sponsor", booth.Roles);
        Assert.False(booth.AllDay);
        Assert.Equal(11, booth.StartsAt.Hour);
        Assert.Equal(14, booth.EndsAt!.Value.Hour);
        Assert.Equal(Start, DateOnly.FromDateTime(booth.StartsAt.DateTime));
        // Move-in is 4 days before the pre-day; main day is the end date.
        Assert.Equal(Start.AddDays(-4), DateOnly.FromDateTime(d.First().StartsAt.DateTime));
        Assert.Contains(d, e => e.Title.Contains("Move-in") && e.Roles == "organizer" && e.AllDay);
        // The three timed pre-day social events.
        var party = Assert.Single(d, e => e.Title == "Party");
        Assert.False(party.AllDay);
        Assert.Equal(16, party.StartsAt.Hour);
        Assert.Equal("all", party.Roles);
        var photo = Assert.Single(d, e => e.Title == "Group photo");
        Assert.Equal(17, photo.StartsAt.Hour);
        Assert.Equal(30, photo.StartsAt.Minute);
        Assert.DoesNotContain("sponsor", photo.Roles);   // all except sponsors
        var dinner = Assert.Single(d, e => e.Title == "Appreciation Dinner");
        Assert.Equal(18, dinner.StartsAt.Hour);
        Assert.Equal("all", dinner.Roles);
    }

    [Fact]
    public async Task GetForRole_hides_move_in_from_speakers_and_photo_from_sponsors()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var svc = new ScheduleService(db);

        var speaker = await svc.GetForRoleAsync(ev, ParticipantRole.Speaker);
        Assert.DoesNotContain(speaker, e => e.Title.Contains("Move-in")); // organizer-only
        Assert.Contains(speaker, e => e.Title == "Pre-day / Master Class");
        Assert.Contains(speaker, e => e.Title == "Party");                // 'all'

        var sponsor = await svc.GetForRoleAsync(ev, ParticipantRole.Sponsor);
        Assert.DoesNotContain(sponsor, e => e.Title == "Group photo");    // all except sponsors
        Assert.Contains(sponsor, e => e.Title == "Appreciation Dinner");  // 'all'
        Assert.Contains(sponsor, e => e.Title.Contains("booth photos"));  // §122 sponsor-only
        Assert.DoesNotContain(speaker, e => e.Title.Contains("booth photos")); // not for speakers

        var organizer = await svc.GetForRoleAsync(ev, ParticipantRole.Organizer);
        Assert.Contains(organizer, e => e.Title.Contains("Move-in"));
    }

    [Fact]
    public async Task GetEffective_returns_default_when_empty_then_persisted_rows()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var svc = new ScheduleService(db);

        // No rows yet -> derived default (10), not persisted.
        var derived = await svc.GetEffectiveAsync(ev);
        Assert.Equal(10, derived.Count);
        Assert.Empty(await svc.GetAllAsync(ev));

        // Persist one custom row -> effective becomes the persisted set only.
        db.ScheduleEntries.Add(new ScheduleEntry
        {
            EventId = ev, Title = "Custom", Roles = "all", AllDay = true,
            StartsAt = ScheduleService.EventLocal(new DateTime(2027, 2, 8, 0, 0, 0)),
        });
        await db.SaveChangesAsync();

        var effective = await svc.GetEffectiveAsync(ev);
        var only = Assert.Single(effective);
        Assert.Equal("Custom", only.Title);
    }

    [Fact]
    public async Task EnsureSeeded_persists_defaults_once_and_is_idempotent()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var svc = new ScheduleService(db);

        var first = await svc.EnsureSeededAsync(ev, "mok@expertslive.dk");
        Assert.Equal(10, first);
        Assert.Equal(10, (await svc.GetAllAsync(ev)).Count);
        Assert.All(await svc.GetAllAsync(ev), e => Assert.Equal("mok@expertslive.dk", e.LastUpdatedByEmail));

        var second = await svc.EnsureSeededAsync(ev, "someone@else");
        Assert.Equal(0, second);                       // already seeded -> no-op
        Assert.Equal(10, (await svc.GetAllAsync(ev)).Count);
    }
}
