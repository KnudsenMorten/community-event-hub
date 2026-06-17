using CommunityHub.Core.Reminders;
using CommunityHub.Core.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace CommunityHub.Pages.Sponsors;

/// <summary>
/// PUBLIC, no-login sponsors page (REQUIREMENTS § 7). Lists the active edition's
/// sponsor companies <b>grouped by tier</b> (highest first), each with their logo,
/// the resolved public company name, and an optional link. Sponsors are public, so
/// there is no publish gate — every sponsor company on file is shown. Read-only —
/// there is no write path to abuse.
///
/// Also surfaces a "become a sponsor" CTA (REQUIREMENTS §21) when a public
/// sponsorship-contact email / URL is configured, so a prospective sponsor has a
/// real way to reach out; when nothing is configured the CTA is simply hidden (no
/// dead link). The href is built by the pure <see cref="BecomeSponsorCtaBuilder"/>.
///
/// Mobile-first (~360px) + a11y (semantic per-tier sections, logo alt text / monogram
/// fallback, <c>role="status"</c> count). Empty states for "no live event" and
/// "no sponsors yet". Data built by <see cref="PublicSponsorsService"/>.
/// </summary>
[AllowAnonymous]
public class IndexModel : PageModel
{
    private readonly PublicSponsorsService _svc;
    private readonly BecomeSponsorOptions _becomeSponsor;

    public IndexModel(PublicSponsorsService svc, IOptions<BecomeSponsorOptions> becomeSponsor)
    {
        _svc = svc;
        _becomeSponsor = becomeSponsor.Value;
    }

    public PublicSponsorsView? View { get; private set; }

    /// <summary>True when there is no active event (distinct from "no sponsors yet").</summary>
    public bool NoActiveEvent { get; private set; }

    /// <summary>
    /// The resolved "become a sponsor" CTA, or <c>null</c> when no public
    /// sponsorship contact is configured (the page then renders no CTA).
    /// </summary>
    public BecomeSponsorCta? BecomeSponsor { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        View = await _svc.BuildAsync(ct);
        NoActiveEvent = View is null;
        BecomeSponsor = BecomeSponsorCtaBuilder.Build(_becomeSponsor, View?.EventDisplayName);
        return Page();
    }
}
