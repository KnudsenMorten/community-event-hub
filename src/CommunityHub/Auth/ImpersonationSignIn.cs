using System.Security.Claims;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CommunityHub.Auth;

/// <summary>
/// Builds and issues the session cookie for an <b>acting-as</b> session. The
/// principal carries the TARGET participant's identity claims (so every existing
/// page renders the target's view and on-behalf writes land on the target's own
/// rows) PLUS the <see cref="ImpersonationClaims"/> markers that record who is
/// really acting. Used by both the organizer "Switch to user" flow and the
/// secretary-token entry; the markers are what distinguishes the two and what
/// prevents a nested impersonation.
/// </summary>
public static class ImpersonationSignIn
{
    /// <summary>
    /// Sign the current request in AS <paramref name="target"/>, marked as an
    /// acting-as session of the given <paramref name="kind"/>. The acting
    /// session is deliberately short-lived (not persistent) — it is a working
    /// context, not a remembered login.
    /// </summary>
    public static Task SignInAsTargetAsync(
        HttpContext http,
        Participant target,
        ImpersonationActorKind kind,
        int? actorParticipantId,
        string actorLabel,
        TimeProvider clock)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, target.Id.ToString()),
            new(ClaimTypes.Email, target.Email),
            new(ClaimTypes.Name, target.FullName),
            new(ClaimTypes.Role, target.Role.ToString()),
            new("EventId", target.EventId.ToString()),
            // Acting-as markers — presence flips CurrentParticipant.IsActingAs.
            new(CommunityHub.Core.Auth.ActingAsClaims.ActorKind, kind.ToString()),
            new(CommunityHub.Core.Auth.ActingAsClaims.ActorLabel, actorLabel),
        };
        if (actorParticipantId is not null)
        {
            claims.Add(new Claim(
                CommunityHub.Core.Auth.ActingAsClaims.ActorParticipantId,
                actorParticipantId.Value.ToString()));
        }

        var identity = new ClaimsIdentity(
            claims, CookieAuthenticationDefaults.AuthenticationScheme);

        var props = new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = clock.GetUtcNow().AddHours(8),
            AllowRefresh = false,
        };

        return http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            props);
    }

    /// <summary>
    /// Restore a normal (non-acting) session for <paramref name="organizer"/> —
    /// used by "Return to organizer". Issues the organizer's own identity claims
    /// with NO acting-as markers, so the session is once again a plain logged-in
    /// organizer.
    /// </summary>
    public static Task ReturnToOrganizerAsync(
        HttpContext http, Participant organizer, TimeProvider clock)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, organizer.Id.ToString()),
            new(ClaimTypes.Email, organizer.Email),
            new(ClaimTypes.Name, organizer.FullName),
            new(ClaimTypes.Role, organizer.Role.ToString()),
            new("EventId", organizer.EventId.ToString()),
        };
        var identity = new ClaimsIdentity(
            claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var props = new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = clock.GetUtcNow().AddHours(8),
            AllowRefresh = true,
        };
        return http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            props);
    }
}
