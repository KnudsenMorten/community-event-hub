using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §724 — the <c>welcome-email</c> FEATURE RING must not gate a welcome. Only its ON/OFF may.
/// </summary>
/// <remarks>
/// <para>🔴 <b>This is the test that was missing, and its absence cost a live incident.</b> §707.6
/// removed the feature-ring clamp from the transport — *"the transport no longer asks a FEATURE for
/// a ring under any circumstance"* — but a SECOND, earlier enforcement point survived inside
/// <see cref="WelcomeEmailService"/>. Nothing asserted the decision, so the leftover was invisible.</para>
///
/// <para>§721 is what it cost: <c>welcome-speaker</c> and <c>welcome-sponsor</c> were set to Ring 3
/// and the Settings page said Ring 3, while <c>welcome-email</c> sat at Ring 2 — so every Ring-3
/// speaker and sponsor was skipped before their mail's own ring was ever read. 13 speakers and 19
/// sponsors were being chased by a task reminder without ever having been welcomed. Operator
/// 2026-07-31: <i>"but we killed the welcome-email gate !!!"</i> — he was right.</para>
///
/// <para>The audience now lives in exactly one place: the mail's own (mail × role) ring, applied in
/// <c>BrevoEmailSender</c>. These tests pin the two halves of that — the ring no longer blocks, the
/// switch still does.</para>
/// </remarks>
public sealed class WelcomeFeatureRingNoLongerGatesTests
{
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-07-31T09:00:00Z");
    }

    private static WelcomeEmailService NewService(
        CommunityHubDbContext db, CapturingEmailSender sender, FeatureGateService gate, RingResolver rings)
    {
        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            PrivateTemplateDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ceh-no-private-welcome-724"),
        }));
        return new WelcomeEmailService(db, templates, sender, new FixedClock(), gate: gate, rings: rings);
    }

    /// <summary>A Ring-3 (broad) speaker — the exact §721 shape.</summary>
    private static async Task<(int eventId, int participantId)> SeedBroadSpeakerAsync(CommunityHubDbContext db)
    {
        var ev = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = ev.Id, Email = "broad.speaker@x.dk", FullName = "Pat Lee",
            Role = ParticipantRole.Speaker, IsActive = true, Ring = Ring.Broad,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return (ev.Id, p.Id);
    }

    private static async Task SetFeatureAsync(
        CommunityHubDbContext db, int eventId, bool enabled, Ring releasedTo)
    {
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = eventId,
            FeatureKey = "welcome-email",
            Enabled = enabled,
            ReleasedToRing = releasedTo,
            UpdatedAt = DateTimeOffset.Parse("2026-07-31T08:00:00Z"),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_broad_ring_speaker_is_welcomed_even_when_the_FEATURE_ring_is_narrower()
    {
        using var db = ScenarioFixture.NewDb();
        var (eventId, pid) = await SeedBroadSpeakerAsync(db);

        // The §721 configuration exactly: participant Ring 3, feature released only to Ring 2.
        await SetFeatureAsync(db, eventId, enabled: true, releasedTo: Ring.Ring2);

        var sender = new CapturingEmailSender();
        var gate = new FeatureGateService(db);
        var svc = NewService(db, sender, gate, new RingResolver(db));

        var sent = await svc.SendWelcomeAsync(pid);

        Assert.True(sent, "A Ring-3 speaker must still be welcomed: the FEATURE ring no longer "
            + "gates a welcome (§724). Only the mail's own ring may narrow the audience, and that "
            + "is applied in BrevoEmailSender, not here.");
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task The_feature_SWITCH_still_stops_the_welcome()
    {
        using var db = ScenarioFixture.NewDb();
        var (eventId, pid) = await SeedBroadSpeakerAsync(db);

        // Ring wide open — only the ON/OFF is off.
        await SetFeatureAsync(db, eventId, enabled: false, releasedTo: Ring.Broad);

        var sender = new CapturingEmailSender();
        var gate = new FeatureGateService(db);
        var svc = NewService(db, sender, gate, new RingResolver(db));

        var sent = await svc.SendWelcomeAsync(pid);

        Assert.False(sent, "welcome-email switched OFF must still stop every welcome — §707.6 kept "
            + "the feature's ON/OFF and removed only its RING.");
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task A_skipped_send_is_never_recorded_as_welcomed()
    {
        using var db = ScenarioFixture.NewDb();
        var (eventId, pid) = await SeedBroadSpeakerAsync(db);
        await SetFeatureAsync(db, eventId, enabled: false, releasedTo: Ring.Broad);

        var sender = new CapturingEmailSender();
        var gate = new FeatureGateService(db);
        await NewService(db, sender, gate, new RingResolver(db)).SendWelcomeAsync(pid);

        // 🔑 The property §326 wanted and §724 preserves: a person who was NOT mailed is not written
        // to SentReminders, so switching the feature back on re-sends to them on the next pass
        // rather than leaving them marked welcomed-but-never-mailed.
        Assert.Empty(db.SentReminders.Where(s => s.OccasionKey == $"welcome:{pid}"));
    }
}
