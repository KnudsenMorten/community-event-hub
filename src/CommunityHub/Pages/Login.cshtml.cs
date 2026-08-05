using System.Security.Claims;
using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

/// <summary>
/// The PIN login page (CONTEXT.md section 5). Two steps on one page:
///   Step 1 - the participant enters their email; a PIN is emailed.
///   Step 2 - they enter the PIN; on success a signed session cookie is
///            issued and they are redirected to the hub.
/// Designed to work inside the Backstage iframe: one tap to request the code
/// (Option B, CONTEXT.md 5a).
/// </summary>
// Anonymous: this IS the sign-in page (email -> PIN request -> PIN verify). Under
// the fail-closed FallbackPolicy it must be reachable without a cookie, otherwise
// no one could ever log in. All POST handlers (request PIN / verify PIN) are on
// this page model, so the attribute covers them too.
[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly PinLoginService _pinLogin;
    private readonly IIdentityProvider _identityProvider;
    private readonly CommunityHubDbContext _db;
    // §299 7.1: 1-day ticket-holder sign-in gate (RingCap=1 exception for testers).
    // Optional so older test wiring without the gate keeps constructing the page.
    private readonly OneDayAccessGate? _oneDayGate;

    public LoginModel(
        PinLoginService pinLogin,
        IIdentityProvider identityProvider,
        CommunityHubDbContext db,
        OneDayAccessGate? oneDayGate = null)
    {
        _pinLogin = pinLogin;
        _identityProvider = identityProvider;
        _db = db;
        _oneDayGate = oneDayGate;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Pin { get; set; } = string.Empty;

    /// <summary>
    /// Optional local post-login redirect, carried through both PIN steps so an
    /// invite link (/Login?email=...&amp;ReturnUrl=/Forms/Hotel) lands the user on
    /// the intended page. Only local ("/"-prefixed) URLs are honoured.
    /// </summary>
    [BindProperty]
    public string? ReturnUrl { get; set; }

    /// <summary>"email" = ask for email; "pin" = a PIN has been sent.</summary>
    [BindProperty]
    public string Step { get; set; } = "email";

    /// <summary>
    /// "Remember me" — a single checkbox replacing the old "Stay signed in for"
    /// dropdown (operator 2026-06-22). CHECKED ⇒ a persistent "until I sign out"
    /// session (IsPersistent + 365-day expiry — the ASP.NET Core idiom); UNCHECKED
    /// ⇒ a normal non-persistent working session (browser-session cookie, 8-hour
    /// sliding expiry). Carried from step 1 (email) through to step 2 (PIN) via a
    /// hidden field so the choice the user makes up front is honoured at sign-in.
    /// </summary>
    [BindProperty]
    public bool RememberMe { get; set; }

    public string? Message { get; set; }
    public bool IsError { get; set; }

    /// <summary>
    /// Prestage the email (and optional ReturnUrl) from the query string so an
    /// invite link can be a just-click-send experience: the recipient opens
    /// /Login?email=&lt;addr&gt;&amp;ReturnUrl=... with the email field already filled
    /// and only has to request + enter the PIN. The link is built per-env from
    /// each environment's own base URL, so this works in dev and prod alike.
    /// </summary>
    public IActionResult OnGet(string? email, string? returnUrl, string? blocked = null)
    {
        // §299 7.1: a magic-link/go entry point bounced a 1-day ticket holder here —
        // show the operator's exact block message on the sign-in form.
        if (string.Equals(blocked, "1day", StringComparison.OrdinalIgnoreCase))
        {
            IsError = true;
            Message = CommunityHub.Core.Auth.OneDayAccessGate.BlockedMessage;
        }

        // "Remember me" defaults to CHECKED on the initial sign-in form (operator
        // 2026-06-28 §170): most people sign in on their own phone/laptop and want
        // to stay signed in. Only the first GET renders the step-1 form; the later
        // PIN/RequestPin POSTs carry the user's actual choice via a hidden field, so
        // setting it here only pre-checks the box — it never overrides an explicit
        // un-check. The obvious Sign-out remains for shared/kiosk devices.
        RememberMe = true;

        if (!string.IsNullOrWhiteSpace(email))
        {
            Email = email.Trim();
        }
        ReturnUrl = SafeLocalReturnUrl(returnUrl);

        // Already signed in? Don't show the sign-in form again — that is what made it
        // look like the session "kept prompting to log in" (opening a prestage bookmark
        // while still authenticated re-rendered the Login form). Send the user straight
        // to their hub. A DIFFERENT prestaged email means they are deliberately SWITCHING
        // accounts (one browser = one session cookie), so we still show the form then.
        if (User?.Identity?.IsAuthenticated == true)
        {
            var currentEmail = User.FindFirst(ClaimTypes.Email)?.Value;
            var switchingAccount = !string.IsNullOrWhiteSpace(Email)
                && !string.Equals(Email, currentEmail, StringComparison.OrdinalIgnoreCase);
            if (!switchingAccount)
            {
                return Redirect(ReturnUrl ?? "/");
            }
        }
        return Page();
    }

    /// <summary>Honour only local ("/"-prefixed, non-protocol-relative) URLs.
    /// Backslashes are rejected outright: browsers treat "/\evil.com" (and "\/",
    /// "\\") as protocol-relative, so any '\' makes the URL a redirect vector (§234).</summary>
    private static string? SafeLocalReturnUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.StartsWith('/') && !url.StartsWith("//")
            && !url.Contains('\\')
            ? url
            : null;

    /// <summary>Step 1: the participant asked for a PIN.</summary>
    public async Task<IActionResult> OnPostRequestPinAsync(CancellationToken ct)
    {
        var activeEventId = await GetActiveEventIdAsync(ct);
        if (activeEventId is null)
        {
            IsError = true;
            Message = "No active event is configured.";
            return Page();
        }

        // §326ap (operator 2026-07-25: "a 1-day ticket holder should be blocked already when
        // they try to sign-in … it doesn't make sense they try to login and wait for pin to
        // get message"). The §299 7.1 gate in OnPostVerifyPin stays as the security backstop
        // (and covers magic-link/go); this moves the ANSWER to the first step and — just as
        // importantly — stops mailing a PIN to someone who can never use it. Unknown and
        // non-1-day addresses fall through to the unchanged neutral response, so the endpoint
        // still does not enumerate who is registered.
        if (_oneDayGate is not null
            && await _oneDayGate.IsBlockedByEmailAsync(activeEventId.Value, Email, ct))
        {
            Step = "email";
            IsError = true;
            Message = OneDayAccessGate.BlockedMessage;
            return Page();
        }

        var result = await _pinLogin.RequestPinAsync(activeEventId.Value, Email, ct);

        // Whether or not the email was known, advance to the PIN step with the
        // same neutral message - the endpoint must not reveal who is registered.
        //
        // §361 EXCEPTION: when the service reports the request was NOT accepted (a non-2-day
        // attendee, who can never be sent a code), STAY on the email step. Advancing would show a
        // PIN box that can never be filled — precisely the dead end the operator reported:
        // "of course i never get the pin as we dont allow 1-day ticket holder to login".
        Step = result.Accepted ? "pin" : "email";
        IsError = !result.Accepted;
        Message = result.Message;
        return Page();
    }

    /// <summary>Step 2: the participant submitted the PIN.</summary>
    // §728 — the STABLE sign-in code. This already reached the trail (as the auto-captured
    // `POST /Login [VerifyPin]`, correctly categorised Auth), so nothing was MISSING — the row now
    // moves onto the `auth.sign-in` constant that existed for it all along, so filtering by a
    // stable code works for auth exactly as it now does for the wizard and the task list.
    // ⚠️ The summary is a STATIC label: the filter never records posted values, so no e-mail or PIN
    // can reach the trail through it.
    [CommunityHub.Audit.Audit("Signed in with a PIN",
        Action = CommunityHub.Core.Audit.AuditActions.SignIn,
        Category = CommunityHub.Core.Domain.AuditCategory.Auth)]
    public async Task<IActionResult> OnPostVerifyPinAsync(CancellationToken ct)
    {
        var activeEventId = await GetActiveEventIdAsync(ct);
        if (activeEventId is null)
        {
            IsError = true;
            Message = "No active event is configured.";
            return Page();
        }

        var claim = new IdentityClaim { Email = Email, Pin = Pin };
        var result = await _identityProvider.EstablishIdentityAsync(
            activeEventId.Value, claim, ct);

        if (!result.Succeeded || result.Profile is null)
        {
            Step = "pin";
            IsError = true;
            Message = result.FailureReason ?? "Invalid email or code.";
            return Page();
        }

        // §299 7.1: a 1-day ticket holder is refused BEFORE any cookie is issued —
        // with the exact operator message — unless inside the one-day-hub-access
        // released ring (ring 0/1 test users validating the 1-day experience).
        if (_oneDayGate is not null
            && await _oneDayGate.IsBlockedAsync(result.Profile.Id, ct))
        {
            Step = "email";
            IsError = true;
            Message = OneDayAccessGate.BlockedMessage;
            return Page();
        }

        // Build the signed session cookie via the shared sign-in path (claims +
        // cookie lifetime identical to the magic-link entry points). Session
        // lifetime from the single "Remember me" checkbox: CHECKED ⇒ persistent
        // (IsPersistent + 365-day expiry, survives browser restarts), UNCHECKED ⇒ a
        // normal non-persistent 8-hour session cookie.
        var participant = result.Profile;
        await CommunityHub.Auth.ParticipantSessionSignIn.SignInAsync(
            HttpContext,
            participant.Id, participant.Email, participant.FullName,
            participant.Role, participant.EventId,
            persistent: RememberMe);

        var safeReturn = SafeLocalReturnUrl(ReturnUrl);
        return safeReturn is not null ? Redirect(safeReturn) : RedirectToPage("/Index");
    }

    /// <summary>The single active event (CONTEXT.md section 3).</summary>
    private async Task<int?> GetActiveEventIdAsync(CancellationToken ct)
    {
        var ev = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        return ev;
    }
}
