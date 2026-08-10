using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1046 — THE EXHIBITOR CREATE PATH IS GATED, AND NOW SOMETHING PROVES IT.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10: <i>"i thought this was solved in the recent session, investigate code,
/// if not fixed, then fix it - dev must not allow writes in zoho"</i>. It <b>was</b> solved — §1041
/// wired <see cref="LiveBackstageExhibitorApi.CreateAsync"/> to the guard. What was missing is a test:
/// <b>no test anywhere referenced this class</b>, so the single line that makes the guarantee true
/// could have been deleted and every suite would have stayed green.</para>
///
/// <para>🔴 <b><c>ExternalWriteCoverageTests</c> cannot cover this, by construction.</b> It asserts a
/// file that writes outbound <i>mentions</i> <c>IExternalWriteGuard</c>. Delete the
/// <c>AllowAsync</c> line and leave the field and the constructor parameter — which is exactly what a
/// refactor does — and it still passes. It is a spelling check, not a behaviour check. That is §968's
/// lesson in its own right: <i>a guard with no test is a decision nobody has to revisit when its
/// premise disappears.</i></para>
///
/// <para>🔑 <b>Why this path deserves its own test rather than trusting <c>ZohoClient</c>'s.</b>
/// §1041 found this class BECAUSE it bypassed <c>ZohoClient</c>'s write gate entirely — it composes
/// the Backstage URL and POSTs on its own <see cref="HttpClient"/>, using <c>ZohoClient</c> only to
/// fetch a token. So the sibling guarantee in <c>ZohoHostBlockedTests</c> says nothing about it.</para>
///
/// <para>⚠️ DEV is additionally saved by its READ-ONLY refresh token (§1038) — Zoho itself would
/// refuse the write. That is defence in depth and <b>not</b> a reason to leave the code path
/// untested: the next environment to receive a writing credential would have no such backstop.</para>
/// </remarks>
public sealed class BackstageExhibitorHostBlockedTests
{
    /// <summary>Counts every outbound call — the token request as well as the create POST.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"tok\",\"expires_in\":3600}"),
            });
        }
    }

    private sealed class RefuseAllWrites : IExternalWriteGuard
    {
        public Task<bool> AllowAsync(string system, string operation, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> IsAllowedAsync(CancellationToken ct = default) => Task.FromResult(false);
    }

    private static readonly ExhibitorRecord Exhibitor =
        new(CompanyId: "42", CompanyName: "Contoso", ContactEmail: "booth@contoso.example");

    private static (LiveBackstageExhibitorApi Api, CountingHandler Http) NewApi(
        IExternalWriteGuard? writes)
    {
        var handler = new CountingHandler();

        // Fully configured on purpose: CanCreate must be TRUE so a refusal is provably the GUARD's
        // doing and not "the booth category was missing" — the two failure modes are indistinguishable
        // from the outside, and the wrong one passing would make this test worthless.
        var zohoOptions = new ZohoOptions
        {
            Enabled = true,
            ClientId = "cid",
            ClientSecret = "secret",
            RefreshToken = "refresh",
            BackstagePortalId = "portal-1",
            BackstageEventId = "event-1",
        };

        var zoho = new ZohoClient(
            new HttpClient(handler), zohoOptions, log: null, writes: null, alerts: null,
            tokenCache: new ZohoAccessTokenCache(), externalOptions: null);

        var api = new LiveBackstageExhibitorApi(
            new HttpClient(handler),
            zoho,
            zohoOptions,
            new BackstageExhibitorOptions { DefaultBoothCategoryId = "booth-1" },
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveBackstageExhibitorApi>(),
            writes);

        return (api, handler);
    }

    /// <summary>
    /// THE GUARANTEE: a blocked host creates no exhibitor — and spends no Zoho token request finding
    /// that out. The token budget is 10 per 10 minutes across all four hosts (§783.12b), so an
    /// "asks first, then refuses" gate would still damage PROD.
    /// </summary>
    [Fact]
    public async Task A_blocked_host_creates_no_exhibitor_and_asks_zoho_for_nothing()
    {
        var (api, http) = NewApi(new RefuseAllWrites());

        Assert.True(api.CanCreate);            // configured — so only the guard can stop it
        await api.CreateAsync(Exhibitor, default);

        Assert.Equal(0, http.Calls);
    }

    /// <summary>
    /// ⚠️ A policy refusal RETURNS; it does not throw. §1041b's lesson: the caller's job is to create
    /// exhibitors, and "this environment does not do that" is configuration, not an error to alert on.
    /// A throw here would turn every DEV sync run into a failure report.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_quiet_it_does_not_throw()
    {
        var (api, _) = NewApi(new RefuseAllWrites());

        var ex = await Record.ExceptionAsync(() => api.CreateAsync(Exhibitor, default));

        Assert.Null(ex);
    }

    /// <summary>
    /// The gate must not be a blanket off-switch — PROD's exhibitor create still has to happen.
    /// Without this, "block everything" would pass the test above and silently break production.
    /// </summary>
    [Fact]
    public async Task An_allowed_host_still_reaches_backstage()
    {
        var (api, http) = NewApi(writes: null);   // null ⇒ AllowAllExternalWrites, i.e. permitted

        await api.CreateAsync(Exhibitor, default);

        // Two calls: the OAuth token request, then the exhibitor_requests POST.
        Assert.Equal(2, http.Calls);
    }
}
