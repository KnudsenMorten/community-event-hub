using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// PUBLIC, anonymous attendee analytics dashboard (§29/§30) — the sponsor "who's coming +
/// let me analyze" view, mirroring telemetry.sponsor.expertslive.dk. A segment dropdown
/// (10 sponsor-relevant presets) drives headline metrics (count, % of total, % on a 2-day
/// pre-day ticket), a sales-over-time graph and breakdown tables — all aggregate-only,
/// read live from the Zoho Backstage API, on the standalone public layout (§27).
/// </summary>
[AllowAnonymous]
public class AttendeeTelemetryModel : PageModel
{
    private readonly AttendeeTelemetryService _svc;

    public AttendeeTelemetryModel(AttendeeTelemetryService svc) => _svc = svc;

    [BindProperty(SupportsGet = true)] public string? Segment { get; set; }

    public AttendeeTelemetry? Data { get; private set; }
    public IReadOnlyList<TelemetrySegment> Segments => AttendeeTelemetryService.Segments;

    public async Task OnGetAsync(CancellationToken ct) => Data = await _svc.GetAsync(Segment, ct);
}
