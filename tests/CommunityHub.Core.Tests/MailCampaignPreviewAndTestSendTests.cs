using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>§1381 — the campaign page's preview and "send test to me", and the five registered campaign mails.</summary>
public sealed class MailCampaignPreviewAndTestSendTests
{
    private const int EventId = 9;

    private sealed class RecordingContextSender(IEmailContextAccessor ctx) : IEmailSender
    {
        public List<(string To, string Subject, string Html, EmailContext? Ctx)> Sent { get; } = new();
        public Task SendAsync(string to, string subject, string html, CancellationToken ct = default)
        { Sent.Add((to, subject, html, ctx.Current)); return Task.CompletedTask; }
        public Task SendAsync(string to, string subject, string html, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => SendAsync(to, subject, html, ct);
        public Task SendAsync(string to, string subject, string html, string text, CancellationToken ct = default) => SendAsync(to, subject, html, ct);
        public Task SendWithIcsAsync(string to, string subject, string html, string ics, string name, CancellationToken ct = default) => SendAsync(to, subject, html, ct);
        public Task SendWithAttachmentsAsync(string to, string subject, string html, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => SendAsync(to, subject, html, ct);
    }

    private static async Task<(MailCampaignService Svc, RecordingContextSender Mail, CommunityHubDbContext Db, MailCampaign C)> NewAsync(string templateKey)
    {
        var db = new CommunityHubDbContext(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"campaign-preview-{Guid.NewGuid():N}").Options);
        db.Events.Add(new Event { Id = EventId, Code = "ELDK27", CommunityName = "Experts Live Denmark", DisplayName = "ELDK27", IsActive = true });
        var c = new MailCampaign { EventId = EventId, Name = "Mail 1", TemplateKey = templateKey,
            Audience = MailAudience.PreviousAttendeesNotThisEdition, IncludeUnsubscribeLink = true };
        db.MailCampaigns.Add(c);
        await db.SaveChangesAsync();
        var ctx = new EmailContextAccessor();
        var mail = new RecordingContextSender(ctx);
        var opts = Options.Create(new EmailTemplateOptions { HubUrl = "https://hub.test" });
        var svc = new MailCampaignService(db, new MailAudienceResolver(db), new MailSuppressionService(db, "secret", TimeProvider.System),
            new EmailTemplateProvider(opts), mail, ctx, TimeProvider.System, opts);
        return (svc, mail, db, c);
    }

    [Theory]
    [InlineData("campaign-learn-from-experts")]
    [InlineData("campaign-preday-deep-dive")]
    [InlineData("campaign-ai-new-world")]
    [InlineData("campaign-networking")]
    [InlineData("campaign-bring-your-team")]
    public async Task Each_campaign_mail_renders_with_a_first_name_greeting_a_ticket_button_and_the_unsubscribe_link(string key)
    {
        var (svc, _, db, c) = await NewAsync(key);
        using var _db = db;

        var (subject, html) = await svc.RenderAsync(c, "ada@example.test", "Ada Lovelace");

        Assert.False(string.IsNullOrWhiteSpace(subject));
        Assert.Contains("Hi Ada,", html);
        Assert.Contains("buyTickets", html);
        Assert.Contains("unsubscribe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{{", html);   // every token filled
        Assert.True(EmailTemplateCatalog.Map.ContainsKey(key));
        Assert.Contains(key, EmailTemplateCatalog.CampaignTemplateKeys);
    }

    [Fact]
    public async Task A_missing_name_greets_there()
    {
        var (svc, _, db, c) = await NewAsync("campaign-networking");
        using var _db = db;
        var (_, html) = await svc.RenderAsync(c, "x@example.test", null);
        Assert.Contains("Hi there,", html);
    }

    [Fact]
    public async Task Send_test_goes_only_to_the_organizer_marked_TEST_and_touches_no_ledger()
    {
        var (svc, mail, db, c) = await NewAsync("campaign-learn-from-experts");
        using var _db = db;

        await svc.SendTestAsync(c, "org@x.test", "Olivia Organizer");

        var sent = Assert.Single(mail.Sent);
        Assert.Equal("org@x.test", sent.To);
        Assert.StartsWith("[TEST] ", sent.Subject);
        Assert.True(sent.Ctx!.RingExempt);
        Assert.Equal("campaign-test", sent.Ctx.Category);   // never "campaign": it must not borrow the ring-3 rule
        Assert.Empty(db.MailCampaignRecipients);
        Assert.Equal(0, c.SentCount);
    }

    // ---- §1381 — the rollout guard: steps 2–4 are review sends, step 5 (Broad) is the only real send ----------

    private sealed class FakeOutcome(bool delivered) : IEmailDeliveryOutcome
    {
        public bool LastSendDelivered => delivered;
        public string? LastDropReason => delivered ? null : "ring-drop";
        public void Record(bool d, string? reason = null) { }
    }

