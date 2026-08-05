using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §764 — copies every community and guest speaker's photo into the SharePoint speakers folder, so
/// all speaker pictures (including the sponsor-uploaded ones) sit in one place.
/// </summary>
/// <remarks>
/// <para>Daily and interval-driven, so the cadence is the operator's on the Jobs page (§510). Daily
/// is the right default because the input changes rarely — a speaker updates their picture once, if
/// ever — and the work is a fetch from someone else's CDN.</para>
///
/// <para>🔒 <b>DEV writes nothing to SharePoint.</b> That is not this job's rule to enforce: the
/// §340-H write guard blocks it centrally and this job simply reports INACTIVE with the reason, so
/// the Jobs page says "external writes are disabled on this host" rather than showing a job that
/// runs and mysteriously never produces a file.</para>
/// </remarks>
public sealed class SpeakerPhotoArchiveJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SpeakerPhotoArchiveService _archive;
    private readonly ILogger<SpeakerPhotoArchiveJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public SpeakerPhotoArchiveJob(
        CommunityHubDbContext db, SpeakerPhotoArchiveService archive,
        ILogger<SpeakerPhotoArchiveJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db; _archive = archive; _log = log; _activity = activity;
    }

    // Base tick only — the real spacing is the operator's interval (§510), default 1440 = daily.
    [Function("SpeakerPhotoArchiveJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogWarning("SpeakerPhotoArchiveJob: no active event."); return; }

        var result = await _archive.RunAsync(eventId.Value, ct);

        if (!result.Ran)
        {
            _log.LogInformation("SpeakerPhotoArchiveJob: {Reason}", result.InactiveReason);
            _activity?.ReportInactive(result.InactiveReason ?? "No speaker photos were copied.");
            return;
        }

        _activity?.ReportWork();

        _log.LogInformation(
            "SpeakerPhotoArchiveJob: archived {Archived}, unchanged {Skipped}, failed {Failed}, "
            + "no photo {NoPhoto}, uncategorized {Uncategorized}.",
            result.Archived, result.Skipped, result.Failed, result.NoPhoto, result.Uncategorized);
    }
}
