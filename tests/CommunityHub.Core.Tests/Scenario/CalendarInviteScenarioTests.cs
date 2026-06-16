using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: when a person is activated, the hub attaches an .ics invite to the
/// activation email so the event lands in their calendar (REQUIREMENTS §5).
///
/// Proves the <see cref="CalendarInviteEmailService"/> contract:
///  - a valid RFC 5545 VEVENT (METHOD:REQUEST) is sent on first activation;
///  - it is idempotent (a second pass sends nothing — one invite per person, ever);
///  - it routes to the speaker's effective address (override ?? identity);
///  - it is GATED on the organizer's CalendarSyncEnabled switch (off => no invite).
/// </summary>
public sealed class CalendarInviteScenarioTests
{
    private static CalendarInviteEmailService NewService(
        Data.CommunityHubDbContext db, CapturingEmailSender sender) =>
        new(db, sender, new EmailContextAccessor(), ScenarioFixture.Clock);

    /// <summary>
    /// Mark a seeded participant fully Active (IsActive AND LifecycleState=Active)
    /// — the state a person is in once an organizer activates them, which is when
    /// the calendar invite is sent.
    /// </summary>
    private static async Task ActivateAsync(Data.CommunityHubDbContext db, int participantId)
    {
        var p = await db.Participants.FirstAsync(x => x.Id == participantId);
        p.IsActive = true;
        p.LifecycleState = ParticipantLifecycleState.Active;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Activation_sends_one_ics_invite_with_a_valid_vevent()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);
        await ActivateAsync(db, seed.VolunteerId);

        var sent = await svc.SendActivationInviteAsync(seed.VolunteerId);

        Assert.True(sent);
        Assert.Single(sender.Sent);
        // The .ics attachment is a valid VEVENT meeting request.
        var ics = sender.LastIcs;
        Assert.NotNull(ics);
        Assert.StartsWith("BEGIN:VCALENDAR", ics);
        Assert.Contains("METHOD:REQUEST", ics);
        Assert.Contains("BEGIN:VEVENT", ics);
        Assert.Contains("END:VCALENDAR", ics);
    }

    [Fact]
    public async Task Activation_invite_is_idempotent()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);
        await ActivateAsync(db, seed.VolunteerId);

        Assert.True(await svc.SendActivationInviteAsync(seed.VolunteerId));
        Assert.False(await svc.SendActivationInviteAsync(seed.VolunteerId)); // no re-send
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Invite_routes_to_the_speaker_effective_email_override()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        await ActivateAsync(db, seed.SpeakerOneId);
        // Speaker one has a SpeakerProfile in the seed; set a contact override.
        var sp = await db.SpeakerProfiles.FirstAsync(x => x.ParticipantId == seed.SpeakerOneId);
        sp.ContactEmailOverride = "preferred@example.test";
        await db.SaveChangesAsync();

        Assert.True(await svc.SendActivationInviteAsync(seed.SpeakerOneId));
        Assert.Equal("preferred@example.test", sender.Sent.Single().To);
    }

    [Fact]
    public async Task No_invite_is_sent_when_calendar_sync_is_disabled()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);
        await ActivateAsync(db, seed.VolunteerId);

        var ev = await db.Events.FirstAsync(e => e.Id == seed.EventId);
        ev.CalendarSyncEnabled = false;
        await db.SaveChangesAsync();

        Assert.False(await svc.SendActivationInviteAsync(seed.VolunteerId));
        Assert.Empty(sender.Sent);
    }
}
