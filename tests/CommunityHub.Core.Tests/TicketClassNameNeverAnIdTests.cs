using System.Net;
using System.Text.Json;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1019 — a ticket class is named by its NAME, never by its 17-digit id. Anywhere.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-09, the FOURTH time he had to say it: *"this is the 4 time i tell you that
/// no-one knows a id - it must be the ticket class name"* · *"so mails, erp integration, etc must
/// use the ticket class name; not the id"* · *"also the gui page"*.</para>
///
/// <para>🔑 <b>Why the earlier two fixes kept failing, which is the whole lesson.</b> The name was
/// looked up from CLAIMS (§794), then widened to CLAIMS + ATTENDEES (§1013b). Both are really the
/// same source — <i>somebody must already have BOUGHT this class</i>. A <b>prepaid pool is created
/// before anybody buys</b>; that is what prepaid means. So in exactly the situation he kept looking
/// at, the lookup was empty and every surface printed the id. Widening a purchase-derived source a
/// third time would have failed a third time.</para>
///
/// <para>✅ <b>The fix is a different KIND of source</b>: Backstage's own ticket-class list, which
/// knows every class from the moment it is defined. Live-probed 2026-08-09 —
/// <c>GET …/ticket_classes</c> returns <c>14880000003485482 → "2-day (Pre-day  + Main Event)"</c>.
/// (<c>ticketclasses</c>/<c>tickets</c>/<c>ticket-classes</c> all 404.)</para>
/// </remarks>
public sealed class TicketClassNameNeverAnIdTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        public StubHandler(HttpStatusCode code, string body) { _code = code; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_code)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static ZohoClient NewZoho(HttpStatusCode code, string body) =>
        new(new HttpClient(new StubHandler(code, body)),
            new ZohoOptions { BackstagePortalId = "P", BackstageEventId = "E" });

    /// <summary>The real PROD payload shape, as the live probe returned it.</summary>
    private const string LiveJson = """
    {"ticket_classes":[
      {"id":"14880000003485481","name":"1-day  (Main Event)"},
      {"id":"14880000003485482","name":"2-day (Pre-day  + Main Event)"},
      {"id":"14880000003485483","name":"Test"}
    ]}
    """;

    [Fact]
    public async Task The_ticket_class_list_maps_every_id_to_its_name()
    {
        var map = await NewZoho(HttpStatusCode.OK, LiveJson).GetTicketClassNamesAsync("tok");

        Assert.Equal(3, map.Count);
        // The exact id he kept seeing on screen, and the name it should have shown.
        Assert.Equal("2-day (Pre-day  + Main Event)", map["14880000003485482"]);
        Assert.Equal("1-day  (Main Event)", map["14880000003485481"]);
    }

    /// <summary>
    /// 🔒 Fail-soft: an unreadable endpoint yields EMPTY, never an exception. The caller then keeps
    /// whatever name it already had — losing a name it could display would be worse than not
    /// improving one.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.OK, """{"ticket_classes":[]}""")]
    public async Task An_unreadable_list_is_empty_and_never_throws(HttpStatusCode code, string body)
    {
        Assert.Empty(await NewZoho(code, body).GetTicketClassNamesAsync("tok"));
    }

    [Fact]
    public async Task A_row_missing_its_name_is_skipped_rather_than_mapped_to_blank()
    {
        // A class with no name must not produce an empty label — an empty cell reads as a bug,
        // and the id (ugly as it is) at least tells the operator which class to go and name.
        var map = await NewZoho(HttpStatusCode.OK,
            """{"ticket_classes":[{"id":"1","name":""},{"id":"2","name":"Crew"}]}""")
            .GetTicketClassNamesAsync("tok");

        Assert.False(map.ContainsKey("1"));
        Assert.Equal("Crew", map["2"]);
    }

    /// <summary>
    /// §1019 — the ERP invoice line prints the NAME. This is the surface that reaches a paying
    /// customer, so a 17-digit id here is the worst possible place for one.
    /// </summary>
    [Fact]
    public void The_prepaid_invoice_line_carries_the_class_name()
    {
        var desc = CouponInvoiceLineComposer.ComposePrepaidDescription(
            "2-day (Pre-day  + Main Event)", "ELDK27-Arrow-Finland-pool1", 20, conversionNote: null);

        Assert.Contains("2-day (Pre-day  + Main Event)", desc);
        Assert.DoesNotContain("14880000003485482", desc);
    }
}
