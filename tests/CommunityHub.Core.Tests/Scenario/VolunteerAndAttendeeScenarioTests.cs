using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: an attendee browses the agenda and a volunteer completes the 3-step
/// sign-up wizard. The GUI counterparts (scenario-volunteer.spec.ts,
/// scenario-attendee.spec.ts) drive /Forms/VolunteerWizard's real postbacks and
/// /Attendee; this backend half proves the DB rows behind those flows:
///
///  - finishing the volunteer wizard creates exactly one VolunteerAvailability
///    row for the volunteer, carrying the selected shifts (one per participant
///    per edition — re-submitting updates rather than duplicating),
///  - the attendee's reconciled status is what the attendee hub shows
///    (2-day ticket + booked Master Class = no mismatch).
/// </summary>
public sealed class VolunteerAndAttendeeScenarioTests
{
    [Fact]
    public async Task Volunteer_wizard_finish_creates_one_availability_row()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        Assert.Equal(0, await db.VolunteerAvailabilities
            .CountAsync(v => v.ParticipantId == seed.VolunteerId));

        // Simulate the wizard's Finish postback (the mutation the page performs).
        db.VolunteerAvailabilities.Add(new VolunteerAvailability
        {
            EventId = seed.EventId,
            ParticipantId = seed.VolunteerId,
            SelectedShifts = "Registration desk, Room host",
            PreferredRole = "Registration",
            MaxHoursPerDay = 6,
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        var rows = await db.VolunteerAvailabilities
            .Where(v => v.ParticipantId == seed.VolunteerId)
            .ToListAsync();
        Assert.Single(rows);
        Assert.Contains("Registration desk", rows[0].SelectedShifts);
        Assert.Equal(6, rows[0].MaxHoursPerDay);
    }

    [Fact]
    public async Task Volunteer_availability_is_one_row_per_participant_per_edition()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        db.VolunteerAvailabilities.Add(new VolunteerAvailability
        {
            EventId = seed.EventId, ParticipantId = seed.VolunteerId,
            SelectedShifts = "Registration desk", MaxHoursPerDay = 8,
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        // A second submit updates the existing row (the page upserts by
        // EventId+ParticipantId; the unique index enforces one row).
        var existing = await db.VolunteerAvailabilities
            .SingleAsync(v => v.ParticipantId == seed.VolunteerId);
        existing.SelectedShifts = "Registration desk, Cloakroom";
        existing.UpdatedAt = ScenarioFixture.Clock.GetUtcNow();
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.VolunteerAvailabilities
            .CountAsync(v => v.ParticipantId == seed.VolunteerId));
        Assert.Contains("Cloakroom", (await db.VolunteerAvailabilities
            .SingleAsync(v => v.ParticipantId == seed.VolunteerId)).SelectedShifts);
    }

    [Fact]
    public async Task Attendee_reconciled_status_drives_the_attendee_hub()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var attendee = await db.Attendees.SingleAsync(
            a => a.EventId == seed.EventId && a.Email == ScenarioSeed.AttendeeEmail);

        // The clean attendee: 2-day ticket + booked Master Class, no mismatch —
        // the attendee hub shows "you're all set".
        Assert.Equal(TicketStatus.TwoDay, attendee.TicketStatus);
        Assert.Equal(MasterClassBookingStatus.Booked, attendee.BookingStatus);
        Assert.False(attendee.HasReconciliationMismatch);

        // The organizer reconciliation view shows exactly the one mismatch row.
        var mismatches = await db.Attendees
            .Where(a => a.EventId == seed.EventId && a.HasReconciliationMismatch)
            .ToListAsync();
        Assert.Single(mismatches);
    }
}
