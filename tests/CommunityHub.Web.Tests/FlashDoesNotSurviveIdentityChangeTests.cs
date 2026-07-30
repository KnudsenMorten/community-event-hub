using System;
using System.Collections.Generic;
using CommunityHub.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §428 (operator 2026-07-27) — a <c>[TempData]</c> flash must not outlive the participant it
/// was written for.
///
/// <para>TempData lives in its OWN cookie, keyed to the BROWSER, not to the session. So the
/// success message <i>"✅ Uploaded 47 - Test Session_v1.pdf"</i>, written minutes earlier as the
/// operator's own account, was still queued when he signed in as a test speaker — and rendered
/// directly above <i>"No sessions are linked to you yet"</i>. Both lines were true; together they
/// read as a platform bug and cost a round of diagnosis (see §428 RESOLVED).</para>
///
/// <para>The fix hangs off the one shared sign-in path, so PIN login, the welcome magic-link,
/// <c>/go</c> and the acting-as switch all get it. This pins the behaviour directly: a pending
/// flash is gone after an identity change, and clearing never throws when TempData is not even
/// wired (a sign-in must never fail over a cosmetic message).</para>
/// </summary>
public sealed class FlashDoesNotSurviveIdentityChangeTests
{
    /// <summary>A TempData provider backed by a plain dictionary — stands in for the cookie
    /// provider so the test exercises the clearing, not ASP.NET's cookie machinery.</summary>
    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> Store { get; private set; } =
            new Dictionary<string, object?>();

        public IDictionary<string, object?> LoadTempData(HttpContext context) =>
            new Dictionary<string, object?>(Store);

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) =>
            Store = new Dictionary<string, object?>(values);
    }

    private static (HttpContext Http, DictionaryTempDataProvider Provider) NewContext()
    {
        var provider = new DictionaryTempDataProvider();
        var services = new ServiceCollection();
        services.AddSingleton<ITempDataProvider>(provider);
        services.AddSingleton<ITempDataDictionaryFactory, TempDataDictionaryFactory>();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        return (http, provider);
    }

    [Fact]
    public void A_pending_flash_is_dropped_when_the_signed_in_participant_changes()
    {
        var (http, provider) = NewContext();
        provider.Store["SlideUploadMessage"] = "✅ Uploaded 47 - Test Session_v1.pdf";

        ParticipantSessionSignIn.DropPendingFlash(http);

        Assert.DoesNotContain("SlideUploadMessage", provider.Store.Keys);
        Assert.Empty(provider.Store);
    }

    [Fact]
    public void Clearing_is_safe_when_TempData_is_not_wired_at_all()
    {
        // No ITempDataDictionaryFactory registered — the sign-in must still complete.
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };

        ParticipantSessionSignIn.DropPendingFlash(http);   // must not throw
    }
}
