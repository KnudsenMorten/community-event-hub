using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO §436 — "when it detects, i expect also an email to arrive that the graphics file
/// has been released with a button to Help Promote" (operator 2026-07-27).
///
/// The mail moved from a DAILY 08:30 sweep onto the RELEASE itself, so it arrives when the
/// graphic actually becomes usable. All three trigger paths (organizer Release click,
/// SharePoint sync auto-release, daily safety net) call the same
/// <see cref="SpeakerGraphicsReadyNotifier"/>, so the contracts that matter are:
///  - a RELEASED graphic notifies its speaker, with the Help Promote CTA in the body;
///  - a GENERATED (not yet released) graphic notifies NOBODY — the review gate holds;
///  - the SECOND release for the same speaker sends NOTHING (the shared ledger key is what
///    stops the three triggers between them mailing a speaker twice);
///  - narrowing to one speaker never mails the other speakers;
///  - the feature switch off ⇒ completely inert.
///
/// Real ReminderEngine + real ledger; only the transport is a spy. NO real data.
/// </summary>
public sealed class SpeakerGraphicsReadyNotifierScenarioTests
{
    private sealed class SpyEmailSender : IEmailSender
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        {
            Sent.Add((toEmail, subject, htmlBody));
            return Task.CompletedTask;
        }

