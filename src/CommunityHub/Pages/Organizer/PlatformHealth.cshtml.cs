using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §392 — the organizer-facing view of what the PLATFORM is actually doing (operator 2026-07-26:
/// <i>"can you also add telemetry view graphs + stats on the admin interface so i will be able to
/// follow issues + trends + concurrent users etc"</i> → <i>"go build the telemetry page"</i>).
///
/// <para><b>Why the name is PlatformHealth and not Telemetry.</b> <c>/Organizer/Telemetry</c> was
/// already taken — by ATTENDEE telemetry (§55: who is coming, which companies, which topics). That
/// is a completely different question from "is the site slow right now", and two pages called
/// Telemetry would be a support burden every time he asked someone to "open telemetry".</para>
///
/// <para><b>Why it exists at all.</b> He reported repeated hangs on Master Class add/cancel/move-up
/// and asked whether anything could be traced. The honest answer was no — §391 found the web tier
/// had been emitting NOTHING. This page is the other half of that fix: telemetry nobody can read is
/// only marginally better than telemetry nobody collects.</para>
///
/// <para><b>Read-only and fail-soft.</b> It runs KQL against Application Insights as the app's
/// managed identity and renders whatever comes back. A telemetry outage, an unpropagated role
/// assignment or a throttled workspace produces an explanation ON the page, never a 500 — a
/// diagnostics page that breaks when the platform is unhealthy is exactly backwards.</para>
/// </summary>
[Authorize]
public class PlatformHealthModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly PlatformTelemetryService _telemetry;

    public PlatformHealthModel(
        ICurrentParticipantAccessor participant, PlatformTelemetryService telemetry)
    {
        _participant = participant;
        _telemetry = telemetry;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>Look-back window in hours. The service clamps to 1..168.</summary>
    [BindProperty(SupportsGet = true)] public int Hours { get; set; } = 24;

    /// <summary>The windows offered as one-click buttons — the questions actually asked.</summary>
    public static readonly (int Hours, string Label)[] Windows =
    [
        (1, "Last hour"),
        (6, "6 hours"),
        (24, "24 hours"),
        (72, "3 days"),
        (168, "7 days"),
    ];

    public PlatformTelemetryService.Snapshot? Data { get; private set; }

    public bool IsConfigured => _telemetry.IsConfigured;

    /// <summary>
    /// The tallest bar in the traffic chart, used to scale every other bar. Never below 1, so the
    /// chart cannot divide by zero — an empty window renders flat rather than throwing.
    /// </summary>
    public int PeakRequests =>
        Data is null || Data.Traffic.Count == 0 ? 1 : Math.Max(1, Data.Traffic.Max(t => t.Requests));

    /// <summary>Same, for the latency overlay.</summary>
    public double PeakP95 =>
        Data is null || Data.Traffic.Count == 0 ? 1 : Math.Max(1, Data.Traffic.Max(t => t.P95Ms));

    /// <summary>Failure rate over the window as a percentage; 0 when nothing was served.</summary>
    public double FailureRatePercent =>
        Data is null || Data.TotalRequests == 0
            ? 0
            : Math.Round(100.0 * Data.TotalFailed / Data.TotalRequests, 1);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // The same gate every other organizer page uses: real organizers only, never while
        // acting-as. This is platform-wide operational data, not anything scoped to the person
        // being impersonated, so showing it through an act-as session would be a leak with no
        // legitimate use.
        if (me.Role != ParticipantRole.Organizer || me.IsActingAs)
        {
            AccessDenied = true;
            return Page();
        }

        Data = await _telemetry.GetAsync(Hours, ct);
        return Page();
    }

    /// <summary>
    /// A duration rendered the way someone reading a hang report thinks about it.
    ///
    /// <para>Formatted with the INVARIANT culture deliberately. The host runs in a Danish locale, so
    /// the default would print <c>"1,0 s"</c> — a comma reads as a thousands separator to an English
    /// reader, and every other word on this page is English. Consistency with the surrounding copy
    /// matters more here than matching the server's locale, which nobody chose.</para>
    /// </summary>
    public static string Ms(double ms) => ms >= 1000
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{ms / 1000:0.0} s")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{ms:0} ms");

    /// <summary>
    /// The colour a latency is shown in. The thresholds are deliberately blunt: this page answers
    /// "is something wrong right now", and a precise number nobody can interpret at a glance answers
    /// that worse than three colours do.
    /// </summary>
    public static string LatencyColour(double ms) =>
        ms >= 3000 ? "#b91c1c" : ms >= 1000 ? "#b45309" : "#15803d";

    /// <summary>Bucket label — time-of-day for short windows, date + time once it spans days.</summary>
    public string BucketLabel(DateTimeOffset bucket) =>
        Hours <= 24 ? bucket.ToLocalTime().ToString("HH:mm") : bucket.ToLocalTime().ToString("d MMM HH:mm");
}
