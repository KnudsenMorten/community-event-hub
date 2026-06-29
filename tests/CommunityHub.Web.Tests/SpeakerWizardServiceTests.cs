using CommunityHub.Core.Config;
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

    /// <summary>Signal-groups provider with Speakers in scope (§109).</summary>
    private static SignalGroupsProvider TestSignal() => new(new SignalGroupsConfig
    {
        BroadcastLabel = "ELDK27 Broadcast",
        BroadcastUrl = "https://signal.group/#broadcast",
        Roles = new()
        {
            ["Speaker"] = new() { ChatLabel = "Speakers", ChatUrl = "https://signal.group/#spk" },
        },
    });

    private static SpeakerWizardService Wizard(CommunityHubDbContext db) => new(db, TestSignal());

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
    public async Task Calendar_email_is_step_one_and_details_always_shown()
    {
        var (db, ev, pid) = await SeedSpeakerAsync();
        var view = await Wizard(db).BuildAsync(ev, pid);

        // §141: Calendar email (optional) is the FIRST wizard step, ahead of details.
        Assert.Equal("calendar", view.Steps[0].Key);
        Assert.Equal("/Forms/CalendarEmail", view.Steps[0].Route);
        Assert.Contains(view.Steps, s => s.Key == "details");
        Assert.Equal("/Speaker/Details", view.Steps.Single(s => s.Key == "details").Route);
    }

    [Fact]
    public async Task Travel_and_uploads_are_no_longer_wizard_steps()
    {
        // §141/§142: travel reimbursement + both presentation uploads were DROPPED from
        // the wizard. Even WITH the travel entitlement, no travel step appears; the two
        // uploads (always-on before) are gone too. Remaining: calendar + details (always),
        // hotel (entitled), then promote (§116) + signal (§109) + accept (§119).
        var (db, ev, pid) = await SeedSpeakerAsync(OrderItem.Hotel, OrderItem.TravelReimbursement);
        var view = await Wizard(db).BuildAsync(ev, pid);

        Assert.Equal(
            new[] { "calendar", "details", "hotel", "promote", "signal", "party", "accept" },
            view.Steps.Select(s => s.Key).ToArray());
        Assert.DoesNotContain(view.Steps, s => s.Key == "travel");
        Assert.DoesNotContain(view.Steps, s => s.Key == "upload-preview");
        Assert.DoesNotContain(view.Steps, s => s.Key == "upload-final");
        Assert.DoesNotContain(view.Steps, s => s.Key == "dinner");
        Assert.DoesNotContain(view.Steps, s => s.Key == "swag");
        Assert.DoesNotContain(view.Steps, s => s.Key == "lunch");
    }

    [Fact]
    public async Task Lunch_appears_for_either_day_and_swag_for_either_item()
    {
        var (db, ev, pid) = await SeedSpeakerAsync(OrderItem.LunchMainDay, OrderItem.Polo);
        var view = await Wizard(db).BuildAsync(ev, pid);

        Assert.Contains(view.Steps, s => s.Key == "lunch");
        Assert.Contains(view.Steps, s => s.Key == "swag");
    }

    [Fact]
    public async Task Lunch_is_hidden_when_speaker_lacks_lunch_entitlement()
    {
        // Bug fix (§3 parity): lunch is ENTITLEMENT-GATED (LunchPreDay/LunchMainDay) so the
        // wizard, the nav and the Lunch page all agree — a speaker with no lunch
        // entitlement sees no Lunch step.
        var (db, ev, pid) = await SeedSpeakerAsync();
        var view = await Wizard(db).BuildAsync(ev, pid);

        Assert.DoesNotContain(view.Steps, s => s.Key == "lunch");
    }

    [Fact]
    public async Task Lunch_sits_at_step_6_in_the_canonical_order()
    {
        // With all preceding steps entitled (incl. a lunch entitlement), lunch is the 6th
        // visible step: calendar(1), details(2), hotel(3), dinner(4), swag(5), lunch(6).
        var (db, ev, pid) = await SeedSpeakerAsync(
            OrderItem.Hotel, OrderItem.AppreciationDinner, OrderItem.Swag, OrderItem.LunchMainDay);
        var view = await Wizard(db).BuildAsync(ev, pid);

        Assert.Equal(
            new[] { "calendar", "details", "hotel", "dinner", "swag", "lunch", "promote", "signal", "party", "accept" },
            view.Steps.Select(s => s.Key).ToArray());
        Assert.Equal("lunch", view.Steps[5].Key);
    }

    [Fact]
    public async Task Completion_is_detected_from_persisted_data()
    {
        var (db, ev, pid) = await SeedSpeakerAsync(OrderItem.Hotel, OrderItem.LunchMainDay);
        // Hotel done; calendar + details not done (no profile markers).
        db.HotelBookings.Add(new HotelBooking { EventId = ev, ParticipantId = pid, NeedsRoom = true });
        await db.SaveChangesAsync();

        var view = await Wizard(db).BuildAsync(ev, pid);

        Assert.True(view.Steps.Single(s => s.Key == "hotel").Done);
        Assert.False(view.Steps.Single(s => s.Key == "calendar").Done);
        Assert.False(view.Steps.Single(s => s.Key == "details").Done);
        // 8 entitled steps now: calendar (always) + details (always) + hotel + lunch
        // (entitled) + promote (§116) + signal (§109) + party (§164) + accept (§119).
        // Uploads + travel are no longer wizard steps. Only hotel is done.
        Assert.Equal(8, view.EntitledCount);
        Assert.Equal(1, view.DoneCount);
        Assert.Equal(12, view.Percent); // round(100/8) = 12 (banker's rounding of 12.5)
        // Next incomplete step is Calendar email (now first in order).
        Assert.Equal("calendar", view.NextStep!.Key);
        Assert.False(view.AllDone);
    }

    [Fact]
    public async Task All_done_when_every_entitled_step_has_data()
    {
        var (db, ev, pid) = await SeedSpeakerAsync(OrderItem.Hotel, OrderItem.LunchMainDay);
        // Organizer-funded profile adds NO speaker-hat entitlements, so the only
        // entitled steps are Calendar email + Speaker Details (always) + the Hotel +
        // Lunch overrides + promote + signal + accept.
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = ev, ParticipantId = pid, Biography = "Hi, I speak.",
            SpeakerFunding = SpeakerFunding.Organizer,
            // P13: details is "done" only once the SPEAKER has edited it (the
            // speaker-edit marker), not merely from an imported biography.
            BioLastEditedBySpeakerAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            // §141: the calendar step is done once the speaker has SAVED it (the
            // CalendarEmailSetAt marker), even though the address itself is left blank.
            CalendarEmailSetAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        });
        db.HotelBookings.Add(new HotelBooking { EventId = ev, ParticipantId = pid, NeedsRoom = false });
        // §62: lunch is now an always-on step for every speaker, so AllDone requires a
        // persisted lunch signup too.
        db.LunchSignups.Add(new LunchSignup { EventId = ev, ParticipantId = pid, LunchPreDay = true });
        db.Tasks.AddRange(
            // §116 promote + §109 signal: manual mark-done tasks, completed here.
            new ParticipantTask
            {
                EventId = ev, AssignedParticipantId = pid,
                Title = "Help to promote your session(s)",
                SourceKey = WizardStepTasks.Promote(pid),
                State = TaskState.Done,
            },
            new ParticipantTask
            {
                EventId = ev, AssignedParticipantId = pid,
                Title = "Join Signal groups",
                SourceKey = WizardStepTasks.Signal(pid),
                State = TaskState.Done,
            });
        // §119 accept: a persisted acceptance row.
        db.ParticipantPolicyAcceptances.Add(new ParticipantPolicyAcceptance
        {
            EventId = ev, ParticipantId = pid, AcceptedByEmail = "s@x.dk",
            AcceptedAt = DateTimeOffset.UtcNow,
        });
        // §164 party: a saved RSVP row (Yes or No) for this speaker = the party step done.
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = ev, ParticipantId = pid, Name = "Speaker One", Email = "s@x.dk", Attending = true,
        });
        await db.SaveChangesAsync();

        var view = await Wizard(db).BuildAsync(ev, pid);

        Assert.True(view.AllDone);
        Assert.Equal(100, view.Percent);
        Assert.Null(view.NextStep);
        Assert.Equal(0, view.NextStepNumber);
    }
}
