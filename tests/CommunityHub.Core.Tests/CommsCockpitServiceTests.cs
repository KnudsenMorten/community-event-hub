using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="CommsCockpitService"/> — the organizer Comms
/// cockpit (REQUIREMENTS §20 Organizer "Comms cockpit"). The cockpit consolidates
/// all outreach into one place: a unified email+SoMe <b>timeline</b>, the
/// <b>who-got-what</b> + per-campaign delivery views sourced from the real
/// <see cref="EmailLog"/> (incl. the honest dropped-by-allowlist / failed states),
/// the <b>resend</b> candidate set (participant-linked undelivered mail) and the
/// upcoming-scheduled call-out.
///
/// Uses the EF Core InMemory provider so the real DbContext mapping + LINQ run,
/// seeded with one rich edition plus a second edition planted to prove every number
/// is event-scoped. A separate empty-event test proves the snapshot is a safe
/// all-zero / all-delivered state. A pure-read test proves building never mutates.
/// </summary>
public sealed class CommsCockpitServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    // Fixed "now" so future/past + outcome maths is deterministic.
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"comms-{Guid.NewGuid():N}")
            .Options);

    private static CommsCockpitService NewSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(Now));

    /// <summary>Seed one rich edition; returns the linked participant id.</summary>
    private static async Task<int> SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "CX27", CommunityName = "Comms Test",
            DisplayName = "Comms Cockpit 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        });
        db.Events.Add(new Event
        {
            Id = OtherEventId, Code = "OTHER", CommunityName = "Other",
            DisplayName = "Other 2027",
            StartDate = new DateOnly(2027, 5, 1), EndDate = new DateOnly(2027, 5, 2),
            IsActive = false,
        });

        var alex = new Participant
        {
            EventId = EventId, Email = "alex@expertslive.dk", FullName = "Alex Speaker",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(alex);
        await db.SaveChangesAsync();

        void Email(string to, string? name, string category, bool success, string? error,
                   DateTimeOffset at, int? pid = null, int ev = EventId)
            => db.EmailLogs.Add(new EmailLog
            {
                EventId = ev, ToEmail = to, ActualToEmail = success ? to : string.Empty,
                RecipientName = name, Category = category, Subject = $"{category} subject",
                Success = success, Error = error, SentAt = at, ParticipantId = pid,
            });

        // --- Emails: a mix of outcomes, two recipients -----------------------
        // Alex (linked participant): a delivered welcome + a FAILED reminder (resend candidate).
        Email("alex@expertslive.dk", "Alex Speaker", "welcome", success: true, error: null,
            at: Now.AddDays(-3), pid: alex.Id);
        Email("alex@expertslive.dk", "Alex Speaker", "task-deadline", success: false,
            error: "SMTP 550 mailbox unavailable", at: Now.AddDays(-1), pid: alex.Id);

        // Sam (no linked participant): a DROPPED-by-allowlist broadcast — NOT a resend candidate
        // (no participant to target) but must count as Dropped, never Sent.
        Email("sam@example.test", "Sam Attendee", "broadcast", success: false,
            error: "Dropped: not in PROD allowlist", at: Now.AddDays(-2), pid: null);
        Email("sam@example.test", "Sam Attendee", "broadcast", success: true, error: null,
            at: Now.AddDays(-5), pid: null);

        // Other-edition email — scope guard (must never appear in EventId counts).
        Email("ghost@example.test", "Ghost", "welcome", success: true, error: null,
            at: Now.AddDays(-1), ev: OtherEventId);

        // An OLD email outside the 30-day window — must be excluded from the timeline.
        Email("alex@expertslive.dk", "Alex Speaker", "welcome", success: true, error: null,
            at: Now.AddDays(-90), pid: alex.Id);

        // --- SoMe posts ------------------------------------------------------
        void Post(SoMePostType type, SoMePostStatus status, bool active,
                  DateTimeOffset scheduled, DateTimeOffset? published, string? text,
                  string? lastError = null, int ev = EventId)
            => db.SoMePosts.Add(new SoMePost
            {
                EventId = ev, Type = type, Status = status, IsActive = active,
                ScheduledAtUtc = scheduled, PublishedAtUtc = published,
                ManualTextOverride = text, AutoGenerated = false, LastError = lastError,
            });

        // A future Active Queued post -> Scheduled (upcoming).
        Post(SoMePostType.Sponsor, SoMePostStatus.Queued, active: true,
            scheduled: Now.AddDays(2), published: null, text: "Thanks to our sponsor!");
        // A published post -> Sent.
        Post(SoMePostType.Speaker, SoMePostStatus.Published, active: true,
            scheduled: Now.AddDays(-2), published: Now.AddDays(-2), text: "Meet our speaker");
        // A failed post -> Failed.
        Post(SoMePostType.AdHoc, SoMePostStatus.Failed, active: true,
            scheduled: Now.AddDays(-1), published: null, text: "Oops",
            lastError: "LinkedIn 500");
        // An INACTIVE queued post -> Dropped (will never fire), not Scheduled.
        Post(SoMePostType.AdHoc, SoMePostStatus.Queued, active: false,
            scheduled: Now.AddDays(3), published: null, text: "Cancelled post");
        // Other-edition post — scope guard.
        Post(SoMePostType.Sponsor, SoMePostStatus.Published, active: true,
            scheduled: Now.AddDays(-1), published: Now.AddDays(-1), text: "Ghost post",
            ev: OtherEventId);

        await db.SaveChangesAsync();
        return alex.Id;
    }

    [Fact]
    public async Task Counters_are_real_outcomes_scoped_to_event()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var s = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal("Comms Cockpit 2027", s.EventDisplayName);
        // 4 in-window emails for this edition (the 90-day-old + ghost excluded).
        Assert.Equal(4, s.TotalEmails);
        Assert.Equal(2, s.EmailsSent);     // alex welcome + sam old broadcast
        Assert.Equal(1, s.EmailsDropped);  // sam allowlist drop
        Assert.Equal(1, s.EmailsFailed);   // alex task-deadline

        Assert.Equal(1, s.SoMeScheduled);  // the future Active Queued sponsor post
        Assert.Equal(1, s.SoMePublished);  // speaker post (ghost excluded)
        Assert.Equal(1, s.SoMeFailed);     // ad-hoc post
        Assert.False(s.AllDelivered);      // a drop + a fail + a SoMe fail
    }

    [Fact]
    public async Task Timeline_unifies_email_and_some_future_first_window_enforced()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var s = await NewSvc(db).BuildAsync(EventId);

        // 4 emails + 4 in-window SoMe posts (ghost excluded) = 8 items.
        Assert.Equal(8, s.Timeline.Count);
        Assert.Contains(s.Timeline, i => i.Channel == CommsChannel.Email);
        Assert.Contains(s.Timeline, i => i.Channel == CommsChannel.SoMe);

        // The future scheduled post floats to the very top.
        var first = s.Timeline[0];
        Assert.True(first.IsFuture);
        Assert.Equal(CommsChannel.SoMe, first.Channel);
        Assert.Equal(CommsOutcome.Scheduled, first.Outcome);

        // The 90-day-old email is outside the window — not on the timeline.
        Assert.DoesNotContain(s.Timeline, i => i.When == Now.AddDays(-90));

        // Exactly one upcoming item (the future Active Queued post); the inactive
        // queued post is NOT upcoming (it will never fire -> Dropped).
        Assert.Single(s.UpcomingScheduled);
        Assert.All(s.UpcomingScheduled, u => Assert.True(u.IsFuture));
    }

    [Fact]
    public async Task Some_inactive_queued_post_is_dropped_not_scheduled()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var s = await NewSvc(db).BuildAsync(EventId);

        var inactive = Assert.Single(
            s.Timeline, i => i.Channel == CommsChannel.SoMe && i.Title.Contains("Cancelled"));
        Assert.Equal(CommsOutcome.Dropped, inactive.Outcome);
        Assert.False(inactive.IsFuture);
    }

    [Fact]
    public async Task WhoGotWhat_is_per_recipient_real_outcome_undelivered_first()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var s = await NewSvc(db).BuildAsync(EventId);

        // Two distinct recipients in-window: alex + sam.
        Assert.Equal(2, s.WhoGotWhat.Count);

        var alex = Assert.Single(s.WhoGotWhat, w => w.Email == "alex@expertslive.dk");
        Assert.Equal(1, alex.Sent);     // welcome (the 90-day-old is out of window)
        Assert.Equal(0, alex.Dropped);
        Assert.Equal(1, alex.Failed);   // task-deadline
        Assert.True(alex.HasUndelivered);
        Assert.NotNull(alex.ParticipantId);

        var sam = Assert.Single(s.WhoGotWhat, w => w.Email == "sam@example.test");
        Assert.Equal(1, sam.Sent);      // never count the dropped one as sent
        Assert.Equal(1, sam.Dropped);
        Assert.Equal(0, sam.Failed);
        Assert.Null(sam.ParticipantId);

        // Both have undelivered mail, ordered by most-undelivered then recency.
        Assert.All(s.WhoGotWhat, w => Assert.True(w.HasUndelivered));
    }

    [Fact]
    public async Task Campaigns_group_by_category_with_real_outcome()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var s = await NewSvc(db).BuildAsync(EventId);

        var welcome = Assert.Single(s.Campaigns, c => c.Category == "welcome");
        Assert.Equal(1, welcome.Sent);          // only alex welcome in-window
        Assert.False(welcome.HasUndelivered);

        var broadcast = Assert.Single(s.Campaigns, c => c.Category == "broadcast");
        Assert.Equal(1, broadcast.Sent);
        Assert.Equal(1, broadcast.Dropped);
        Assert.True(broadcast.HasUndelivered);

        var taskDl = Assert.Single(s.Campaigns, c => c.Category == "task-deadline");
        Assert.Equal(1, taskDl.Failed);
        Assert.True(taskDl.HasUndelivered);
    }

    [Fact]
    public async Task Resend_candidates_are_participant_linked_undelivered_only()
    {
        using var db = NewDb();
        var alexId = await SeedAsync(db);

        var s = await NewSvc(db).BuildAsync(EventId);

        // Only alex's FAILED task-deadline qualifies: sam's drop has no participant
        // to target, and the welcome was delivered. One candidate per participant.
        var c = Assert.Single(s.ResendCandidates);
        Assert.Equal(alexId, c.ParticipantId);
        Assert.Equal("Alex Speaker", c.Name);
        Assert.Equal("alex@expertslive.dk", c.Email);
        Assert.Equal(CommsOutcome.Failed, c.Outcome);
        Assert.Equal("task-deadline", c.Category);
        Assert.Contains("550", c.Error);
    }

    [Fact]
    public async Task Snapshot_is_pure_read_only_and_repeatable()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = NewSvc(db);

        var a = await svc.BuildAsync(EventId);
        var b = await svc.BuildAsync(EventId);

        Assert.Equal(a.TotalEmails, b.TotalEmails);
        Assert.Equal(a.EmailsFailed, b.EmailsFailed);
        Assert.Equal(a.Timeline.Count, b.Timeline.Count);
        Assert.Equal(a.ResendCandidates.Count, b.ResendCandidates.Count);
        // Building it never wrote rows.
        Assert.Equal(6, await db.EmailLogs.CountAsync());   // 4 in-window + 90-day + ghost
        Assert.Equal(5, await db.SoMePosts.CountAsync());
    }

    [Fact]
    public async Task Other_edition_returns_its_own_scoped_snapshot()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var s = await NewSvc(db).BuildAsync(OtherEventId);

        Assert.Equal("Other 2027", s.EventDisplayName);
        Assert.Equal(1, s.TotalEmails);       // only the ghost email
        Assert.Equal(1, s.EmailsSent);
        Assert.Equal(0, s.EmailsFailed);
        Assert.Equal(1, s.SoMePublished);     // only the ghost post
        Assert.Empty(s.ResendCandidates);     // ghost email had no participant link
    }

    [Fact]
    public async Task Empty_event_is_a_safe_all_delivered_snapshot()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "EMPTY", CommunityName = "Empty",
            DisplayName = "Empty 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var s = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal("Empty 2027", s.EventDisplayName);
        Assert.Equal(0, s.TotalEmails);
        Assert.Empty(s.Timeline);
        Assert.Empty(s.WhoGotWhat);
        Assert.Empty(s.Campaigns);
        Assert.Empty(s.ResendCandidates);
        Assert.Empty(s.UpcomingScheduled);
        Assert.True(s.AllDelivered);          // nothing failed/dropped -> calm state
    }
}
