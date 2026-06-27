using System.Net;
using System.Text;
using System.Text.Json;
using CommunityHub.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// REQUIREMENTS §41b — the "Zoho ← CEH fill-blank" reconcile must push the company
/// LinkedIn/Twitter URLs onto a Backstage EXHIBITOR whose social pages are blank, and it
/// must do so INDEPENDENTLY of the website field (the original gap: website pushed, social
/// did not). These tests pin the two halves of the contract on the REAL <see cref="ZohoClient"/>
/// driven through a fake HTTP handler — NO live Zoho call:
///   • WRITE: <see cref="ZohoClient.UpdateExhibitorAsync"/> emits
///     <c>company_social_pages.{linkedin,twitter}</c> even when <c>websiteUrl</c> is null
///     (because Zoho already had a website, so the §41b gate sends null for it).
///   • READ:  <see cref="ZohoClient.GetExhibitorByIdAsync"/> parses the SAME
///     <c>company_social_pages.{linkedin,twitter}</c> the write produces, so the blank-gate
///     sees what the write set — and treats an object-wrapped URL as "set", not blank.
/// </summary>
public sealed class ZohoExhibitorSocialFillBlankTests
{
    private const string Portal = "P1";
    private const string Event = "E1";
    private const string Exhibitor = "EX1";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _respond;
        public string? LastBody { get; private set; }
        public HttpMethod? LastMethod { get; private set; }

        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            var (status, body) = _respond(request);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (ZohoClient Client, StubHandler Handler) NewClient(
        Func<HttpRequestMessage, (HttpStatusCode, string)> respond)
    {
        var options = new ZohoOptions
        {
            Enabled = true,
            ApiDomain = "https://zoho.test",
            BackstagePortalId = Portal,
            BackstageEventId = Event,
        };
        var handler = new StubHandler(respond);
        var client = new ZohoClient(new HttpClient(handler), options, NullLogger<ZohoClient>.Instance);
        return (client, handler);
    }

    [Fact]
    public async Task UpdateExhibitor_pushes_social_pages_even_when_website_is_null()
    {
        // The §41b caller sends websiteUrl=null when Zoho already has a website (BlankInZoho==false),
        // but still passes linkedin/twitter because Zoho's social pages are blank. The write must
        // include company_social_pages regardless of the (absent) website.
        var (client, handler) = NewClient(_ => (HttpStatusCode.OK, "{}"));

        var ok = await client.UpdateExhibitorAsync(
            "tok", Exhibitor,
            companyOverview: null, companyShortDescription: null,
            companyName: "Patch My PC",
            websiteUrl: null,
            linkedInUrl: "https://www.linkedin.com/company/patchmypc",
            twitterUrl: "https://x.com/PatchMyPC");

        Assert.True(ok);
        Assert.Equal(HttpMethod.Put, handler.LastMethod);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("website_url", out _)); // website not sent (it was already set in Zoho)
        Assert.True(root.TryGetProperty("company_social_pages", out var social));
        Assert.Equal("https://www.linkedin.com/company/patchmypc", social.GetProperty("linkedin").GetString());
        Assert.Equal("https://x.com/PatchMyPC", social.GetProperty("twitter").GetString());
    }

    [Fact]
    public async Task UpdateExhibitor_pushes_only_linkedin_when_twitter_is_blank()
    {
        var (client, handler) = NewClient(_ => (HttpStatusCode.OK, "{}"));

        await client.UpdateExhibitorAsync(
            "tok", Exhibitor,
            companyOverview: null, companyShortDescription: null,
            companyName: "Robopack",
            websiteUrl: null,
            linkedInUrl: "https://www.linkedin.com/company/robopack-aps",
            twitterUrl: null);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var social = doc.RootElement.GetProperty("company_social_pages");
        Assert.Equal("https://www.linkedin.com/company/robopack-aps", social.GetProperty("linkedin").GetString());
        Assert.False(social.TryGetProperty("twitter", out _));
    }

    [Fact]
    public async Task GetExhibitor_reads_empty_social_pages_as_blank_so_the_gate_pushes()
    {
        // Live company 10/19 shape: company_social_pages is {} (or absent) ⇒ read must report blank,
        // so BlankInZoho(...) is true and the reconcile pushes the CEH value.
        const string body = """
            { "id": "EX1", "website_url": "https://example.com", "company_social_pages": {} }
            """;
        var (client, _) = NewClient(_ => (HttpStatusCode.OK, body));

        var z = await client.GetExhibitorByIdAsync("tok", Exhibitor);

        Assert.NotNull(z);
        Assert.Null(z!.LinkedInUrl);
        Assert.Null(z.TwitterUrl);
        Assert.Equal("https://example.com", z.WebsiteUrl); // website read so its gate stays correct too
    }

    [Fact]
    public async Task GetExhibitor_reads_social_pages_written_by_the_update_path()
    {
        // The exact shape UpdateExhibitorAsync writes — read must round-trip it (so a second sync
        // sees it as set and never re-pushes / never overwrites).
        const string body = """
            { "id": "EX1",
              "company_social_pages": { "linkedin": "https://www.linkedin.com/company/codetwo/", "twitter": "https://x.com/codetwo" } }
            """;
        var (client, _) = NewClient(_ => (HttpStatusCode.OK, body));

        var z = await client.GetExhibitorByIdAsync("tok", Exhibitor);

        Assert.Equal("https://www.linkedin.com/company/codetwo/", z!.LinkedInUrl);
        Assert.Equal("https://x.com/codetwo", z.TwitterUrl);
    }

    [Fact]
    public async Task GetExhibitor_reads_object_wrapped_social_url_as_set_not_blank()
    {
        // Defensive: some Zoho Backstage schemas return the social entry as an object that wraps
        // the URL. The read must treat that as SET (so the §41b gate does not falsely re-push /
        // mis-detect it as blank), keeping read↔write blank-detection symmetric.
        const string body = """
            { "id": "EX1",
              "company_social_pages": { "linkedin": { "url": "https://www.linkedin.com/company/acme" } } }
            """;
        var (client, _) = NewClient(_ => (HttpStatusCode.OK, body));

        var z = await client.GetExhibitorByIdAsync("tok", Exhibitor);

        Assert.Equal("https://www.linkedin.com/company/acme", z!.LinkedInUrl);
        Assert.Null(z.TwitterUrl);
    }
}