        public Task SendAsync(string toEmail, string subject, string htmlBody,
            IReadOnlyCollection<string>? cc, CancellationToken ct = default) =>
            SendAsync(toEmail, subject, htmlBody, ct);

        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody,
            CancellationToken ct = default) =>
            SendAsync(toEmail, subject, htmlBody, ct);

        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody,
            string icsContent, string icsFileName, CancellationToken ct = default) =>
            SendAsync(toEmail, subject, htmlBody, ct);

        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default) =>
            SendAsync(toEmail, subject, htmlBody, ct);
    }

    private static SpeakerGraphicsReadyNotifier NewNotifier(
        CommunityHubDbContext db, SpyEmailSender sender)
    {
        var clock = TimeProvider.System;
        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            PrivateTemplateDirectory = Path.Combine(Path.GetTempPath(), "ceh-no-private-graphics-ready"),
            HubUrl = "https://hub.example.test",
        }));
        return new SpeakerGraphicsReadyNotifier(
            db,
            new ReminderEngine(db, sender, clock),
            templates,
            new FeatureGateService(db),
            new AuditTrailService(db, clock));
    }

    /// <summary>The feature is OFF by default in the catalog — every test that expects a mail
    /// has to switch it on for the edition, exactly as an organizer would.</summary>
    private static async Task EnableFeatureAsync(CommunityHubDbContext db, int eventId)
    {
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = eventId,
            FeatureKey = SpeakerGraphicsReadyNotifier.FeatureKey,
            Enabled = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The seed leaves participants in the onboarding default
    /// (<see cref="ParticipantLifecycleState.Inactive"/>); the notifier deliberately mails
    /// only ACTIVATED speakers, so a test speaker has to be activated the way an organizer
    /// would before they can be notified at all.
    /// </summary>
    private static async Task ActivateAsync(CommunityHubDbContext db, params int[] participantIds)
    {
        foreach (var p in await db.Participants.Where(p => participantIds.Contains(p.Id)).ToListAsync())
        {
            p.LifecycleState = ParticipantLifecycleState.Active;
        }
        await db.SaveChangesAsync();
    }

    private static async Task<GraphicAsset> AddGraphicAsync(
        CommunityHubDbContext db, int eventId, int participantId, GraphicAssetStatus status)
    {
        var asset = new GraphicAsset
        {
            EventId = eventId,
            Type = GraphicAssetType.Session,
            StableKey = $"session-{Guid.NewGuid():N}",
            ParticipantId = participantId,
            Status = status,
            FileName = "graphic.png",
            StorageItemId = "item-graphic.png",
        };
        db.GraphicAssets.Add(asset);
        await db.SaveChangesAsync();
        return asset;
    }

    [Fact]
    public async Task A_released_graphic_mails_its_speaker_the_help_promote_link()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await EnableFeatureAsync(db, seed.EventId);
        await ActivateAsync(db, seed.SpeakerOneId, seed.SpeakerTwoId);
        await AddGraphicAsync(db, seed.EventId, seed.SpeakerOneId, GraphicAssetStatus.Released);

        var sender = new SpyEmailSender();
        var sent = await NewNotifier(db, sender).NotifyAsync(seed.EventId);

        Assert.Equal(1, sent);
        var mail = Assert.Single(sender.Sent);
        Assert.Equal(ScenarioSeed.SpeakerOneEmail, mail.To);
        // The CTA the operator asked for is in the body, pointing at Help Promote.
        Assert.Contains("Help Promote", mail.Html);
        Assert.Contains("/Speaker/Graphics", mail.Html);
    }

    [Fact]
    public async Task A_generated_graphic_notifies_nobody_the_release_gate_still_holds()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await EnableFeatureAsync(db, seed.EventId);
        await ActivateAsync(db, seed.SpeakerOneId, seed.SpeakerTwoId);
        await AddGraphicAsync(db, seed.EventId, seed.SpeakerOneId, GraphicAssetStatus.Generated);

        var sender = new SpyEmailSender();
        var sent = await NewNotifier(db, sender).NotifyAsync(seed.EventId);

        Assert.Equal(0, sent);
        Assert.Empty(sender.Sent);
    }

    /// <summary>
    /// THE contract that lets three triggers share one mail. Releasing a second graphic for a
    /// speaker who has already been told must not tell them again — otherwise moving the
    /// trigger off the daily sweep would turn a batch of releases into a batch of mails.
    /// </summary>
    [Fact]
    public async Task Re_running_about_the_SAME_graphics_sends_nothing()
    {
        // The idempotency that must survive §664: three triggers (release click, sync run, daily
        // sweep) firing about the same released set still produce exactly ONE mail.
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await EnableFeatureAsync(db, seed.EventId);
        await ActivateAsync(db, seed.SpeakerOneId, seed.SpeakerTwoId);
        await AddGraphicAsync(db, seed.EventId, seed.SpeakerOneId, GraphicAssetStatus.Released);

        var sender = new SpyEmailSender();
        var notifier = NewNotifier(db, sender);

        Assert.Equal(1, await notifier.NotifyAsync(seed.EventId));

        // Nothing new was released — neither the narrow nor the wide call may mail again.
        Assert.Equal(0, await notifier.NotifyAsync(seed.EventId, new[] { seed.SpeakerOneId }));
        Assert.Equal(0, await notifier.NotifyAsync(seed.EventId));

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task A_NEW_graphic_for_an_already_notified_speaker_DOES_mail_again()
    {
        // §664, the operator's own case: he was notified on the 28th, then the Test Master Class
        // graphic was released on the 29th and he heard nothing — the old key
        // "graphics-ready:{participantId}" meant "told about graphics" FOREVER.
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await EnableFeatureAsync(db, seed.EventId);
        await ActivateAsync(db, seed.SpeakerOneId, seed.SpeakerTwoId);
        await AddGraphicAsync(db, seed.EventId, seed.SpeakerOneId, GraphicAssetStatus.Released);

        var sender = new SpyEmailSender();
        var notifier = NewNotifier(db, sender);

        Assert.Equal(1, await notifier.NotifyAsync(seed.EventId));

        // A genuinely NEW graphic ⇒ a new occasion ⇒ exactly one more mail.
        await AddGraphicAsync(db, seed.EventId, seed.SpeakerOneId, GraphicAssetStatus.Released);
        Assert.Equal(1, await notifier.NotifyAsync(seed.EventId, new[] { seed.SpeakerOneId }));
        Assert.Equal(2, sender.Sent.Count);

        // ...and that new set is now itself deduped — one new graphic must not mail forever.
        Assert.Equal(0, await notifier.NotifyAsync(seed.EventId));
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task The_664_backfill_does_not_re_announce_graphics_already_notified()
    {
        // The trap the old code's own comment warned about: switching key shapes must not
        // re-announce to everyone. A legacy "graphics-ready:{id}" row is carried onto the new key.
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await EnableFeatureAsync(db, seed.EventId);
        await ActivateAsync(db, seed.SpeakerOneId, seed.SpeakerTwoId);
        var asset = await AddGraphicAsync(
            db, seed.EventId, seed.SpeakerOneId, GraphicAssetStatus.Released);

        // Stamp the graphic as released BEFORE the legacy notification, then write the legacy row
        // exactly as the pre-§664 code would have.
        var released = DateTimeOffset.UtcNow.AddDays(-2);
        asset.ReleasedAt = released;
        db.SentReminders.Add(new SentReminder
        {
            EventId = seed.EventId,
            ReminderType = "speaker-graphics-ready",
            OccasionKey = $"graphics-ready:{seed.SpeakerOneId}",
            RecipientEmail = ScenarioSeed.SpeakerOneEmail,
            SentAt = released.AddHours(1),
        });
        await db.SaveChangesAsync();

        var sender = new SpyEmailSender();
        var notifier = NewNotifier(db, sender);

        // They were already told about THIS graphic — the first run under the new key must be silent.
        Assert.Equal(0, await notifier.NotifyAsync(seed.EventId));
        Assert.Empty(sender.Sent);

        // Idempotent: running again does not accumulate carried rows or start mailing.
        Assert.Equal(0, await notifier.NotifyAsync(seed.EventId));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task The_664_backfill_still_announces_a_graphic_released_AFTER_the_old_mail()
    {
        // The half that actually fixes his report. The backfill must reconstruct what the legacy
        // mail could have been about — NOT stamp today's set as "already told", which would
        // re-implement the very bug and leave the master-class graphic silent forever.
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await EnableFeatureAsync(db, seed.EventId);
        await ActivateAsync(db, seed.SpeakerOneId, seed.SpeakerTwoId);
        var asset = await AddGraphicAsync(
            db, seed.EventId, seed.SpeakerOneId, GraphicAssetStatus.Released);

        // The legacy mail went out BEFORE this graphic was released (his exact timeline).
        var legacySentAt = DateTimeOffset.UtcNow.AddDays(-2);
        asset.ReleasedAt = legacySentAt.AddDays(1);
        db.SentReminders.Add(new SentReminder
        {
            EventId = seed.EventId,
            ReminderType = "speaker-graphics-ready",
            OccasionKey = $"graphics-ready:{seed.SpeakerOneId}",
            RecipientEmail = ScenarioSeed.SpeakerOneEmail,
            SentAt = legacySentAt,
        });
        await db.SaveChangesAsync();

        var sender = new SpyEmailSender();
        var notifier = NewNotifier(db, sender);

        Assert.Equal(1, await notifier.NotifyAsync(seed.EventId));
        Assert.Equal(ScenarioSeed.SpeakerOneEmail, Assert.Single(sender.Sent).To);
    }

    [Fact]
    public async Task Narrowing_to_one_speaker_never_mails_the_others()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await EnableFeatureAsync(db, seed.EventId);
        await ActivateAsync(db, seed.SpeakerOneId, seed.SpeakerTwoId);
        await AddGraphicAsync(db, seed.EventId, seed.SpeakerOneId, GraphicAssetStatus.Released);
        await AddGraphicAsync(db, seed.EventId, seed.SpeakerTwoId, GraphicAssetStatus.Released);

        var sender = new SpyEmailSender();
        var notifier = NewNotifier(db, sender);

        // The organizer released speaker TWO's graphic — only they hear about it.
        Assert.Equal(1, await notifier.NotifyAsync(seed.EventId, new[] { seed.SpeakerTwoId }));
        Assert.Equal(ScenarioSeed.SpeakerTwoEmail, Assert.Single(sender.Sent).To);

        // An EMPTY narrowing means "this release affected nobody" — not "sweep everyone".
        Assert.Equal(0, await notifier.NotifyAsync(seed.EventId, Array.Empty<int>()));
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task The_feature_switch_off_makes_it_completely_inert()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        // NOT enabled — the catalog default for speaker-graphics-promote is OFF.
        await ActivateAsync(db, seed.SpeakerOneId);
        await AddGraphicAsync(db, seed.EventId, seed.SpeakerOneId, GraphicAssetStatus.Released);

        var sender = new SpyEmailSender();
        Assert.Equal(0, await NewNotifier(db, sender).NotifyAsync(seed.EventId));
        Assert.Empty(sender.Sent);
        Assert.Empty(await db.SentReminders.ToListAsync());
    }
}
