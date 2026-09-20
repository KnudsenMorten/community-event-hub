using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Sponsors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// §1236 — the sponsor portal's company switcher: POST only, no page of its own.
/// </summary>
/// <remarks>
/// <para>🔒 The switch is a POST (antiforgery-protected by the global filter) because it changes what
/// the portal shows; a GET would let another site flip a contact's company with a link.</para>
///
/// <para>🔴 It does not decide anything: <see cref="SponsorActingCompanyService.TrySelectAsync"/>
/// re-checks the company against the contact's own links and refuses anything else, and every read
/// re-validates afterwards. So a forged post is inert, not merely unlikely.</para>
/// </remarks>
public class SwitchCompanyModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SponsorActingCompanyService _acting;

    public SwitchCompanyModel(
        ICurrentParticipantAccessor participant, SponsorActingCompanyService acting)
    {
        _participant = participant;
        _acting = acting;
    }

    /// <summary>A direct GET has nothing to show — send them to the portal.</summary>
    public IActionResult OnGet() => RedirectToPage("/Sponsor/Index");

    public async Task<IActionResult> OnPostAsync(
        string? companyId, string? returnUrl, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Sponsor) return RedirectToPage("/Index");

        await _acting.TrySelectAsync(me.ParticipantId, me.EventId, companyId, ct);

        // 🔒 LOCAL redirects only: returnUrl comes from the form, so an absolute URL here would make
        // the switcher an open redirect.
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }
        return RedirectToPage("/Sponsor/Index");
    }
}
