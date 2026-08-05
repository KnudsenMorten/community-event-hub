using CommunityHub.Core.Data;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §879 — the WEEKLY list of volunteers sitting in the pre-selection queue awaiting review.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-05: <i>"volunteers awaiting review should go out weekly"</i>. The other
/// half of the split is <see cref="SpeakersHeldJob"/> at ten minutes. The difference is not taste:
/// a held speaker BLOCKS the Zoho flow and cost him hours on 2026-08-05, while an unreviewed
/// volunteer is a queue to work through when there is time.</para>
///
/// <para>🔑 <b>No content hash here, and that is deliberate.</b> Its cadence is its own guard — a
/// weekly mail about a queue that has not changed is a reminder, which is what a housekeeping list
/// wants. Adding a hash would silence the reminder exactly when nobody has got round to the queue,
/// i.e. when it is most worth sending.</para>
/// </remarks>
public sealed class VolunteersAwaitingReviewJob
{
    /// <summary>Shared with <see cref="SpeakersHeldJob"/> — §595/§642: the key is a live DB row and
    /// does not move when the display wording does.</summary>
    public const string FeatureKey = "digest-emails";

    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly CommunityHub.Core.Email.OrganizerReviewMailService _mail;
    private readonly ILogger<VolunteersAwaitingReviewJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public VolunteersAwaitingReviewJob(
        CommunityHubDbContext db, FeatureGateService gate,
        CommunityHub.Core.Email.OrganizerReviewMailService mail,
        ILogger<VolunteersAwaitingReviewJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db; _gate = gate; _mail = mail; _log = log; _activity = activity;
    }

    // Base tick only (§510/§869.3) — the operator's interval on the Jobs page is the real cadence,
    // JobCatalog default 10080 (a week).
    [Function("VolunteersAwaitingReviewJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogWarning("VolunteersAwaitingReviewJob: no active event.");
            return;
        }

        if (!await _gate.AreAllEnabledAsync(
                eventId.Value, ct, FeatureKey, FeatureCatalog.OutboundEmailKey))
        {
            _activity?.ReportInactive(
                "Review e-mails (or outbound e-mail) are switched off, so nobody is being told that "
                + "volunteers are waiting for review.");
            return;
        }

        var count = await _mail.VolunteersAwaitingAsync(eventId.Value, ct);
        if (count <= 0)
        {
            _activity?.ReportInactive("No volunteer is awaiting review, so no mail was sent.");
            return;
        }

        _activity?.ReportWork();

        try { await _mail.SendVolunteersAwaitingAsync(count, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "VolunteersAwaitingReviewJob: send failed; the next run retries.");
        }

        _log.LogInformation(
            "VolunteersAwaitingReviewJob: {Count} volunteer(s) awaiting review.", count);
    }
}
