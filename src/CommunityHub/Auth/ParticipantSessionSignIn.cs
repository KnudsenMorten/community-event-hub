using System.Security.Claims;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

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
}
