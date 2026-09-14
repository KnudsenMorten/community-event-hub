using System.Globalization;
using System.Text.Json;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1140c — <c>vat_zone_number</c> must go out as a JSON NUMBER, and a non-string value in the
/// billing payload must still verify as APPLIED.
///
/// <para>Operator 2026-08-26, on the same 52-company alert: <i>"maybe confusion about the name
/// vat_zone_number or ?"</i> · <i>"i remember that there were wrong references to the actual field
/// so this job might need to change"</i>. The name was right by then; the TYPE was not.</para>
///
/// <para>🔑 <b>This is §1140b one layer over.</b> §1140 fixed the key and assumed the type; §1140b
/// fixed the READ to cope with a number and left the WRITE stringifying it. Every other field in
/// the ERP-owned block really is a string in Company Manager — <c>currency</c>, <c>phone</c>,
/// <c>corporate_identification_number</c>, all measured — so the one numeric field inherited the
/// wrong treatment three times running. The retired PowerShell sync had it right: it casts the CVR
/// to string and deliberately leaves the zone an int
/// (<c>Sync-ERP-Customers-to-Webshop.ps1:332-334</c>).</para>
/// </summary>
public sealed class CompanyManagerWriteTypeTests
{
    /// <summary>Captures the PUT body so the test can assert the WIRE FORM, not our intent.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":32}"),
            };
        }
    }

    private static (CompanyManagerClient Client, CapturingHandler Handler) NewClient()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler);
        var client = new CompanyManagerClient(http, new CompanyManagerOptions
        {
            Enabled = true,
            BaseUrl = "https://webshop.example.test/wp-json/company-manager/v1",
            Username = "u",
            Password = "p",
        });
        return (client, handler);
    }

    [Fact]
    public async Task An_int_in_the_payload_is_written_as_a_JSON_number()
    {
        var (client, handler) = NewClient();

        await client.UpdateCompanyAsync(32, new Dictionary<string, object?>
        {
            ["vat_zone_number"] = 2,
            ["currency"] = "EUR",
        });

        // 🔒 The assertion is on the raw body: `"vat_zone_number":2`, unquoted, sitting beside a
        // currency that IS quoted. That pairing is the whole point — one payload, two types.
        Assert.Contains("\"vat_zone_number\":2", handler.Body);
        Assert.DoesNotContain("\"vat_zone_number\":\"2\"", handler.Body);
        Assert.Contains("\"currency\":\"EUR\"", handler.Body);
    }

    [Fact]
    public async Task The_string_form_that_shipped_with_1140_is_what_this_test_forbids()
    {
        // The defect, stated rather than merely fixed: `zone.ToString()` in the dictionary produces
        // a quoted value for a field the API returns as a number.
        var (client, handler) = NewClient();

        await client.UpdateCompanyAsync(32, new Dictionary<string, object?>
        {
            ["vat_zone_number"] = 2.ToString(CultureInfo.InvariantCulture),
        });

        Assert.Contains("\"vat_zone_number\":\"2\"", handler.Body);   // what we no longer send
    }

    // ---- the read-back's `want`, which `as string` silently broke -----------------------------

    /// <summary>
    /// Mirrors the rendering in <c>ErpWebshopContactSyncService</c>'s §897 read-back loop.
    /// </summary>
    private static string? Want(object? value) => value switch
    {
        null => null,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var v => v.ToString(),
    };

    [Fact]
    public void A_boxed_int_renders_as_text_where_as_string_gave_null()
    {
        object boxed = 2;

        // 🔴 The trap: `as string` on a boxed int is null, and a null `want` can never equal the
        // read-back — so a write that landed perfectly scores itself REFUSED and mails him
        // hand-entry work. That is precisely the §1140b failure, re-armed by introducing the first
        // non-string value into the billing dictionary.
        Assert.Null(boxed as string);

        Assert.Equal("2", Want(boxed));
    }

    [Fact]
    public void The_write_and_the_read_back_agree_on_the_number()
    {
        // The full round trip, in the two forms the field actually takes: an int on the way out,
        // a JSON number on the way back.
        object written = 2;
        var payload = JsonDocument.Parse("""{ "vat_zone_number": 2 }""").RootElement;

        var readBack = payload.GetProperty("vat_zone_number").ToString();

        Assert.Equal(readBack, Want(written));
    }

    [Fact]
    public void Strings_are_unaffected()
    {
        Assert.Equal("EUR", Want("EUR"));
        Assert.Null(Want(null));
    }

    /// <summary>
    /// 🔴 §1226 — a new webshop user's USERNAME is the full address. The local part collided across
    /// companies: info@shc.dk asked for "info", already held by another company's info@ address, and
    /// the webshop answered 409 on every run.
    /// </summary>
    [Fact]
    public async Task A_new_user_is_created_with_the_full_email_as_username()
    {
        var (client, handler) = NewClient();

        await client.CreateUserAsync("info@shc.example", "Accounting", "", 104);

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("info@shc.example", doc.RootElement.GetProperty("username").GetString());
        Assert.Equal("info@shc.example", doc.RootElement.GetProperty("email").GetString());
        Assert.Equal(104, doc.RootElement.GetProperty("company_id").GetInt32());
    }
}
