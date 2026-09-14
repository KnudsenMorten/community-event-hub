using System.Net;
using System.Text;
using CommunityHub.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1128 — the sponsor GET reads the CONTACT Backstage holds, so the hand-entry report can ask the
/// same question about the contact it already asks about every other field.
///
/// <para>Operator 2026-08-25, on a contact that differed only in capitalisation: <i>"this is not
/// relevant to wan about as it is only letters (capital vs small) … this is from zoho already
/// correct"</i>.</para>
///
/// <para>🔑 <b>The case difference was the TELL, not the cause.</b> Every other hand-entry line asks
/// <c>NeedsManualEntry(z?.Field, info.Field)</c> — <i>"does the LIVE Zoho value differ?"</i>, already
/// trimmed and case-insensitive. The Contact line asked <i>"has CEH's value changed since CEH last
/// TOLD him?"</i> against its own stamp, and so never looked at Zoho at all — firing on the first
/// report for a company regardless of what Backstage already had.</para>
///
/// <para>🔒 <b>The JSON below is the SHAPE OF A REAL RESPONSE</b>, captured from a live sponsor GET
/// on 2026-08-25, not invented. §767 shipped a sweep that matched nothing for four production runs
/// because a convention was guessed instead of listed; this fixture is that lesson applied.</para>
/// </summary>
public sealed class ZohoSponsorContactReadTests
{
    private const string Portal = "P1";
    private const string Event = "E1";
    private const string Sponsor = "SP1";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        public StubHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }

    private static ZohoClient NewClient(string body) =>
        new(new HttpClient(new StubHandler(body)),
            new ZohoOptions
            {
                Enabled = true,
                ApiDomain = "https://zoho.test",
                BackstagePortalId = Portal,
                BackstageEventId = Event,
            },
            NullLogger<ZohoClient>.Instance);

    /// <summary>The real payload shape — contact at the ROOT, no "sponsor" wrapper.</summary>
    private const string RobopackResponse = """
    {
      "id": "14880000003846195",
      "company_name": "Robopack",
      "website_url": "https://robopack.com/",
      "contact": { "first_name": "Laura", "last_name": "Gulbe", "email": "laura@robopack.com" },
      "description": "Some description"
    }
    """;

    [Fact]
    public async Task The_sponsor_read_returns_the_contact_Backstage_holds()
    {
        var detail = await NewClient(RobopackResponse).GetSponsorByIdAsync("tok", Sponsor);

        Assert.NotNull(detail);
        Assert.Equal("laura@robopack.com", detail!.ContactEmail);
        Assert.Equal("Laura", detail.ContactFirstName);
        Assert.Equal("Gulbe", detail.ContactLastName);
    }

    [Fact]
    public async Task The_other_fields_still_parse_unchanged()
    {
        // 🔒 The contact is an ADDITION to the shared reader, not a replacement — this pins that
        // adding it did not disturb what every existing caller already relies on.
        var detail = await NewClient(RobopackResponse).GetSponsorByIdAsync("tok", Sponsor);

        Assert.Equal("https://robopack.com/", detail!.WebsiteUrl);
        Assert.Equal("Some description", detail.Description);
    }

    [Fact]
    public async Task A_response_with_no_contact_object_reads_null_rather_than_throwing()
    {
        // ⚠️ The EXHIBITOR GET has no `contact` property at all, and it shares this reader.
        var detail = await NewClient("""{ "id": "X", "website_url": "https://x.example" }""")
            .GetSponsorByIdAsync("tok", Sponsor);

        Assert.NotNull(detail);
        Assert.Null(detail!.ContactEmail);
        Assert.Null(detail.ContactFirstName);
        Assert.Null(detail.ContactLastName);
    }

    [Fact]
    public async Task A_contact_with_a_blank_email_reads_null()
    {
        var detail = await NewClient(
                """{ "id": "X", "contact": { "first_name": "A", "last_name": "B", "email": "" } }""")
            .GetSponsorByIdAsync("tok", Sponsor);

        Assert.Null(detail!.ContactEmail);
        Assert.Equal("A", detail.ContactFirstName);
    }

    [Fact]
    public async Task A_nested_sponsor_wrapper_is_still_honoured()
    {
        // The shared reader supports both shapes (root or nested under "sponsor"). The live response
        // is flat, but the nested branch predates this change and must keep working.
        var detail = await NewClient(
                """{ "sponsor": { "contact": { "email": "laura@robopack.com" } } }""")
            .GetSponsorByIdAsync("tok", Sponsor);

        Assert.Equal("laura@robopack.com", detail!.ContactEmail);
    }

    // ── The point of the whole change: case is not a difference ──────────────────────────

    [Theory]
    [InlineData("laura@robopack.com", "Laura@robopack.com")]   // the reported case
    [InlineData("laura@robopack.com", "LAURA@ROBOPACK.COM")]
    [InlineData("laura@robopack.com", "  laura@robopack.com ")]
    public async Task A_contact_differing_only_in_case_or_spacing_is_not_a_mismatch(
        string inZoho, string inCeh)
    {
        // 🔑 Mirrors what SponsorZohoSyncService now does: NeedsManualEntry(z.ContactEmail,
        // desiredEmail). That helper is private, so this asserts the property it depends on — the
        // live value is available and comparable — using the same normalisation rule (trim +
        // case-insensitive) the helper applies.
        var detail = await NewClient(
                $$"""{ "id": "X", "contact": { "email": "{{inZoho}}" } }""")
            .GetSponsorByIdAsync("tok", Sponsor);

        Assert.True(string.Equals(
            detail!.ContactEmail?.Trim(), inCeh.Trim(), StringComparison.OrdinalIgnoreCase),
            $"'{detail.ContactEmail}' and '{inCeh}' differ only in case/spacing and must not be "
            + "reported as manual work.");
    }

    [Fact]
    public async Task A_genuinely_different_contact_IS_a_mismatch()
    {
        // 🔒 The negative case: the fix must not silence a real change.
        var detail = await NewClient(
                """{ "id": "X", "contact": { "email": "laura@robopack.com" } }""")
            .GetSponsorByIdAsync("tok", Sponsor);

        Assert.False(string.Equals(
            detail!.ContactEmail, "someone.else@robopack.com", StringComparison.OrdinalIgnoreCase));
    }
}
