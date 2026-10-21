using System.Net;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1300 — the sponsor announcement TAGS the company, first in the Tag line, from an organization id
/// the scheduled routine looks up (operator 2026-09-23: *"it should tag the company in the tag area,
/// so the companys linkedin is automatically tagged - and then people are mentioned"*).
/// </summary>
public sealed class SponsorCompanyMentionTests
{
    private const string Company = "pinksky";

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-23T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Answers every request with one canned response and counts the calls.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static CommunityHubDbContext NewDb() => new(
        new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"sponsor-org-{Guid.NewGuid():N}").Options);

    private static async Task<int> SeedAsync(
        CommunityHubDbContext db, string? linkedInUrl, string? orgId = null, string? lookedUpFrom = null,
        bool withSigner = true, bool withToken = true)
    {
        var ev = new Event { CommunityName = "Demo", Code = "DEMO", DisplayName = "Demo 2027" };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = ev.Id, SponsorCompanyId = Company, CompanyName = "Pinksky",
            SponsorPackage = SponsorPackage.Gold, LinkedInUrl = linkedInUrl,
            LinkedInOrganizationId = orgId, LinkedInOrganizationLookedUpFrom = lookedUpFrom,
        });
        if (withSigner)
        {
            db.Participants.Add(new Participant
            {
                EventId = ev.Id, FullName = "Dana Signer", IsActive = true, Role = ParticipantRole.Sponsor,
                SponsorCompanyId = Company, IsSigner = true, LinkedInPersonUrn = "urn:li:person:AAA",
            });
        }
        if (withToken)
        {
            db.LinkedInOAuthTokens.Add(new LinkedInOAuthToken
            {
                Kind = LinkedInTokenStore.OrgKind, AccessToken = "tok",
                ExpiresAt = DateTimeOffset.Parse("2026-12-31T00:00:00Z"),
            });
        }
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static SponsorOrganizationResolutionService NewSweep(
        CommunityHubDbContext db, StubHandler handler, FixedClock clock)
    {
        var options = new LinkedInOptions();
        var client = new LinkedInOrganizationLookupClient(
            new HttpClient(handler), options,
            new LinkedInTokenStore(db, options, new HttpClient(handler), clock));
        return new SponsorOrganizationResolutionService(db, client, clock);
    }

    private static string RenderSponsorPost(Dictionary<string, string?> v) =>
        SoMeTemplateRenderer.Render(
            "We are very proud to announce {SponsorName} as a {SponsorTier} sponsor.\n\n"
            + "Tag: {SponsorCompanyMention} {SponsorSigner} {SponsorEventCoordinators}\n\n#Tags", v);

    // ------------------------------------------------ the finder response

    [Fact]
    public void Parse_reads_the_numeric_id_for_the_slug_asked_for()
    {
        var r = LinkedInOrganizationLookupClient.Parse("pinksky",
            """{"elements":[{"vanityName":"pinksky","localizedName":"Pinksky","id":12345678}]}""");
        Assert.Equal(OrganizationLookupStatus.Resolved, r.Status);
        Assert.Equal("12345678", r.OrganizationId);
    }

    /// <summary>🔒 Tagging a stranger's company in a public post is worse than plain text.</summary>
    [Fact]
    public void Parse_refuses_an_element_for_a_different_slug()
    {
        var r = LinkedInOrganizationLookupClient.Parse("pinksky",
            """{"elements":[{"vanityName":"pinkskyfoods","id":999}]}""");
        Assert.Equal(OrganizationLookupStatus.NotFound, r.Status);
        Assert.Null(r.OrganizationId);
    }

    [Fact]
    public void Parse_treats_an_empty_result_as_not_found_and_garbage_as_a_failure()
    {
        Assert.Equal(OrganizationLookupStatus.NotFound,
            LinkedInOrganizationLookupClient.Parse("x", """{"elements":[]}""").Status);
        Assert.Equal(OrganizationLookupStatus.LookupFailed,
            LinkedInOrganizationLookupClient.Parse("x", "<html>").Status);
    }

    // ------------------------------------------------ when to ask

    [Theory]
    [InlineData(null, null, null, false, true)]          // never checked → ask
    [InlineData("1", "pinksky", null, false, false)]     // resolved for this slug → never again
    [InlineData("1", null, null, false, false)]          // set by hand → it wins
    [InlineData("1", "oldname", null, false, true)]      // the URL's slug changed → ask
    [InlineData(null, null, 1, false, false)]            // negative answer an hour ago → wait
    [InlineData(null, null, 25, false, true)]            // …a day later → ask again
    public void ShouldAsk_follows_the_cache_rules(
        string? orgId, string? lookedUpFrom, int? checkedHoursAgo, bool _, bool expected)
    {
        var now = DateTimeOffset.Parse("2026-09-23T10:00:00Z");
        DateTimeOffset? checkedAt = checkedHoursAgo is int h ? now.AddHours(-h) : null;
        Assert.Equal(expected,
            SponsorOrganizationResolutionService.ShouldAsk("pinksky", orgId, lookedUpFrom, checkedAt, now));
    }

    // ------------------------------------------------ the sweep

    [Fact]
    public async Task The_sweep_looks_the_company_up_once_and_caches_it()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db, "https://www.linkedin.com/company/pinksky/");
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"elements":[{"vanityName":"pinksky","id":12345678}]}""");
        var clock = new FixedClock();

        var first = await NewSweep(db, handler, clock).ResolveAsync(ev);
        var second = await NewSweep(db, handler, clock).ResolveAsync(ev);

        Assert.Equal(1, first.Resolved);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, handler.Calls);                         // cached — not asked again
        Assert.Contains("q=vanityName&vanityName=pinksky", handler.LastUrl);
        var s = await db.SponsorInfos.SingleAsync();
        Assert.Equal("12345678", s.LinkedInOrganizationId);
        Assert.Equal("pinksky", s.LinkedInOrganizationLookedUpFrom);
    }

    /// <summary>🔒 "Could not ask" is not "no such company" — a failure never erases a known id.</summary>
    [Fact]
    public async Task A_failed_lookup_keeps_the_existing_id()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db, "https://www.linkedin.com/company/pinksky-new", orgId: "111", lookedUpFrom: "pinksky");
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, "{}");

        var r = await NewSweep(db, handler, new FixedClock()).ResolveAsync(ev);

        Assert.Equal(1, r.Failed);
        Assert.Equal("111", (await db.SponsorInfos.SingleAsync()).LinkedInOrganizationId);
    }

    [Fact]
    public async Task Without_a_LinkedIn_connection_nothing_is_asked_and_nothing_breaks()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db, "https://www.linkedin.com/company/pinksky", withToken: false);
        var handler = new StubHandler(HttpStatusCode.OK, "{}");

        var r = await NewSweep(db, handler, new FixedClock()).ResolveAsync(ev);

        Assert.Equal(1, r.Failed);
        Assert.Equal(0, handler.Calls);
        Assert.Null((await db.SponsorInfos.SingleAsync()).LinkedInOrganizationId);
    }

    // ------------------------------------------------ the post

    /// <summary>The operator's screenshot, fixed: company first in the Tag line, then the people.</summary>
    [Fact]
    public async Task The_Tag_line_tags_the_company_first_then_the_people()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db, "https://www.linkedin.com/company/pinksky", orgId: "12345678", lookedUpFrom: "pinksky");

        var text = RenderSponsorPost(await new SoMeVariableResolver(db).SponsorValuesAsync(ev, Company));

        Assert.Contains("Tag: @[Pinksky](urn:li:organization:12345678) @[Dana Signer](urn:li:person:AAA)", text);
        Assert.Contains("announce Pinksky as a Gold sponsor", text);   // named, tagged once
    }

    /// <summary>No id yet: the people are still tagged, and the company is NOT printed as a bare name.</summary>
    [Fact]
    public async Task Without_an_id_the_Tag_line_carries_only_the_people()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db, "https://www.linkedin.com/company/pinksky");

        var v = await new SoMeVariableResolver(db).SponsorValuesAsync(ev, Company);

        Assert.Null(v["SponsorCompanyMention"]);
        Assert.Contains("Tag: @[Dana Signer](urn:li:person:AAA)", RenderSponsorPost(v));
    }

    /// <summary>🔴 The latent bug: with nothing to tag, the line used to publish as a bare "Tag:".</summary>
    [Fact]
    public async Task With_nothing_to_tag_the_Tag_line_disappears()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db, null, withSigner: false);

        var text = RenderSponsorPost(await new SoMeVariableResolver(db).SponsorValuesAsync(ev, Company));

        Assert.DoesNotContain("Tag:", text);
        Assert.DoesNotContain("\n\n\n", text);
    }

    [Fact]
    public void The_default_Sponsor_template_tags_the_company_first_in_the_Tag_line()
    {
        var body = SoMeTemplateCatalog.DefaultBody(SoMeTemplateKind.Sponsor);
        Assert.Contains("Tag: {SponsorCompanyMention} {SponsorSigner} {SponsorEventCoordinators}", body);
        Assert.Contains("announce {SponsorName} as", body);
        Assert.Empty(SoMeTemplateRenderer.UnknownPlaceholders(body));
    }

    // ------------------------------------------------ the renderer rule

    [Fact]
    public void A_line_is_dropped_only_when_every_placeholder_on_it_is_known_and_empty()
    {
        var v = new Dictionary<string, string?> { ["A"] = null, ["B"] = "b" };
        Assert.Equal("keep", SoMeTemplateRenderer.Render("Tag: {A}\nkeep", v));
        Assert.Equal("Tag: b", SoMeTemplateRenderer.Render("Tag: {A} {B}", v));
        Assert.Equal("Tag: {Typo}", SoMeTemplateRenderer.Render("Tag: {A} {Typo}", v));   // unknown stays visible
        Assert.Equal("plain line", SoMeTemplateRenderer.Render("plain line", v));
    }
}
