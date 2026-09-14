using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1080 stages 3–4 — previewing, approving and BATCH SENDING a campaign.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Almost every test here is about NOT sending.</b> This is the only code in the hub that
/// can write to thousands of people at once; the failure that matters is not "the mail looked wrong"
/// but "it went out at all".</para>
/// </remarks>
public sealed class MailCampaignSendTests
{
    private const int EventId = 42;
    private const string Secret = "signing-secret";

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"campaign-{Guid.NewGuid():N}").Options);

    private static (MailCampaignService Svc, CapturingEmailSender Mail) New(
        CommunityHubDbContext db, string? secret = Secret, DateTimeOffset? now = null)
    {
        var mail = new CapturingEmailSender();
        var clock = new FixedClock(now ?? Now);
        var svc = new MailCampaignService(
            db,
            new MailAudienceResolver(db),
            new MailSuppressionService(db, secret, clock),
            new EmailTemplateProvider(Options.Create(new EmailTemplateOptions { HubUrl = "https://hub.test" })),
            mail,
            new EmailContextAccessor(),
            clock,
            Options.Create(new EmailTemplateOptions { HubUrl = "https://hub.test" }));
        return (svc, mail);
    }

    private static async Task<(CommunityHubDbContext Db, MailCampaign Campaign)> SeedAsync(
        int attendees = 5, Action<MailCampaign>? tweak = null)
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "Experts Live Denmark",
            DisplayName = "ELDK27", IsActive = true,
        });
        for (var i = 1; i <= attendees; i++)
            db.Attendees.Add(new Attendee
            {
                EventId = EventId, Email = $"p{i}@x.test", FullName = $"Person {i}",
                BackstageTicketId = Guid.NewGuid().ToString("N"), MirrorState = MirrorState.Active,
                TicketStatus = TicketStatus.TwoDay,
            });

        var campaign = new MailCampaign
        {
            EventId = EventId, Name = "Save the date", TemplateKey = "welcome",
            Audience = MailAudience.AllAttendees, BatchSize = 2, BatchIntervalMinutes = 15,
            DryRunAcknowledgedAt = Now, DryRunAcknowledgedByEmail = "org@x.test",
        };
        tweak?.Invoke(campaign);
        db.MailCampaigns.Add(campaign);
        await db.SaveChangesAsync();
        return (db, campaign);
    }

    // ---- the gate --------------------------------------------------------

    /// <summary>
    /// 🔴 No acknowledged dry run ⇒ nothing sends, whatever else is true. His decision, and the
    /// thing between a mistyped audience and everybody.
    /// </summary>
    [Fact]
    public async Task Without_an_acknowledged_dry_run_nothing_is_sent()
    {
        var (db, campaign) = await SeedAsync(tweak: c => c.DryRunAcknowledgedAt = null);
        using var _ = db;
        var (svc, mail) = New(db);

        Assert.False(svc.Gate(campaign).MaySend);
        Assert.Equal(0, await svc.StartAsync(campaign));
        Assert.Equal(0, await svc.SendBatchAsync(campaign));
        Assert.Empty(mail.Sent);
    }

    /// <summary>
    /// 🔴 A campaign that CARRIES the link and cannot sign it fails CLOSED — a dead opt-out link
    /// costs the sending reputation and the legal basis at once.
    /// </summary>
    /// <remarks>
    /// ⚠️ §1080b — this used to assert that ANY campaign needs the secret. That was wrong once the
    /// link became per-campaign: the requirement follows the LINK, not the feature.
    /// </remarks>
    [Fact]
    public async Task A_campaign_carrying_the_link_needs_a_secret_to_sign_it()
    {
        var (db, campaign) = await SeedAsync(tweak: c => c.IncludeUnsubscribeLink = true);
        using var _ = db;
        var (svc, mail) = New(db, secret: null);

        var gate = svc.Gate(campaign);
        Assert.False(gate.MaySend);
        Assert.Contains("carries an unsubscribe link", gate.Reason);
        Assert.Contains("no secret is configured", gate.Reason);
        await svc.StartAsync(campaign);
        Assert.Equal(0, await svc.SendBatchAsync(campaign));
        Assert.Empty(mail.Sent);
    }

    /// <summary>Changing the audience revokes the approval — it was an approval of something else.</summary>
    [Fact]
    public async Task Changing_the_campaign_invalidates_the_acknowledgement()
    {
        var (db, campaign) = await SeedAsync();
        using var _ = db;
        var (svc, _) = New(db);

        svc.InvalidateAcknowledgement(campaign);

        Assert.Null(campaign.DryRunAcknowledgedAt);
        Assert.False(svc.Gate(campaign).MaySend);
    }

    // ---- preview ---------------------------------------------------------

    /// <summary>
    /// The preview shows the count AND real addresses. A count can be right about the wrong
    /// audience; five actual addresses is what catches it.
    /// </summary>
    [Fact]
    public async Task The_preview_reports_the_total_the_suppressed_and_a_sample()
    {
        var (db, campaign) = await SeedAsync(attendees: 5);
        using var _ = db;
        var (svc, _) = New(db);
        await new MailSuppressionService(db, Secret).SuppressAsync(
            EventId, "p1@x.test", MailSuppressionReason.Unsubscribed);

        var preview = await svc.PreviewAsync(campaign);

        Assert.Equal(5, preview.Total);
        Assert.Equal(1, preview.Suppressed);
        Assert.Equal(4, preview.Sendable);
        Assert.DoesNotContain("p1@x.test", preview.Sample);
        Assert.NotEmpty(preview.Sample);
    }

    // ---- sending ---------------------------------------------------------

    [Fact]
    public async Task A_batch_sends_only_its_batch_size_and_the_rest_follow()
    {
        var (db, campaign) = await SeedAsync(attendees: 5);
        using var _ = db;
        var (svc, mail) = New(db);

        Assert.Equal(5, await svc.StartAsync(campaign));
        Assert.Equal(2, await svc.SendBatchAsync(campaign));
        Assert.Equal(2, mail.Messages.Count);
        Assert.Equal(MailCampaignState.Sending, campaign.State);

        await svc.SendBatchAsync(campaign);
        await svc.SendBatchAsync(campaign);

        Assert.Equal(5, mail.Messages.Count);
        Assert.Equal(MailCampaignState.Sent, campaign.State);
        Assert.NotNull(campaign.CompletedAt);
    }

    /// <summary>
    /// 🔴 <b>Nobody is mailed twice</b>, even if the batch runs again — the ledger is the guarantee,
    /// not the loop that happens to be running.
    /// </summary>
    [Fact]
    public async Task Running_start_again_does_not_re_add_or_re_send_anyone()
    {
        var (db, campaign) = await SeedAsync(attendees: 3);
        using var _ = db;
        var (svc, mail) = New(db);

        await svc.StartAsync(campaign);
        await svc.SendBatchAsync(campaign);
        await svc.StartAsync(campaign);          // e.g. a restart, or an organizer pressing again
        await svc.SendBatchAsync(campaign);
        await svc.SendBatchAsync(campaign);

        Assert.Equal(3, mail.Messages.Count);
        Assert.Equal(3, mail.Messages.Select(m => m.To).Distinct().Count());
    }

    /// <summary>
    /// 🔴 <b>Suppression is honoured AT SEND TIME.</b> Somebody who unsubscribes after the campaign
    /// started must not receive the next batch — which is exactly what happens if the audience is
    /// merely filtered once, up front.
    /// </summary>
    [Fact]
    public async Task An_unsubscribe_after_the_campaign_started_is_honoured_on_the_next_batch()
    {
        var (db, campaign) = await SeedAsync(attendees: 4);
        using var _ = db;
        var (svc, mail) = New(db);
        await svc.StartAsync(campaign);
        await svc.SendBatchAsync(campaign);       // p1, p2

        // …and now somebody in the remaining half opts out.
        await new MailSuppressionService(db, Secret).SuppressAsync(
            EventId, "p3@x.test", MailSuppressionReason.Unsubscribed);

        await svc.SendBatchAsync(campaign);

        Assert.DoesNotContain(mail.Messages, m => m.To == "p3@x.test");
        Assert.Equal(1, campaign.SuppressedCount);
        var row = await db.MailCampaignRecipients.SingleAsync(r => r.Email == "p3@x.test");
        Assert.Equal(MailCampaignRecipientState.Suppressed, row.State);
    }

    /// <summary>
    /// 🔴 §1080b — <b>NO unsubscribe link on an ordinary campaign.</b> Operator 2026-08-13: <i>"no
    /// other templates must have that or no other emails must have that"</i>. An opt-out on a
    /// mailing to this edition's own attendees invites them to unsubscribe from the operational mail
    /// they need — their ticket, their session, their booth.
    /// </summary>
    [Fact]
    public async Task An_ordinary_campaign_carries_no_unsubscribe_link()
    {
        var (db, campaign) = await SeedAsync(attendees: 1);   // audience: this edition's attendees
        using var _ = db;
        var (svc, mail) = New(db);

        Assert.False(campaign.IncludeUnsubscribeLink);         // 🔒 off by default
        await svc.StartAsync(campaign);
        await svc.SendBatchAsync(campaign);

        var html = Assert.Single(mail.Messages).Html;
        Assert.DoesNotContain("/unsubscribe", html);
        Assert.DoesNotContain("Unsubscribe", html);
    }

    /// <summary>
    /// 🔑 …and the one mailing that must carry it, does — appended by the SENDER, so it cannot be
    /// lost by somebody editing the template text.
    /// </summary>
    [Fact]
    public async Task The_previous_attendees_campaign_carries_the_link()
    {
        var (db, campaign) = await SeedAsync(attendees: 0, tweak: c =>
        {
            c.Audience = MailAudience.PreviousAttendeesNotThisEdition;
            c.IncludeUnsubscribeLink = true;
        });
        using var _ = db;
        db.ExternalRecipients.Add(new ExternalRecipient
        {
            EventId = EventId, Email = "old-friend@x.test", FullName = "Old Friend",
        });
        await db.SaveChangesAsync();
        var (svc, mail) = New(db);

        await svc.StartAsync(campaign);
        await svc.SendBatchAsync(campaign);

        var html = Assert.Single(mail.Messages).Html;
        Assert.Contains("/unsubscribe?e=old-friend%40x.test", html);
    }

    /// <summary>
    /// 🔴 That audience may NOT be sent without it — the existing-customer basis depends on the
    /// opt-out being present, so the gate refuses rather than sending a mailing that lacks it.
    /// </summary>
    [Fact]
    public async Task The_previous_attendees_campaign_is_refused_without_the_link()
    {
        var (db, campaign) = await SeedAsync(tweak: c =>
        {
            c.Audience = MailAudience.PreviousAttendeesNotThisEdition;
            c.IncludeUnsubscribeLink = false;
        });
        using var _ = db;
        var (svc, mail) = New(db);

        var gate = svc.Gate(campaign);
        Assert.False(gate.MaySend);
        Assert.Contains("unsubscribe link", gate.Reason);
        Assert.Equal(0, await svc.SendBatchAsync(campaign));
        Assert.Empty(mail.Sent);
    }

    /// <summary>
    /// 🔒 A campaign that carries no link needs no signing secret — the secret requirement follows
    /// the link, not the feature.
    /// </summary>
    [Fact]
    public async Task An_ordinary_campaign_sends_even_with_no_secret_configured()
    {
        var (db, campaign) = await SeedAsync(attendees: 1);
        using var _ = db;
        var (svc, mail) = New(db, secret: null);

        Assert.True(svc.Gate(campaign).MaySend);
        await svc.StartAsync(campaign);
        Assert.Equal(1, await svc.SendBatchAsync(campaign));
        Assert.DoesNotContain("/unsubscribe", Assert.Single(mail.Messages).Html);
    }

    /// <summary>
    /// ⚠️ One failure does not stop a mailing to thousands — it is recorded against that recipient,
    /// which is what lets an organizer see which addresses to look at.
    /// </summary>
    [Fact]
    public async Task A_failure_is_recorded_and_the_batch_continues()
    {
        var (db, campaign) = await SeedAsync(attendees: 3);
        using var _ = db;
        campaign.BatchSize = 3;
        var mail = new ThrowsForOne("p2@x.test");
        var clock = new FixedClock(Now);
        var svc = new MailCampaignService(
            db, new MailAudienceResolver(db), new MailSuppressionService(db, Secret, clock),
            new EmailTemplateProvider(Options.Create(new EmailTemplateOptions())), mail,
            new EmailContextAccessor(), clock);

        await svc.StartAsync(campaign);
        await svc.SendBatchAsync(campaign);

        Assert.Equal(2, campaign.SentCount);
        Assert.Equal(1, campaign.FailedCount);
        var failed = await db.MailCampaignRecipients.SingleAsync(r => r.Email == "p2@x.test");
        Assert.Equal(MailCampaignRecipientState.Failed, failed.State);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));
    }

    /// <summary>
    /// ⚠️ The pacing is the campaign's own interval. A campaign that just sent a batch is NOT due
    /// again — otherwise the batching it exists to provide is defeated by a fast job tick.
    /// </summary>
    [Fact]
    public async Task A_campaign_is_not_due_again_until_its_interval_has_passed()
    {
        var (db, campaign) = await SeedAsync();
        using var _ = db;
        campaign.State = MailCampaignState.Sending;
        campaign.LastBatchAt = Now.AddMinutes(-5);        // interval is 15
        await db.SaveChangesAsync();

        Assert.Empty(await New(db).Svc.DueAsync(EventId));
        Assert.Single(await New(db, now: Now.AddMinutes(20)).Svc.DueAsync(EventId));
    }

    /// <summary>A scheduled campaign waits for its time.</summary>
    [Fact]
    public async Task A_scheduled_campaign_is_not_due_before_its_time()
    {
        var (db, campaign) = await SeedAsync();
        using var _ = db;
        campaign.State = MailCampaignState.Scheduled;
        campaign.ScheduledFor = Now.AddHours(3);
        await db.SaveChangesAsync();

        Assert.Empty(await New(db).Svc.DueAsync(EventId));
        Assert.Single(await New(db, now: Now.AddHours(4)).Svc.DueAsync(EventId));
    }

    private sealed class ThrowsForOne(string badAddress) : IEmailSender
    {
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
            => to == badAddress ? throw new InvalidOperationException("mailbox unavailable") : Task.CompletedTask;
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc,
            CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string i, string f,
            CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h,
            IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => SendAsync(to, s, h, ct);
    }
}
