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

        // §1301 — Backstage's double space is collapsed; and the operator's layout: count, class,
        // BLANK line, coupon.
        Assert.Equal(
            "Prepaid tickets: 20\nTicket Class: 2-day (Pre-day + Main Event)\n\nCoupon: ELDK27-Arrow-Finland-pool1",
            desc.Replace("\r\n", "\n"));
        Assert.DoesNotContain("14880000003485482", desc);
    }

    /// <summary>Answers the token endpoint with a token and everything else with the class list.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        public int ClassCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var isToken = r.RequestUri!.AbsolutePath.Contains("/oauth/v2/token");
            if (!isToken) ClassCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    isToken ? """{"access_token":"tok","expires_in":3600}""" : LiveJson,
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static CouponPrepaidInvoiceService NewPrepaidService(ZohoClient? zoho) =>
        new(null!, null!, new EconomicErpOptions(), new InvoicingOptions(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CouponPrepaidInvoiceService>.Instance, zoho);

    /// <summary>
    /// 🔴 §1301 — the operator's invoice 189: a NEW pool's first invoice carried only the id, so the
    /// class line vanished. The service now looks the id up in Backstage before composing.
    /// </summary>
    [Fact]
    public async Task The_prepaid_invoice_service_turns_a_class_id_into_its_name()
    {
        var handler = new RoutingHandler();
        var zoho = new ZohoClient(new HttpClient(handler),
            new ZohoOptions { BackstagePortalId = "P", BackstageEventId = "E", RefreshToken = "r", ClientId = "c", ClientSecret = "s" });

        var name = await NewPrepaidService(zoho).ResolveTicketClassNameAsync("14880000003485482", default);

        Assert.Equal("2-day (Pre-day  + Main Event)", name);
        Assert.Equal(1, handler.ClassCalls);
    }

    /// <summary>A real name is used as-is — no Zoho call; and without Zoho the id comes back unchanged.</summary>
    [Fact]
    public async Task A_real_name_passes_through_and_no_zoho_means_no_change()
    {
        var handler = new RoutingHandler();
        var zoho = new ZohoClient(new HttpClient(handler),
            new ZohoOptions { BackstagePortalId = "P", BackstageEventId = "E", RefreshToken = "r", ClientId = "c", ClientSecret = "s" });

        Assert.Equal("1-day", await NewPrepaidService(zoho).ResolveTicketClassNameAsync("1-day", default));
        Assert.Equal(0, handler.ClassCalls);
        Assert.Equal("14880000003485482",
            await NewPrepaidService(null).ResolveTicketClassNameAsync("14880000003485482", default));
    }

    /// <summary>§1116 still holds: an unresolved id is DROPPED — the blank line and coupon remain.</summary>
    [Fact]
    public void An_unresolved_id_is_still_left_out_of_the_prepaid_line()
    {
        var desc = CouponInvoiceLineComposer.ComposePrepaidDescription(
            "14880000003485482", "ELDK27-KbhKommune", 6, conversionNote: null);

        Assert.Equal("Prepaid tickets: 6\n\nCoupon: ELDK27-KbhKommune", desc.Replace("\r\n", "\n"));
    }
}
