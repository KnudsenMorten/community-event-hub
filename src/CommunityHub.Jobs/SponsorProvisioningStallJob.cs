using CommunityHub.Core.Data;
using CommunityHub.Core.Reminders;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §335 — daily check for a sponsor company stuck waiting for its SharePoint upload folder.
///
/// A Gold+ booth company's welcome is deliberately blocked until its folder exists (see
/// <see cref="SponsorWelcomeEmailService"/>). That block is meant to be transient — the sponsor
/// pull provisions the folder every ~30 minutes — but when provisioning genuinely fails the
/// pull's catch blocks log a warning and continue, leaving the company blocked with **no
/// visible symptom at all**. This job turns that silence into an Action-queue item.
///
/// Deliberately NOT gated on the <c>welcome-email</c> feature (unlike
/// <see cref="SponsorWelcomeReconcileJob"/>): a broken upload folder is a broken upload folder
/// whether or not welcomes are switched on, and gating it would hide the problem exactly while
/// the operator was still preparing to turn welcomes on.
/// </summary>
public sealed class SponsorProvisioningStallJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SponsorProvisioningStallDetector _detector;
    private readonly ILogger<SponsorProvisioningStallJob> _log;
    // §545(b) — optional, so an un-instrumented job simply says nothing (silence = UNKNOWN).
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public SponsorProvisioningStallJob(
        CommunityHubDbContext db,
        SponsorProvisioningStallDetector detector,
        ILogger<SponsorProvisioningStallJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db;
        _detector = detector;
        _log = log;
        _activity = activity;
    }

    [Function("SponsorProvisioningStallJob")]
    public async Task Run([TimerTrigger("0 20 7 * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogInformation("SponsorProvisioningStallJob: no active edition; skipped.");
            _activity?.ReportInactive(
                "There is no ACTIVE edition, so stalled sponsor provisioning is not being detected.");
            return;
        }

        _activity?.ReportWork();

        var result = await _detector.RunAsync(eventId.Value, ct);

        if (result.Stuck > 0)
        {
            _log.LogWarning(
                "SponsorProvisioningStallJob: {Stuck} sponsor company(ies) still have no upload "
                + "folder and are blocked from welcome — {Companies}. Raised in the Action queue.",
                result.Stuck, string.Join(", ", result.StuckCompanies));
        }
        if (result.Cleared > 0)
        {
            _log.LogInformation(
                "SponsorProvisioningStallJob: {Cleared} company(ies) recovered; items resolved.",
                result.Cleared);
        }
    }
}
