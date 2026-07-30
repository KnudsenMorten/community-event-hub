using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO (REQUIREMENTS §193): the hub e-mails a participant a calendar
/// INVITATION for a single item (a task due date, a session, …) instead of
/// offering a "Download .ics". Proves the <see cref="CalendarInviteEmailService"/>
/// contract:
///  - a valid RFC 5545 VEVENT (METHOD:REQUEST) is attached + sent;
///  - an all-day item emits a DATE-valued VEVENT (a deadline reminder);
///  - it routes to the participant's effective calendar address
///    (calendar override ?? contact override ?? identity);
///  - it is GATED on the organizer's CalendarSyncEnabled switch (off => nothing).
/// </summary>
public sealed class CalendarInviteScenarioTests
{
    private static CalendarInviteEmailService NewService(
        Data.CommunityHubDbContext db, CapturingEmailSender sender) =>
        new(db, sender, new EmailContextAccessor(), ScenarioFixture.Clock);

    // §432: returns the resolved recipient alongside the sent flag. CalendarInviteResult converts
    // implicitly to bool, so the existing assertions in this file read unchanged.
    private static Task<CalendarInviteResult> SendTaskReminderAsync(
        CalendarInviteEmailService svc, int participantId) =>
        svc.SendItemInviteAsync(
            participantId,
            uid: $"task-1@test",
            summary: "Upload final presentation",
            description: "Deadline from your Event Hub.",
            location: null,
            start: new DateTimeOffset(2027, 1, 10, 0, 0, 0, TimeSpan.Zero),
            end: new DateTimeOffset(2027, 1, 11, 0, 0, 0, TimeSpan.Zero),
            allDay: true,
            fileName: "reminder.ics",
            introHtml: "Here is a reminder.");

    [Fact]
    public async Task Invite_sends_an_all_day_request_vevent()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        var sent = await SendTaskReminderAsync(svc, seed.VolunteerId);

        Assert.True(sent);
        Assert.Single(sender.Sent);
        var ics = sender.LastIcs;
        Assert.NotNull(ics);
        Assert.StartsWith("BEGIN:VCALENDAR", ics);
        Assert.Contains("METHOD:REQUEST", ics);
        Assert.Contains("BEGIN:VEVENT", ics);
        Assert.Contains("SUMMARY:Upload final presentation", ics);
        // All-day deadline → DATE-valued DTSTART, not a timed UTC stamp.
        Assert.Contains("DTSTART;VALUE=DATE:20270110", ics);
        Assert.Contains("END:VCALENDAR", ics);
    }

    [Fact]
    public async Task Invite_names_the_recipient_as_attendee_and_the_hub_as_organizer()
    {
        // §234 6: a METHOD:REQUEST is only processed as a REAL invitation when the
        // ORGANIZER is the SENDER (the hub's from-address) and the RECIPIENT is an
        // ATTENDEE. The old code set the recipient as organizer — clients refuse to
        // process "an invite from yourself", leaving the .ics an inert attachment.
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        Assert.True(await SendTaskReminderAsync(svc, seed.VolunteerId));
        var ics = sender.LastIcs!;
        var to = sender.Sent.Single().To;

        // ORGANIZER = the shipped from-address defaults (no EmailOptions injected here).
        Assert.Contains("ORGANIZER;CN=Experts Live Denmark:mailto:info@expertslive.dk", ics);
        // ATTENDEE = the recipient, awaiting action, replies not solicited.
        Assert.Contains($"ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=FALSE;", ics);
        Assert.Contains($":mailto:{to}", ics);
        Assert.DoesNotContain($"ORGANIZER;CN={to}", ics);
    }

    [Fact]
    public async Task Invite_routes_to_the_contact_override()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        var sp = await db.SpeakerProfiles.FirstAsync(x => x.ParticipantId == seed.SpeakerOneId);
        sp.ContactEmailOverride = "preferred@example.test";
        await db.SaveChangesAsync();

        Assert.True(await SendTaskReminderAsync(svc, seed.SpeakerOneId));
        Assert.Equal("preferred@example.test", sender.Sent.Single().To);
    }

    [Fact]
    public async Task Invite_routes_to_the_calendar_email_over_the_contact_override()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        // §141: with BOTH set, the calendar-specific email wins for the invite.
        var sp = await db.SpeakerProfiles.FirstAsync(x => x.ParticipantId == seed.SpeakerOneId);
        sp.CalendarEmail = "calendar@example.test";
        sp.ContactEmailOverride = "preferred@example.test";
        await db.SaveChangesAsync();

        Assert.True(await SendTaskReminderAsync(svc, seed.SpeakerOneId));
        Assert.Equal("calendar@example.test", sender.Sent.Single().To);
    }

    [Fact]
    public async Task No_invite_is_sent_when_calendar_sync_is_disabled()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        var ev = await db.Events.FirstAsync(e => e.Id == seed.EventId);
        ev.CalendarSyncEnabled = false;
        await db.SaveChangesAsync();

        Assert.False(await SendTaskReminderAsync(svc, seed.VolunteerId));
        Assert.Empty(sender.Sent);
    }
}
