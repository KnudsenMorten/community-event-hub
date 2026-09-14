using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1060(j) — the DAILY forecast: which approved social-media posts publish in the next 24 hours.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"new req. daily email with approved some post during next 24 to
/// info@expertslive.dk"</i>.</para>
///
/// <para>🔑 Distinct from the §1060(i) auto-approval notice, which fires per post as it is approved.
/// That one is an EVENT ("this was approved without me"); this is a FORECAST ("here is what goes out
/// today"). They overlap in content, and that is correct.</para>
///
/// <para>Cadence: BASE TICK ONLY (§878) — the operator's interval on <c>/Organizer/Jobs</c> governs,
/// defaulted to daily. 🔒 Safe to run more often than intended: a run with nothing due sends nothing,
/// so the failure mode of a mis-set interval is a repeated mail rather than a wrong one.</para>
/// </remarks>
public sealed class SoMeNext24HoursDigestJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SoMeNext24HoursDigest _digest;
    private readonly ILogger<SoMeNext24HoursDigestJob> _log;

    public SoMeNext24HoursDigestJob(
        CommunityHubDbContext db, SoMeNext24HoursDigest digest, ILogger<SoMeNext24HoursDigestJob> log)
    {
        _db = db; _digest = digest; _log = log;
    }

    [Function("SoMeNext24HoursDigestJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogWarning("SoMeNext24HoursDigestJob: no active event."); return; }

        var count = await _digest.RunAsync(eventId.Value, ct);
        _log.LogInformation("SoMeNext24HoursDigestJob: {Count} post(s) in the next 24 hours.", count);
    }
}
