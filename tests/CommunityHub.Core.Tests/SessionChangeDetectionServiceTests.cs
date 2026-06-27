using System.Net.Http;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §38e session change-detection engine: a real Backstage time/location change emails the
/// affected speaker — gated by the kill switch, the released ring, AND the date gate (ring
/// 0/1 immediate; ring 2/3 only after the go-live date). First-populate seeds silently; no
/// source ⇒ graceful no-op. EF in-memory + a capturing sender + a canned agenda pull.
/// </summary>
public sealed class SessionChangeDetectionServiceTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset GoLive = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeGoLive = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterGoLive = new(2026, 12, 2, 9, 0, 0, TimeSpan.Zero);

    private sealed class NoOpContext : IEmailContextAccessor
    {
        public EmailContext? Current => null;
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) => new D();
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static ZohoClient NewZoho() =>
        new(new HttpClient(), new ZohoOptions()); // never called (pull is overridden)

    private static SessionChangeDetectionService NewService(
        CommunityHubDbContext db, CapturingEmailSender sender, DateTimeOffset now,
        BackstageSessionsResult pull)
    {
        var clock = new FixedClock(now);
        // §59: detection ENQUEUES a Pending delta instead of emailing inline. Wire the queue
        // (no ops-alert sender, so NotifyNew is a quiet no-op in these tests) so the engine's
        // enqueue path is exercised; the queue's apply-time speaker email isn't reached here.
        var queue = new SyncDeltaQueueService(db, clock: clock);
        return new(db, NewZoho(), new ZohoOptions(), new FeatureGateService(db),
            new RingResolver(db), sender, new NoOpContext(),
            templates: null, clock: clock,
            pullOverride: _ => Task.FromResult(pull),
            queue: queue);
    }

    private static async Task<int> SeedSessionAsync(
        CommunityHubDbContext db, string backstageId,
        DateTimeOffset? storedStart, DateTimeOffset? storedEnd, string? storedRoom)
    {
        var s = new Session
        {
            EventId = EventId,
            SessionizeId = $"sz-{backstageId}",
            Title = $"Talk {backstageId}",
            BackstageSessionId = backstageId,
            BackstageStartsAt = storedStart,
            BackstageEndsAt = storedEnd,
            BackstageRoom = storedRoom,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    /// <summary>Seed an UNLINKED CEH session (no BackstageSessionId) with a given title —
    /// the first-populate title-match path links it.</summary>
    private static async Task<int> SeedUnlinkedSessionAsync(
        CommunityHubDbContext db, string title)
    {
        var s = new Session
        {
            EventId = EventId,
            SessionizeId = $"sz-{title}",
            Title = title,
            BackstageSessionId = null,   // never linked yet
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    private static async Task<int> SeedSpeakerAsync(
        CommunityHubDbContext db, int sessionId, string email, Ring ring)
    {
        var p = new Participant
        {
            EventId = EventId,
            Email = email,
            FullName = "Sam Speaker",
            Role = ParticipantRole.Speaker,
            Ring = ring,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = sessionId, ParticipantId = p.Id });
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task EnableFeatureAsync(
        CommunityHubDbContext db, bool enabled, DateTimeOffset? activeFromBroad,
        Ring? releasedRing = null,
        // §57: these existing tests exercise the ACTIVE engine, so default the edition to
        // stage 3 (Zoho→CEH). The direction-gate tests below pass other stages explicitly.
        SessionSyncDirection direction = SessionSyncDirection.ZohoToCeh)
    {
        db.Events.Add(new Event
        {
            Id = EventId,
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = EventId,
            FeatureKey = SessionChangeDetectionService.FeatureKey,
            Enabled = enabled,
            ActiveFromForBroadRings = activeFromBroad,
            // The catalog default released ring is Ring1; a test exercising ring-2/3
            // speakers promotes the feature to Broad so the DATE gate (not the released
            // ring) is what governs them.
            ReleasedToRingOverride = releasedRing,
        });
        db.SessionSourceSettings.Add(new SessionSourceSetting
        {
            EventId = EventId,
            Source = SessionSourceKinds.Default,
            SyncDirection = direction,
        });
        await db.SaveChangesAsync();
    }

    private static BackstageSessionsResult Pull(params BackstageSession[] sessions) =>
        BackstageSessionsResult.Available(sessions);

    // -------------------------------------------------------------------------

    [Fact]
    public async Task First_populate_seeds_silently_and_never_emails()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true, activeFromBroad: null);
        var sid = await SeedSessionAsync(db, "bs-1", null, null, null); // nothing stored yet
        await SeedSpeakerAsync(db, sid, "sam@x.dk", Ring.Ring1);

        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender, AfterGoLive,
            Pull(new BackstageSession("bs-1",
                new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2027, 2, 9, 11, 0, 0, TimeSpan.Zero), "Room A")));

        var r = await svc.RunAsync(EventId);

        Assert.True(r.SourceAvailable);
        Assert.Equal(1, r.Matched);
        Assert.Equal(1, r.Seeded);
        Assert.Equal(0, r.Changed);
        Assert.Empty(sender.Messages);   // seed silently
        // The stored snapshot is now seeded.
        var s = db.Sessions.First();
        Assert.Equal("Room A", s.BackstageRoom);
        Assert.NotNull(s.BackstageChangeCheckedAt);
    }

    [Fact]
    public async Task Real_time_change_enqueues_and_never_emails_inline()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true, activeFromBroad: GoLive);
        var oldStart = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
        var sid = await SeedSessionAsync(db, "bs-1", oldStart, oldStart.AddHours(1), "Room A");
        await SeedSpeakerAsync(db, sid, "sam@x.dk", Ring.Ring1);

        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender, BeforeGoLive,
            Pull(new BackstageSession("bs-1",
                oldStart.AddHours(2), oldStart.AddHours(3), "Room A"))); // time moved

        var r = await svc.RunAsync(EventId);

        // §59: detected, ENQUEUED, NOT auto-applied and NOT emailed inline.
        Assert.Equal(1, r.Changed);
        Assert.Equal(1, r.Enqueued);
        Assert.Equal(0, r.Emailed);
        Assert.Empty(sender.Messages);

        // The stored snapshot is UNTOUCHED until an operator approves (the old→new diff
        // must survive for the approval).
        var s = db.Sessions.First();
        Assert.Equal(oldStart, s.BackstageStartsAt);

        // A Pending Session Update delta now exists carrying the StartsAt/EndsAt diff.
        var delta = Assert.Single(db.SyncDeltas);
        Assert.Equal(SyncDeltaStatus.Pending, delta.Status);
        Assert.Equal(SyncDeltaEntityType.Session, delta.EntityType);
        Assert.Equal(SyncDeltaChangeKind.Update, delta.ChangeKind);
        Assert.Equal(sid.ToString(), delta.EntityId);
        Assert.Contains(delta.Changes, c => c.Field == SyncDeltaQueueService.FieldStartsAt);
    }

    [Fact]
    public async Task Real_room_change_enqueues()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true, activeFromBroad: null);
        var start = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
        var sid = await SeedSessionAsync(db, "bs-1", start, start.AddHours(1), "Room A");
        await SeedSpeakerAsync(db, sid, "sam@x.dk", Ring.Ring1);

        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender, AfterGoLive,
            Pull(new BackstageSession("bs-1", start, start.AddHours(1), "Room B"))); // room moved

        var r = await svc.RunAsync(EventId);

        Assert.Equal(1, r.Changed);
        Assert.Equal(1, r.Enqueued);
        Assert.Empty(sender.Messages);
        // Snapshot untouched (kept for the approval); the delta carries the Room diff.
        Assert.Equal("Room A", db.Sessions.First().BackstageRoom);
        var delta = Assert.Single(db.SyncDeltas);
        Assert.Contains(delta.Changes, c => c.Field == SyncDeltaQueueService.FieldRoom && c.NewValue == "Room B");
    }

    [Fact]
    public async Task No_change_does_not_email_but_stamps_check_time()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true, activeFromBroad: null);
        var start = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
        var sid = await SeedSessionAsync(db, "bs-1", start, start.AddHours(1), "Room A");
        await SeedSpeakerAsync(db, sid, "sam@x.dk", Ring.Ring1);

        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender, AfterGoLive,
            Pull(new BackstageSession("bs-1", start, start.AddHours(1), "Room A"))); // identical

        var r = await svc.RunAsync(EventId);

        Assert.Equal(0, r.Changed);
        Assert.Empty(sender.Messages);
        Assert.NotNull(db.Sessions.First().BackstageChangeCheckedAt);
    }

    [Fact]
    public async Task Change_is_enqueued_regardless_of_speaker_ring()
    {
        // §59: the released-ring + date gates that previously decided who got the inline
        // speaker email no longer gate DETECTION — every real change is enqueued for the
        // operator (the ring/date gate is the operator's apply-time concern). A ring-3
        // speaker before go-live still produces a Pending delta.
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true, activeFromBroad: GoLive, releasedRing: Ring.Broad);
        var start = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
        var sid = await SeedSessionAsync(db, "bs-1", start, start.AddHours(1), "Room A");
        await SeedSpeakerAsync(db, sid, "broad@x.dk", Ring.Broad); // ring 3

        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender, BeforeGoLive,
            Pull(new BackstageSession("bs-1", start.AddHours(2), start.AddHours(3), "Room A")));

        var r = await svc.RunAsync(EventId);

        Assert.Equal(1, r.Changed);
        Assert.Equal(1, r.Enqueued);
        Assert.Empty(sender.Messages);          // never emails inline
        Assert.Single(db.SyncDeltas);
        Assert.Equal(start, db.Sessions.First().BackstageStartsAt); // snapshot untouched
    }

    [Fact]
    public async Task Kill_switch_off_means_no_enqueue_even_on_change()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: false, activeFromBroad: null); // killed
        var start = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
        var sid = await SeedSessionAsync(db, "bs-1", start, start.AddHours(1), "Room A");
        await SeedSpeakerAsync(db, sid, "sam@x.dk", Ring.Ring1);

        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender, AfterGoLive,
            Pull(new BackstageSession("bs-1", start.AddHours(2), start.AddHours(3), "Room A")));

        var r = await svc.RunAsync(EventId);

        // The change is still detected, but the kill switch suppresses enqueue + email.
        Assert.Equal(1, r.Changed);
        Assert.Equal(0, r.Enqueued);
        Assert.Equal(0, r.Emailed);
        Assert.Empty(sender.Messages);
        Assert.Empty(db.SyncDeltas);
    }

    [Fact]
    public async Task Unavailable_source_is_a_graceful_no_op()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true, activeFromBroad: null);
        var start = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
        var sid = await SeedSessionAsync(db, "bs-1", start, start.AddHours(1), "Room A");
        await SeedSpeakerAsync(db, sid, "sam@x.dk", Ring.Ring1);

        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender, AfterGoLive,
            BackstageSessionsResult.Unavailable("agenda scope not granted"));

        var r = await svc.RunAsync(EventId);

        Assert.False(r.SourceAvailable);
        Assert.Equal("agenda scope not granted", r.UnavailableReason);
        Assert.Empty(sender.Messages);
        // No write happened — stored values untouched.
        Assert.Null(db.Sessions.First().BackstageChangeCheckedAt);
    }

    // ---- §38e BUG 2: title-match first-populate (BackstageSessionId starts null) ----

    private static BackstageSession Bs(
        string id, string title, DateTimeOffset start, DateTimeOffset end, string? room) =>
        new(id, start, end, room, title);

    [Fact]
    public async Task Title_match_first_populate_sets_id_silently_and_never_emails()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true, activeFromBroad: null);
        // CEH session is NOT linked yet — BackstageSessionId is null.
        var sid = await SeedUnlinkedSessionAsync(db, "Identity Master Class");
        await SeedSpeakerAsync(db, sid, "sam@x.dk", Ring.Ring1);

        var start = new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero);
        var sender = new CapturingEmailSender();
        // The pulled Backstage session matches by NORMALIZED title (extra/trailing spaces +
        // different case must still match).
        var svc = NewService(db, sender, AfterGoLive,
            Pull(Bs("bs-id-99", "  identity   master class ", start, start.AddHours(7), "Room A")));

        var r = await svc.RunAsync(EventId);

        Assert.Equal(1, r.Matched);
        Assert.Equal(1, r.Seeded);
        Assert.Equal(0, r.Changed);
        Assert.Empty(sender.Messages); // first populate is SILENT

        var s = db.Sessions.Single();
        Assert.Equal("bs-id-99", s.BackstageSessionId); // id now POPULATED
        Assert.Equal(start, s.BackstageStartsAt);
        Assert.Equal("Room A", s.BackstageRoom);
        Assert.NotNull(s.BackstageChangeCheckedAt);
    }

    [Fact]
    public async Task After_title_first_populate_a_later_time_change_enqueues()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true, activeFromBroad: GoLive);
        var sid = await SeedUnlinkedSessionAsync(db, "Intune Master Class");
        await SeedSpeakerAsync(db, sid, "sam@x.dk", Ring.Ring1);

        var start = new DateTimeOffset(2027, 2, 9, 8, 0, 0, TimeSpan.Zero);

        // PASS 1: first populate via title — silent, sets the id, NO queue item.
        var sender1 = new CapturingEmailSender();
        var svc1 = NewService(db, sender1, BeforeGoLive,
            Pull(Bs("bs-77", "Intune Master Class", start, start.AddHours(7), "Room A")));
        var r1 = await svc1.RunAsync(EventId);
        Assert.Equal(1, r1.Seeded);
        Assert.Empty(sender1.Messages);
        Assert.Empty(db.SyncDeltas);
        Assert.Equal("bs-77", db.Sessions.Single().BackstageSessionId);

        // PASS 2: same id now linked; the time moves → a real CHANGE → ENQUEUED (not emailed).
        var sender2 = new CapturingEmailSender();
        var svc2 = NewService(db, sender2, BeforeGoLive,
            Pull(Bs("bs-77", "Intune Master Class", start.AddHours(2), start.AddHours(9), "Room A")));
        var r2 = await svc2.RunAsync(EventId);

        Assert.Equal(0, r2.Seeded);
        Assert.Equal(1, r2.Changed);
        Assert.Equal(1, r2.Enqueued);
        Assert.Empty(sender2.Messages);
        // Snapshot kept at the seeded baseline until approval.
        Assert.Equal(start, db.Sessions.Single().BackstageStartsAt);
        Assert.Single(db.SyncDeltas);
    }

    [Fact]
    public async Task Ambiguous_title_is_skipped_not_misassigned()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true, activeFromBroad: null);
        // TWO unlinked CEH sessions share the same title → ambiguous, must NOT auto-link.
        await SeedUnlinkedSessionAsync(db, "TBD");
        await SeedUnlinkedSessionAsync(db, "TBD");

        var start = new DateTimeOffset(2027, 2, 10, 14, 0, 0, TimeSpan.Zero);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender, AfterGoLive,
            Pull(Bs("bs-tbd", "TBD", start, start.AddHours(1), null)));

        var r = await svc.RunAsync(EventId);

        Assert.Equal(0, r.Matched);
        Assert.Equal(1, r.Unmatched);
        Assert.All(db.Sessions, s => Assert.Null(s.BackstageSessionId)); // neither linked
        Assert.Empty(sender.Messages);
    }

    [Fact]
    public async Task Unmatchable_backstage_session_is_skipped()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true, activeFromBroad: null);
        await SeedUnlinkedSessionAsync(db, "Known Talk");

        var start = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender, AfterGoLive,
            Pull(Bs("bs-x", "A Backstage Talk With No CEH Match", start, start.AddHours(1), null)));

        var r = await svc.RunAsync(EventId);

        Assert.Equal(0, r.Matched);
        Assert.Equal(1, r.Unmatched);
        Assert.Null(db.Sessions.Single().BackstageSessionId);
    }

    [Fact]
    public async Task Already_linked_by_id_still_wins_over_title()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true, activeFromBroad: null);
        var start = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
        // Linked session keeps its stored id even though its title differs from the pull.
        var sid = await SeedSessionAsync(db, "bs-linked", start, start.AddHours(1), "Room A");
        await SeedSpeakerAsync(db, sid, "sam@x.dk", Ring.Ring1);

        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender, AfterGoLive,
            Pull(Bs("bs-linked", "A Totally Different Title", start, start.AddHours(1), "Room A")));

        var r = await svc.RunAsync(EventId);

        Assert.Equal(1, r.Matched);
        Assert.Equal(0, r.Changed); // identical times/room → no change
        Assert.Equal("bs-linked", db.Sessions.Single().BackstageSessionId);
    }

    // ---- pure date-gate logic -------------------------------------------------

    [Theory]
    [InlineData(Ring.Ring0)]
    [InlineData(Ring.Ring1)]
    public void Date_gate_never_limits_ring0_or_ring1(Ring ring)
    {
        Assert.True(SessionChangeDetectionService.IsWithinDateGate(ring, GoLive, BeforeGoLive));
        Assert.True(SessionChangeDetectionService.IsWithinDateGate(ring, GoLive, AfterGoLive));
    }

    [Fact]
    public void Date_gate_holds_broad_rings_until_the_date()
    {
        Assert.False(SessionChangeDetectionService.IsWithinDateGate(Ring.Ring2, GoLive, BeforeGoLive));
        Assert.False(SessionChangeDetectionService.IsWithinDateGate(Ring.Broad, GoLive, BeforeGoLive));
        Assert.True(SessionChangeDetectionService.IsWithinDateGate(Ring.Ring2, GoLive, AfterGoLive));
        Assert.True(SessionChangeDetectionService.IsWithinDateGate(Ring.Broad, GoLive, AfterGoLive));
    }

    [Fact]
    public void Date_gate_with_no_date_falls_back_to_the_seeded_default()
    {
        // §38e/§52: a null ActiveFromForBroadRings no longer lets broad rings through —
        // it falls back to the 1 Dec 2026 default, so broad rings stay date-held by default.
        Assert.False(SessionChangeDetectionService.IsWithinDateGate(Ring.Broad, null, BeforeGoLive));
        Assert.False(SessionChangeDetectionService.IsWithinDateGate(Ring.Ring2, null, BeforeGoLive));
        // …and are allowed once now passes the default date.
        Assert.True(SessionChangeDetectionService.IsWithinDateGate(Ring.Broad, null, AfterGoLive));
        Assert.True(SessionChangeDetectionService.IsWithinDateGate(Ring.Ring2, null, AfterGoLive));
        // The default constant is exactly 1 Dec 2026 UTC.
        Assert.Equal(GoLive, SessionChangeDetectionService.DefaultBroadRingsActiveFrom);
    }

    // (The ring-3 date-gate-via-email tests were removed with §59: detection no longer
    // emails inline, so the released-ring + date gate no longer apply at detection time.
    // The date-gate LOGIC remains covered by the pure IsWithinDateGate unit tests below,
    // and the enqueue-regardless-of-ring behaviour by
    // Change_is_enqueued_regardless_of_speaker_ring above.)

    // ---- §57 SESSION SYNC DIRECTION gate --------------------------------------

    [Theory]
    [InlineData(SessionSyncDirection.SessionizeToCeh)] // stage 1 = default
    [InlineData(SessionSyncDirection.CehToZoho)]        // stage 2
    public async Task Engine_is_inert_unless_direction_is_stage3_ZohoToCeh(SessionSyncDirection dir)
    {
        using var db = ScenarioFixture.NewDb();
        // Feature fully enabled, a real time change pulled — only the DIRECTION holds it back.
        await EnableFeatureAsync(db, enabled: true, activeFromBroad: GoLive, direction: dir);
        var start = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
        var sid = await SeedSessionAsync(db, "bs-1", start, start.AddHours(1), "Room A");
        await SeedSpeakerAsync(db, sid, "sam@x.dk", Ring.Ring1);

        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender, AfterGoLive,
            Pull(new BackstageSession("bs-1", start.AddHours(2), start.AddHours(3), "Room B")));

        var r = await svc.RunAsync(EventId);

        // Inert: nothing pulled/matched/seeded/changed/written, and it is flagged as a
        // direction no-op (not a source-unavailable error).
        Assert.True(r.DirectionInactive);
        Assert.False(r.SourceAvailable);
        Assert.Contains($"stage {(int)dir}", r.UnavailableReason);
        Assert.Equal(0, r.Matched);
        Assert.Equal(0, r.Seeded);
        Assert.Equal(0, r.Changed);
        Assert.Empty(sender.Messages);
        // Nothing written back — the stored snapshot is untouched (no first-populate either).
        var s = db.Sessions.Single();
        Assert.Equal(start, s.BackstageStartsAt);
        Assert.Equal("Room A", s.BackstageRoom);
    }

    [Fact]
    public async Task Default_edition_with_no_setting_row_is_inert()
    {
        // No SessionSourceSetting row at all ⇒ defaults to stage 1 ⇒ §38e inert.
        using var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId,
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = EventId,
            FeatureKey = SessionChangeDetectionService.FeatureKey,
            Enabled = true,
        });
        var start = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
        var sid = await SeedSessionAsync(db, "bs-1", start, start.AddHours(1), "Room A");
        await SeedSpeakerAsync(db, sid, "sam@x.dk", Ring.Ring1);

        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender, AfterGoLive,
            Pull(new BackstageSession("bs-1", start.AddHours(2), start.AddHours(3), "Room B")));

        var r = await svc.RunAsync(EventId);

        Assert.True(r.DirectionInactive);
        Assert.Contains("stage 1", r.UnavailableReason);
        Assert.Empty(sender.Messages);
        Assert.Equal("Room A", db.Sessions.Single().BackstageRoom); // untouched
    }

    [Fact]
    public async Task Engine_runs_when_direction_is_stage3_ZohoToCeh()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true, activeFromBroad: GoLive,
            direction: SessionSyncDirection.ZohoToCeh);
        var start = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
        var sid = await SeedSessionAsync(db, "bs-1", start, start.AddHours(1), "Room A");
        await SeedSpeakerAsync(db, sid, "sam@x.dk", Ring.Ring1);

        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender, AfterGoLive,
            Pull(new BackstageSession("bs-1", start.AddHours(2), start.AddHours(3), "Room A")));

        var r = await svc.RunAsync(EventId);

        Assert.False(r.DirectionInactive);
        Assert.True(r.SourceAvailable);
        Assert.Equal(1, r.Changed);
        // §59: at stage 3 the engine runs and ENQUEUES the change (no inline email).
        Assert.Equal(1, r.Enqueued);
        Assert.Empty(sender.Messages);
        Assert.Single(db.SyncDeltas);
    }
}
