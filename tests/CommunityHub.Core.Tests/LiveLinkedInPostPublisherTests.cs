using System.Net;
using CommunityHub.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §19/§31 live LinkedIn publisher — proves it is correctly WIRED yet safe:
/// DryRun (default) calls LinkedIn NEVER (logs intent, leaves the post queued); a
/// real run posts to the org feed and returns the post id from the response header;
/// failures are honest; and the company-page value is normalized to an org URN.
/// </summary>
public sealed class LiveLinkedInPostPublisherTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            if (request.Content is not null) LastBody = await request.Content.ReadAsStringAsync(ct);
            return _responder(request);
        }
    }

    private static LiveLinkedInPostPublisher Make(LinkedInOptions o, StubHandler h) =>
        new(new HttpClient(h), o, NullLogger<LiveLinkedInPostPublisher>.Instance);

    private static LinkedInPost Post(string org = "12345") =>
        new(org, "Hello community!", null, Array.Empty<string>());

    [Fact]
    public async Task Not_configured_cannot_publish()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var pub = Make(new LinkedInOptions { Enabled = false }, h);
        Assert.False(pub.CanPublish);
        var r = await pub.PublishAsync(Post());
        Assert.False(r.Published);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task DryRun_holds_the_post_and_calls_linkedin_never()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var pub = Make(new LinkedInOptions { Enabled = true, AccessToken = "tok", DryRun = true }, h);

        Assert.True(pub.CanPublish);   // wired...
        var r = await pub.PublishAsync(Post());

        Assert.False(r.Published);                          // ...but held
        Assert.Equal(0, h.Calls);                           // NO LinkedIn call
        Assert.Contains("DRY-RUN", r.Message);
    }

    [Fact]
    public async Task Real_run_posts_and_returns_the_post_id_header()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.Created);
        resp.Headers.TryAddWithoutValidation("x-restli-id", "urn:li:share:7000");
        var h = new StubHandler(_ => resp);
        var pub = Make(new LinkedInOptions { Enabled = true, AccessToken = "tok", DryRun = false }, h);

        var r = await pub.PublishAsync(Post("12345"));

        Assert.True(r.Published);
        Assert.Equal("urn:li:share:7000", r.ExternalPostId);
        Assert.Equal(1, h.Calls);
        Assert.Contains("/rest/posts", h.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"author\":\"urn:li:organization:12345\"", h.LastBody);
        Assert.Contains("\"lifecycleState\":\"PUBLISHED\"", h.LastBody);
        Assert.Equal("Bearer", h.LastRequest!.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Api_error_is_reported_not_faked()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = new StringContent("{\"message\":\"no access\"}") });
        var pub = Make(new LinkedInOptions { Enabled = true, AccessToken = "tok", DryRun = false }, h);

        var r = await pub.PublishAsync(Post());

        Assert.False(r.Published);
        Assert.Contains("403", r.Message);
    }

    [Fact]
    public async Task Unresolvable_organization_is_refused_without_calling()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var pub = Make(new LinkedInOptions { Enabled = true, AccessToken = "tok", DryRun = false }, h);

        var r = await pub.PublishAsync(Post("https://www.linkedin.com/company/expertslive-denmark"));

        Assert.False(r.Published);
        Assert.Equal(0, h.Calls);
        Assert.Contains("organization id", r.Message);
    }

    [Theory]
    [InlineData("12345", "urn:li:organization:12345")]
    [InlineData("urn:li:organization:999", "urn:li:organization:999")]
    [InlineData("https://www.linkedin.com/company/123456/", "urn:li:organization:123456")]
    [InlineData("expertslive", null)]
    public void Org_urn_normalization(string input, string? expected)
    {
        Assert.Equal(expected, LiveLinkedInPostPublisher.ToOrganizationUrn(input));
    }

    // ---- §326k: little-text-format commentary escaping ------------------------
    // LinkedIn's Posts API treats ( ) { } [ ] < > @ | * _ ~ \ as control characters
    // and TRUNCATES/mangles the rendered post at the first unescaped one (the
    // operator's live symptom: "only half the text comes over").

    [Fact]
    public void Commentary_escaping_covers_little_text_controls_but_keeps_hashtags()
    {
        var raw = "Zero Trust (300) — see [my] session {live} @ ELDK27 <today> *now* _really_ ~soon~ | \\ #ELDK27";
        var escaped = LiveLinkedInPostPublisher.EscapeCommentary(raw);

        Assert.Contains(@"\(300\)", escaped);
        Assert.Contains(@"\[my\]", escaped);
        Assert.Contains(@"\{live\}", escaped);
        Assert.Contains(@"\@ ELDK27", escaped);
        Assert.Contains(@"\<today\>", escaped);
        Assert.Contains(@"\*now\*", escaped);
        Assert.Contains(@"\_really\_", escaped);
        Assert.Contains(@"\~soon\~", escaped);
        Assert.Contains(@"\|", escaped);
        // Hashtags must keep auto-linking — '#' is never escaped.
        Assert.Contains("#ELDK27", escaped);
        Assert.DoesNotContain(@"\#", escaped);
        // Plain text passes through untouched.
        Assert.Equal("Hope to see you there!", LiveLinkedInPostPublisher.EscapeCommentary("Hope to see you there!"));
    }

    [Fact]
    public async Task Payload_commentary_is_escaped_at_the_chokepoint()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.Created);
        resp.Headers.TryAddWithoutValidation("x-restli-id", "urn:li:share:7001");
        var h = new StubHandler(_ => resp);
        var pub = Make(new LinkedInOptions { Enabled = true, AccessToken = "tok", DryRun = false }, h);

        var r = await pub.PublishAsync(
            new LinkedInPost("12345", "Deep dive (300) #ELDK27", null, Array.Empty<string>()));

        Assert.True(r.Published);
        // In the raw JSON body the backslash-escaped text \(300\) serializes as \\(300\\).
        Assert.Contains(@"Deep dive \\(300\\) #ELDK27", h.LastBody);
    }
}
