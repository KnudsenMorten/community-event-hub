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

    private static BackstageSession Bs(
        string id, string title, DateTimeOffset start, DateTimeOffset end, string? room) =>
        new(id, start, end, room, title);


    // ============ §1020: THE ENGINE IS RETIRED, PERMANENTLY, IN CODE ============
    //
    // Operator 2026-08-09: *"Zoho→CEH kill switch - disable/remove this from settings totally and
    // leave it off in the code. we cannot have anyone turn this on by mistake."*
    //
    // 🔴 WHAT HAPPENED TO THE 15 TESTS THAT WERE HERE. They asserted the §38e engine's real
    // behaviour — first-populate seeds silently, a time change enqueues, an ambiguous title is
    // skipped — and every one was CORRECT for the engine as it stood. They are gone because the
    // behaviour is gone: RunAsync now returns before it reads anything.
    //
    // ⚠️ They were NOT stale tests being tidied away, and the distinction matters to whoever revives
    // this. The engine SOURCE is deliberately kept (it is the only Zoho→CEH implementation, and
    // rebuilding it means re-learning §553's "not found is not gone" and §594's unreadable-field
    // trap). Its coverage did NOT survive the retirement, so **reviving the engine means rebuilding
    // these tests first** — otherwise it will run with nothing checking it.

    private static async Task<int> SeedRetirementCaseAsync(CommunityHubDbContext db)
    {
        await EnableFeatureAsync(db, enabled: true, activeFromBroad: null, releasedRing: Ring.Broad,
            direction: SessionSyncDirection.ZohoToCeh);
        return await SeedSessionAsync(db, "bs-1",
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero), "Hall A");
    }

    /// <summary>
    /// 🔴 THE POINT OF §1020: the old switch ON, the direction at stage 3, and a REAL change
    /// waiting in the agenda — and still nothing happens. If this ever fails, a settings toggle has
    /// become able to make Backstage a writer of the schedule again, behind CEH's back.
    /// </summary>
    [Fact]
    public async Task The_engine_is_inert_even_with_the_old_switch_on_and_a_real_change_waiting()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedRetirementCaseAsync(db);
        var mail = new CapturingEmailSender();

        var r = await NewService(db, mail, AfterGoLive,
            Pull(Bs("bs-1", "Talk bs-1",
                new DateTimeOffset(2027, 2, 10, 14, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2027, 2, 10, 15, 0, 0, TimeSpan.Zero), "Hall Z"))).RunAsync(EventId);

        Assert.True(r.DirectionInactive);
        Assert.Empty(mail.Messages);
        Assert.Equal(0, r.Enqueued);
    }

    /// <summary>
    /// 🔒 The stored Backstage snapshot must be left EXACTLY as it was. A retired engine that still
    /// re-seeded its baseline would quietly rewrite CEH's record of what Zoho holds — and CEH is
    /// now the owner, so that record is what the §1002 difference mail is diffed against.
    /// </summary>
    [Fact]
    public async Task It_writes_nothing_back_to_the_session()
    {
        using var db = ScenarioFixture.NewDb();
        var sid = await SeedRetirementCaseAsync(db);

        await NewService(db, new CapturingEmailSender(), AfterGoLive,
            Pull(Bs("bs-1", "Talk bs-1",
                new DateTimeOffset(2027, 2, 10, 14, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2027, 2, 10, 15, 0, 0, TimeSpan.Zero), "Hall Z"))).RunAsync(EventId);

        var s = db.Sessions.Single(x => x.Id == sid);
        Assert.Equal(new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero), s.BackstageStartsAt);
        Assert.Equal("Hall A", s.BackstageRoom);
        Assert.Null(s.BackstageChangeCheckedAt);
    }

    /// <summary>
    /// The reason is read by a human in a job log asking "why is this doing nothing?". It must
    /// answer that, and pre-empt the obvious next worry — the venue screens.
    /// </summary>
    [Fact]
    public async Task The_reason_says_it_is_permanent_and_that_signage_is_unaffected()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedRetirementCaseAsync(db);

        var r = await NewService(db, new CapturingEmailSender(), AfterGoLive, Pull()).RunAsync(EventId);

        Assert.True(r.DirectionInactive);
        var reason = r.UnavailableReason ?? string.Empty;
        Assert.Contains("PERMANENTLY OFF", reason);
        Assert.Contains("Signage", reason);
    }
}
