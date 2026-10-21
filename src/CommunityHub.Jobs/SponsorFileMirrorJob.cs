using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1312 — named copies of the SoMe sponsor graphics and the exhibitor walls filed by booth level
/// (<see cref="SponsorFileMirrorService"/>). Copies only: the originals and every reader of them are untouched.
/// </summary>
public sealed class SponsorFileMirrorJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SponsorFileMirrorService _mirror;
    private readonly ILogger<SponsorFileMirrorJob> _log;

    public SponsorFileMirrorJob(CommunityHubDbContext db, SponsorFileMirrorService mirror, ILogger<SponsorFileMirrorJob> log)
    {
        _db = db;
        _mirror = mirror;
        _log = log;
    }

    // BASE TICK ONLY — the real cadence (10 min) is the JobCatalog interval.
    [Function("SponsorFileMirrorJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogWarning("SponsorFileMirrorJob: no active event."); return; }

        var r = await _mirror.RunAsync(eventId.Value, ct);
        _log.LogInformation("SponsorFileMirrorJob: copied {Graphics} named graphic(s) and {Walls} wall(s); {UpToDate} copies current.",
            r.GraphicsCopied, r.WallsCopied, r.UpToDate);
        foreach (var p in r.Problems) _log.LogWarning("SponsorFileMirrorJob: {Problem}", p);
    }
}
