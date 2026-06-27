using System.Net;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §34 — the e-conomic "add contact" wire shape. Regression guard for the bug where
/// the create POST sent a nested <c>customer</c> object (400) and serialized blank
/// fields as null. The proven webhook integration sends ONLY name + present optional
/// fields, with the customer in the URL path.
/// </summary>
public sealed class LiveEconomicContactAdminClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _resp;
        public string? LastBody { get; private set; }
        public Uri? LastUri { get; private set; }
        public StubHandler(HttpResponseMessage resp) => _resp = resp;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            if (request.Content is not null) LastBody = await request.Content.ReadAsStringAsync(ct);
            return _resp;
        }
    }

    private static (LiveEconomicContactAdminClient client, StubHandler h) Make(string responseJson)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(responseJson) };
        var h = new StubHandler(resp);
        var opts = new EconomicErpOptions
        {
            Enabled = true,
            ApiBaseUrl = "https://restapi.e-conomic.com",
            AppSecretToken = "secret",
            AgreementGrantToken = "grant",
        };
        return (new LiveEconomicContactAdminClient(new HttpClient(h), opts, NullLogger<LiveEconomicContactAdminClient>.Instance), h);
    }

    [Fact]
    public async Task Create_posts_name_only_body_without_nested_customer()
    {
        var (client, h) = Make("{\"customerContactNumber\": 42}");

        var n = await client.CreateContactAsync(1001, new EconomicContactInput("Jane Doe", "", null, null));

        Assert.Equal(42, n);
        Assert.Equal("https://restapi.e-conomic.com/customers/1001/contacts", h.LastUri!.ToString());
        Assert.Contains("\"name\":\"Jane Doe\"", h.LastBody);
        Assert.DoesNotContain("customer", h.LastBody);   // no nested customer object (was the 400 bug)
        Assert.DoesNotContain("email", h.LastBody);       // blank optionals omitted, not null
        Assert.DoesNotContain("null", h.LastBody);
    }

    [Fact]
    public async Task Create_includes_only_present_optional_fields()
    {
        var (client, h) = Make("{\"customerContactNumber\": 7}");

        await client.CreateContactAsync(1001, new EconomicContactInput("John Roe", "john@x.dk", null, "Signer"));

        Assert.Contains("\"email\":\"john@x.dk\"", h.LastBody);
        Assert.Contains("\"notes\":\"Signer\"", h.LastBody);
        Assert.DoesNotContain("phone", h.LastBody);       // phone not supplied -> omitted
        Assert.DoesNotContain("customer", h.LastBody);
    }
}
