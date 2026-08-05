using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>Signs the participant out and clears the session cookie.</summary>
public class LogoutModel : PageModel
{
    // §728 — the stable sign-OUT code, matching auth.sign-in on the Login page so an organizer can
    // filter a person's whole session history by two codes instead of two route strings.
    [CommunityHub.Audit.Audit("Signed out",
        Action = CommunityHub.Core.Audit.AuditActions.SignOut,
        Category = CommunityHub.Core.Domain.AuditCategory.Auth)]
    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }

    public IActionResult OnGet() => RedirectToPage("/Index");
}
