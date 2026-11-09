using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1373 — A SPONSOR-TIER INTRO NEVER FREEZES A COMPANY NAME.
/// </summary>
/// <remarks>
/// Operator 2026-10-28: <i>"this shows the billing name which is wrong. some post must always show the
/// public name, fix this for any existing posts planned and scheduled and future"</i>. The Diamond
/// intro on PROD read "GLOBETEAM A/S, Nerdio, and twoday Danmark A/S": the AI wrote it from the tier
/// list once, it was stored (§901) and reused (§911), and the live {SponsorList} below it had since
/// moved to public names and a different line-up.
/// </remarks>
public sealed class SoMeTierIntroNamesTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private sealed class ReplyHandler(string reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            LastBody = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
            var json = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":"
                       + System.Text.Json.JsonSerializer.Serialize(reply) + "}}]}";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"some-tier-intro-{Guid.NewGuid():N}").Options);

    private static SoMeScheduleService Svc(CommunityHubDbContext db, ReplyHandler? handler = null)
    {
        var clock = new FixedClock(Now);
        var intro = handler is null ? null : new SoMeIntroGenerator(
            new HttpClient(handler),
            new CommunityHub.Core.Assistant.OpenAiOptions
            {
                Enabled = true, Endpoint = "https://example.openai.azure.com",
                Deployment = "gpt", ApiKey = "k",
            });
        return new SoMeScheduleService(
            db, new SoMeTemplateService(db, clock), new SoMeVariableResolver(db), clock, null, intro);
    }

    /// <summary>An edition with one Diamond sponsor known publicly as "Twoday", billed as "twoday Danmark A/S".</summary>
    private static async Task<SoMePost> SeedAsync(CommunityHubDbContext db, string intro, bool active = true)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            VenueName = "Bella Center", StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.SoMeSettings.Add(new SoMeSettings
        {
            EventId = EventId, Enabled = true,
            EventSystemUrl = "https://eldk27.expertslive.dk",
            EventTags = "#ELDK27", OrganizerCredits = "Organizer One",
        });
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "co-1", CompanyName = "Twoday",
            SponsorPackage = SponsorPackage.Diamond, HasCurrentBoothOrder = true,
        });
        db.ShopCompanies.Add(new ShopCompany
        {
            EventId = EventId, SponsorCompanyId = "co-1",
            LegalName = "twoday Danmark A/S", PublicName = "Twoday",
        });
        var post = new SoMePost
        {
            EventId = EventId, Type = SoMePostType.Sponsor,
            TemplateKind = SoMeTemplateKind.SponsorCategory, SubjectKey = "tier:Diamond", Occurrence = 2,
            ScheduledAtUtc = new DateTimeOffset(2027, 1, 21, 8, 0, 0, TimeSpan.Zero),
            Status = SoMePostStatus.Queued, IsActive = active,
            PlanState = SoMePostPlanState.Scheduled,
            AutoText = "Our {SponsorTier} sponsors\n\n{IntroText}\n\n{SponsorList}",
            IntroText = intro, CreatedAt = Now.AddDays(-30),
        };
        db.SoMePosts.Add(post);
        await db.SaveChangesAsync();
        return post;
    }

    [Fact]
    public async Task A_scheduled_tier_intro_carrying_a_billing_name_names_the_public_names_live()
    {
        using var db = NewDb();
        var post = await SeedAsync(db, "A huge shoutout to our Diamond sponsors - twoday Danmark A/S! 💎");
        var handler = new ReplyHandler("A huge shoutout to our Diamond sponsors {SponsorNames}! 💎");

        await Svc(db, handler).RunAsync(EventId);

        var after = await db.SoMePosts.AsNoTracking().SingleAsync(p => p.Id == post.Id);
        Assert.Equal("A huge shoutout to our Diamond sponsors {SponsorNames}! 💎", after.IntroText);
        // 🔒 His approval and schedule are untouched — only the machine-written line changed.
        Assert.True(after.IsActive);
        Assert.Equal(post.ScheduledAtUtc, after.ScheduledAtUtc);
        // The model was asked for the placeholder and given no names to type.
        Assert.Contains("{SponsorNames}", handler.LastBody);
        Assert.DoesNotContain("twoday", handler.LastBody, StringComparison.OrdinalIgnoreCase);

        // 🔑 What reads on LinkedIn: the PUBLIC name, resolved now.
        var composer = new SoMePostComposer(db, new SoMeVariableResolver(db));
        var text = composer.Resolve(after.EffectiveText, await composer.ValuesForAsync(after));
        Assert.Contains("A huge shoutout to our Diamond sponsors Twoday! 💎", text);
        Assert.DoesNotContain("A/S", text);
    }

    [Fact]
    public async Task Without_the_AI_the_fixed_sentence_names_the_sponsors_live()
    {
        using var db = NewDb();
        var post = await SeedAsync(db, "Cheers to twoday Danmark A/S, our Diamond sponsor!");

        await Svc(db).RunAsync(EventId);   // no AI configured

        var after = await db.SoMePosts.AsNoTracking().SingleAsync(p => p.Id == post.Id);
        Assert.Equal(SoMeScheduleService.TierIntroFallback, after.IntroText);
        var composer = new SoMePostComposer(db, new SoMeVariableResolver(db));
        Assert.Contains("A huge thank-you to our Diamond sponsors Twoday!",
            composer.Resolve(after.EffectiveText, await composer.ValuesForAsync(after)));
    }

    [Fact]
    public async Task A_regenerated_intro_that_types_a_name_gets_the_fixed_sentence()
    {
        using var db = NewDb();
        var post = await SeedAsync(db, "Thanks twoday Danmark A/S!");

        await Svc(db, new ReplyHandler("Big thanks to Twoday for the support!")).RunAsync(EventId);

        Assert.Equal(SoMeScheduleService.TierIntroFallback,
            (await db.SoMePosts.AsNoTracking().SingleAsync(p => p.Id == post.Id)).IntroText);
    }

    [Fact]
    public async Task A_live_tier_intro_is_left_alone()
    {
        using var db = NewDb();
        var post = await SeedAsync(db, "Big cheers to {SponsorNames}, our Diamond sponsors!");
        // ⚠️ The planner may still ask the AI for the OTHER round's intro (round 1 is not seeded);
        // what matters is that this post's intro is not replaced.
        await Svc(db, new ReplyHandler("A different sentence {SponsorNames}.")).RunAsync(EventId);

        Assert.Equal("Big cheers to {SponsorNames}, our Diamond sponsors!",
            (await db.SoMePosts.AsNoTracking().SingleAsync(p => p.Id == post.Id)).IntroText);
    }

    [Theory]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "Nerdio" }, "Nerdio")]
    [InlineData(new[] { "Nerdio", "Twoday" }, "Nerdio and Twoday")]
    [InlineData(new[] { "Apento", "Nerdio", "Twoday" }, "Apento, Nerdio and Twoday")]
    public void The_names_read_as_a_sentence(string[] names, string? expected) =>
        Assert.Equal(expected, SoMeVariableResolver.JoinNames(names));

    [Theory]
    [InlineData("twoday Danmark A/S", "twoday")]
    [InlineData("Admin By Request Nordics", "Admin By Request")]
    [InlineData("Glueckkanja AG", "Glueckkanja")]
    [InlineData("APENTO ApS", "APENTO")]
    [InlineData("Nerdio", "Nerdio")]
    public void The_core_name_survives_a_legal_suffix(string name, string core) =>
        Assert.Equal(core, SoMeScheduleService.CoreCompanyName(name));

    [Fact]
    public void A_name_matches_as_a_whole_word_only()
    {
        var names = new[] { "Nerdio" };
        Assert.True(SoMeScheduleService.NamesACompany("Thanks, Nerdio!", names));
        Assert.False(SoMeScheduleService.NamesACompany("Thanks, Nerdiosity!", names));
    }
}
