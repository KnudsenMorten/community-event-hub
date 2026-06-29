using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Login;

/// <summary>
/// One-tap sign-in via signed magic-link token. /Login/Magic?token=...&amp;r=/Forms/Hotel
/// </summary>
// Anonymous: this is a sign-in entry point — the signed token IS the credential, so
// it must be reachable without an existing cookie under the fail-closed FallbackPolicy.
[AllowAnonymous]
public class MagicModel : PageModel
{
    private readonly MagicLinkService _magic;
    private readonly CommunityHub.Core.Auth.IWelcomeAutoLoginTokenService _welcomeAutoLogin;
    private readonly CommunityHubDbContext _db;

    public MagicModel(
        MagicLinkService magic,
        CommunityHub.Core.Auth.IWelcomeAutoLoginTokenService welcomeAutoLogin,
        CommunityHubDbContext db)
    {
        _magic = magic;
        _welcomeAutoLogin = welcomeAutoLogin;
        _db = db;
    }

    /// <summary>
    /// The recovery-state error resource KEY to show on a failed sign-in
    /// (localized in the view). Null on success. Distinct keys let the page
    /// tailor the message: an expired/invalid link vs. a deactivated account.
    /// </summary>
    public string? ErrorKey { get; private set; }

    /// <summary>
    /// The participant's email, recovered from a genuine-but-expired link so the
    /// recovery "request a new code" affordance lands on the PIN flow with the
    /// field pre-filled. Null when it can't be recovered (tampered/alien link);
    /// the recovery link then drops the participant on a blank Login form.
    /// Never trusted as identity — the PIN flow still authenticates.
    /// </summary>
    public string? RecoveryEmail { get; private set; }

    /// <summary>
    /// The intended local post-login destination (the magic link's <c>r=</c>),
    /// carried through the recovery link so re-authenticating via PIN still lands
    /// the participant where the link meant to take them. Local URLs only.
    /// </summary>
    public string? ReturnUrl { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        string? token, string? r, CancellationToken ct)
    {
        // Carry the intended destination through the recovery flow (local only).
        ReturnUrl = !string.IsNullOrWhiteSpace(r) && r.StartsWith('/') && !r.StartsWith("//")
            ? r
            : null;

        // Try the WELCOME auto-login token first: it is single-use, so redeeming
        // it here CONSUMES the backing grant (a second tap is refused). It uses
        // its own DataProtection purpose, so a reusable invitation token simply
        // doesn't redeem here and falls through to the legacy path below.
        var welcome = await _welcomeAutoLogin.RedeemAsync(token ?? string.Empty, ct);
        var participantId = welcome.Success ? welcome.ParticipantId : _magic.ValidateToken(token ?? string.Empty);
        if (participantId is null)
        {
            // Expired-but-genuine link: recover the email so "request a new code"
            // is one tap. ValidateToken already rejected it (so we never sign in),
            // but PeekParticipantId still resolves a real, untampered just-expired
            // token. A tampered/alien token resolves to null -> blank recovery.
            var expiredId = _magic.PeekParticipantId(token ?? string.Empty);
            if (expiredId is not null)
            {
                RecoveryEmail = await _db.Participants
                    .Where(p => p.Id == expiredId)
                    .Select(p => p.Email)
                    .FirstOrDefaultAsync(ct);
            }
            ErrorKey = "Login.MagicInvalid";
            return Page();
        }

        var participant = await _db.Participants
            .Where(p => p.Id == participantId && p.IsActive)
            .Select(p => new { p.Id, p.Email, p.FullName, p.Role, p.EventId })
            .FirstOrDefaultAsync(ct);
        if (participant is null)
        {
            // The token is valid but the account is deactivated. We still know who
            // it was for (so the recovery link can pre-fill), but the real recourse
            // is to contact the organizer — the PIN flow will also refuse an
            // inactive account, so we DON'T pre-fill here to avoid implying it'll work.
            ErrorKey = "Login.MagicInactive";
            return Page();
        }

        // A magic-link is a deliberate personal sign-in -> give it a FOREVER session
        // (persistent: true = IsPersistent + 365-day expiry, the §170 idiom for "stay
        // signed in until explicit sign-out"). Shared sign-in path so claims + cookie
        // lifetime match the PIN login and the §169 /go link exactly.
        await ParticipantSessionSignIn.SignInAsync(
            HttpContext,
            participant.Id, participant.Email, participant.FullName,
            participant.Role, participant.EventId,
            persistent: true);

        // Only redirect to local URLs starting with '/'; block absolute / cross-site.
        // Default lands on '/' = the hub main menu.
        var safeReturn = ReturnUrl ?? "/";
        return Redirect(safeReturn);
    }

    /// <summary>
    /// The recovery "request a new sign-in code" link: the email-+-PIN Login page,
    /// pre-staged with the recovered email (when known) and the intended return
    /// URL, so the participant requests a fresh PIN in one tap and still lands
    /// where the expired link meant to take them. Built with proper query-string
    /// encoding; falls back to a bare /Login when nothing is recoverable.
    /// </summary>
    public string RecoveryLink()
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(RecoveryEmail))
        {
            query.Add("email=" + Uri.EscapeDataString(RecoveryEmail));
        }
        if (!string.IsNullOrWhiteSpace(ReturnUrl))
        {
            query.Add("ReturnUrl=" + Uri.EscapeDataString(ReturnUrl));
        }
        return query.Count == 0 ? "/Login" : "/Login?" + string.Join("&", query);
    }
}
