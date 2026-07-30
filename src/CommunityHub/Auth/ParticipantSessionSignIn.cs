using System.Security.Claims;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;

namespace CommunityHub.Auth;

/// <summary>
/// The ONE place that turns a resolved participant into a signed CommunityHub
/// session cookie. Shared by every sign-in entry point — PIN login, the welcome
/// magic-link (<c>/Login/Magic</c>) and the §169 personal email magic-link
/// (<c>/go</c>) — so the claim set + cookie lifetime live in exactly one place
/// and never drift between paths.
/// </summary>
public static class ParticipantSessionSignIn
{
    /// <summary>
    /// Establish the session cookie for a participant.
    /// <paramref name="persistent"/>:
    ///   <c>true</c>  → <c>IsPersistent</c> + a 365-day expiry, the ASP.NET Core
    ///     idiom for "stay signed in until explicit sign-out" — the §170
    ///     "remember me" choice, and always-on for a deliberate personal
    ///     magic-link sign-in;
    ///   <c>false</c> → a normal non-persistent session cookie (cleared when the
    ///     browser closes) with the default 8-hour sliding expiry.
    /// <c>AllowRefresh</c> (sliding) is set in both cases.
    /// </summary>
    public static Task SignInAsync(
        HttpContext http,
        int participantId,
        string email,
        string fullName,
        ParticipantRole role,
        int eventId,
        bool persistent)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, participantId.ToString()),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Name, fullName),
            new(ClaimTypes.Role, role.ToString()),
            new("EventId", eventId.ToString()),
        };
        var identity = new ClaimsIdentity(
            claims, CookieAuthenticationDefaults.AuthenticationScheme);

        var (expiresUtc, isPersistent) = persistent
            ? (DateTimeOffset.UtcNow.AddDays(365), true)
            : (DateTimeOffset.UtcNow.AddHours(8), false);

        DropPendingFlash(http);

        return http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = isPersistent,
                ExpiresUtc = expiresUtc,
                AllowRefresh = true,
            });
    }

    /// <summary>
    /// §428 (operator 2026-07-27): drop any pending <c>[TempData]</c> flash when the signed-in
    /// participant CHANGES.
    ///
    /// <para>TempData lives in its OWN cookie, keyed to the browser, not to the session — so a
    /// success message written as one person is still queued for the next page load after you
    /// sign in as someone else. That is exactly what made a platform bug out of a data problem:
    /// <i>"✅ Uploaded 47 - Test Session_v1.pdf"</i> (written minutes earlier as the operator's
    /// own account) rendered directly above <i>"No sessions are linked to you yet"</i> on the
    /// test speaker's page. Both statements were true; together they read as a contradiction.</para>
    ///
    /// <para>A flash is about what the PREVIOUS identity just did, so it never survives an
    /// identity change. Called from the one shared sign-in path — PIN login, the welcome
    /// magic-link and <c>/go</c> all route through it — plus the acting-as switch.</para>
    /// </summary>
    public static void DropPendingFlash(HttpContext http)
    {
        try
        {
            var factory = http.RequestServices?.GetService<ITempDataDictionaryFactory>();
            var tempData = factory?.GetTempData(http);
            if (tempData is null) return;
            tempData.Clear();
            // Clearing marks the dictionary dirty, so the save filter writes an EMPTY
            // TempData — which is how the cookie provider deletes the cookie. Belt-and-
            // braces for paths that redirect before the filter runs:
            tempData.Save();
        }
        catch
        {
            // A flash we failed to clear is cosmetic; it must never break a sign-in.
        }
    }
}
