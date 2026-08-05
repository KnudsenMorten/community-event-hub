using CommunityHub.Core.Data;
using CommunityHub.Core.Diagnostics;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §655 — retries mail that failed for a reason that fixes itself. Every 20 minutes, so a message
/// gets its three attempts spread across roughly an hour.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-29: *"build the retry job. i agree to your recommendation"* — the
/// recommendation being: only the self-correcting failures, three attempts over about an hour, and
/// never a bad address.</para>
///
/// <para>🔒 <b>Gated on `outbound-email`.</b> This job SENDS, so the transport kill switch must stop
/// it like everything else. A retry engine that ignored the master switch would keep mailing after
/// he had deliberately silenced the hub — the exact opposite of what a kill switch is for.</para>
///
/// <para>The 20-minute cadence is deliberate rather than arbitrary: <see cref="FailedMailRetryService.MinGapBetweenAttempts"/>
/// is also 20 minutes, so each tick can advance every eligible message by exactly one attempt
/// without the two numbers fighting.</para>
/// </remarks>
public sealed class FailedMailRetryJob
{
    private readonly CommunityHubDbContext _db;
    private readonly FailedMailRetryService _retry;
    private readonly FeatureGateService _gate;
    private readonly ILogger<FailedMailRetryJob> _log;
    private readonly JobActivityReporter? _activity;

    public FailedMailRetryJob(
        CommunityHubDbContext db,
        FailedMailRetryService retry,
        FeatureGateService gate,
        ILogger<FailedMailRetryJob> log,
        JobActivityReporter? activity = null)
    {
        _db = db; _retry = retry; _gate = gate; _log = log; _activity = activity;
    }

    [Function("FailedMailRetryJob")]
    // §869.3 — BASE TICK ONLY. The real cadence is the operator's §510 interval on the Jobs page
    // (JobCatalog default 20, matching MinGapBetweenAttempts). ⚠️ Setting it BELOW 20 does not
    // retry anything faster — the service's own per-message gap still applies.
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogInformation("FailedMailRetryJob: no active edition.");
            _activity?.ReportInactive("There is no ACTIVE edition, so no failed mail is retried.");
            return;
        }

        if (!await _gate.IsFeatureEnabledAsync("outbound-email", eventId.Value, ct))
        {
            _log.LogInformation("FailedMailRetryJob: outbound e-mail is off.");
            _activity?.ReportInactive(
                "Outbound e-mail is switched off, so nothing is being retried — failed messages are "
                + "waiting rather than lost.");
            return;
        }

        var r = await _retry.RunAsync(ct);

        // §633 — zero retried is NORMAL here (most passes have nothing to do), so this reports WORK
        // rather than a no-op. A quiet mail system is the healthy case, not a silent one.
        _activity?.ReportWork();

        if (r.Retried > 0 || r.AlreadyDelivered > 0)
        {
            _log.LogInformation(
                "FailedMailRetryJob: examined {Examined}, retried {Retried}, delivered {Ok}, "
                + "out of attempts {Exhausted}, already-delivered (skipped) {Superseded}.",
                r.Examined, r.Retried, r.Succeeded, r.Exhausted, r.AlreadyDelivered);
        }
    }
}
