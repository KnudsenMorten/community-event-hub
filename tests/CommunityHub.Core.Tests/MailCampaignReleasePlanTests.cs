using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1388 — the staged release plan (operator 2026-11-08: "ring 1 gets mail at 8am … then we can stop if last minute issues
/// are found" → "send to organizers at 8, send to 5% at 9 then rest at 10"). In-memory DB, a movable clock, a recording
/// sender, fake names; 40 previous attendees, so the 5% wave is 2 people.
/// </summary>
public sealed class MailCampaignReleasePlanTests
{
    private const int EventId = 9;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-09T06:00:00Z");   // 07:00 Copenhagen
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Sender(IEmailContextAccessor ctx) : IEmailSender
    {
        public List<(string To, string Subject, EmailContext? Ctx)> Sent { get; } = new();
        public Task SendAsync(string to, string subject, string html, CancellationToken ct = default)
        { Sent.Add((to, subject, ctx.Current)); return Task.CompletedTask; }
        public Task SendAsync(string to, string subject, string html, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => SendAsync(to, subject, html, ct);
        public Task SendAsync(string to, string subject, string html, string text, CancellationToken ct = default) => SendAsync(to, subject, html, ct);
        public Task SendWithIcsAsync(string to, string subject, string html, string ics, string name, CancellationToken ct = default) => SendAsync(to, subject, html, ct);
        public Task SendWithAttachmentsAsync(string to, string subject, string html, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => SendAsync(to, subject, html, ct);
    }

    private sealed record W(CommunityHubDbContext Db, MailCampaignService Svc, EmailTemplateRingService Rings, Sender Mail, Clock Clock, MailCampaign C);

    private static async Task<W> NewAsync()
    {
        var db = new CommunityHubDbContext(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"release-plan-{Guid.NewGuid():N}").Options);
        db.Events.Add(new Event { Id = EventId, Code = "ELDK27", CommunityName = "Experts Live Denmark", DisplayName = "ELDK27", IsActive = true });
        var c = new MailCampaign { EventId = EventId, Name = "Mail 1", TemplateKey = "campaign-learn-from-experts",
            Audience = MailAudience.PreviousAttendeesNotThisEdition, IncludeUnsubscribeLink = true, BatchSize = 50,
            DryRunAcknowledgedAt = DateTimeOffset.UtcNow, DryRunAcknowledgedByEmail = "org@x.test", DryRunRecipientCount = 40 };
        db.MailCampaigns.Add(c);
        for (var i = 1; i <= 40; i++)
            db.ExternalRecipients.Add(new ExternalRecipient { EventId = EventId, Email = $"past{i}@example.test", FullName = $"Past Person {i}" });
        foreach (var (email, ring) in new[] { ("r0@x.test", 0), ("r1@x.test", 1) })
            db.Participants.Add(new Participant { EventId = EventId, Email = email, FullName = email, Role = ParticipantRole.Organizer,
                IsActive = true, Ring = (Ring)ring });
        await db.SaveChangesAsync();

        var clock = new Clock();
        var ctx = new EmailContextAccessor();
        var mail = new Sender(ctx);
        var opts = Options.Create(new EmailTemplateOptions { HubUrl = "https://hub.test", TemplateDirectory = RepoPaths.EmailTemplates() });
        var rings = new EmailTemplateRingService(db, new FeatureGateService(db), clock);
        await rings.SetRingAsync(EventId, c.TemplateKey, Ring.Ring0, "org@x.test");
        var svc = new MailCampaignService(db, new MailAudienceResolver(db), new MailSuppressionService(db, "secret", clock),
            new EmailTemplateProvider(opts), mail, ctx, clock, opts, rings, null,
            new CommunityHub.Core.Integrations.DocLibrary.LogisticsRecipients { ReviewMailbox = "ops@crew.example" });
        return new W(db, svc, rings, mail, clock, c);
    }

    private static DateTimeOffset At(string utc) => DateTimeOffset.Parse(utc);
    private static Task<string?> Plan(W w) =>
        w.Svc.SetPlanAsync(w.C, At("2026-11-09T07:00:00Z"), At("2026-11-09T08:00:00Z"), At("2026-11-09T09:00:00Z"), "org@x.test");
    private static IEnumerable<string> Audience(W w) => w.Mail.Sent.Where(s => s.To.StartsWith("past")).Select(s => s.To);

    /// <summary>The job's order per tick: plan steps, then a batch for every due campaign.</summary>
    private static async Task TickAsync(W w)
    {
        await w.Svc.RunPlanStepsAsync(EventId);
        foreach (var c in await w.Svc.DueAsync(EventId))
        {
            if (!await w.Svc.ReleasedToEveryoneAsync(c)) continue;
            if (c.State == MailCampaignState.Scheduled) await w.Svc.StartAsync(c);
            await w.Svc.SendBatchAsync(c);
        }
    }

    [Fact]
    public async Task Organizers_at_8_then_5_percent_of_the_audience_at_9_then_everyone_else_at_10()
    {
        var w = await NewAsync();
        Assert.Null(await Plan(w));

        await TickAsync(w);                                                          // 07:00 Copenhagen: nothing
        Assert.Empty(w.Mail.Sent);

        w.Clock.Now = At("2026-11-09T07:00:00Z");                                   // 08:00 — organizers only
        await TickAsync(w);
        Assert.Equal(new[] { "r0@x.test" }, w.Mail.Sent.Where(s => s.Subject.StartsWith("[PREVIEW")).Select(s => s.To));
        Assert.Empty(Audience(w));
        Assert.Contains(w.Mail.Sent, s => s.To == "ops@crew.example" && s.Ctx!.RingExempt);

        w.Clock.Now = At("2026-11-09T08:00:00Z");                                   // 09:00 — the 5% wave (2 of 40)
        await TickAsync(w);
        Assert.Equal(2, w.C.PlanWaveSize);
        Assert.Equal(2, Audience(w).Count());
        w.Clock.Now = At("2026-11-09T08:30:00Z");                                   // later ticks inside the wave send no more
        await TickAsync(w);
        Assert.Equal(2, Audience(w).Count());

        w.Clock.Now = At("2026-11-09T09:00:00Z");                                   // 10:00 — everyone else
        await TickAsync(w);
        Assert.Equal(40, Audience(w).Count());
        Assert.Equal(40, Audience(w).Distinct().Count());                            // nobody twice
        Assert.False(w.C.PlanActive);
    }

    [Fact]
    public async Task Stop_after_the_first_wave_sends_nothing_more_and_returns_the_campaign_to_draft()
    {
        var w = await NewAsync();
        await Plan(w);
        w.Clock.Now = At("2026-11-09T07:00:00Z"); await TickAsync(w);
        w.Clock.Now = At("2026-11-09T08:00:00Z"); await TickAsync(w);
        Assert.Equal(2, Audience(w).Count());

        await w.Svc.StopPlanAsync(w.C, "org@x.test");
        w.Clock.Now = At("2026-11-09T12:00:00Z");                                   // well past every step
        await TickAsync(w);

        Assert.Equal(2, Audience(w).Count());
        Assert.Equal(MailCampaignState.Draft, w.C.State);
        Assert.DoesNotContain(w.C, await w.Svc.DueAsync(EventId));
    }

    [Fact]
    public async Task Stop_before_9_means_no_previous_attendee_gets_anything()
    {
        var w = await NewAsync();
        await Plan(w);
        w.Clock.Now = At("2026-11-09T07:00:00Z"); await TickAsync(w);               // organizers saw it — and found a problem
        await w.Svc.StopPlanAsync(w.C, "org@x.test");
        w.Clock.Now = At("2026-11-09T12:00:00Z"); await TickAsync(w);

        Assert.Empty(Audience(w));
        Assert.Empty(w.Db.MailCampaignRecipients);
    }

    [Fact]
    public async Task A_missed_tick_still_runs_the_steps_in_order_one_per_tick()
    {
        var w = await NewAsync();
        await Plan(w);
        w.Clock.Now = At("2026-11-09T09:30:00Z");                                   // the job was down until after 10:00

        Assert.Contains("Step 1", Assert.Single(await w.Svc.RunPlanStepsAsync(EventId)));
        Assert.Contains("Step 2", Assert.Single(await w.Svc.RunPlanStepsAsync(EventId)));
        Assert.Contains("Step 3", Assert.Single(await w.Svc.RunPlanStepsAsync(EventId)));
    }

    [Fact]
    public async Task A_plan_needs_the_times_in_order_in_the_future_and_an_approved_list()
    {
        var w = await NewAsync();
        Assert.NotNull(await w.Svc.SetPlanAsync(w.C, At("2026-11-09T08:00:00Z"), At("2026-11-09T07:30:00Z"), At("2026-11-09T09:00:00Z"), "o"));
        Assert.NotNull(await w.Svc.SetPlanAsync(w.C, At("2026-11-09T05:00:00Z"), At("2026-11-09T08:00:00Z"), At("2026-11-09T09:00:00Z"), "o"));
        w.C.DryRunAcknowledgedAt = null;
        Assert.NotNull(await Plan(w));
        Assert.Null(w.C.PlanEveryoneAt);
    }
}
