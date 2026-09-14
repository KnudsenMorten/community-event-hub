using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1077 stage 4 — the WEEKLY volume-package reminder: chase a company that was invited and has not
/// answered, until it completes or declines.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"Weekly reminders until completed or 'no interest'"</i>.</para>
///
/// <para>🔒 <b>Gated by <see cref="VolumePackageReminderService.FeatureKey"/>, default OFF.</b> This
/// is scheduled outbound mail to a customer — the only thing in the feature that writes to someone
/// repeatedly without a human deciding each time. The switch is the difference between "built" and
/// "running", and it is his to flip.</para>
///
/// <para>⚠️ <b>The cadence lives in the SERVICE, not in the trigger.</b> The timer fires daily and
/// the service decides who is a week overdue; a weekly TRIGGER would mean one missed run costs a
/// company a fortnight, and a restart at the wrong moment could skip a week silently.</para>
/// </remarks>
public sealed class VolumePackageReminderJob
{
    private readonly CommunityHubDbContext _db;
    private readonly VolumePackageReminderService _reminders;
    private readonly FeatureGateService _gate;
    private readonly ILogger<VolumePackageReminderJob> _log;

    public VolumePackageReminderJob(
        CommunityHubDbContext db, VolumePackageReminderService reminders,
        FeatureGateService gate, ILogger<VolumePackageReminderJob> log)
    {
        _db = db; _reminders = reminders; _gate = gate; _log = log;
    }

    [Function("VolumePackageReminderJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogWarning("VolumePackageReminderJob: no active event.");
            return;
        }

        if (!await _gate.IsFeatureEnabledAsync(
                VolumePackageReminderService.FeatureKey, eventId.Value, ct))
        {
            _log.LogInformation(
                "VolumePackageReminderJob: reminders are switched off ({Key}) — nobody chased.",
                VolumePackageReminderService.FeatureKey);
            return;
        }

        var result = await _reminders.RunAsync(eventId.Value, ct);
        _log.LogInformation("VolumePackageReminderJob: {Result}.", result);
    }
}