    private static (MailCampaignService Svc, RecordingContextSender Mail, EmailTemplateRingService Rings) Guarded(
        CommunityHubDbContext db, bool delivered = true)
    {
        var ctx = new EmailContextAccessor();
        var mail = new RecordingContextSender(ctx);
        var opts = Options.Create(new EmailTemplateOptions { HubUrl = "https://hub.test" });
        var rings = new EmailTemplateRingService(db, new CommunityHub.Core.Settings.FeatureGateService(db), TimeProvider.System);
        var svc = new MailCampaignService(db, new MailAudienceResolver(db), new MailSuppressionService(db, "secret", TimeProvider.System),
            new EmailTemplateProvider(opts), mail, ctx, TimeProvider.System, opts, rings, new FakeOutcome(delivered));
        return (svc, mail, rings);
    }

    private static async Task<MailCampaign> ApprovedPreviousAttendeeCampaignAsync(CommunityHubDbContext db, MailCampaign c)
    {
        db.ExternalRecipients.Add(new ExternalRecipient { EventId = EventId, Email = "past@example.test", FullName = "Past Person" });
        c.DryRunAcknowledgedAt = DateTimeOffset.UtcNow; c.DryRunAcknowledgedByEmail = "org@x.test"; c.DryRunRecipientCount = 1;
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task The_campaign_does_not_start_until_its_mail_is_released_to_Broad()
    {
        var (_, _, db, c) = await NewAsync("campaign-learn-from-experts");
        using var _db = db;
        await ApprovedPreviousAttendeeCampaignAsync(db, c);
        var (svc, mail, rings) = Guarded(db);

        foreach (var r in new[] { CommunityHub.Core.Settings.Ring.Ring0, CommunityHub.Core.Settings.Ring.Ring1, CommunityHub.Core.Settings.Ring.Ring2 })
        {
            await rings.SetRingAsync(EventId, c.TemplateKey, r, "org@x.test");
            Assert.False(await svc.ReleasedToEveryoneAsync(c));
            Assert.Equal(0, await svc.StartAsync(c));
            Assert.Equal(0, await svc.SendBatchAsync(c));
        }
        Assert.Empty(mail.Sent);
        Assert.Empty(db.MailCampaignRecipients);   // the list is not even frozen yet
        Assert.NotEqual(MailCampaignState.Sent, c.State);

        await rings.SetRingAsync(EventId, c.TemplateKey, CommunityHub.Core.Settings.Ring.Broad, "org@x.test");
        Assert.True(await svc.ReleasedToEveryoneAsync(c));
        Assert.Equal(1, await svc.StartAsync(c));
        Assert.Equal(1, await svc.SendBatchAsync(c));
        Assert.Equal("past@example.test", Assert.Single(mail.Sent).To);
    }

    [Fact]
    public async Task A_send_the_transport_dropped_is_recorded_as_not_delivered_never_as_sent()
    {
        var (_, _, db, c) = await NewAsync("campaign-learn-from-experts");
        using var _db = db;
        await ApprovedPreviousAttendeeCampaignAsync(db, c);
        var (svc, _, rings) = Guarded(db, delivered: false);
        await rings.SetRingAsync(EventId, c.TemplateKey, CommunityHub.Core.Settings.Ring.Broad, "org@x.test");

        await svc.StartAsync(c);
        Assert.Equal(0, await svc.SendBatchAsync(c));

        var row = Assert.Single(db.MailCampaignRecipients);
        Assert.Equal(MailCampaignRecipientState.Failed, row.State);
        Assert.Contains("ring-drop", row.Error);
        Assert.Equal(0, c.SentCount);
    }

    [Fact]
    public async Task A_review_send_reaches_rings_0_to_N_only_through_the_ring_gate_and_never_the_audience()
    {
        var (_, _, db, c) = await NewAsync("campaign-learn-from-experts");
        using var _db = db;
        await ApprovedPreviousAttendeeCampaignAsync(db, c);
        foreach (var (email, ring) in new[] { ("r0@x.test", 0), ("r1@x.test", 1), ("r2@x.test", 2), ("r3@x.test", 3) })
            db.Participants.Add(new Participant { EventId = EventId, Email = email, FullName = email, Role = ParticipantRole.Organizer,
                IsActive = true, Ring = (CommunityHub.Core.Settings.Ring)ring });
        await db.SaveChangesAsync();
        var (svc, mail, _) = Guarded(db);

        var (asked, delivered) = await svc.SendReviewAsync(c, CommunityHub.Core.Settings.Ring.Ring1);

        Assert.Equal((2, 2), (asked, delivered));
        Assert.Equal(new[] { "r0@x.test", "r1@x.test" }, mail.Sent.Select(s => s.To).OrderBy(x => x));
        Assert.All(mail.Sent, s =>
        {
            Assert.StartsWith("[PREVIEW ring 1] ", s.Subject);
            Assert.False(s.Ctx!.RingExempt);                  // the transport's ring gate decides
            Assert.Equal("campaign-review", s.Ctx.Category);  // never "campaign"
        });
        Assert.DoesNotContain(mail.Sent, s => s.To == "past@example.test");
        Assert.Empty(db.MailCampaignRecipients);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SendReviewAsync(c, CommunityHub.Core.Settings.Ring.Broad));
    }

