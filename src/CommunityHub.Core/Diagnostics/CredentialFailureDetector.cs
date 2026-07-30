using System.Net;

namespace CommunityHub.Core.Diagnostics;

/// <summary>
/// §545(b) CREDENTIAL — decides whether an HTTP response means <i>"our credential is dead"</i>
/// rather than <i>"that request was not allowed"</i>, and says so in words the operator can act on.
/// </summary>
/// <remarks>
/// <para><b>Why this class exists separately from the handler.</b> The decision is the risky part,
/// so it is pure and testable; the handler around it only plumbs. §524 is the cautionary tale in
/// this repo: Zoho returns <b>HTTP 200</b> with <c>{"error":"invalid_code"}</c> for a revoked
/// refresh token, the status check passed, and the whole integration went dark with nothing to act
/// on. Status codes alone are not the whole story.</para>
///
/// <para>🔒 <b>A credential alert cannot self-heal, so it must be RARE and RIGHT.</b> §524: *"a dead
/// refresh token is the ONE Zoho fault that cannot self-heal — OAuth only reissues one through
/// re-consent"*. The same is true of an expired client secret or a revoked app password: no amount
/// of retrying fixes it, a human must go and rotate something. That makes these alerts genuinely
/// worth sending — and makes a false one expensive, because it sends him to rotate a secret that
/// was fine.</para>
/// </remarks>
public static class CredentialFailureDetector
{
    /// <summary>What a failed call says about our credential.</summary>
    public enum Verdict
    {
        /// <summary>Nothing to say — not an auth problem, or not one we can attribute.</summary>
        None = 0,
        /// <summary>Our credential is rejected outright: expired, revoked or rotated.</summary>
        CredentialRejected = 1,
        /// <summary>The credential is valid but lacks a permission/scope the call needs.</summary>
        PermissionMissing = 2,
    }

    /// <summary>The verdict plus a sentence written for the operator, not for a log grep.</summary>
    public sealed record Result(Verdict Verdict, string Detail)
    {
        public bool ShouldAlert => Verdict != Verdict.None;
    }

    private static readonly Result Nothing = new(Verdict.None, "");

    /// <summary>
    /// Classify one response. <paramref name="body"/> may be null when it was not read — the
    /// status alone is still enough for 401/403.
    /// </summary>
    public static Result Classify(string integration, HttpStatusCode status, string? body)
    {
        // 🔒 §524's lesson: a 200 can still carry an auth failure. Check the BODY before trusting
        // the status, because that is the exact shape that hid a dead Zoho grant for weeks.
        if (body is { Length: > 0 })
        {
            var b = body.AsSpan(0, Math.Min(body.Length, 2000)).ToString();

            if (Contains(b, "invalid_client") || Contains(b, "invalid_grant")
                || Contains(b, "invalid_code") || Contains(b, "unauthorized_client")
                || Contains(b, "AADSTS7000215") || Contains(b, "AADSTS700016"))
            {
                return new Result(Verdict.CredentialRejected,
                    $"{integration} rejected our credential — the client secret, app password or "
                    + "refresh token has expired, been rotated, or been revoked. This cannot fix "
                    + "itself: someone has to issue a new one and store it.");
            }

            if (Contains(b, "insufficient_scope") || Contains(b, "AADSTS65001")
                || Contains(b, "invalid_scope"))
            {
                return new Result(Verdict.PermissionMissing,
                    $"{integration} accepted our identity but refused the call for lack of a "
                    + "permission or scope. The credential is fine; the grant is incomplete.");
            }
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => new Result(Verdict.CredentialRejected,
                $"{integration} answered 401 Unauthorized — our credential is not being accepted. "
                + "Expired, rotated or revoked; it needs reissuing, not retrying."),

            HttpStatusCode.Forbidden => new Result(Verdict.PermissionMissing,
                $"{integration} answered 403 Forbidden — we are authenticated, but this call is not "
                + "permitted. Usually a missing scope or a permission that was removed."),

            _ => Nothing,
        };
    }

    /// <summary>
    /// The throttle key for this integration's credential alert. Stable per integration so a job
    /// running every 5 minutes cannot flood the inbox — the §302 "70-mail night" rule.
    /// </summary>
    public static string ThrottleKey(string integration) =>
        "credential-failure:" + integration.ToLowerInvariant().Replace(' ', '-');

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
