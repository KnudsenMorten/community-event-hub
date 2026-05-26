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

    /// <summary>"email" = ask for email; "pin" = a PIN has been sent.</summary>
    [BindProperty]
    public string Step { get; set; } = "email";

    public string? Message { get; set; }
    public bool IsError { get; set; }

    public void OnGet()
    {
    }

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

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        return RedirectToPage("/Index");
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
