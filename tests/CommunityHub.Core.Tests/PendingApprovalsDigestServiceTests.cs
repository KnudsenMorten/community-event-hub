using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §203: a daily DIGEST to the organizer ops mailbox (info@expertslive.dk) when
/// there is something PENDING organizer action — people in the pre-selection queue
/// (prospective volunteers / speakers / media). Proves: ONE batched mail with the
/// category counts + direct queue links when pending &gt; 0, and NOTHING when zero.
/// </summary>
public sealed class PendingApprovalsDigestServiceTests
{
    private static (PendingApprovalsDigestService Svc, CapturingEmailSender Sender) NewService(
        CommunityHubDbContext db)
    {
        var sender = new CapturingEmailSender();
        var alerts = new EngineAlertSender(
            sender, new EmailContextAccessor(), TimeProvider.System,
            NullLogger<EngineAlertSender>.Instance);
        var svc = new PendingApprovalsDigestService(
            db, alerts,
            Options.Create(new EmailTemplateOptions { HubUrl = "https://hub.example.test" }));
        return (svc, sender);
    }

    private static async Task<int> NewEventAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "ELDK27", CommunityName = "C", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 9, 1), EndDate = new DateOnly(2027, 9, 2), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        return evt.Id;
    }

    private static void AddQueued(
        CommunityHubDbContext db, int eventId, string name,
        ParticipantRole role, ParticipantQueueSource source,
        ParticipantLifecycleState state = ParticipantLifecycleState.Inactive)
        => db.Participants.Add(new Participant
        {
            EventId = eventId, FullName = name, Email = $"{name.Replace(' ', '.')}@example.test",
            Role = role, QueueSource = source, LifecycleState = state, IsActive = false,
            IsTestUser = true,
        });

    [Fact]
    public async Task Sends_one_digest_with_counts_and_links_when_something_is_pending()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await NewEventAsync(db);

        AddQueued(db, eventId, "Pending Speaker", ParticipantRole.Speaker, ParticipantQueueSource.SessionizeSync);
        AddQueued(db, eventId, "Pending Volunteer", ParticipantRole.Volunteer, ParticipantQueueSource.VolunteerInterestForm);
        AddQueued(db, eventId, "Pending Media", ParticipantRole.Volunteer, ParticipantQueueSource.MediaTeamSignup);
        // An ALREADY-active person must NOT count as pending.
        AddQueued(db, eventId, "Active Person", ParticipantRole.Speaker, ParticipantQueueSource.SessionizeSync,
            ParticipantLifecycleState.Active);
        await db.SaveChangesAsync();

        var (svc, sender) = NewService(db);
        var sent = await svc.SendPendingDigestAsync(eventId);

        Assert.True(sent);
        var mail = Assert.Single(sender.Messages);
        Assert.Equal(PendingApprovalsDigestService.Recipient, mail.To);   // info@expertslive.dk
        Assert.Equal("info@expertslive.dk", mail.To);

        // Counts: 3 pending total (the Active one excluded), broken out per source.
        Assert.Contains("3", mail.Subject);
        Assert.Contains("New Sessionize-synced speakers", mail.Html);
        Assert.Contains("New volunteers", mail.Html);
        Assert.Contains("media", mail.Html);
        // DIRECT links to the queue page(s).
        Assert.Contains("https://hub.example.test/Organizer/PreselectionQueue", mail.Html);
        Assert.Contains("SourceFilter=SessionizeSync", mail.Html);

        // §500 — the mail must say WHY the speakers are held and WHAT unblocks them. Without it an
        // inactive speaker reads as a fault rather than a decision nobody has taken yet: it is the
        // missing category + ring, and only an organizer can supply those.
        Assert.Contains("speaker category", mail.Html);
        Assert.Contains("ring", mail.Html);
        Assert.Contains("activates the speaker automatically", mail.Html);
    }

    /// <summary>
    /// §500 — the explanation is tied to there BEING speakers waiting. A digest about volunteers
    /// only must not carry a paragraph about speaker categories: boilerplate that shows up when it
    /// does not apply is what trains people to stop reading these mails.
    /// </summary>
    [Fact]
    public void The_speaker_explanation_appears_only_when_speakers_are_waiting()
    {
        using var db = ScenarioFixture.NewDb();
        var (svc, _) = NewService(db);

        var withSpeakers = svc.BuildDigest(
            new PendingApprovalsDigestService.PendingCounts(2, 2, 0, 0));
        var withoutSpeakers = svc.BuildDigest(
            new PendingApprovalsDigestService.PendingCounts(2, 0, 2, 0));

        Assert.Contains("Why these speakers are inactive", withSpeakers.Html);
        Assert.DoesNotContain("Why these speakers are inactive", withoutSpeakers.Html);
    }

    [Fact]
    public async Task Sends_nothing_when_there_is_nothing_pending()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await NewEventAsync(db);
        // Only an active participant — nothing awaiting review.
        AddQueued(db, eventId, "Active Person", ParticipantRole.Speaker, ParticipantQueueSource.SessionizeSync,
            ParticipantLifecycleState.Active);
        await db.SaveChangesAsync();

        var (svc, sender) = NewService(db);
        var sent = await svc.SendPendingDigestAsync(eventId);

        Assert.False(sent);
        Assert.Empty(sender.Messages);
    }

    [Fact]
    public async Task Sponsors_in_the_queue_are_not_counted_as_pending_approvals()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await NewEventAsync(db);
        AddQueued(db, eventId, "Sponsor Contact", ParticipantRole.Sponsor, ParticipantQueueSource.Manual);
        await db.SaveChangesAsync();

        var (svc, sender) = NewService(db);
        var counts = await svc.CountPendingAsync(eventId);

        Assert.Equal(0, counts.QueueTotal);
        Assert.False(await svc.SendPendingDigestAsync(eventId));
        Assert.Empty(sender.Messages);
    }
}
