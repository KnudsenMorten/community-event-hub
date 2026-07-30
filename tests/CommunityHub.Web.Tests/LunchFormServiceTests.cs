using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Resources;
using CommunityHub.Forms;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Lunch-logistics form behaviour (REQUIREMENTS §178), driven through the shared
/// <see cref="LunchFormService"/> over an in-memory DB:
///   • §178a — the earliest (Sunday) day is labelled "Packing day", not "Setup day"
///     (Monday stays "Setup day").
///   • §178c — the pre-day lunch is now a CHECKBOX: a saved Yes round-trips as checked.
///   • §178b — a day the volunteer is marked Unavailable on is force-unchecked on load
///     and can never be persisted true (server guard); with no availability recorded,
///     every day stays enabled. FAKE data only.
/// </summary>
public sealed class LunchFormServiceTests
{
    private const int EventId = 77;

    // The conference pre-day = StartDate; the day before is the setup day, and the day
    // before THAT is the packing day (Sunday in the real edition).
    private static readonly DateOnly PreDay        = new(2027, 2, 9);  // Tue — Master Class / pre-day
    private static readonly DateOnly SetupDay      = new(2027, 2, 8);  // Mon — setup
    private static readonly DateOnly PackingDay    = new(2027, 2, 7);  // Sun — packing

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"lunch-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-12-01T10:00:00Z");
    }

    private static IStringLocalizer<SharedResource> Loc()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions { ResourcesPath = "" }), NullLoggerFactory.Instance);
        return new StringLocalizer<SharedResource>(factory);
    }

    private static LunchFormService NewService(CommunityHubDbContext db) =>
        new(db, new FixedClock(), Loc());

    private static async Task<Participant> SeedVolunteerAsync(CommunityHubDbContext db)
    {
        if (!await db.Events.AnyAsync(e => e.Id == EventId))
        {
            db.Events.Add(new Event
            {
                Id = EventId, CommunityName = "Test Community", Code = "TEST",
                DisplayName = "Test Event", StartDate = PreDay, EndDate = PreDay.AddDays(1),
            });
            await db.SaveChangesAsync();
        }
        var p = new Participant
        {
            EventId = EventId, Email = $"vol-{Guid.NewGuid():N}@example.test",
            FullName = "Volunteer Person", Role = ParticipantRole.Volunteer,
            IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private static async Task MarkUnavailableAsync(CommunityHubDbContext db, int participantId, DateOnly day)
    {
        db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
        {
            EventId = EventId, ParticipantId = participantId, Day = day,
            Level = VolunteerAvailabilityLevel.Unavailable,
        });
        await db.SaveChangesAsync();
    }

    private static Task<LunchFormModel> LoadAsync(LunchFormService svc, Participant p) =>
        svc.LoadAsync(EventId, p.Id, p.Role, p.FullName, p.Email, default);

    private static Task<WizardStepOutcome> SaveAsync(LunchFormService svc, LunchFormModel m, Participant p) =>
        svc.SaveAsync(m, EventId, p.Id, p.FullName, p.Email, p.Role, new ModelStateDictionary(), default);

    // ===== §178a — Sunday is "Packing day", Monday stays "Setup day" ============

    [Fact]
    public async Task Sunday_is_labelled_packing_day_and_monday_stays_setup_day()
    {
        using var db = NewDb();
        var p = await SeedVolunteerAsync(db);

        var model = await LoadAsync(NewService(db), p);

        // "Packing day" prefix is the §178a change; the date text inside is culture-formatted
        // (day number + year are the stable, locale-independent parts).
        Assert.StartsWith("Packing day", model.EarlySetupDayLabel);
        Assert.Contains("2027", model.EarlySetupDayLabel);
        Assert.DoesNotContain("Setup day", model.EarlySetupDayLabel);
        Assert.StartsWith("Setup day", model.SetupDayLabel);   // Monday unchanged
    }

    // ===== §178c — the pre-day CHECKBOX round-trips (saved Yes => checked) =======

    [Fact]
    public async Task PreDay_checkbox_value_round_trips()
    {
        using var db = NewDb();
        var p = await SeedVolunteerAsync(db);
        var svc = NewService(db);

        // Save with the pre-day lunch CHECKED.
        var save = new LunchFormModel { LunchPreDay = true };
        Assert.Equal(WizardStepOutcome.Advance, await SaveAsync(svc, save, p));

        // Reload — the box shows checked.
        var reloaded = await LoadAsync(svc, p);
        Assert.True(reloaded.LunchPreDay);

        // Save again UNCHECKED — round-trips to false.
        var clear = new LunchFormModel { LunchPreDay = false };
        Assert.Equal(WizardStepOutcome.Advance, await SaveAsync(svc, clear, p));
        Assert.False((await LoadAsync(svc, p)).LunchPreDay);
    }

    // ===== §178b — availability force-unchecks + guards a day =====================

    [Fact]
    public async Task Unavailable_day_is_disabled_and_force_unchecked_on_load()
    {
        using var db = NewDb();
        var p = await SeedVolunteerAsync(db);
        var svc = NewService(db);

        // A stale signup said "yes" to the packing-day lunch...
        await SaveAsync(svc, new LunchFormModel { LunchEarlySetupDay = true }, p);
        // ...then the volunteer marks the packing day (Sunday) Unavailable.
        await MarkUnavailableAsync(db, p.Id, PackingDay);

        var model = await LoadAsync(svc, p);

        Assert.True(model.EarlySetupDayUnavailable);           // the view disables + dims it
        Assert.False(model.LunchEarlySetupDay);                // force-unchecked despite the stale yes
        Assert.False(model.SetupDayUnavailable);               // Monday is still available
        Assert.False(model.PreDayUnavailable);                 // Tuesday is still available
    }

    [Fact]
    public async Task Checked_value_for_an_unavailable_day_is_never_persisted()
    {
        using var db = NewDb();
        var p = await SeedVolunteerAsync(db);
        var svc = NewService(db);
        await MarkUnavailableAsync(db, p.Id, PackingDay);

        // A crafted POST tries to set the disabled day true — the service drops it.
        await SaveAsync(svc, new LunchFormModel { LunchEarlySetupDay = true, LunchSetupDay = true }, p);

        var signup = await db.LunchSignups.SingleAsync(l => l.ParticipantId == p.Id);
        Assert.False(signup.LunchEarlySetupDay);   // guarded off (Sunday unavailable)
        Assert.True(signup.LunchSetupDay);         // Monday available => kept
    }

    [Fact]
    public async Task No_availability_recorded_leaves_every_day_enabled()
    {
        using var db = NewDb();
        var p = await SeedVolunteerAsync(db);

        var model = await LoadAsync(NewService(db), p);

        Assert.False(model.EarlySetupDayUnavailable);
        Assert.False(model.SetupDayUnavailable);
        Assert.False(model.PreDayUnavailable);
    }
}
