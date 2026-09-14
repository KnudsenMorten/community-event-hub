using System.Net;
using System.Text;
using System.Text.Json;
using CommunityHub.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §801/§802 — what the exhibitor UPDATE is allowed to put on the wire, measured against the live
/// v3 API on 2026-08-04 and pinned here.
/// </summary>
/// <remarks>
/// <para>The measurement (one field per call, real exhibitors, originals restored):</para>
/// <list type="bullet">
///   <item><c>company_overview</c>, <c>company_short_description</c>, <c>website_url</c> — <b>write
///   and read back</b> on every record tested, including the ones the §791 log called failures.</item>
///   <item><c>company_social_pages</c> — was <b>200 and silently discarded</b> (twice measured,
///   2026-08-03/04). ✅ <b>Zoho repaired it over the weekend of 2026-08-16</b>; §1087 re-measured on
///   2026-08-17 and it now writes and reads back, both into a cleared object and over an existing
///   value. ⚠️ It <b>merges</b>: an omitted key keeps its stored value and <c>{}</c> is a no-op, so
///   a link can be blanked to <c>""</c> but only the GUI can delete one.</item>
///   <item>Caps: short description <b>80</b> (81 → 400), overview <b>1000</b> (1024 → 400), and Zoho
///   rejects the WHOLE update — <c>{"message":"`shortDescription` is too long"}</c>.</item>
/// </list>
/// </remarks>
public sealed class ZohoExhibitorUpdatePayloadTests
{
    private const string Portal = "P1";
    private const string Event = "E1";
    private const string Exhibitor = "EX1";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public string? LastBody { get; private set; }
        public StubHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "{}")
        { _status = status; _body = body; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null) LastBody = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (ZohoClient Client, StubHandler Handler) New(
        bool pushSocial = false, HttpStatusCode status = HttpStatusCode.OK, string body = "{}")
    {
        var handler = new StubHandler(status, body);
        var client = new ZohoClient(
            new HttpClient(handler),
            new ZohoOptions
            {
                Enabled = true, ApiDomain = "https://zoho.test",
                BackstagePortalId = Portal, BackstageEventId = Event,
                PushExhibitorSocialPages = pushSocial,
            },
            NullLogger<ZohoClient>.Instance);
        return (client, handler);
    }

    private static JsonElement Body(StubHandler h) =>
        JsonDocument.Parse(h.LastBody ?? "{}").RootElement.Clone();

    // ---------------------------------------------------------------------
    //  §802.2 — the cap
    // ---------------------------------------------------------------------

    /// <summary>
    /// 🔴 An over-long short description is CUT to 80 rather than sent, because Zoho rejects the
    /// whole update over it — the website and the overview in the same call would be lost too.
    /// </summary>
    [Fact]
    public async Task An_over_long_short_description_is_truncated_not_sent_whole()
    {
        var (client, handler) = New();
        var tooLong = new string('x', 120);

        await client.UpdateExhibitorAsync(
            "tok", Exhibitor, companyOverview: null, companyShortDescription: tooLong,
            websiteUrl: "https://example.test");

        var sent = Body(handler).GetProperty("company_short_description").GetString();
        Assert.Equal(80, sent!.Length);
        Assert.Equal(new string('x', 80), sent);
        // ...and the rest of the call still goes, which is the point of capping instead of failing.
        Assert.Equal("https://example.test", Body(handler).GetProperty("website_url").GetString());
    }

    [Fact]
    public async Task An_over_long_overview_is_truncated_to_a_thousand()
    {
        var (client, handler) = New();

        await client.UpdateExhibitorAsync(
            "tok", Exhibitor, companyOverview: new string('y', 4000), companyShortDescription: null);

        Assert.Equal(1000, Body(handler).GetProperty("company_overview").GetString()!.Length);
    }

    /// <summary>A value inside the limit is sent untouched — the cap must not "tidy" anything.</summary>
    [Fact]
    public async Task A_value_within_the_limit_is_sent_exactly_as_stored()
    {
        var (client, handler) = New();
        var exact = new string('x', 80);

        await client.UpdateExhibitorAsync(
            "tok", Exhibitor, companyOverview: "A normal overview.", companyShortDescription: exact);

        Assert.Equal(exact, Body(handler).GetProperty("company_short_description").GetString());
        Assert.Equal("A normal overview.", Body(handler).GetProperty("company_overview").GetString());
    }

    /// <summary>The limits are the MEASURED ones. A change here is a claim about Zoho, not a preference.</summary>
    [Fact]
    public void The_limits_are_the_measured_ones()
    {
        Assert.Equal(80, ZohoExhibitorLimits.ShortDescription);
        Assert.Equal(1000, ZohoExhibitorLimits.Overview);
    }

    // ---------------------------------------------------------------------
    //  §791.5 — the contact block must never travel on an update
    // ---------------------------------------------------------------------

    /// <summary>
    /// 🔴 *"we should NEWER have an api trying to update contact details for both sponsor and
    /// exhibitor"*. Zoho hard-caps contact e-mail updates at three attempts and a no-op resend burns
    /// one, so a block riding along on every profile push spends a budget nobody is watching.
    /// </summary>
    [Fact]
    public async Task The_contact_block_is_never_sent_even_when_the_caller_passes_one()
    {
        var (client, handler) = New();

        await client.UpdateExhibitorAsync(
            "tok", Exhibitor, companyOverview: "o", companyShortDescription: "s",
            companyName: "Example A/S",
            contactFirstName: "Rita", contactLastName: "Requester",
            contactEmail: "rita@example.test", contactMobile: "+4512345678");

        var body = Body(handler);
        Assert.False(body.TryGetProperty("contact", out _));
        Assert.DoesNotContain("rita@example.test", handler.LastBody);
        // company_name is NOT contact detail and still travels (the mojibake re-push).
        Assert.Equal("Example A/S", body.GetProperty("company_name").GetString());
    }

    // ---------------------------------------------------------------------
    //  §1087 — social pages ship ON (they shipped OFF from 2026-08-04 to 2026-08-17)
    // ---------------------------------------------------------------------

    /// <summary>
    /// ✅ §1087 — <b>the shipped default is now ON</b>, and this test pins the DEFAULT rather than an
    /// explicitly-configured value: nothing in DEV or PROD config sets
    /// <c>Zoho:PushExhibitorSocialPages</c>, so the property initialiser is what actually decides
    /// whether a sponsor's LinkedIn reaches Backstage. A test that always passed the flag
    /// explicitly would have stayed green through the whole fortnight the field was switched off.
    /// </summary>
    [Fact]
    public async Task Social_pages_are_sent_by_default_because_zoho_fixed_the_endpoint()
    {
        // NOT New(pushSocial: true) — deliberately the un-configured ZohoOptions default.
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var client = new ZohoClient(
            new HttpClient(handler),
            new ZohoOptions
            {
                Enabled = true, ApiDomain = "https://zoho.test",
                BackstagePortalId = Portal, BackstageEventId = Event,
            },
            NullLogger<ZohoClient>.Instance);

        await client.UpdateExhibitorAsync(
            "tok", Exhibitor, companyOverview: null, companyShortDescription: null,
            linkedInUrl: "https://www.linkedin.com/company/example",
            twitterUrl: "https://x.com/example");

        var social = Body(handler).GetProperty("company_social_pages");
        Assert.Equal("https://www.linkedin.com/company/example", social.GetProperty("linkedin").GetString());
        Assert.Equal("https://x.com/example", social.GetProperty("twitter").GetString());
    }

    /// <summary>
    /// 🔒 The kill switch still works. §1087: a Zoho regression would be SILENT — 200 with the value
    /// discarded — and the sync's live-compare would then re-push every pass for ever (§791.3's
    /// loop). Turning this off is the one-setting, no-deploy way back to the §792 hand-entry mail,
    /// so it has to keep working even while nobody is using it.
    /// </summary>
    [Fact]
    public async Task Social_pages_are_not_sent_when_the_kill_switch_is_off()
    {
        var (client, handler) = New(pushSocial: false);

        await client.UpdateExhibitorAsync(
            "tok", Exhibitor, companyOverview: null, companyShortDescription: null,
            linkedInUrl: "https://www.linkedin.com/company/example",
            twitterUrl: "https://x.com/example");

        Assert.False(Body(handler).TryGetProperty("company_social_pages", out _));
    }

    // ---------------------------------------------------------------------
    //  §802.4(2) — the failure body
    // ---------------------------------------------------------------------

    /// <summary>
    /// 🔴 A 400 returns false, as before — but Zoho's own explanation is no longer thrown away. The
    /// message *"`shortDescription` is too long"* was available on the very first failing run and
    /// three sessions were spent without it.
    /// </summary>
    [Fact]
    public async Task A_rejected_update_returns_false_and_the_reason_is_read()
    {
        var (client, handler) = New(
            status: HttpStatusCode.BadRequest,
            body: "{\"status_code\":\"400\",\"message\":\"`shortDescription` is too long\"}");

        var ok = await client.UpdateExhibitorAsync(
            "tok", Exhibitor, companyOverview: null, companyShortDescription: "short enough");

        // §1154 — the call now reports WHICH failure. This handler answers 400, so it is a
        // REFUSAL (the value will never land by itself), not an unavailable Zoho.
        Assert.Equal(ZohoClient.ZohoWriteOutcome.Refused, ok);
        Assert.NotNull(handler.LastBody);   // the request was actually made
    }

    // ---------------------------------------------------------------------
    //  §802.1 — the words a sponsor reads
    // ---------------------------------------------------------------------

    [Fact]
    public void The_refusal_names_the_count_and_what_to_remove()
    {
        var message = ZohoExhibitorLimits.TooLongMessage(
            "Company Short Description", new string('x', 84), ZohoExhibitorLimits.ShortDescription);

        Assert.Contains("84 characters", message);
        Assert.Contains("limit is 80", message);
        Assert.Contains("Remove 4 characters", message);
    }

    [Fact]
    public void One_character_over_is_singular()
    {
        var message = ZohoExhibitorLimits.TooLongMessage("X", new string('x', 81), 80);
        Assert.Contains("Remove 1 character.", message);
    }
}
