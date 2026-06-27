using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §43/§44 generic "Get started" wizard service (Volunteer / Organizer / Media /
/// EventPartner) — the design-A shell that lists the steps a participant is ENTITLED
/// to (§44a), in a fixed guided order, with completion read from each page's
/// persisted data (§44b — a saved value = done). These prove: Profile is always the
/// first step; entitlement gates which logistics steps appear; the volunteer
/// availability step is volunteer-only; completion is detected from data; and a
/// multi-hat participant (e.g. volunteer + supported speaker) gets exactly the steps
/// their effective entitlement set grants (hotel/swag for the speaker hat).
/// </summary>
public sealed class RoleWizardServiceTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"rolewiz-{Guid.NewGuid()}").Options);

    /// <summary>A Signal-groups provider matching the shipped config scope (§109):
    /// Volunteers + Event Partners get chat+broadcast, Media gets broadcast only,
    /// Organizers are out of scope.</summary>
    private static SignalGroupsProvider TestSignal() => new(new SignalGroupsConfig
    {
        BroadcastLabel = "ELDK27 Broadcast",
        BroadcastUrl = "https://signal.group/#broadcast",
        Roles = new()
        {
            ["Speaker"] = new() { ChatLabel = "Speakers", ChatUrl = "https://signal.group/#spk" },
            ["Volunteer"] = new() { ChatLabel = "Volunteers", ChatUrl = "https://signal.group/#vol" },
            ["EventPartner"] = new() { ChatLabel = "Volunteers", ChatUrl = "https://signal.group/#vol" },
            ["Media"] = new(),
        },
    });

    private static RoleWizardService Wizard(CommunityHubDbContext db) => new(db, TestSignal());

    private static async Task<(CommunityHubDbContext db, int eventId, int pid)> SeedAsync(
        ParticipantRole role, SpeakerProfile? speaker = null, params OrderItem[] overrideIncludes)
    {
        var db = NewDb();
        var ev = new Event { Code = "e", DisplayName = "E", CommunityName = "C", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = ev.Id, FullName = "Person One", Email = "p@x.dk",
            Role = role, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        if (speaker is not null)
        {
            speaker.EventId = ev.Id; speaker.ParticipantId = p.Id;
            db.SpeakerProfiles.Add(speaker);
        }
        foreach (var item in overrideIncludes)
            db.ParticipantOrderOverrides.Add(new ParticipantOrderOverride
            { EventId = ev.Id, ParticipantId = p.Id, Item = item, Include = true });
        await db.SaveChangesAsync();

        return (db, ev.Id, p.Id);
    }

    [Fact]
    public void Handles_only_the_four_generic_roles()
    {
        Assert.True(RoleWizardService.Handles(ParticipantRole.Volunteer));
        Assert.True(RoleWizardService.Handles(ParticipantRole.Organizer));
        Assert.True(RoleWizardService.Handles(ParticipantRole.Media));
        Assert.True(RoleWizardService.Handles(ParticipantRole.EventPartner));
        Assert.False(RoleWizardService.Handles(ParticipantRole.Speaker));
        Assert.False(RoleWizardService.Handles(ParticipantRole.Sponsor));
        Assert.False(RoleWizardService.Handles(ParticipantRole.Attendee));
    }

    [Fact]
    public async Task Profile_is_always_the_first_step()
    {
        var (db, ev, pid) = await SeedAsync(ParticipantRole.Organizer);
        var view = await Wizard(db).BuildAsync(ev, pid);

        Assert.Equal("profile", view.Steps[0].Key);
        Assert.Equal("/Profile", view.Steps[0].Route);
    }

    [Fact]
    public async Task Volunteer_gets_profile_then_availability_then_entitlements_in_order()
    {
        // Default volunteer hat: Polo, Swag, Hotel, AppreciationDinner, LunchMainDay
        // (operator 2026-06-26: volunteers are now entitled to a hotel booking; still
        // no Travel).
        var (db, ev, pid) = await SeedAsync(ParticipantRole.Volunteer);
        var view = await Wizard(db).BuildAsync(ev, pid);

        // §109 signal (volunteers are in scope) + §119 accept (always last) close the list.
        Assert.Equal(
            new[] { "profile", "availability", "hotel", "dinner", "lunch", "swag", "signal", "accept" },
            view.Steps.Select(s => s.Key).ToArray());
        Assert.Contains(view.Steps, s => s.Key == "hotel");         // now entitled (operator 2026-06-26)
        Assert.DoesNotContain(view.Steps, s => s.Key == "travel");  // not entitled
    }

    [Fact]
    public async Task Availability_step_is_volunteer_only()
    {
        var (db, ev, pid) = await SeedAsync(ParticipantRole.Media);
        var view = await Wizard(db).BuildAsync(ev, pid);
        Assert.DoesNotContain(view.Steps, s => s.Key == "availability");
    }

    [Fact]
    public async Task Media_gets_hotel_dinner_lunch_from_its_hat_but_no_swag_or_travel()
    {
        // Media hat: Polo, Hotel, AppreciationDinner, LunchPreDay, LunchMainDay.
        // Polo => swag step appears; no Swag(item)/Travel. (Polo is in the hat.)
        var (db, ev, pid) = await SeedAsync(ParticipantRole.Media);
        var view = await Wizard(db).BuildAsync(ev, pid);

        // Media is broadcast-only in scope for §109 signal; §119 accept always closes.
        Assert.Equal(
            new[] { "profile", "hotel", "dinner", "lunch", "swag", "signal", "accept" },
            view.Steps.Select(s => s.Key).ToArray());
        Assert.DoesNotContain(view.Steps, s => s.Key == "travel");
    }

    [Fact]
    public async Task Profile_done_when_phone_present_and_availability_done_when_row_saved()
    {
        var (db, ev, pid) = await SeedAsync(ParticipantRole.Volunteer);
        // Add phone => profile done.
        (await db.Participants.FirstAsync(p => p.Id == pid)).Phone = "+45 12 34 56 78";
        // Add an availability row => availability done.
        db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
        {
            EventId = ev, ParticipantId = pid, Day = new DateOnly(2026, 2, 9),
            Level = VolunteerAvailabilityLevel.Full,
        });
        await db.SaveChangesAsync();

        var view = await Wizard(db).BuildAsync(ev, pid);

        Assert.True(view.Steps.Single(s => s.Key == "profile").Done);
        Assert.True(view.Steps.Single(s => s.Key == "availability").Done);
        // Logistics not done yet (no rows).
        Assert.False(view.Steps.Single(s => s.Key == "dinner").Done);
    }

    [Fact]
    public async Task Multi_hat_volunteer_plus_supported_speaker_gets_hotel_swag_travel()
    {
        // A supported-speaker hat grants Hotel, TravelReimbursement, Swag, etc. — so a
        // volunteer who also speaks (supported) MUST get hotel/travel steps via the
        // speaker hat (§44a — entitlement is the union across hats).
        var (db, ev, pid) = await SeedAsync(
            ParticipantRole.Volunteer,
            new SpeakerProfile { SpeakerFunding = SpeakerFunding.Supported, SpeakingMainDay = true });

        var view = await Wizard(db).BuildAsync(ev, pid);

        Assert.Contains(view.Steps, s => s.Key == "hotel");
        Assert.Contains(view.Steps, s => s.Key == "travel");
        Assert.Contains(view.Steps, s => s.Key == "swag");
        Assert.Contains(view.Steps, s => s.Key == "dinner");
        // Volunteer-specific availability step is still present.
        Assert.Contains(view.Steps, s => s.Key == "availability");
    }

    [Fact]
    public async Task Self_funded_speaker_hat_grants_dinner_and_lunch_but_not_hotel_or_travel()
    {
        // §44a: a SELF-FUNDED speaker hat grants the appreciation dinner + main-day
        // lunch only — NOT hotel or travel. Proven here on the shared entitlement
        // gating the generic wizard uses (the role-44 example a sponsor+speaker must
        // get the dinner step but no hotel; the sponsor wizard is bespoke, but this
        // pins the same self-funded-speaker rule the gating relies on).
        var (db, ev, pid) = await SeedAsync(
            ParticipantRole.Volunteer,
            new SpeakerProfile { SpeakerFunding = SpeakerFunding.SponsorSelfFunded, SpeakingMainDay = true });

        var view = await Wizard(db).BuildAsync(ev, pid);

        Assert.Contains(view.Steps, s => s.Key == "dinner");   // self-funded speaker hat + volunteer hat
        Assert.Contains(view.Steps, s => s.Key == "lunch");    // main-day lunch
        Assert.DoesNotContain(view.Steps, s => s.Key == "travel");  // neither hat grants travel
        // Hotel is NOW present via the VOLUNTEER hat (operator 2026-06-26: volunteers
        // are hotel-entitled). The self-funded SPEAKER hat itself grants no hotel —
        // that rule is pinned separately by
        // OrderEntitlementsTests.SponsorSelfFunded_speaker_gets_dinner_and_main_lunch_only.
        Assert.Contains(view.Steps, s => s.Key == "hotel");
    }

    [Fact]
    public async Task Accept_step_is_always_present_and_last_for_every_role()
    {
        // §119: the Code of Conduct + Privacy "I accept" step applies across roles and
        // is always the final step.
        var (db, ev, pid) = await SeedAsync(ParticipantRole.Organizer);
        var view = await Wizard(db).BuildAsync(ev, pid);

        Assert.Equal("accept", view.Steps[^1].Key);
        Assert.Equal("/Forms/Accept", view.Steps[^1].Route);
    }

    [Fact]
    public async Task Organizer_is_out_of_scope_for_the_signal_step()
    {
        // §109: Organizers are not in the signal-groups config → no signal step (but
        // they still get the accept step).
        var (db, ev, pid) = await SeedAsync(ParticipantRole.Organizer);
        var view = await Wizard(db).BuildAsync(ev, pid);

        Assert.DoesNotContain(view.Steps, s => s.Key == "signal");
        Assert.Contains(view.Steps, s => s.Key == "accept");
    }

    [Fact]
    public async Task Signal_step_done_when_its_task_is_done_and_accept_done_when_row_exists()
    {
        var (db, ev, pid) = await SeedAsync(ParticipantRole.Volunteer);
        // §109: completion is the manual signal: task being Done.
        db.Tasks.Add(new ParticipantTask
        {
            EventId = ev, AssignedParticipantId = pid,
            Title = "Join Signal groups",
            SourceKey = WizardStepTasks.Signal(pid),
            State = TaskState.Done,
        });
        // §119: completion is a persisted acceptance row (who/when).
        db.ParticipantPolicyAcceptances.Add(new ParticipantPolicyAcceptance
        {
            EventId = ev, ParticipantId = pid, AcceptedByEmail = "p@x.dk",
            AcceptedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var view = await Wizard(db).BuildAsync(ev, pid);

        Assert.True(view.Steps.Single(s => s.Key == "signal").Done);
        Assert.True(view.Steps.Single(s => s.Key == "accept").Done);
    }

    [Fact]
    public async Task No_signal_step_when_no_signal_config()
    {
        // With no signal provider (no config), the signal step is omitted — accept
        // still appears.
        var (db, ev, pid) = await SeedAsync(ParticipantRole.Volunteer);
        var view = await new RoleWizardService(db).BuildAsync(ev, pid);

        Assert.DoesNotContain(view.Steps, s => s.Key == "signal");
        Assert.Contains(view.Steps, s => s.Key == "accept");
    }
}
