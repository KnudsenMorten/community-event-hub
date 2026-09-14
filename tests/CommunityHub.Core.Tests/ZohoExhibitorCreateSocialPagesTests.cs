using System.Net;
using System.Text;
using System.Text.Json;
using CommunityHub.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1033 — THE EXHIBITOR **CREATE** CARRIES `company_social_pages`. IT NEVER DID BEFORE.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10, on an exhibitor's LinkedIn arriving as a hand-entry line:
/// <i>"in the exhibitor create in zoho … the api does positive support adding (and updating) social
/// media pages"</i>.</para>
///
/// <para>🔴 <b>What was actually true:</b> §791.3, §801.2, §803.2 and §803.3 measured the field
/// exhaustively — and <b>every one of those calls was a PUT</b>. The CREATE path was never measured
/// because it never sent the field: `CreateExhibitorAsync`'s payload simply had no
/// `company_social_pages` key. "No record has ever received a value this way" was a fact about
/// updates and an untested assumption about creates.</para>
///
/// <para>🔑 Zoho's create-an-exhibitor documentation lists the field, with a two-key sample. ⚠️ The
/// UPDATE doc listed it too and that endpoint discards it — so the doc justifies TRYING, never
/// concluding. What decides it is the read-back in `SponsorZohoProvisionService`, which reports only
/// what Zoho kept (§791.2: say <i>pushed</i>, never <i>updated</i>, for anything unread).</para>
///
/// <para>🔒 These tests pin the REQUEST, which is the half that is CEH's to get right. Whether Zoho
/// stores it is Zoho's behaviour and cannot be asserted from here — pretending otherwise is how
/// §784.13 spent three sessions believing a 200.</para>
/// </remarks>
public sealed class ZohoExhibitorCreateSocialPagesTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{ "exhibitor": { "id": "EX-NEW" } }""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (ZohoClient Client, CapturingHandler Handler) NewClient()
    {
        var handler = new CapturingHandler();
        var client = new ZohoClient(
            new HttpClient(handler),
            new ZohoOptions
            {
                Enabled = true,
                ApiDomain = "https://zoho.test",
                BackstagePortalId = "P1",
                BackstageEventId = "E1",
            },
            NullLogger<ZohoClient>.Instance);
        return (client, handler);
    }

    private static async Task<JsonElement> CreateAsync(
        ZohoClient client, CapturingHandler handler, string? linkedIn, string? twitter)
    {
        var result = await client.CreateExhibitorAsync(
            "tok", "Arki Test A/S", "https://example.test", "About them",
            exhibitorCategoryId: "CAT-1",
            contactFirstName: "Aa", contactLastName: "Bb", contactEmail: "aa@example.test",
            boothLabel: "E-28", linkedInUrl: linkedIn, twitterUrl: twitter);

        Assert.True(result.Ok);
        return JsonDocument.Parse(handler.LastBody!).RootElement;
    }

    [Fact]
    public async Task The_create_payload_carries_the_linkedin_url()
    {
        var (client, handler) = NewClient();

        var body = await CreateAsync(
            client, handler, "https://www.linkedin.com/company/arkimentum", twitter: null);

        var social = body.GetProperty("company_social_pages");
        Assert.Equal("https://www.linkedin.com/company/arkimentum", social.GetProperty("linkedin").GetString());
        // Only what CEH holds — an absent value must not be sent as an empty string, which Zoho
        // would be free to store as a blank social row.
        Assert.False(social.TryGetProperty("twitter", out _));
    }

    [Fact]
    public async Task Both_platforms_travel_when_both_are_known()
    {
        var (client, handler) = NewClient();

        var body = await CreateAsync(
            client, handler, "https://www.linkedin.com/company/x", "https://x.com/x");

        var social = body.GetProperty("company_social_pages");
        Assert.Equal("https://www.linkedin.com/company/x", social.GetProperty("linkedin").GetString());
        Assert.Equal("https://x.com/x", social.GetProperty("twitter").GetString());
    }

    /// <summary>
    /// 🔒 No social, no key. An empty `company_social_pages: {}` is not nothing — §803.2 saw Zoho
    /// create the empty CONTAINER from exactly that, which then reads back as "present but blank"
    /// and muddies the blank-detection the §41b fill-blank gate depends on.
    /// </summary>
    [Fact]
    public async Task A_company_with_no_social_sends_no_social_key_at_all()
    {
        var (client, handler) = NewClient();

        var body = await CreateAsync(client, handler, linkedIn: null, twitter: null);

        Assert.False(body.TryGetProperty("company_social_pages", out _));
    }

    /// <summary>
    /// ⚠️ The create must keep sending everything it already sent. A regression here is invisible:
    /// the record is still created, just poorer, and nobody looks at a create that succeeded.
    /// </summary>
    /// <remarks>
    /// 🔴 §1163 — TWO KEYS CHANGED HERE, AND THIS TEST WAS ASSERTING THE WRONG ONES. It locked in
    /// <c>description</c> and <c>booth_label</c>, both of which Zoho rejects with
    /// <c>400 {"message":"Extra key found"}</c> — measured in production 2026-08-31. The test was
    /// written from the payload rather than from Zoho, so it froze the defect instead of catching
    /// it. Corrected against what the rest of ZohoClient had already measured:
    /// <c>company_overview</c> is the exhibitor's text field, and the booth is assigned by
    /// <c>AssignExhibitorBoothAsync</c> as <c>booth_id</c> after the create.
    /// </remarks>
    [Fact]
    public async Task The_rest_of_the_create_payload_is_unchanged()
    {
        var (client, handler) = NewClient();

        var body = await CreateAsync(client, handler, "https://www.linkedin.com/company/x", null);

        Assert.Equal("Arki Test A/S", body.GetProperty("company_name").GetString());
        Assert.Equal("CAT-1", body.GetProperty("exhibitor_category_id").GetString());
        Assert.Equal("https://example.test", body.GetProperty("website_url").GetString());
        Assert.Equal("About them", body.GetProperty("company_overview").GetString());
        Assert.Equal("aa@example.test", body.GetProperty("contact").GetProperty("email").GetString());
        // 🔒 §41a — Zoho derives the type from the category; sending it causes "category not found".
        Assert.False(body.TryGetProperty("exhibitor_type", out _));
    }

    /// <summary>
    /// 🔴 §1163 — the two keys that made Zoho refuse the create must NEVER come back.
    /// </summary>
    /// <remarks>
    /// The failure they caused was worse than a failed create: the operator had deleted the record
    /// by hand, and a create that succeeded would have silently re-created it every fifteen minutes.
    /// The 400 was the only thing stopping that fight.
    /// </remarks>
    [Fact]
    public async Task The_keys_zoho_refuses_are_not_sent()
    {
        var (client, handler) = NewClient();

        var body = await CreateAsync(client, handler, "https://www.linkedin.com/company/x", null);

        // The exhibitor's text field is company_overview; `description` belongs to the SPONSOR record.
        Assert.False(body.TryGetProperty("description", out _));
        // The booth field is booth_id, resolved from the label AFTER the create.
        Assert.False(body.TryGetProperty("booth_label", out _));
    }

    /// <summary>
    /// 🔴 The create is NOT gated on <c>PushExhibitorSocialPages</c>, and must stay ungated even now
    /// that the switch defaults ON (§1087). The switch is the UPDATE path's kill switch: if Zoho
    /// ever regresses it goes back to <c>false</c>, and the create — which happens once per company,
    /// cannot loop, and was never the broken path — must keep sending social pages when it does.
    /// </summary>
    [Fact]
    public async Task The_create_is_not_gated_on_the_update_switch()
    {
        var handler = new CapturingHandler();
        var client = new ZohoClient(
            new HttpClient(handler),
            new ZohoOptions
            {
                Enabled = true,
                ApiDomain = "https://zoho.test",
                BackstagePortalId = "P1",
                BackstageEventId = "E1",
                PushExhibitorSocialPages = false,   // the KILL SWITCH thrown (§1087: default is now true)
            },
            NullLogger<ZohoClient>.Instance);

        var body = await CreateAsync(client, handler, "https://www.linkedin.com/company/x", null);

        Assert.Equal(
            "https://www.linkedin.com/company/x",
            body.GetProperty("company_social_pages").GetProperty("linkedin").GetString());
    }
}
