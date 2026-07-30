using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

/// <summary>
/// The §169 personal email magic-link resolver:
/// <c>/go/{token}[/{**target}] [?r=/Path]</c>.
///
/// The token IS the credential: this page redeems it
/// (<see cref="IEmailMagicLinkService.ResolveAsync"/>), signs the recipient in
/// via the SHARED sign-in path (same claims/cookie as a PIN sign-in, persistent
/// session per §170), then redirects to the intended in-hub target — carried
/// either as <c>?r=/Path</c> or as the trailing catch-all path (so a template's
/// <c>{{hubUrl}}/Speaker/Graphics</c> still deep-links cleanly). Only local,
/// "/"-prefixed, non-protocol-relative targets are honoured.
///
/// <para><b>Fail-safe.</b> A bad / expired / revoked / unknown token NEVER errors:
/// it falls through to the normal Login page, pre-staged with the recovery email
/// (when the link was genuine but dead) and the intended destination, so the
/// recipient signs in with email + PIN in one extra tap.</para>
/// </summary>
// Anonymous: this is a sign-in entry point — the token IS the credential, so it
// must be reachable without an existing cookie under the fail-closed FallbackPolicy.
[AllowAnonymous]
public class GoModel : PageModel
{
    private readonly IEmailMagicLinkService _magic;
    private readonly CommunityHubDbContext _db;

    public GoModel(IEmailMagicLinkService magic, CommunityHubDbContext db)
    {
        _magic = magic;
        _db = db;
    }

    public async Task<IActionResult> OnGetAsync(
        string token, string? r, string? target, CancellationToken ct)
    {
        // Intended destination: an explicit ?r= wins; otherwise the trailing
        // catch-all path. Local-only, never an external/protocol-relative URL.
        var dest = SafeLocal(r) ?? SafeLocal(WithQuery(NormalizeTarget(target))) ?? "/";

        EmailMagicLinkResolution resolution;
        try
        {
            resolution = await _magic.ResolveAsync(token ?? string.Empty, ct);
        }
        catch
        {
            // Defence in depth: ResolveAsync is contracted never to throw, but a
            // magic-link must NEVER 500 — degrade to the recovery login.
            return Redirect(BuildLoginRecovery(null, dest));
        }

        if (!resolution.Success || resolution.ParticipantId is null)
        {
            return Redirect(BuildLoginRecovery(resolution.RecoveryEmail, dest));
        }

        var p = await _db.Participants
            .Where(x => x.Id == resolution.ParticipantId && x.IsActive)
            .Select(x => new { x.Id, x.Email, x.FullName, x.Role, x.EventId })
            .FirstOrDefaultAsync(ct);
        if (p is null)
        {
            return Redirect(BuildLoginRecovery(null, dest));
        }

        // §299 7.1: refuse a blocked 1-day ticket holder BEFORE issuing the cookie.
        var oneDayGate = HttpContext.RequestServices
            .GetService<CommunityHub.Core.Auth.OneDayAccessGate>();
        if (oneDayGate is not null && await oneDayGate.IsBlockedAsync(p.Id, ct))
        {
            return Redirect("/Login?blocked=1day");
        }

        // Personal magic-link = a deliberate sign-in → persistent session (§170),
        // via the one shared sign-in path.
        await Auth.ParticipantSessionSignIn.SignInAsync(
            HttpContext, p.Id, p.Email, p.FullName, p.Role, p.EventId, persistent: true);

        return Redirect(dest);
    }

    /// <summary>Honour only local ("/"-prefixed, non-protocol-relative) URLs.
    /// Any '\' is rejected: browsers treat "/\evil.com" as protocol-relative (§234).</summary>
    private static string? SafeLocal(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.StartsWith('/') && !url.StartsWith("//")
            && !url.Contains('\\')
            ? url
            : null;

    /// <summary>Turn the catch-all route segment ("Speaker/Graphics") into a local path ("/Speaker/Graphics").</summary>
    private static string? NormalizeTarget(string? target) =>
        string.IsNullOrWhiteSpace(target) ? null : "/" + target.TrimStart('/');

    /// <summary>
    /// §365 — carry the QUERY STRING through to the destination.
    ///
    /// <para>A route catch-all captures the PATH only, so <c>/go/{tok}/Forms/Wizard?step=masterclass</c>
    /// gave <c>target = "Forms/Wizard"</c> and the <c>?step=</c> was silently dropped — every one of
    /// the nine <c>{{hubUrl}}/Forms/Wizard?step=masterclass</c> CTAs (and the party equivalent) landed
    /// the attendee on the wizard's FIRST step instead of the step the mail promised. Since §351-7
    /// re-pointed those CTAs at the wizard precisely to stop them going to a shadow page, losing the
    /// step made the mail button feel broken in a different way.</para>
    ///
    /// <para><c>r</c> is EXCLUDED: it is this page's own routing parameter, not the destination's, and
    /// re-appending it would make <c>?r=</c> reappear on the target URL. Everything else is passed
    /// verbatim. The result still goes through <see cref="SafeLocal"/>, so the local-only guarantee is
    /// unchanged — a query string cannot make a "/"-prefixed path external.</para>
    /// </summary>
    private string? WithQuery(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        var carried = Request.Query
            .Where(kv => !string.Equals(kv.Key, "r", StringComparison.OrdinalIgnoreCase))
            .SelectMany(kv => kv.Value.Select(v => (kv.Key, Value: v)))
            .ToList();
        if (carried.Count == 0) return path;

        var qs = string.Join("&", carried.Select(kv =>
            Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? string.Empty)));
        return path + (path.Contains('?') ? "&" : "?") + qs;
    }

    /// <summary>
    /// The fail-safe recovery URL: the email + PIN Login page, pre-staged with the
    /// recovery email (when known) and the intended return URL, mirroring the
    /// welcome magic-link's recovery affordance.
    /// </summary>
    private static string BuildLoginRecovery(string? email, string dest)
    {
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(email))
        {
            q.Add("email=" + Uri.EscapeDataString(email));
        }
        if (!string.IsNullOrWhiteSpace(dest) && dest != "/")
        {
            q.Add("ReturnUrl=" + Uri.EscapeDataString(dest));
        }
        return q.Count == 0 ? "/Login" : "/Login?" + string.Join("&", q);
    }
}
