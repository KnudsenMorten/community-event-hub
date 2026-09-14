using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1145 — writes the missing <c>volunteer-photo-{Name}-{id}</c> alias for volunteers who uploaded
/// their picture BEFORE §1132 introduced the convention.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-28: <i>"the volunteers that signed up before that change only have one
/// file with id. do we have a service that fixes that"</i> — we did not, and
/// <see cref="SpeakerPhotoArchiveJob"/> is why that was an oversight rather than a decision: the
/// speaker side got exactly this self-healing behaviour and the volunteer side was missed.</para>
///
/// <para>🔒 Same cadence as the speaker job, at the operator's instruction (<i>"same frequency as
/// the speaker, think every 10 min"</i>): a 5-minute base tick with the real spacing owned by the
/// operator's interval (§510), defaulting to 10 minutes.</para>
///
/// <para>🔑 The work converges to nothing — a volunteer who has an alias is skipped — so a frequent
/// tick costs one folder listing and a string comparison per photo once the backlog is cleared.</para>
/// </remarks>
public sealed class VolunteerPhotoAliasBackfillJob
{
    private readonly CommunityHubDbContext _db;
    private readonly VolunteerPhotoAliasBackfillService _backfill;
    private readonly ILogger<VolunteerPhotoAliasBackfillJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public VolunteerPhotoAliasBackfillJob(
        CommunityHubDbContext db, VolunteerPhotoAliasBackfillService backfill,
        ILogger<VolunteerPhotoAliasBackfillJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db; _backfill = backfill; _log = log; _activity = activity;
    }

    // Base tick only — the real spacing is the operator's interval (§510), default 10 minutes.
    [Function("VolunteerPhotoAliasBackfillJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogWarning("VolunteerPhotoAliasBackfillJob: no active event.");
            return;
        }

        var result = await _backfill.RunAsync(eventId.Value, ct);

        if (result.Inactive)
        {
            // §335 — a job that cannot run says so, rather than reporting a zero that reads as success.
            _log.LogInformation(
                "VolunteerPhotoAliasBackfillJob: inactive (no readable/writable volunteer-photo folder).");
            _activity?.ReportInactive("No readable/writable volunteer-photo folder.");
            return;
        }

        if (result.Written == 0 && result.Failed == 0)
        {
            _activity?.ReportInactive(
                $"Every volunteer photo already has its name alias ({result.AlreadyPresent}).");
            return;
        }

        _activity?.ReportWork();

        _log.LogInformation(
            "VolunteerPhotoAliasBackfillJob: wrote {Written} alias(es), {Present} already present, "
            + "{Unnamed} without a usable name, {Failed} failed.",
            result.Written, result.AlreadyPresent, result.Unnamed, result.Failed);
    }
}
