using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// 🔴 §1199 — CAPACITY vs. DEMAND: can the calendar actually hold the campaign?
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-12: <i>"i need to have a overview whre I can see the calculation between
/// capacity (available some post timeslot vs. planned /scheduled) … we cannot sell the same seat
/// twice like an airplane company"</i> · <i>"i can setup predictions until we know the final count -
/// 42 sponsors, 66 sessions (incl. 9 sponsor sessions)"</i>.</para>
///
/// <para>🔑 The planner already knows when it runs out — a run reports <c>no room 2</c> and names the
/// subjects (§842.5). That says it failed, not by how much, not where in the year, and not what would
/// fix it. This page is the arithmetic behind that number.</para>
///
/// <para>🔒 The forecast is NOT stored. He is modelling counts that the data will hold for real within
/// weeks; a saved prediction would immediately become a second, stale copy of a number the tables
/// already answer — which is the defect this codebase has spent the day removing.</para>
/// </remarks>
[Authorize]
public class SoMeCapacityModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SoMeCapacityReport _report;
    private readonly TimeProvider _clock;

    public SoMeCapacityModel(
        ICurrentParticipantAccessor participant, SoMeCapacityReport report, TimeProvider clock)
    {
        _participant = participant;
        _report = report;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>The figures the page is showing — actual today, or his forecast.</summary>
    public SoMeCapacityResult? Result { get; private set; }

    /// <summary>
    /// §1200 — TODAY'S REAL FIGURES, always computed, even while showing a forecast.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"it could be great to be able to toggle between
    /// Forecast/estimate vs. Actual/Current numbers"</i>.</para>
    ///
    /// <para>🔑 <b>A toggle that only swaps the view answers half the question.</b> What he is
    /// deciding is whether the shape holds AS THE NUMBERS GROW — "it fits today, does it fit at 42
    /// sponsors" — and that needs both figures visible at once, not one then the other. So the
    /// toggle chooses which drives the verdict, and the other stays on screen beside it.</para>
    /// </remarks>
    public SoMeCapacityResult? Actual { get; private set; }

    /// <summary>§1200 — "forecast" or "actual". Actual is the default: reality before speculation.</summary>
    [BindProperty(SupportsGet = true)] public string? Mode { get; set; }

    public bool ShowingForecast => string.Equals(Mode, "forecast", StringComparison.OrdinalIgnoreCase);

    // The forecast boxes. Empty = use what the edition holds today, which is also what they
    // pre-fill with, so the page opens showing reality rather than an empty what-if.
    [BindProperty(SupportsGet = true)] public int? Tracks { get; set; }
    [BindProperty(SupportsGet = true)] public int? MasterClasses { get; set; }
    [BindProperty(SupportsGet = true)] public int? TechnicalSessions { get; set; }
    [BindProperty(SupportsGet = true)] public int? SponsorSessions { get; set; }
    [BindProperty(SupportsGet = true)] public int? Tiers { get; set; }
    [BindProperty(SupportsGet = true)] public int? Sponsors { get; set; }

    /// <summary>True when any box differs from today's actual count — so the page can say so.</summary>
    public bool IsForecast { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var today = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), SoMeSchedulePlanner.DanishTime).DateTime);

        // 🔑 §1200 — ALWAYS the real figures, so a forecast can be read against them rather than
        // instead of them.
        Actual = await _report.BuildAsync(me.EventId, today, forecast: null, ct);

        if (ShowingForecast)
        {
            var forecast = new SoMeCapacityReport.Forecast(
                Tracks, MasterClasses, TechnicalSessions, SponsorSessions, Tiers, Sponsors);

            Result = await _report.BuildAsync(me.EventId, today, forecast, ct);
        }
        else
        {
            // ⚠️ Actual mode ignores whatever is in the boxes rather than half-applying them — a
            // verdict computed from a mixture of real and guessed counts would describe nothing.
            Result = Actual;
        }

        IsForecast = ShowingForecast;

        // Pre-fill the boxes from what was actually used, so the form round-trips and a cleared box
        // visibly returns to reality rather than to zero.
        foreach (var d in Result.Demand)
        {
            switch (d.Category)
            {
                case SoMeAnnouncementCategory.SpeakerTracks: Tracks ??= d.Subjects; break;
                case SoMeAnnouncementCategory.MasterClasses: MasterClasses ??= d.Subjects; break;
                case SoMeAnnouncementCategory.TechnicalSessions: TechnicalSessions ??= d.Subjects; break;
                case SoMeAnnouncementCategory.SponsorSpeakerSessions: SponsorSessions ??= d.Subjects; break;
                case SoMeAnnouncementCategory.SponsorTiers: Tiers ??= d.Subjects; break;
                case SoMeAnnouncementCategory.Sponsors: Sponsors ??= d.Subjects; break;
            }
        }

        return Page();
    }
}
