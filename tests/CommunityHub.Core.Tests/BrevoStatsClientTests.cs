using System.Net;
using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>§1381 — reading a campaign's opens/clicks from Brevo by tag. A fake handler; nothing leaves the test.</summary>
public sealed class BrevoStatsClientTests
{
    private sealed class Fake(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static readonly DateOnly From = new(2026, 11, 11), To = new(2026, 11, 20);

    [Fact]
    public async Task Without_a_key_it_says_not_connected_and_calls_nothing()
    {
        var fake = new Fake(HttpStatusCode.OK, "{}");
        var client = new BrevoStatsClient(new HttpClient(fake), new BrevoApiOptions());

        var r = await client.ForTagAsync("ceh-campaign-1", From, To);

        Assert.Null(r.Stats);
        Assert.Contains("not connected", r.Problem);
        Assert.Null(fake.Last);
    }

    [Fact]
    public async Task It_asks_for_the_tag_and_date_range_and_reads_the_numbers()
    {
        var fake = new Fake(HttpStatusCode.OK,
            """{"requests":120,"delivered":100,"opens":90,"uniqueOpens":40,"clicks":30,"uniqueClicks":12,"unsubscribed":2,"hardBounces":1,"softBounces":3,"spamReports":0,"blocked":0}""");
        var client = new BrevoStatsClient(new HttpClient(fake), new BrevoApiOptions { ApiKey = "k" });

        var r = await client.ForTagAsync("ceh-campaign-7", From, To);

        var s = Assert.IsType<BrevoTagStats>(r.Stats);
        Assert.Equal(100, s.Delivered);
        Assert.Equal(40, s.UniqueOpens);
        Assert.Equal(0.4, s.OpenRate, 3);
        Assert.Equal(0.12, s.ClickRate, 3);
        var url = fake.Last!.RequestUri!.ToString();
        Assert.Contains("tag=ceh-campaign-7", url);
        Assert.Contains("startDate=2026-11-11", url);
        Assert.Contains("endDate=2026-11-20", url);
        Assert.Equal("k", fake.Last.Headers.GetValues("api-key").Single());
    }

    [Fact]
    public async Task A_refusal_passes_Brevos_own_sentence_through()
    {
        var fake = new Fake(HttpStatusCode.Unauthorized,
            """{"message":"We have detected you are using an unrecognised IP address 1.2.3.4.","code":"unauthorized"}""");
        var client = new BrevoStatsClient(new HttpClient(fake), new BrevoApiOptions { ApiKey = "k" });

        var r = await client.ForTagAsync("t", From, To);

        Assert.Null(r.Stats);
        Assert.Contains("401", r.Problem);
        Assert.Contains("unrecognised IP address", r.Problem);
    }
}
