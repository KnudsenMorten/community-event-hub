using CommunityHub.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §234 4a — the PIN Login page's local-ReturnUrl guard. Browsers treat
/// "/\evil.com" (any backslash variant) as protocol-relative, so a plain
/// "starts with / but not //" check is an open redirect. The guard must
/// reject any destination containing '\' and anything protocol-relative,
/// while still honouring genuine local paths. Bad destinations fall back to
/// the safe default (null → hub root), never an error.
/// </summary>
public sealed class LoginReturnUrlGuardTests
{
    // OnGet touches only Email/ReturnUrl/User — no service or DB is reached, so
    // the dependencies can be absent (same pattern as the gate-only authz tests).
    private static LoginModel NewModel() =>
        new(pinLogin: null!, identityProvider: null!, db: null!)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
        };

    [Theory]
    [InlineData("//evil.example.com")]
    [InlineData("/\\evil.example.com")]
    [InlineData("\\\\evil.example.com")]
    [InlineData("/Forms\\..\\evil")]
    [InlineData("https://evil.example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void OnGet_drops_non_local_or_backslash_return_urls(string? bad)
    {
        var model = NewModel();

        model.OnGet(email: null, returnUrl: bad);

        Assert.Null(model.ReturnUrl);   // safe fallback, no error
    }

    [Fact]
    public void OnGet_keeps_a_genuine_local_return_url()
    {
        var model = NewModel();

        model.OnGet(email: null, returnUrl: "/Forms/Hotel");

        Assert.Equal("/Forms/Hotel", model.ReturnUrl);
    }
}
