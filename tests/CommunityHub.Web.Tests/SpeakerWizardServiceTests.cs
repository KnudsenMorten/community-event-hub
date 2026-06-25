using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §28 speaker onboarding wizard service — the design-A shell that lists the steps a
/// speaker is ENTITLED to, in a fixed guided order, with completion read from each
/// form's persisted data. These prove: entitlement gates which steps appear,
/// Speaker Details is always shown, completion is detected from data, and progress /
/// next-step are derived correctly.
/// </summary>
public sealed class SpeakerWizardServiceTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"spkwiz-{Guid.NewGuid()}").Options);

    private static async Task<(CommunityHubDbContext db, int eventId, int pid)> SeedSpeakerAsync(
        params OrderItem[] entitlements)
    {
        var db = NewDb();
        var ev = new Event { Code = "e", DisplayName = "E", CommunityName = "C", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = ev.Id, FullName = "Speaker One", Email = "s@x.dk",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        // Entitlements come from order overrides (grant the given items).
        foreach (var item in entitlements)
            db.ParticipantOrderOverrides.Add(new ParticipantOrderOverride
            {
                EventId = ev.Id, ParticipantId = p.Id, Item = item, Include = true,
            });
        await db.SaveChangesAsync();

        return (db, ev.Id, p.Id);
    }

    [Fact]
    public async Task Speaker_details_is_always_shown_even_with_no_entitlements()
    {
        var (db, ev, pid) = await SeedSpeakerAsync();
        var view = await new SpeakerWizardService(db).BuildAsync(ev, pid);

        Assert.Contains(view.Steps, s => s.Key == "details");
        Assert.Equal("/Speaker/Details", view.Steps.Single(s => s.Key == "details").Route);
    }

    [Fact]
    public async Task Only_entitled_steps_appear_in_order()
    {
        var (db, ev, pid) = await SeedSpeakerAsync(OrderItem.Hotel, OrderItem.TravelReimbursement);
        var view = await new SpeakerWizardService(db).BuildAsync(ev, pid);

        // details (always) + hotel + travel — in the fixed wizard order.
        Assert.Equal(new[] { "details", "hotel", "travel" }, view.Steps.Select(s => s.Key).ToArray());
        Assert.DoesNotContain(view.Steps, s => s.Key == "dinner");
        Assert.DoesNotContain(view.Steps, s => s.Key == "lunch");
    }

    [Fact]
    public async Task Lunch_appears_for_either_day_and_swag_for_either_item()
    {
        var (db, ev, pid) = await SeedSpeakerAsync(OrderItem.LunchMainDay, OrderItem.Polo);
        var view = await new SpeakerWizardService(db).BuildAsync(ev, pid);

        Assert.Contains(view.Steps, s => s.Key == "lunch");
        Assert.Contains(view.Steps, s => s.Key == "swag");
    }

    [Fact]
    public async Task Completion_is_detected_from_persisted_data()
    {
        var (db, ev, pid) = await SeedSpeakerAsync(OrderItem.Hotel);
        // Hotel done; details not done (no profile bio).
        db.HotelBookings.Add(new HotelBooking { EventId = ev, ParticipantId = pid, NeedsRoom = true });
        await db.SaveChangesAsync();

        var view = await new SpeakerWizardService(db).BuildAsync(ev, pid);

        Assert.True(view.Steps.Single(s => s.Key == "hotel").Done);
        Assert.False(view.Steps.Single(s => s.Key == "details").Done);
        Assert.Equal(2, view.EntitledCount);
        Assert.Equal(1, view.DoneCount);
        Assert.Equal(50, view.Percent);
        // Next incomplete step is Speaker Details (first in order).
        Assert.Equal("details", view.NextStep!.Key);
        Assert.False(view.AllDone);
    }

    [Fact]
    public async Task All_done_when_every_entitled_step_has_data()
    {
        var (db, ev, pid) = await SeedSpeakerAsync(OrderItem.Hotel);
        // Organizer-funded profile adds NO speaker-hat entitlements, so the only
        // entitled steps are Speaker Details (always) + the Hotel override.
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = ev, ParticipantId = pid, Biography = "Hi, I speak.",
            SpeakerFunding = SpeakerFunding.Organizer,
        });
        db.HotelBookings.Add(new HotelBooking { EventId = ev, ParticipantId = pid, NeedsRoom = false });
        await db.SaveChangesAsync();

        var view = await new SpeakerWizardService(db).BuildAsync(ev, pid);

        Assert.True(view.AllDone);
        Assert.Equal(100, view.Percent);
        Assert.Null(view.NextStep);
        Assert.Equal(0, view.NextStepNumber);
    }
}
