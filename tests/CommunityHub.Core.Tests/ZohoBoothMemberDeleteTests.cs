using System.Net;
using System.Text;
using CommunityHub.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Verifies the REAL Zoho member-delete path (REQUIREMENTS §41a/§56 — member ONLY,
/// never the exhibitor/sponsor RECORD): <see cref="ZohoClient.GetBoothMembersAsync"/>
/// now captures the Zoho member <c>id</c> (so an email can be resolved to an id), and
/// <see cref="ZohoClient.DeleteBoothMemberAsync"/> DELETEs by id and treats a 2xx
/// <c>{"status":"success"}</c> as success while logging+failing on errors. Drives the
/// real client through a fake HTTP handler — NO live Zoho call.
/// </summary>
public sealed class ZohoBoothMemberDeleteTests
{
    private const string Portal = "P1";
    private const string Event = "E1";
    private const string Exhibitor = "EX1";

    /// <summary>A fake handler returning a canned (method, path)→(status, body) response and recording the last request.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _respond;
        public HttpMethod? LastMethod { get; private set; }
        public string? LastPath { get; private set; }

        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastPath = request.RequestUri!.AbsolutePath;
            var (status, body) = _respond(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (ZohoClient Client, StubHandler Handler) NewClient(
        Func<HttpRequestMessage, (HttpStatusCode, string)> respond)
    {
        var options = new ZohoOptions
        {
            Enabled = true,
            ApiDomain = "https://zoho.test",
            BackstagePortalId = Portal,
            BackstageEventId = Event,
        };
        var handler = new StubHandler(respond);
        var client = new ZohoClient(new HttpClient(handler), options, NullLogger<ZohoClient>.Instance);
        return (client, handler);
    }

    private static string MembersJson() => """
        { "members": [
          { "id": "M-100", "role": "ADMIN", "contact": { "first_name": "Aa", "last_name": "Bb", "email": "Member.One@2linkit.net" } },
          { "id": "M-200", "role": "staff", "contact": { "first_name": "Cc", "last_name": "Dd", "email": "member.two@2linkit.net" } }
        ] }
        """;

    [Fact]
    public async Task GetBoothMembers_captures_the_zoho_member_id_and_lowercases_email()
    {
        var (client, _) = NewClient(_ => (HttpStatusCode.OK, MembersJson()));

        var members = await client.GetBoothMembersAsync("tok", Exhibitor);

        Assert.Equal(2, members.Count);
        var one = members.Single(m => m.Email == "member.one@2linkit.net"); // lowercased
        Assert.Equal("M-100", one.Id);
        Assert.Equal("ADMIN", one.Role);
        Assert.Equal("M-200", members.Single(m => m.Email == "member.two@2linkit.net").Id);
    }

    [Fact]
    public async Task DeleteBoothMember_DELETEs_by_id_and_returns_true_on_status_success()
    {
        var (client, handler) = NewClient(_ => (HttpStatusCode.OK, """{"status":"success"}"""));

        var ok = await client.DeleteBoothMemberAsync("tok", Exhibitor, "M-100");

        Assert.True(ok);
        Assert.Equal(HttpMethod.Delete, handler.LastMethod);
        Assert.Equal($"/backstage/v3/portals/{Portal}/events/{Event}/exhibitors/{Exhibitor}/members/M-100", handler.LastPath);
    }

    [Fact]
    public async Task DeleteBoothMember_returns_true_on_empty_2xx_body()
    {
        var (client, _) = NewClient(_ => (HttpStatusCode.NoContent, string.Empty));
        Assert.True(await client.DeleteBoothMemberAsync("tok", Exhibitor, "M-100"));
    }

    [Fact]
    public async Task DeleteBoothMember_returns_false_on_non_success_body()
    {
        var (client, _) = NewClient(_ => (HttpStatusCode.OK, """{"status":"failure","message":"nope"}"""));
        Assert.False(await client.DeleteBoothMemberAsync("tok", Exhibitor, "M-100"));
    }

    [Fact]
    public async Task DeleteBoothMember_returns_false_on_http_error_and_does_not_throw()
    {
        var (client, _) = NewClient(_ => (HttpStatusCode.Forbidden, """{"message":"No Permission"}"""));
        Assert.False(await client.DeleteBoothMemberAsync("tok", Exhibitor, "M-100"));
    }

    [Fact]
    public async Task DeleteBoothMember_rejects_blank_ids_without_a_call()
    {
        var called = false;
        var (client, _) = NewClient(_ => { called = true; return (HttpStatusCode.OK, "{}"); });

        Assert.False(await client.DeleteBoothMemberAsync("tok", Exhibitor, ""));
        Assert.False(await client.DeleteBoothMemberAsync("tok", "", "M-100"));
        Assert.False(called);
    }
}
