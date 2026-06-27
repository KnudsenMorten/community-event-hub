using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// REQUIREMENTS §58 / §56 — the NEVER-AUTO-DELETE disappearance alert. After a Sessionize
/// import, CEH speakers/sessions that are LINKED to Sessionize but were NOT in the latest
/// pull must be EMAILED to the operator (ring-exempt ops path) and NEVER deleted. The email
/// only fires when the disappeared set is non-empty (non-spam).
/// </summary>
public sealed class SessionizeDisappearanceDetectorTests
{
    private const int EventId = 1;

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

    /// <summary>A real EngineAlertSender wired to a capturing sender — verifies the actual
    /// ring-exempt ops delivery path, not a hand-rolled mock.</summary>
    private static EngineAlertSender NewAlerts(CapturingEmailSender sender) =>
        new(sender, new NoOpContext(),
            new FixedClock(new DateTimeOffset(2026, 6, 25, 9, 0, 0, TimeSpan.Zero)),
            NullLogger<EngineAlertSender>.Instance);

    private static async Task<int> SeedSpeakerAsync(
        Data.CommunityHubDbContext db, string fullName, string? sessionizeSpeakerId)
    {
        var p = new Participant
        {
            EventId = EventId,
            Email = $"{fullName.Replace(' ', '.').ToLowerInvariant()}@example.test",
            FullName = fullName,
            Role = ParticipantRole.Speaker,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId,
            ParticipantId = p.Id,
            SessionizeSpeakerId = sessionizeSpeakerId,
        });
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task<int> SeedSessionAsync(
        Data.CommunityHubDbContext db, string title, string sessionizeId, bool hubAdded = false)
    {
        var s = new Session
        {
            EventId = EventId,
            SessionizeId = sessionizeId,
            Title = title,
            IsHubAdded = hubAdded,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    [Fact]
    public async Task Disappeared_speakers_and_sessions_are_emailed_and_never_deleted()
    {
        using var db = ScenarioFixture.NewDb();
        // Two linked speakers; one will be missing from the pull.
        var keepSpeaker = await SeedSpeakerAsync(db, "Kept Speaker", "spk-keep");
        var goneSpeaker = await SeedSpeakerAsync(db, "Gone Speaker", "spk-gone");
        // Two linked sessions; one will be missing from the pull.
        var keepSession = await SeedSessionAsync(db, "Kept Talk", "sess-keep");
        var goneSession = await SeedSessionAsync(db, "Gone Talk", "sess-gone");

        var sender = new CapturingEmailSender();
        var det = new SessionizeDisappearanceDetector(db, NewAlerts(sender));

        // The latest pull contains only the "keep" ids — "gone" disappeared.
        var r = await det.ScanAsync(
            EventId, new[] { "spk-keep" }, new[] { "sess-keep" });

        Assert.True(r.Any);
        Assert.True(r.Emailed);
        Assert.Equal("spk-gone", Assert.Single(r.DisappearedSpeakers).SessionizeId);
        Assert.Equal("sess-gone", Assert.Single(r.DisappearedSessions).SessionizeId);

        // EXACTLY ONE ops email, to the ops mailbox, naming both disappeared entities.
        var m = Assert.Single(sender.Messages);
        Assert.Equal(EngineAlertSender.Recipient, m.To);
        Assert.Contains("Gone Speaker", m.Html);
        Assert.Contains("Gone Talk", m.Html);
        Assert.DoesNotContain("Kept Speaker", m.Html);
        Assert.DoesNotContain("Kept Talk", m.Html);

        // NEVER deletes: all four rows still present.
        Assert.Equal(2, db.Participants.Count());
        Assert.Equal(2, db.SpeakerProfiles.Count());
        Assert.Equal(2, db.Sessions.Count());
        Assert.NotNull(db.SpeakerProfiles.Find(
            db.SpeakerProfiles.Single(x => x.ParticipantId == goneSpeaker).Id));
        Assert.NotNull(db.Sessions.Find(goneSession));
        Assert.NotNull(db.Sessions.Find(keepSession));
        Assert.NotNull(db.Participants.Find(keepSpeaker));
    }

    [Fact]
    public async Task Nothing_disappeared_means_no_email()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedSpeakerAsync(db, "Kept Speaker", "spk-keep");
        await SeedSessionAsync(db, "Kept Talk", "sess-keep");

        var sender = new CapturingEmailSender();
        var det = new SessionizeDisappearanceDetector(db, NewAlerts(sender));

        // Everything linked is still in the pull ⇒ nothing missing ⇒ no email (non-spam).
        var r = await det.ScanAsync(
            EventId, new[] { "spk-keep" }, new[] { "sess-keep" });

        Assert.False(r.Any);
        Assert.False(r.Emailed);
        Assert.Empty(sender.Messages);
    }

    [Fact]
    public async Task Hub_added_sessions_never_count_as_disappeared()
    {
        using var db = ScenarioFixture.NewDb();
        // A hub-added session (synthetic hub-* id) is NOT from Sessionize, so an empty pull
        // must NOT flag it. An UNLINKED-to-Sessionize concern only — verify it's excluded.
        await SeedSessionAsync(db, "Sponsor Session", "hub-" + Guid.NewGuid().ToString("N"), hubAdded: true);

        var sender = new CapturingEmailSender();
        var det = new SessionizeDisappearanceDetector(db, NewAlerts(sender));

        // Pull returns no sessions at all; the hub-added one must still not be flagged.
        var r = await det.ScanAsync(EventId, Array.Empty<string>(), Array.Empty<string>());

        Assert.False(r.Any);
        Assert.Empty(r.DisappearedSessions);
        Assert.Empty(sender.Messages);
        Assert.Single(db.Sessions); // never deleted
    }

    // ===================== §59: disappearance → delta queue ================

    [Fact]
    public async Task Disappeared_entities_are_enqueued_as_pending_deltas_and_approve_never_deletes()
    {
        using var db = ScenarioFixture.NewDb();
        var goneSpeaker = await SeedSpeakerAsync(db, "Gone Speaker", "spk-gone");
        var goneSession = await SeedSessionAsync(db, "Gone Talk", "sess-gone");

        var sender = new CapturingEmailSender();
        var queue = new SyncDeltaQueueService(db);
        // Queue wired alongside the existing summary email — both must happen.
        var det = new SessionizeDisappearanceDetector(db, NewAlerts(sender), queue);

        var r = await det.ScanAsync(EventId, Array.Empty<string>(), Array.Empty<string>());

        Assert.True(r.Any);
        Assert.True(r.Emailed);     // summary email kept (delivery itself covered elsewhere; the
                                    // ops-alert throttle key is process-static so don't re-assert
                                    // the captured message here).

        // Both disappeared entities are now Pending Disappeared deltas in the queue.
        var pending = await queue.ListPendingAsync(EventId);
        Assert.Equal(2, pending.Count);
        Assert.All(pending, d => Assert.Equal(SyncDeltaChangeKind.Disappeared, d.ChangeKind));
        var speakerDelta = Assert.Single(pending, d => d.EntityType == SyncDeltaEntityType.Speaker);
        var sessionDelta = Assert.Single(pending, d => d.EntityType == SyncDeltaEntityType.Session);
        Assert.Equal("Gone Speaker", speakerDelta.EntityLabel);
        Assert.Equal("Gone Talk", sessionDelta.EntityLabel);
        Assert.Equal(goneSession.ToString(), sessionDelta.EntityId);

        // APPROVE a Disappeared item = ACKNOWLEDGE ONLY — terminal at Approved, never Applied,
        // and NEVER deletes the underlying row.
        var decision = await queue.ApproveAsync(sessionDelta.Id, "ops@x.dk");
        Assert.True(decision.Found);
        Assert.False(decision.Applied);                         // acknowledge-only, no apply
        var after = await queue.GetAsync(sessionDelta.Id);
        Assert.Equal(SyncDeltaStatus.Approved, after!.Status);  // NOT Applied
        Assert.NotNull(db.Sessions.Find(goneSession));          // row still present
        Assert.NotNull(db.SpeakerProfiles.Find(
            db.SpeakerProfiles.Single(x => x.ParticipantId == goneSpeaker).Id));
    }

    [Fact]
    public async Task Disappearance_enqueue_dedupes_across_repeated_runs()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedSessionAsync(db, "Gone Talk", "sess-gone");

        var queue = new SyncDeltaQueueService(db);
        var det = new SessionizeDisappearanceDetector(db, alerts: null, queue: queue);

        await det.ScanAsync(EventId, Array.Empty<string>(), Array.Empty<string>());
        await det.ScanAsync(EventId, Array.Empty<string>(), Array.Empty<string>());

        // Two runs over the same missing session ⇒ ONE pending delta (dedupe), not two.
        Assert.Single(await queue.ListPendingAsync(EventId));
    }

    [Fact]
    public async Task No_alerts_sender_still_detects_but_skips_email_and_never_deletes()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedSpeakerAsync(db, "Gone Speaker", "spk-gone");

        // No EngineAlertSender wired (optional dep null) — the scan still runs read-only.
        var det = new SessionizeDisappearanceDetector(db, alerts: null);

        var r = await det.ScanAsync(EventId, Array.Empty<string>(), Array.Empty<string>());

        Assert.True(r.Any);
        Assert.False(r.Emailed);                 // no sender ⇒ email skipped
        Assert.Single(r.DisappearedSpeakers);
        Assert.Single(db.SpeakerProfiles);       // never deleted
        Assert.Single(db.Participants);
    }
}
