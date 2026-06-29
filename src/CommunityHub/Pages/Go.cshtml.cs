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
        var dest = SafeLocal(r) ?? SafeLocal(NormalizeTarget(target)) ?? "/";

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

        // Personal magic-link = a deliberate sign-in → persistent session (§170),
        // via the one shared sign-in path.
        await Auth.ParticipantSessionSignIn.SignInAsync(
            HttpContext, p.Id, p.Email, p.FullName, p.Role, p.EventId, persistent: true);

        return Redirect(dest);
    }

    /// <summary>Honour only local ("/"-prefixed, non-protocol-relative) URLs.</summary>
    private static string? SafeLocal(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.StartsWith('/') && !url.StartsWith("//")
            ? url
            : null;

    /// <summary>Turn the catch-all route segment ("Speaker/Graphics") into a local path ("/Speaker/Graphics").</summary>
    private static string? NormalizeTarget(string? target) =>
        string.IsNullOrWhiteSpace(target) ? null : "/" + target.TrimStart('/');

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