    [Fact]
    public void The_campaign_tag_names_the_audience_and_the_campaign()
    {
        Assert.Equal("ceh-previous-attendees-no-ticket-c12", MailCampaignService.TagFor(
            new MailCampaign { Id = 12, Audience = MailAudience.PreviousAttendeesNotThisEdition }));
        Assert.Equal("ceh-attendees-c13", MailCampaignService.TagFor(
            new MailCampaign { Id = 13, Audience = MailAudience.AllAttendees }));
        // every audience has its own slug — no two groups can share a tag
        var slugs = Enum.GetValues<MailAudience>().Select(MailCampaignService.AudienceSlug).ToList();
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
    }

    /// <summary>
    /// 🔴 §1381 — an organizer's EDITED text (Email templates page, §25h per-edition override) is what the campaign
    /// sends — in the test send AND the real send. Found 2026-11-07: the campaign rendered before any edition context
    /// was set, so every send silently fell back to the shipped template.
    /// </summary>
    [Fact]
    public async Task An_organizers_edited_template_is_what_the_campaign_sends()
    {
        var (_, _, db, c) = await NewAsync("campaign-learn-from-experts");
        using var _db = db;
        await ApprovedPreviousAttendeeCampaignAsync(db, c);
        db.EmailTemplateOverrides.Add(new EmailTemplateOverride
        {
            EventId = EventId, TemplateKey = "campaign-learn-from-experts",
            OverrideText = "Subject: Edited by the organizer\n<p>Hi {{firstName}}, this text was edited in the hub.</p>",
        });
        await db.SaveChangesAsync();

        // the provider exactly as the hosts wire it: override store in a scope, edition read from the email context
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, db);
        Microsoft.Extensions.DependencyInjection.MemoryCacheServiceCollectionExtensions.AddMemoryCache(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, TimeProvider.System);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<EmailTemplateOverrideStore>(services);
        var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var ctx = new EmailContextAccessor();
        var mail = new RecordingContextSender(ctx);
        var opts = Options.Create(new EmailTemplateOptions { HubUrl = "https://hub.test" });
        var provider = new EmailTemplateProvider(opts,
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(sp), ctx);
        var rings = new EmailTemplateRingService(db, new CommunityHub.Core.Settings.FeatureGateService(db), TimeProvider.System);
        await rings.SetRingAsync(EventId, c.TemplateKey, CommunityHub.Core.Settings.Ring.Broad, "org@x.test");
        var svc = new MailCampaignService(db, new MailAudienceResolver(db), new MailSuppressionService(db, "secret", TimeProvider.System),
            provider, mail, ctx, TimeProvider.System, opts, rings, new FakeOutcome(true));

        await svc.SendTestAsync(c, "org@x.test", "Org Anizer");
        await svc.StartAsync(c);
        await svc.SendBatchAsync(c);

        Assert.Equal(2, mail.Sent.Count);
        Assert.All(mail.Sent, s =>
        {
            Assert.Contains("Edited by the organizer", s.Subject);
            Assert.Contains("this text was edited in the hub", s.Html);
        });
    }

    /// <summary>
    /// 🔴 §1381 — operator 2026-11-07: "an attendee that bought a ticket 1 hr ago will NOT get the 5 'no ticket yet'
    /// emails". The audience is recalculated for EVERY batch: a previous attendee who buys a ticket after the campaign
    /// started is skipped (LeftAudience), never mailed.
    /// </summary>
    [Fact]
    public async Task A_previous_attendee_who_buys_a_ticket_after_the_start_is_not_mailed()
    {
        var (_, _, db, c) = await NewAsync("campaign-learn-from-experts");
        using var _db = db;
        await ApprovedPreviousAttendeeCampaignAsync(db, c);
        var (svc, mail, rings) = Guarded(db);
        await rings.SetRingAsync(EventId, c.TemplateKey, CommunityHub.Core.Settings.Ring.Broad, "org@x.test");
        Assert.Equal(1, await svc.StartAsync(c));   // past@example.test is on the frozen list

        // …and buys an ELDK27 ticket before their batch goes out (the attendee mirror syncs every 5 minutes).
        db.Attendees.Add(new Attendee
        {
            EventId = EventId, BackstageTicketId = "t-new", Email = "Past@Example.test",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
        });
        await db.SaveChangesAsync();

        Assert.Equal(0, await svc.SendBatchAsync(c));
        Assert.Empty(mail.Sent);
        var row = Assert.Single(db.MailCampaignRecipients);
        Assert.Equal(MailCampaignRecipientState.LeftAudience, row.State);
        Assert.Equal(0, c.SentCount);
    }
}
