using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1077 — the DAILY volume-package re-check: who has ten or more attendees today.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"Daily check of qualifications. if fx an attendee cancels his ticket
/// and company move from 10 to 9, then they dont qualify until they again get 10 or more"</i>.</para>
///
/// <para>🔴 <b>Stage 2 gave this job its one outbound mail</b> — the approval request to the
/// organizer mailbox (<c>info@</c>, §1075), asking who at a newly-qualifying company we should deal
/// with. Nothing reaches the company: stage 2 mails the organizers ABOUT a purchaser, it does not
/// write to them.</para>
///
/// <para>🔒 <b>The COMPUTATION is not feature-gated; the MAIL is.</b> Recomputing has nothing to
/// protect against — no mail, no external call, no writes outside its own two tables — and gating it
/// would only let the organizer page show a stale answer with nothing saying why. The send sits
/// behind <see cref="VolumePackageApprovalMailService.FeatureKey"/>, default OFF, because outbound
/// mail is precisely the part the operator said he wants to approve first (§1077: <i>"critical
/// adjustment … tested very detailed"</i>). ⇒ Switching the feature off stops the asking and never
/// stops the counting.</para>
///
/// <para>Cadence: BASE TICK ONLY (§878); the real interval is the operator's setting on
/// <c>/Organizer/Jobs</c>, defaulted to daily. Safe to run more often — it recomputes from scratch and
/// updates today's snapshot in place rather than appending a second one.</para>
/// </remarks>
public sealed class VolumePackageQualificationJob
{
    private readonly CommunityHubDbContext _db;
    private readonly VolumePackageSweep _sweep;
    private readonly VolumePackageApprovalMailService _approvals;
    private readonly FeatureGateService _gate;
    private readonly ILogger<VolumePackageQualificationJob> _log;

    public VolumePackageQualificationJob(
        CommunityHubDbContext db, VolumePackageSweep sweep,
        VolumePackageApprovalMailService approvals, FeatureGateService gate,
        ILogger<VolumePackageQualificationJob> log)
    {
        _db = db; _sweep = sweep; _approvals = approvals; _gate = gate; _log = log;
    }

    [Function("VolumePackageQualificationJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogWarning("VolumePackageQualificationJob: no active event.");
            return;
        }

        var r = await _sweep.RunAsync(eventId.Value, ct);
        _log.LogInformation("VolumePackageQualificationJob: {Result}.", r);

        // 🔒 The sweep does not send: the mail lives HERE, after it, so the organizer page — which
        // calls the same sweep on Save and on "Recompute now" — cannot mail anybody as a side effect
        // of an organizer looking at the page.
        if (!await _gate.IsFeatureEnabledAsync(
                VolumePackageApprovalMailService.FeatureKey, eventId.Value, ct))
        {
            _log.LogInformation(
                "VolumePackageQualificationJob: approval mail is switched off ({Key}) — counted, "
                + "asked nobody.", VolumePackageApprovalMailService.FeatureKey);
            return;
        }

        var asked = await _approvals.SendPendingAsync(eventId.Value, devSilent: true, ct);
        _log.LogInformation("VolumePackageQualificationJob: approval mail — {Result}.", asked);
    }
}
