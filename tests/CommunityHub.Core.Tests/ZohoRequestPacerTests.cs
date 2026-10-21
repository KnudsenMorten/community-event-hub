using System.Net;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1361 — Zoho's Backstage request limit. Measured on PROD 2026-10-19: ~275 calls in 8 s at the xx:x5 tick, the first
/// "HTTP 400 — Request limit exceeded" at call #110, 41 refused every run, always the same sponsors (Egiss …) because
/// the sync walked a fixed order. Operator: *"i dont see that the zoho backstage chg being applied correctly"* →
/// *"yes, build it and deploy when green"*.
/// </summary>
public sealed class ZohoRequestPacerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 20, 5, 0, TimeSpan.Zero);

    [Fact]
    public void The_budget_lets_80_through_per_minute_then_says_how_long_to_wait()
    {
        var b = new ZohoRequestBudget();
        for (var i = 0; i < ZohoRequestBudget.MaxPerWindow; i++)
            Assert.Equal(TimeSpan.Zero, b.TryReserve(T0.AddMilliseconds(i)));

        var wait = b.TryReserve(T0.AddSeconds(1));
        Assert.True(wait > TimeSpan.FromSeconds(58) && wait <= TimeSpan.FromSeconds(60), $"wait {wait}");
        Assert.Equal(TimeSpan.Zero, b.TryReserve(T0.AddMinutes(1).AddMilliseconds(1)));   // the oldest slot has expired
    }

    [Fact]
    public void After_Zohos_refusal_nothing_is_sent_for_the_pause()
    {
        var b = new ZohoRequestBudget();
        b.Refused(T0);

        Assert.True(b.IsPaused(T0.AddSeconds(30)));
        Assert.True(b.TryReserve(T0.AddSeconds(30)) > TimeSpan.Zero);
        Assert.False(b.IsPaused(T0 + ZohoRequestBudget.PauseAfterRefusal));
        Assert.Equal(TimeSpan.Zero, b.TryReserve(T0 + ZohoRequestBudget.PauseAfterRefusal));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"status_code\":\"400\",\"message\":\"Request limit exceeded\"}", true)]
    [InlineData(HttpStatusCode.TooManyRequests, null, true)]
    [InlineData(HttpStatusCode.BadRequest, "{\"message\":\"The selected booth belongs to a different exhibitor category\"}", false)]
    [InlineData(HttpStatusCode.OK, "Request limit exceeded", false)]
    public void Zohos_limit_refusal_is_recognised_and_an_ordinary_400_is_not(HttpStatusCode status, string? body, bool refusal) =>
        Assert.Equal(refusal, ZohoRequestBudget.IsLimitRefusal(status, body));

    [Theory]
    [InlineData("https://www.zohoapis.eu/backstage/v3/portals/1/events/2/sponsors/3", true)]
    [InlineData("https://accounts.zoho.eu/oauth/v2/token", false)]   // the token endpoint has its own limit + cooldown
    public void Only_the_Backstage_API_is_paced(string url, bool paced) =>
        Assert.Equal(paced, ZohoRequestPacer.IsApiCall(new Uri(url)));

    [Fact]
    public async Task During_the_pause_a_call_is_answered_as_refused_without_reaching_Zoho()
    {
        var budget = new ZohoRequestBudget();
        var inner = new CountingHandler(HttpStatusCode.BadRequest, "{\"status_code\":\"400\",\"message\":\"Request limit exceeded\"}");
        using var client = new HttpClient(new ZohoRequestPacer(budget, TimeProvider.System) { InnerHandler = inner });
        const string url = "https://www.zohoapis.eu/backstage/v3/portals/1/events/2/sponsors/3";

        var first = await client.GetAsync(url);    // Zoho refuses → the pause starts
        var second = await client.GetAsync(url);   // answered here, never sent

        Assert.Equal(1, inner.Calls);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Contains("Request limit exceeded", await second.Content.ReadAsStringAsync());
        Assert.Contains("Request limit exceeded", await first.Content.ReadAsStringAsync());   // the caller can still read Zoho's body
    }

    [Fact]
    public void The_sponsor_order_rotates_every_10_minutes_so_no_company_is_always_last()
    {
        var companies = new[] { "10", "106", "108", "19", "4" };
        var run1 = SponsorZohoSyncService.RotateForRun(companies, T0);
        var run2 = SponsorZohoSyncService.RotateForRun(companies, T0.AddMinutes(10));

        Assert.Equal(companies.OrderBy(c => c), run1.OrderBy(c => c));   // everyone, once
        Assert.NotEqual(run1[0], run2[0]);
        // Over a full cycle every company starts a run once.
        var starters = Enumerable.Range(0, companies.Length)
            .Select(i => SponsorZohoSyncService.RotateForRun(companies, T0.AddMinutes(10 * i))[0]).ToHashSet();
        Assert.Equal(companies.Length, starters.Count);
    }

    private sealed class CountingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
