using System.Globalization;
using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for the §59 DELTA-APPROVAL QUEUE (<see cref="SyncDeltaQueueService"/>):
/// enqueue + dedupe of a pending change, approve APPLIES a session time change and emails
/// the speaker, reject keeps the current value, and a Disappeared item is NEVER deleted on
/// approve (acknowledge only). EF in-memory + a capturing sender + a real audit service.
/// </summary>
public sealed class SyncDeltaQueueServiceTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 6, 26, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class NoOpContext : IEmailContextAccessor
    {
        public EmailContext? Current => null;
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) => new D();
    }

    private static SyncDeltaQueueService NewService(
        CommunityHubDbContext db, CapturingEmailSender? sender = null, IAuditTrail? audit = null) =>
        new(db,
            clock: new FixedClock(Now),
            audit: audit,
            alerts: null,
            sender: sender,
            context: sender is null ? null : new NoOpContext(),
            templates: null);

    private static async Task SeedEventAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> SeedSessionAsync(
        CommunityHubDbContext db, string title,
        DateTimeOffset? start, DateTimeOffset? end, string? room, string? speakerEmail = null)
    {
        var s = new Session
        {
            EventId = EventId, SessionizeId = $"sz-{title}", Title = title,
            BackstageSessionId = "bs-1",
            BackstageStartsAt = start, BackstageEndsAt = end, BackstageRoom = room,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();

        if (speakerEmail is not null)
        {
            var p = new Participant
            {
                EventId = EventId, Email = speakerEmail, FullName = "Sam Speaker",
                Role = ParticipantRole.Speaker,
            };
            db.Participants.Add(p);
            await db.SaveChangesAsync();
            db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = p.Id });
            await db.SaveChangesAsync();
        }
        return s.Id;
    }

    private static IReadOnlyList<SyncFieldChange> TimeMove(
        DateTimeOffset oldStart, DateTimeOffset oldEnd, DateTimeOffset newStart, DateTimeOffset newEnd) =>
        SyncDeltaQueueService.BuildSessionChanges(
            oldStart, oldEnd, "Room A", newStart, newEnd, "Room A");

    // -------------------------------------------------------------------------

    [Fact]
    public async Task Enqueue_creates_one_pending_delta()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEventAsync(db);
        var sid = await SeedSessionAsync(db, "Talk", Now, Now.AddHours(1), "Room A");
        var svc = NewService(db);

        var start = Now;
        await svc.EnqueueSessionUpdateAsync(
            EventId, sid, "Talk", SessionSyncDirection.ZohoToCeh,
            TimeMove(start, start.AddHours(1), start.AddHours(2), start.AddHours(3)));

        var pending = await svc.ListPendingAsync(EventId);
        var d = Assert.Single(pending);
        Assert.Equal(SyncDeltaStatus.Pending, d.Status);
        Assert.Equal(SyncDeltaChangeKind.Update, d.ChangeKind);
        Assert.Equal(sid.ToString(CultureInfo.InvariantCulture), d.EntityId);
        Assert.Contains(d.Changes, c => c.Field == SyncDeltaQueueService.FieldStartsAt);
    }

    [Fact]
    public async Task Enqueue_dedupes_an_existing_pending_delta_in_place()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEventAsync(db);
        var sid = await SeedSessionAsync(db, "Talk", Now, Now.AddHours(1), "Room A");
        var svc = NewService(db);
        var start = Now;

        // First detection.
        await svc.EnqueueSessionUpdateAsync(
            EventId, sid, "Talk", SessionSyncDirection.ZohoToCeh,
            TimeMove(start, start.AddHours(1), start.AddHours(2), start.AddHours(3)));
        // Second detection of the SAME (event, type, id, kind) with a newer target.
        await svc.EnqueueSessionUpdateAsync(
            EventId, sid, "Talk (renamed)", SessionSyncDirection.ZohoToCeh,
            TimeMove(start, start.AddHours(1), start.AddHours(5), start.AddHours(6)));

        var pending = await svc.ListPendingAsync(EventId);
        var d = Assert.Single(pending);                 // NOT duplicated
        Assert.Equal("Talk (renamed)", d.EntityLabel);  // refreshed in place
        var startChange = d.Changes.First(c => c.Field == SyncDeltaQueueService.FieldStartsAt);
        Assert.Equal(start.AddHours(5).ToString("o", CultureInfo.InvariantCulture), startChange.NewValue);
    }

    [Fact]
    public async Task Approve_applies_a_session_time_change_and_emails_the_speaker()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEventAsync(db);
        var start = Now;
        var sid = await SeedSessionAsync(db, "Keynote", start, start.AddHours(1), "Room A", "sam@x.dk");

        var sender = new CapturingEmailSender();
        var audit = new AuditTrailService(db, new FixedClock(Now));
        var svc = NewService(db, sender, audit);

        var delta = await svc.EnqueueSessionUpdateAsync(
            EventId, sid, "Keynote", SessionSyncDirection.ZohoToCeh,
            TimeMove(start, start.AddHours(1), start.AddHours(2), start.AddHours(3)));

        var result = await svc.ApproveAsync(delta.Id, "olivia@x.dk");

        Assert.True(result.Found);
        Assert.True(result.Applied);
        Assert.True(result.Emailed);

        // The CEH session's stored Backstage* now reflects the approved change.
        var s = db.Sessions.Single();
        Assert.Equal(start.AddHours(2), s.BackstageStartsAt);
        Assert.Equal(start.AddHours(3), s.BackstageEndsAt);

        // §88: the approved Zoho change ALSO lands on the CEH DISPLAY fields the hub reads
        // (StartsAt/EndsAt/Room), so My Sessions / the public page show the new time — not
        // only the Backstage* change-tracking snapshot.
        Assert.Equal(start.AddHours(2), s.StartsAt);
        Assert.Equal(start.AddHours(3), s.EndsAt);

        // The speaker was emailed via the session-change template.
        var m = Assert.Single(sender.Messages);
        Assert.Equal("sam@x.dk", m.To);
        Assert.Contains("Keynote", m.Subject);

        // The delta is terminal at Applied + audited.
        var reloaded = (await svc.GetAsync(delta.Id))!;
        Assert.Equal(SyncDeltaStatus.Applied, reloaded.Status);
        Assert.Equal("olivia@x.dk", reloaded.DecidedByEmail);
        Assert.NotNull(reloaded.AppliedAt);
        Assert.Contains(db.AuditEntries, a => a.Action == "syncqueue.approve" && a.TargetId == delta.Id.ToString());
    }

    [Fact]
    public async Task Approve_change_email_uses_TBD_placeholder_when_old_time_is_unset()
    {
        // §83: the "schedule changed" email must never render an EMPTY When/Where cell. When
        // the session had no scheduled time/room yet (the old side is unset), the email shows
        // a "TBD" placeholder rather than a blank cell — and is still sent because the new
        // side IS a real change.
        using var db = ScenarioFixture.NewDb();
        await SeedEventAsync(db);
        var start = Now;
        var sid = await SeedSessionAsync(db, "Talk", start: null, end: null, room: null, "sam@x.dk");

        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        var changes = SyncDeltaQueueService.BuildSessionChanges(
            null, null, null, start.AddHours(2), start.AddHours(3), "Hall C");
        var delta = await svc.EnqueueSessionUpdateAsync(
            EventId, sid, "Talk", SessionSyncDirection.ZohoToCeh, changes);

        var result = await svc.ApproveAsync(delta.Id, "olivia@x.dk");
        Assert.True(result.Applied);
        Assert.True(result.Emailed);

        var m = Assert.Single(sender.Messages);
        Assert.Contains("TBD", m.Html);                 // unset old time/room → placeholder
        Assert.DoesNotContain("(not scheduled)", m.Html);
        Assert.DoesNotContain("(not set)", m.Html);
        // The new (real) values still render, so the email is not blank.
        Assert.Contains("Hall C", m.Html);
    }

    [Fact]
    public async Task Reject_keeps_the_current_value_and_does_not_email()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEventAsync(db);
        var start = Now;
        var sid = await SeedSessionAsync(db, "Keynote", start, start.AddHours(1), "Room A", "sam@x.dk");

        var sender = new CapturingEmailSender();
        var audit = new AuditTrailService(db, new FixedClock(Now));
        var svc = NewService(db, sender, audit);

        var delta = await svc.EnqueueSessionUpdateAsync(
            EventId, sid, "Keynote", SessionSyncDirection.ZohoToCeh,
            TimeMove(start, start.AddHours(1), start.AddHours(2), start.AddHours(3)));

        var result = await svc.RejectAsync(delta.Id, "olivia@x.dk", "Upstream entered in error");

        Assert.True(result.Found);
        Assert.False(result.Applied);
        Assert.Empty(sender.Messages);                          // no speaker email on reject
        Assert.Equal(start, db.Sessions.Single().BackstageStartsAt); // value untouched

        var reloaded = (await svc.GetAsync(delta.Id))!;
        Assert.Equal(SyncDeltaStatus.Rejected, reloaded.Status);
        Assert.Equal("Upstream entered in error", reloaded.Notes);
        Assert.Empty(await svc.ListPendingAsync(EventId));
        Assert.Contains(db.AuditEntries, a => a.Action == "syncqueue.reject");
    }

    [Fact]
    public async Task Approve_on_a_disappeared_item_never_deletes()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEventAsync(db);
        var sid = await SeedSessionAsync(db, "Vanished Talk", Now, Now.AddHours(1), "Room A");
        var svc = NewService(db, audit: new AuditTrailService(db, new FixedClock(Now)));

        var delta = await svc.EnqueueDisappearanceAsync(
            EventId, SyncDeltaEntityType.Session,
            sid.ToString(CultureInfo.InvariantCulture), "Vanished Talk",
            SessionSyncDirection.SessionizeToCeh);

        var result = await svc.ApproveAsync(delta.Id, "olivia@x.dk");

        Assert.True(result.Found);
        Assert.False(result.Applied);                       // acknowledged, not applied
        // The session STILL EXISTS — approve on a Disappeared item must never delete.
        Assert.NotNull(await db.Sessions.FindAsync(sid));
        var reloaded = (await svc.GetAsync(delta.Id))!;
        Assert.Equal(SyncDeltaStatus.Approved, reloaded.Status); // terminal at Approved, not Applied
        Assert.Null(reloaded.AppliedAt);
    }

    [Fact]
    public async Task Decisions_are_idempotent_on_a_non_pending_row()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEventAsync(db);
        var start = Now;
        var sid = await SeedSessionAsync(db, "Talk", start, start.AddHours(1), "Room A", "sam@x.dk");
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        var delta = await svc.EnqueueSessionUpdateAsync(
            EventId, sid, "Talk", SessionSyncDirection.ZohoToCeh,
            TimeMove(start, start.AddHours(1), start.AddHours(2), start.AddHours(3)));

        var first = await svc.ApproveAsync(delta.Id, "olivia@x.dk");
        var second = await svc.ApproveAsync(delta.Id, "olivia@x.dk"); // already decided

        Assert.True(first.Found);
        Assert.False(second.Found);                 // no-op
        Assert.Single(sender.Messages);             // emailed exactly once
    }

    // -------------------------------------------------------------------------
    //  VOLUNTEER availability edit (§45/§59)
    // -------------------------------------------------------------------------

    private static async Task<int> SeedVolunteerAsync(CommunityHubDbContext db, string name)
    {
        var p = new Participant
        {
            EventId = EventId, Email = $"{name}@v.dk", FullName = name,
            Role = ParticipantRole.Volunteer,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task SeedAvailabilityAsync(
        CommunityHubDbContext db, int participantId, DateOnly day,
        VolunteerAvailabilityLevel level, string? note)
    {
        db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
        {
            EventId = EventId, ParticipantId = participantId, Day = day, Level = level, Note = note,
        });
        await db.SaveChangesAsync();
    }

    private static IReadOnlyList<SyncFieldChange> AvailabilityMove(
        DateOnly day, string oldLabel, string newLabel,
        VolunteerAvailabilityLevel newLevel, string? newNote) =>
        SyncDeltaQueueService.BuildVolunteerAvailabilityChanges(new[]
        {
            (day.ToString("yyyy-MM-dd"), oldLabel, newLabel, newLevel, newNote),
        });

    [Fact]
    public async Task Volunteer_edit_enqueues_a_pending_update_and_does_not_apply()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEventAsync(db);
        var day = new DateOnly(2027, 2, 9);
        var vid = await SeedVolunteerAsync(db, "Vera");
        // Already submitted: a Full-day row is the baseline.
        await SeedAvailabilityAsync(db, vid, day, VolunteerAvailabilityLevel.Full, "[Full day]");
        var svc = NewService(db);

        // A later EDIT (Full → Half) is enqueued, NOT applied.
        var delta = await svc.EnqueueVolunteerAvailabilityUpdateAsync(
            EventId, vid, "Vera",
            AvailabilityMove(day, "Full day", "Morning", VolunteerAvailabilityLevel.Half, "[Morning 9–12]"));

        var pending = Assert.Single(await svc.ListPendingAsync(EventId));
        Assert.Equal(SyncDeltaEntityType.Volunteer, pending.EntityType);
        Assert.Equal(SyncDeltaChangeKind.Update, pending.ChangeKind);
        Assert.Equal(vid.ToString(CultureInfo.InvariantCulture), pending.EntityId);
        Assert.Equal("Vera", pending.EntityLabel);

        // The stored availability is UNTOUCHED while the change is pending.
        var row = db.VolunteerDayAvailabilities.Single();
        Assert.Equal(VolunteerAvailabilityLevel.Full, row.Level);
    }

    [Fact]
    public async Task Approve_applies_the_volunteer_availability_change()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEventAsync(db);
        var day = new DateOnly(2027, 2, 9);
        var vid = await SeedVolunteerAsync(db, "Vera");
        await SeedAvailabilityAsync(db, vid, day, VolunteerAvailabilityLevel.Full, "[Full day]");
        var audit = new AuditTrailService(db, new FixedClock(Now));
        var svc = NewService(db, audit: audit);

        var delta = await svc.EnqueueVolunteerAvailabilityUpdateAsync(
            EventId, vid, "Vera",
            AvailabilityMove(day, "Full day", "Morning", VolunteerAvailabilityLevel.Half, "[Morning 9–12] need 13:00 free"));

        var result = await svc.ApproveAsync(delta.Id, "olivia@x.dk");

        Assert.True(result.Found);
        Assert.True(result.Applied);

        // The volunteer's day row now holds the approved new value.
        var row = db.VolunteerDayAvailabilities.Single(x => x.Day == day);
        Assert.Equal(VolunteerAvailabilityLevel.Half, row.Level);
        Assert.Equal("[Morning 9–12] need 13:00 free", row.Note);

        var reloaded = (await svc.GetAsync(delta.Id))!;
        Assert.Equal(SyncDeltaStatus.Applied, reloaded.Status);
        Assert.NotNull(reloaded.AppliedAt);
        // The volunteer was NOT deleted.
        Assert.NotNull(await db.Participants.FindAsync(vid));
        Assert.Contains(db.AuditEntries, a => a.Action == "syncqueue.approve" && a.TargetId == delta.Id.ToString());
    }

    [Fact]
    public async Task Reject_keeps_the_volunteers_current_availability()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEventAsync(db);
        var day = new DateOnly(2027, 2, 9);
        var vid = await SeedVolunteerAsync(db, "Vera");
        await SeedAvailabilityAsync(db, vid, day, VolunteerAvailabilityLevel.Full, "[Full day]");
        var svc = NewService(db);

        var delta = await svc.EnqueueVolunteerAvailabilityUpdateAsync(
            EventId, vid, "Vera",
            AvailabilityMove(day, "Full day", "Not able to help", VolunteerAvailabilityLevel.Unavailable, "[Not able to help]"));

        var result = await svc.RejectAsync(delta.Id, "olivia@x.dk", "Spoke to the volunteer — keep full day");

        Assert.True(result.Found);
        Assert.False(result.Applied);

        // The current (approved) availability is kept untouched.
        var row = db.VolunteerDayAvailabilities.Single(x => x.Day == day);
        Assert.Equal(VolunteerAvailabilityLevel.Full, row.Level);
        Assert.Equal("[Full day]", row.Note);

        var reloaded = (await svc.GetAsync(delta.Id))!;
        Assert.Equal(SyncDeltaStatus.Rejected, reloaded.Status);
        Assert.Empty(await svc.ListPendingAsync(EventId));
    }

    [Fact]
    public async Task Approve_creates_a_missing_volunteer_day_row_idempotently()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEventAsync(db);
        var existingDay = new DateOnly(2027, 2, 9);
        var newDay = new DateOnly(2027, 2, 10);
        var vid = await SeedVolunteerAsync(db, "Vera");
        // Already submitted day 1; the edit also sets a day that had no row yet.
        await SeedAvailabilityAsync(db, vid, existingDay, VolunteerAvailabilityLevel.Full, "[Full day]");
        var svc = NewService(db);

        var delta = await svc.EnqueueVolunteerAvailabilityUpdateAsync(
            EventId, vid, "Vera",
            SyncDeltaQueueService.BuildVolunteerAvailabilityChanges(new[]
            {
                (newDay.ToString("yyyy-MM-dd"), "(not set)", "Half day", VolunteerAvailabilityLevel.Half, (string?)"[Half day]"),
            }));

        await svc.ApproveAsync(delta.Id, "olivia@x.dk");

        var added = db.VolunteerDayAvailabilities.Single(x => x.Day == newDay);
        Assert.Equal(VolunteerAvailabilityLevel.Half, added.Level);
        // The pre-existing day is untouched.
        Assert.Equal(VolunteerAvailabilityLevel.Full,
            db.VolunteerDayAvailabilities.Single(x => x.Day == existingDay).Level);
    }

    // -------------------------------------------------------------------------
    //  §38e/§58 SPEAKER ZohoToCeh Update apply (distinct from the CehToZoho push arm)
    // -------------------------------------------------------------------------

    private static async Task<int> SeedSpeakerAsync(
        CommunityHubDbContext db, string name, string? tagline, string? bio)
    {
        var p = new Participant
        {
            EventId = EventId, Email = "sam@x.dk", FullName = name, Role = ParticipantRole.Speaker,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId, ParticipantId = p.Id, BackstageSpeakerId = "bs-1",
            // Stored baseline = old value the detection engine last saw.
            Tagline = tagline, Biography = bio,
            BackstageName = name, BackstageTagline = tagline, BackstageBio = bio,
        });
        await db.SaveChangesAsync();
        return p.Id;
    }

    [Fact]
    public async Task Approve_applies_the_zoho_values_to_the_ceh_speaker_profile()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEventAsync(db);
        var pid = await SeedSpeakerAsync(db, "Sam Speaker", "Old Tagline", "Old bio.");
        var audit = new AuditTrailService(db, new FixedClock(Now));
        var svc = NewService(db, audit: audit);

        // A §58 ZohoToCeh Speaker Update delta carrying the upstream Zoho values.
        var changes = SyncDeltaQueueService.BuildSpeakerZohoChanges(
            oldName: "Sam Speaker", oldTagline: "Old Tagline", oldBio: "Old bio.",
            oldCountry: null, oldLinkedIn: null, oldTwitter: null,
            newName: "Samuel Speaker", newTagline: "New Tagline", newBio: "New bio.",
            newCountry: "DK", newLinkedIn: "https://li/sam", newTwitter: "@sam");
        var delta = await svc.EnqueueAsync(new SyncDelta
        {
            EventId = EventId,
            EntityType = SyncDeltaEntityType.Speaker,
            EntityId = pid.ToString(CultureInfo.InvariantCulture),
            EntityLabel = "Sam Speaker",
            Source = SessionSyncDirection.ZohoToCeh,
            ChangeKind = SyncDeltaChangeKind.Update,
            Changes = changes,
        });

        var result = await svc.ApproveAsync(delta.Id, "olivia@x.dk");

        Assert.True(result.Found);
        Assert.True(result.Applied);
        Assert.False(result.Emailed);   // ZohoToCeh apply does not email

        // The CEH speaker profile now holds the approved Zoho values.
        var profile = db.SpeakerProfiles.Single();
        Assert.Equal("New Tagline", profile.Tagline);
        Assert.Equal("New bio.", profile.Biography);
        Assert.Equal("DK", profile.Country);
        Assert.Equal("https://li/sam", profile.LinkedIn);
        Assert.Equal("@sam", profile.Twitter);
        // Name was split into First/Last from the Zoho display name.
        Assert.Equal("Samuel", profile.FirstName);
        Assert.Equal("Speaker", profile.LastName);
        // The stored Backstage* baseline is refreshed to the applied value.
        Assert.Equal("New Tagline", profile.BackstageTagline);
        Assert.NotNull(profile.BackstageChangeCheckedAt);

        var reloaded = (await svc.GetAsync(delta.Id))!;
        Assert.Equal(SyncDeltaStatus.Applied, reloaded.Status);
        Assert.NotNull(reloaded.AppliedAt);
        // The speaker was NOT deleted.
        Assert.NotNull(await db.Participants.FindAsync(pid));
        Assert.Contains(db.AuditEntries, a => a.Action == "syncqueue.approve" && a.TargetId == delta.Id.ToString());
    }

    [Fact]
    public async Task ListPending_and_recentlyDecided_are_event_scoped()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEventAsync(db);
        db.Events.Add(new Event
        {
            Id = 2, CommunityName = "O", DisplayName = "O", Code = "O27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        await db.SaveChangesAsync();
        var sid = await SeedSessionAsync(db, "Talk", Now, Now.AddHours(1), "Room A");
        var svc = NewService(db);

        await svc.EnqueueSessionUpdateAsync(
            EventId, sid, "Talk", SessionSyncDirection.ZohoToCeh,
            TimeMove(Now, Now.AddHours(1), Now.AddHours(2), Now.AddHours(3)));
        await svc.EnqueueDisappearanceAsync(
            2, SyncDeltaEntityType.Session, "99", "Other-event item",
            SessionSyncDirection.SessionizeToCeh);

        Assert.Single(await svc.ListPendingAsync(EventId));
        Assert.Single(await svc.ListPendingAsync(2));
    }
}
