using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Pages.Volunteer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// The anonymous volunteer sign-up is now a shift-availability SURVEY: submitting
/// it creates the pending applicant AND records their chosen shifts as a
/// <see cref="VolunteerAvailability"/> row (operator 2026-06-21). Drives the real
/// <see cref="SignupModel"/> over an in-memory DB.
/// </summary>
public sealed class VolunteerSignupSurveyTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vol-signup-{Guid.NewGuid():N}")
            .Options);

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static SignupModel NewModel(CommunityHubDbContext db) =>
        new(db, new FixedClock(), NullLogger<SignupModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
        };

    [Fact]
    public async Task Submitting_with_shifts_creates_applicant_and_availability()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var m = NewModel(db);
        m.FullName = "Avery Helper";
        m.Email = "Avery@Example.com";
        m.SelectedShifts = new List<string> { "Setup (day before)", "Session room support" };
        m.PreferredRole = "Floater";
        m.MaxHoursPerDay = 6;

        await m.OnPostAsync(default);

        var p = await db.Participants.SingleAsync(x => x.EventId == eventId);
        Assert.Equal(ParticipantRole.Volunteer, p.Role);
        Assert.False(p.IsActive);                       // pending applicant
        Assert.Equal("avery@example.com", p.Email);

        var av = await db.VolunteerAvailabilities.SingleAsync(x => x.ParticipantId == p.Id);
        Assert.Contains("Setup (day before)", av.SelectedShifts);
        Assert.Contains("Session room support", av.SelectedShifts);
        Assert.Equal("Floater", av.PreferredRole);
        Assert.Equal(6, av.MaxHoursPerDay);
    }

    [Fact]
    public async Task Tampered_shift_values_are_dropped()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var m = NewModel(db);
        m.FullName = "Bob Helper";
        m.Email = "bob@example.com";
        m.SelectedShifts = new List<string> { "Session room support", "DROP TABLE volunteers" };

        await m.OnPostAsync(default);

        var av = await db.VolunteerAvailabilities.SingleAsync();
        Assert.Contains("Session room support", av.SelectedShifts);
        Assert.DoesNotContain("DROP TABLE", av.SelectedShifts);
    }

    [Fact]
    public async Task No_shifts_still_creates_the_applicant_but_no_availability_row()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var m = NewModel(db);
        m.FullName = "Cara Helper";
        m.Email = "cara@example.com";
        m.SelectedShifts = new List<string>();

        await m.OnPostAsync(default);

        Assert.Equal(1, await db.Participants.CountAsync());
        Assert.Equal(0, await db.VolunteerAvailabilities.CountAsync());
    }

    [Fact]
    public async Task Hours_out_of_range_are_clamped()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var m = NewModel(db);
        m.FullName = "Dee Helper";
        m.Email = "dee@example.com";
        m.SelectedShifts = new List<string> { "Setup (day before)" };
        m.MaxHoursPerDay = 99;

        await m.OnPostAsync(default);

        var av = await db.VolunteerAvailabilities.SingleAsync();
        Assert.Equal(24, av.MaxHoursPerDay);   // clamped to a sane day
    }
}
