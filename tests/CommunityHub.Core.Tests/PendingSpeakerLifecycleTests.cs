using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §498/§499 — "Pending speakers" must show the people there is still a DECISION to take about,
/// and only those.
///
/// <para>The operator's screenshot had <c>Test-Speaker-Inactive</c> sitting in the queue with its
/// own blocker reading <i>"participant inactive"</i>: a speaker who has left, listed as awaiting
/// approval. But the naive fix — hide everyone inactive — would have deleted the queue's whole
/// purpose, because a Sessionize import arrives inactive BY DESIGN and reviewing those people is
/// exactly the work this screen exists for.</para>
///
/// <para>So both directions are asserted here. A test that only proved the drop-out disappears
/// would pass just as happily against a screen that had gone permanently empty.</para>
/// </summary>
public sealed class PendingSpeakerLifecycleTests
{
    private const int EventId = 1;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"pending-speakers-{Guid.NewGuid():N}")
            .Options);

    private static async Task<CommunityHubDbContext> SeedAsync()
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9),
        });

        // (a) Sessionize newcomer — never activated. THE QUEUE'S REASON TO EXIST.
        db.Participants.Add(new Participant
        {
            Id = 10, EventId = EventId, Email = "newcomer@example.test", FullName = "New Comer",
            Role = ParticipantRole.Speaker,
            IsActive = false, LifecycleState = ParticipantLifecycleState.Inactive,
            DeactivatedByOrganizerAt = null,
        });

        // (b) Withdrew — an organizer deactivated them (§253 G8 tombstone). GONE.
        db.Participants.Add(new Participant
        {
            Id = 11, EventId = EventId, Email = "withdrew@example.test", FullName = "With Drew",
            Role = ParticipantRole.Speaker,
            IsActive = false, LifecycleState = ParticipantLifecycleState.Inactive,
            DeactivatedByOrganizerAt = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
        });

        // (c) Was active, login closed by a sync — lifecycle left Active. ALSO GONE.
        db.Participants.Add(new Participant
        {
            Id = 12, EventId = EventId, Email = "lockedout@example.test", FullName = "Locked Out",
            Role = ParticipantRole.Speaker,
            IsActive = false, LifecycleState = ParticipantLifecycleState.Active,
            DeactivatedByOrganizerAt = null,
        });

        foreach (var pid in new[] { 10, 11, 12 })
        {
            // No category ⇒ every one of them WOULD be pending on the §299 6.1 blocker, so the
            // only thing that can remove them from the list is the lifecycle rule under test.
            db.SpeakerProfiles.Add(new SpeakerProfile { EventId = EventId, ParticipantId = pid });
        }

        await db.SaveChangesAsync();
        return db;
    }

    private static SpeakerApprovalService Sut(CommunityHubDbContext db) =>
        new(db, new FeatureGateService(db), new FixedClock());

    [Fact]
    public async Task A_never_activated_speaker_STAYS_in_the_queue()
    {
        // The direction that matters most: this is the review work. If the §498 fix had simply
        // hidden "inactive" people, the queue would silently empty and nobody would be approved.
        using var db = await SeedAsync();

        var result = await Sut(db).PendingAsync(EventId);

        Assert.Contains(result.Speakers, p => p.Email == "newcomer@example.test");
    }

    [Fact]
    public async Task Speakers_who_LEFT_are_gone_from_the_queue()
    {
        // Both exits: the organizer withdrawal (tombstone) and the sync lockout (lifecycle still
        // Active). Neither has a decision left to take.
        using var db = await SeedAsync();

        var result = await Sut(db).PendingAsync(EventId);

        Assert.DoesNotContain(result.Speakers, p => p.Email == "withdrew@example.test");
        Assert.DoesNotContain(result.Speakers, p => p.Email == "lockedout@example.test");
        Assert.Single(result.Speakers);   // exactly the newcomer remains
    }
}
