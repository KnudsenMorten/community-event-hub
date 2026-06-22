using System.Security.Claims;
using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
public class LoginModel : PageModel
{
    private readonly PinLoginService _pinLogin;
    private readonly IIdentityProvider _identityProvider;
    private readonly CommunityHubDbContext _db;

    public LoginModel(
        PinLoginService pinLogin,
        IIdentityProvider identityProvider,
        CommunityHubDbContext db)
    {
        _pinLogin = pinLogin;
        _identityProvider = identityProvider;
        _db = db;
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
    public IActionResult OnGet(string? email, string? returnUrl)
    {
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

    /// <summary>Honour only local ("/"-prefixed, non-protocol-relative) URLs.</summary>
    private static string? SafeLocalReturnUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.StartsWith('/') && !url.StartsWith("//")
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

        var result = await _pinLogin.RequestPinAsync(activeEventId.Value, Email, ct);

        // Whether or not the email was known, advance to the PIN step with the
        // same neutral message - the endpoint must not reveal who is registered.
        Step = "pin";
        IsError = !result.Accepted;
        Message = result.Message;
        return Page();
    }

    /// <summary>Step 2: the participant submitted the PIN.</summary>
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

        // Build the signed session cookie: identity + role + active event.
        var participant = result.Profile;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, participant.Id.ToString()),
            new(ClaimTypes.Email, participant.Email),
            new(ClaimTypes.Name, participant.FullName),
            new(ClaimTypes.Role, participant.Role.ToString()),
            new("EventId", participant.EventId.ToString()),
        };
        var identity = new ClaimsIdentity(
            claims, CookieAuthenticationDefaults.AuthenticationScheme);

        // Session lifetime from the single "Remember me" checkbox.
        //   CHECKED   -> IsPersistent + 365-day expiry — the ASP.NET Core idiom for
        //               "until explicit sign-out"; the cookie survives browser restarts.
        //   UNCHECKED -> a normal non-persistent session cookie (cleared when the
        //               browser closes) with the default 8-hour sliding expiry.
        var (expiresUtc, isPersistent) = RememberMe
            ? (DateTimeOffset.UtcNow.AddDays(365), true)
            : (DateTimeOffset.UtcNow.AddHours(8),  false);
        var authProps = new AuthenticationProperties
        {
            IsPersistent = isPersistent,
            ExpiresUtc   = expiresUtc,
            AllowRefresh = true,
        };

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            authProps);

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
