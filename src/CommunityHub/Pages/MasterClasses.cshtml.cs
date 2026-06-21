using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// Public (no-login) Master Class availability board (REQUIREMENTS §6): each MC with
/// its free / confirmed seats and a traffic-light — green when >20% of seats remain,
/// yellow "Filling up" under 20%, red "Full". Read-only; signing up happens via the
/// attendee self-service link.
/// </summary>
[AllowAnonymous]
public class MasterClassesPublicModel : PageModel
{
    private readonly MasterClassSignupService _svc;
    public MasterClassesPublicModel(MasterClassSignupService svc) => _svc = svc;

    public string? EventName { get; private set; }
    public IReadOnlyList<MasterClassSignupService.McOption> MasterClasses { get; private set; }
        = Array.Empty<MasterClassSignupService.McOption>();

    public async Task OnGetAsync(System.Threading.CancellationToken ct)
    {
        (EventName, MasterClasses) = await _svc.ListActiveAsync(ct);
    }
}
