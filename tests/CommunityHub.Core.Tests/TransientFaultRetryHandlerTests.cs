using System.Net;
using System.Text;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests the bounded transient-fault retry that fronts the Company Manager HttpClient
/// (REQUIREMENTS §138, the 2026-06-27 ErpWebshopReconcile 503 incident). A transient
/// upstream 5xx is retried with back-off and the eventual success is returned, instead
/// of bubbling out of <c>GetCompanyUsersAsync</c>'s <c>EnsureSuccessStatusCode</c> and
/// crashing the reconcile. Back-off is overridden to zero so the tests don't sleep.
/// </summary>
public sealed class TransientFaultRetryHandlerTests
{
    /// <summary>A scripted inner handler returning a queued sequence of responses/throws.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _steps;
        public int Calls { get; private set; }
        public ScriptedHandler(params Func<HttpResponseMessage>[] steps) => _steps = new(steps);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            ct.ThrowIfCancellationRequested();
            var step = _steps.Count > 0 ? _steps.Dequeue() : (() => new HttpResponseMessage(HttpStatusCode.OK));
            return Task.FromResult(step());
        }
    }

    private static HttpResponseMessage Resp(HttpStatusCode code, string body = "[]") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static TransientFaultRetryHandler NewHandler(HttpMessageHandler inner, int maxRetries = 3) =>
        new(maxRetries: maxRetries, delayOverride: _ => TimeSpan.Zero) { InnerHandler = inner };

    [Fact]
    public async Task Transient_5xx_is_retried_then_succeeds()
    {
        var inner = new ScriptedHandler(
            () => Resp(HttpStatusCode.ServiceUnavailable),   // 503
            () => Resp(HttpStatusCode.ServiceUnavailable),   // 503
            () => Resp(HttpStatusCode.OK, "[{\"ok\":1}]"));  // 200
        using var client = new HttpClient(NewHandler(inner));

        using var resp = await client.GetAsync("https://cm.test/companies/1/users");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3, inner.Calls); // 2 failures + 1 success
    }

    [Fact]
    public async Task Transient_exception_then_success_is_retried()
    {
        var inner = new ScriptedHandler(
            () => throw new HttpRequestException("connection reset"),
            () => Resp(HttpStatusCode.OK));
        using var client = new HttpClient(NewHandler(inner));

        using var resp = await client.GetAsync("https://cm.test/companies/1/users");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Retries_are_bounded_and_returns_last_transient_response()
    {
        // Always 503: with maxRetries=3 the inner handler is hit 4 times (1 + 3 retries)
        // and the LAST 503 response is returned (the caller decides what to do with it).
        var inner = new ScriptedHandler(
            () => Resp(HttpStatusCode.ServiceUnavailable),
            () => Resp(HttpStatusCode.ServiceUnavailable),
            () => Resp(HttpStatusCode.ServiceUnavailable),
            () => Resp(HttpStatusCode.ServiceUnavailable),
            () => Resp(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(NewHandler(inner, maxRetries: 3));

        using var resp = await client.GetAsync("https://cm.test/companies/1/users");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal(4, inner.Calls);
    }

    [Fact]
    public async Task Non_transient_4xx_is_not_retried()
    {
        var inner = new ScriptedHandler(
            () => Resp(HttpStatusCode.NotFound),
            () => Resp(HttpStatusCode.OK));
        using var client = new HttpClient(NewHandler(inner));

        using var resp = await client.GetAsync("https://cm.test/companies/1/users");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(1, inner.Calls); // 404 is a real answer — no retry
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried()
    {
        using var cts = new CancellationTokenSource();
        var inner = new ScriptedHandler(() =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var client = new HttpClient(NewHandler(inner));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("https://cm.test/companies/1/users", cts.Token));
        Assert.Equal(1, inner.Calls); // a real cancel is not transient
    }

    [Fact]
    public async Task Drives_CompanyManagerClient_GetCompanyUsers_through_a_503()
    {
        // End-to-end through the REAL client + the incident call path: a 503 then a 200
        // array must yield the parsed user list rather than throwing EnsureSuccessStatusCode.
        var inner = new ScriptedHandler(
            () => Resp(HttpStatusCode.ServiceUnavailable),
            () => Resp(HttpStatusCode.OK,
                "[{\"user_id\":\"68\",\"user_email\":\"a@x.test\",\"full_name\":\"A\",\"display_name\":\"A\"}]"));
        var http = new HttpClient(NewHandler(inner));
        var cm = new CompanyManagerClient(http, new CompanyManagerOptions
        {
            Enabled = true,
            BaseUrl = "https://cm.test/wp-json/company-manager/v1",
            Username = "u",
            Password = "p",
        });

        var users = await cm.GetCompanyUsersAsync(42);

        Assert.Single(users);
        Assert.Equal("a@x.test", users[0].Email);
        Assert.Equal(2, inner.Calls);
    }
}
