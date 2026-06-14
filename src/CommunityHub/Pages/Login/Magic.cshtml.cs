using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Login;

/// <summary>
/// One-tap sign-in via signed magic-link token. /Login/Magic?token=...&amp;r=/Forms/Hotel
/// </summary>
public class MagicModel : PageModel
{
    private readonly MagicLinkService _magic;
    private readonly CommunityHubDbContext _db;

    public MagicModel(MagicLinkService magic, CommunityHubDbContext db)
    {
        _magic = magic;
        _db = db;
    }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        string? token, string? r, CancellationToken ct)
    {
        var participantId = _magic.ValidateToken(token ?? string.Empty);
        if (participantId is null)
        {
            Error = "This sign-in link is invalid or has expired. Please request a new code below.";
            return Page();
        }

        var participant = await _db.Participants
            .Where(p => p.Id == participantId && p.IsActive)
            .Select(p => new { p.Id, p.Email, p.FullName, p.Role, p.EventId })
            .FirstOrDefaultAsync(ct);
        if (participant is null)
        {
            Error = "This sign-in link is no longer valid (account inactive).";
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, participant.Id.ToString()),
            new(ClaimTypes.Email, participant.Email),
            new(ClaimTypes.Name, participant.FullName),
            new(ClaimTypes.Role, participant.Role.ToString()),
            new("EventId", participant.EventId.ToString()),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        // Only redirect to local URLs starting with '/'; block absolute / cross-site.
        var safeReturn = !string.IsNullOrWhiteSpace(r) && r.StartsWith('/') && !r.StartsWith("//")
            ? r
            : "/";
        return Redirect(safeReturn);
    }
}
