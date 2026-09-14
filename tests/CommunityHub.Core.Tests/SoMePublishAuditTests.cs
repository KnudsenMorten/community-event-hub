using CommunityHub.Core.Audit;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1216 — every LinkedIn post the dispatcher publishes (or fails to) is named in the audit trail.
/// Operator 2026-09-12: *"we need to have some posts go into the audit log as well (published)"*.
/// The job's per-RUN count row said how many; these rows say WHICH.
/// </summary>
public sealed class SoMePublishAuditTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly FixedClock Clock = new(Now);

    private static async Task<int> SeedAsync(Data.CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "T27", CommunityName = "Test Community", DisplayName = "Test Community 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        await new SoMeSettingsService(db, Clock)
            .SaveAsync(evt.Id, true, "urn:li:organization:1", null, null, false, null);
        return evt.Id;
    }

    private static async Task<SoMePost> DueApprovedAsync(
        Data.CommunityHubDbContext db, int eventId, string text)
    {
        var queue = new SoMeQueueService(db, Clock);
        var post = await queue.CreateAdHocPostAsync(eventId, text, null, Now.AddMinutes(-1), null, "org@example.test");
        await queue.SetActiveAsync(eventId, post.Id, true, "org@example.test");
        return post;
    }

    private static SoMeDispatchService NewDispatch(
        Data.CommunityHubDbContext db, ILinkedInPostPublisher publisher, IAuditTrail? audit) =>
        new(db, publisher, new SoMeSettingsService(db, Clock), new CapturingEmailSender(), Clock,
            new EmailContextAccessor(), audit: audit);

    [Fact]
    public async Task A_published_post_writes_one_audit_row_naming_it()
    {
        using var db = TestDb.New();
        var eventId = await SeedAsync(db);
        var post = await DueApprovedAsync(db, eventId, "Join us at the  community\nevent in February");

        await NewDispatch(db, new OkPublisher(), new AuditTrailService(db, Clock)).DispatchDueAsync(eventId);

        var row = Assert.Single(await db.AuditEntries.Where(a => a.Action == AuditActions.SoMePostPublished).ToListAsync());
        Assert.Equal(eventId, row.EventId);
        Assert.Equal(AuditOutcome.Success, row.Outcome);
        Assert.Equal("SoMePost", row.TargetType);
        Assert.Equal(post.Id.ToString(), row.TargetId);
        Assert.Contains($"#{post.Id} published", row.Summary);
        Assert.Contains("“Join us at the community event in February”", row.Summary);   // whitespace flattened
        Assert.Contains("urn:li:share:42", row.Detail);
    }

    [Fact]
    public async Task A_failed_publish_writes_a_failure_row_with_the_error()
    {
        using var db = TestDb.New();
        var eventId = await SeedAsync(db);
        await DueApprovedAsync(db, eventId, "boom");

        await NewDispatch(db, new ThrowingPublisher(), new AuditTrailService(db, Clock)).DispatchDueAsync(eventId);

        var row = Assert.Single(await db.AuditEntries.Where(a => a.Action == AuditActions.SoMePostFailed).ToListAsync());
        Assert.Equal(AuditOutcome.Failure, row.Outcome);
        Assert.Contains("kaboom", row.Detail);
    }

    [Fact]
    public async Task A_re_run_writes_no_second_row_and_long_text_is_cut()
    {
        using var db = TestDb.New();
        var eventId = await SeedAsync(db);
        await DueApprovedAsync(db, eventId, new string('x', 400));
        var dispatch = NewDispatch(db, new OkPublisher(), new AuditTrailService(db, Clock));

        await dispatch.DispatchDueAsync(eventId);
        await dispatch.DispatchDueAsync(eventId);

        var row = Assert.Single(await db.AuditEntries.ToListAsync());
        Assert.Contains(new string('x', 100) + "…", row.Summary);
        Assert.DoesNotContain(new string('x', 101), row.Summary);
    }

    [Fact]
    public async Task Without_an_audit_trail_publishing_is_unchanged()
    {
        using var db = TestDb.New();
        var eventId = await SeedAsync(db);
        await DueApprovedAsync(db, eventId, "quiet");

        var result = await NewDispatch(db, new OkPublisher(), audit: null).DispatchDueAsync(eventId);

        Assert.Equal(1, result.Published);
        Assert.Empty(await db.AuditEntries.ToListAsync());
    }

    private sealed class OkPublisher : ILinkedInPostPublisher
    {
        public bool CanPublish => true;
        public Task<LinkedInPublishResult> PublishAsync(LinkedInPost post, CancellationToken ct = default)
            => Task.FromResult(new LinkedInPublishResult(true, "urn:li:share:42", "ok"));
    }

    private sealed class ThrowingPublisher : ILinkedInPostPublisher
    {
        public bool CanPublish => true;
        public Task<LinkedInPublishResult> PublishAsync(LinkedInPost post, CancellationToken ct = default)
            => throw new InvalidOperationException("kaboom");
    }
}
