using System.Net;
using System.Text;
using CommunityHub.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1154 — "Zoho refused it" and "Zoho was down" are different facts, and only one is human work.
///
/// <para>Operator 2026-08-31, on a hand-entry mail listing a sponsor's website and both social
/// pages: <i>"this mail is not relevant for the mentioned fields, as all fields are covered by
/// api"</i>. He was right, and the fields were never the problem — the PUT had come back
/// <b>502</b>, a Zoho gateway page, and a <c>bool</c> collapsed that into the same "could not
/// write" a genuine refusal produces. The fallback then asked him to type in values CEH can write
/// and would retry ten minutes later.</para>
///
/// <para>⚠️ Measured before changing anything: ONE such failure in the 7 days before, ONE after — a
/// rare transient, not a regression, and not a pattern worth redesigning around.</para>
///
/// <para>🔑 The same distinction §1140b drew for the ERP read-back: inventing work out of "I could
/// not reach it" is how a correct system teaches you to distrust its mail.</para>
/// </summary>
public class ZohoWriteOutcomeTests
{
    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public StatusHandler(HttpStatusCode status, string body = "{}")
        {
            _status = status; _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }

    private static ZohoClient Client(HttpStatusCode status, string body = "{}") =>
        new(new HttpClient(new StatusHandler(status, body)),
            new ZohoOptions
            {
                Enabled = true,
                ApiDomain = "https://zoho.test",
                BackstagePortalId = "P1",
                BackstageEventId = "E1",
            },
            NullLogger<ZohoClient>.Instance);

    // ---- the reported case ------------------------------------------------------------------

    /// <summary>
    /// 🔴 THE BUG HE REPORTED. A 502 is Zoho's own failure, not a verdict on the payload.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]           // the measured one
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_5xx_is_UNAVAILABLE_so_it_never_becomes_hand_entry_work(HttpStatusCode status)
    {
        var outcome = await Client(status, "<html>gateway error</html>")
            .UpdateExhibitorAsync("tok", "EX-1", "overview", null);

        Assert.Equal(ZohoClient.ZohoWriteOutcome.Unavailable, outcome);
        Assert.NotEqual(ZohoClient.ZohoWriteOutcome.Refused, outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Try_later_statuses_are_UNAVAILABLE_too(HttpStatusCode status)
    {
        // 429 especially: reporting a rate-limit as hand-entry would turn a busy minute into a page
        // of invented work.
        var outcome = await Client(status).UpdateExhibitorAsync("tok", "EX-1", "overview", null);
        Assert.Equal(ZohoClient.ZohoWriteOutcome.Unavailable, outcome);
    }

    // ---- what must STILL reach him -----------------------------------------------------------

    /// <summary>
    /// 🔒 A 4xx will never fix itself, so it must still become a hand-entry line.
    /// </summary>
    /// <remarks>
    /// The 400 case is real: §801.2 measured Zoho answering
    /// <c>{"message":"`shortDescription` is too long"}</c>, and that value genuinely never lands
    /// until a human shortens it.
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task A_4xx_is_REFUSED_and_still_reaches_him(HttpStatusCode status)
    {
        var outcome = await Client(status, "{\"message\":\"`shortDescription` is too long\"}")
            .UpdateExhibitorAsync("tok", "EX-1", "overview", null);

        Assert.Equal(ZohoClient.ZohoWriteOutcome.Refused, outcome);
    }

    [Fact]
    public async Task A_success_is_WRITTEN()
    {
        Assert.Equal(
            ZohoClient.ZohoWriteOutcome.Written,
            await Client(HttpStatusCode.OK).UpdateExhibitorAsync("tok", "EX-1", "overview", null));
    }

    // ---- the sponsor record gets the same treatment ------------------------------------------

    [Fact]
    public async Task The_SPONSOR_update_classifies_the_same_way()
    {
        // 🔒 Both records, one rule. The sponsor push had the identical bool and the identical
        // fallback, so fixing only the exhibitor would have left half the mail wrong.
        Assert.Equal(
            ZohoClient.ZohoWriteOutcome.Unavailable,
            await Client(HttpStatusCode.BadGateway).UpdateSponsorAsync("tok", "SP-1", "d", "w", "n"));

        Assert.Equal(
            ZohoClient.ZohoWriteOutcome.Refused,
            await Client(HttpStatusCode.BadRequest).UpdateSponsorAsync("tok", "SP-1", "d", "w", "n"));

        Assert.Equal(
            ZohoClient.ZohoWriteOutcome.Written,
            await Client(HttpStatusCode.OK).UpdateSponsorAsync("tok", "SP-1", "d", "w", "n"));
    }
}
