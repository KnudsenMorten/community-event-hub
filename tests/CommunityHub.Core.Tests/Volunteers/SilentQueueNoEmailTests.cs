using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// §150 SILENT QUEUE: the allocation queue makes NO noise. The step-2 engine
/// PROPOSALS and every lead/organizer DRAFT edit
/// (<see cref="VolunteerAllocationService"/>: AddDraft / RemoveDraft / drop-out
/// backfill seeding / Discard) and a NO-OP commit emit ZERO email — the ONLY email
/// path is <see cref="CommitNotificationService.NotifyCommittedAsync"/>, called by the
/// page handler with the people a commit actually changed. These tests run the whole
/// queue lifecycle and assert the recording sender stays empty; the positive
/// "commit sends" path is covered by CommitNotificationServiceTests.
/// </summary>
public sealed class SilentQueueNoEmailTests
{
    private const int EventId = 21;
    private const int OrganizerId = 1;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-27T10:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"silent-queue-{Guid.NewGuid():N}").Options);

    private static VolunteerStructureService.ActorContext Organizer() =>
        new(OrganizerId, "org@example.test", ParticipantRole.Organizer, EventId);

    private static VolunteerAllocationService NewSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(), new FeatureGateService(db), new RingResolver(db));

    // Recording sender wired ONLY into CommitNotificationService; if any queue
    // operation ever sent mail, it would have to funnel through here.
    private sealed class RecordingSender : IEmailSender
    {
        public readonly List<string> Sent = new();
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
        {
            Sent.Add(to);
            return Task.CompletedTask;
        }
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
    }

    private static int AddVolunteer(CommunityHubDbContext db, string email)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email.ToLowerInvariant(), FullName = email,
            Role = ParticipantRole.Volunteer, IsActive = true, Ring = Ring.Broad,
        };
        db.Participants.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    private static int AddTask(CommunityHubDbContext db, string title, int needed)
    {
        var t = new VolunteerTask { EventId = EventId, Title = title, ResourcesNeeded = needed };
        db.VolunteerTasks.Add(t);
        db.SaveChanges();
        return t.Id;
    }

    [Fact]
    public async Task Proposals_draft_edits_and_a_noop_commit_send_no_email()
    {
        using var db = NewDb();
        db.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });
        db.SaveChanges();

        var dropped = AddVolunteer(db, "dropped@x.test");
        var a = AddVolunteer(db, "a@x.test");
        var b = AddVolunteer(db, "b@x.test");
        var task1 = AddTask(db, "Registration", 3);
        var task2 = AddTask(db, "Wardrobe", 2);

        // A real assignment for the leaver so the drop-out re-plan has slots to free.
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = task1, ParticipantId = dropped, AssignedByEmail = "org@example.test",
        });
        db.SaveChanges();

        var ctx = new EmailContextAccessor();
        var sender = new RecordingSender();
        var alloc = NewSvc(db);
        var notify = new CommitNotificationService(db, sender, ctx);

        // 1) Seeding PROPOSALS (queue draft adds — what the step-2 engine produces).
        await alloc.AddDraftAsync(Organizer(), task1, a);
        await alloc.AddDraftAsync(Organizer(), task2, b);
        Assert.Empty(sender.Sent);

        // 2) Drop-out re-plan: frees the leaver's real assignment + SEEDS backfill
        //    drafts. A mutation, but still silent.
        await alloc.SeedDropoutBackfillAsync(Organizer(), dropped);
        Assert.Empty(sender.Sent);

        // 3) Lead edits the queue: remove one drafted allocation.
        await alloc.RemoveDraftAsync(Organizer(), task2, b);
        Assert.Empty(sender.Sent);

        // 4) Discard the whole draft queue — nothing is assigned, nothing is mailed.
        await alloc.DiscardAsync(Organizer());
        Assert.Empty(sender.Sent);

        // 5) A NO-OP commit (empty queue ⇒ nothing committed ⇒ no affected people):
        //    the page handler would call the notifier with an empty set ⇒ zero sends.
        var result = await alloc.CommitAsync(Organizer());
        Assert.Equal(0, result.Committed);
        var issued = await notify.NotifyCommittedAsync(
            Organizer(), EventId, Array.Empty<int>(), VolunteerAllocationService.FeatureKey);
        Assert.Equal(0, issued);

        // The ONLY email path never fired across the entire silent lifecycle.
        Assert.Empty(sender.Sent);
    }
}
