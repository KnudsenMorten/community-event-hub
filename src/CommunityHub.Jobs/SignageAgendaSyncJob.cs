using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Signage;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §754 deliverable 1 — the 5-minute SIGNAGE AGENDA poll. Mirrors the complete Zoho Backstage
/// agenda into CEH so the venue screens render from a local table, never from Zoho at request time.
/// </summary>
/// <remarks>
/// <para>The operator's "sync now" is the generic <c>/Organizer/Jobs</c> trigger, which starts THIS
/// function through the Functions admin endpoint (§543). That is deliberately the only manual path:
/// it runs the production routine early rather than a second copy of it, which is the project rule
/// that a sync never happens outside its scheduled routine.</para>
///
/// <para>🔒 <b>The job reports, it does not decide.</b> Every fail-safe — keep the last-good agenda
/// on failure, never apply an empty pull — lives in <see cref="SignageAgendaSyncService"/>, so the
/// manual trigger and the timer cannot behave differently.</para>
/// </remarks>
public sealed class SignageAgendaSyncJob
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoOptions _options;
    private readonly FeatureGateService _gate;
    private readonly SignageAgendaSyncService _service;
    private readonly ILogger<SignageAgendaSyncJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public SignageAgendaSyncJob(
        CommunityHubDbContext db, ZohoOptions options, FeatureGateService gate,
        SignageAgendaSyncService service, ILogger<SignageAgendaSyncJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db; _options = options; _gate = gate; _service = service; _log = log;
        _activity = activity;
    }

    [Function("SignageAgendaSyncJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("SignageAgendaSyncJob: Zoho disabled.");
            _activity?.ReportInactive("Zoho is switched off, so the signage agenda is not refreshed.");
            return;
        }

        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogWarning("SignageAgendaSyncJob: no active event."); return; }

        if (!await _gate.IsFeatureEnabledAsync("signage-agenda-sync", eventId.Value, ct))
        {
            _log.LogInformation("SignageAgendaSyncJob: feature off.");
            _activity?.ReportInactive(
                "The 'Signage agenda (venue screens)' feature is switched off, so the agenda shown on "
                + "the venue screens is not being refreshed from Zoho.");
            return;
        }

        var result = await _service.RunAsync(eventId.Value, ct);

        if (!result.Ok)
        {
            _log.LogWarning("SignageAgendaSyncJob: {Reason}", result.FailureReason);
            // 🔒 INACTIVE, not a throw. The screens are still showing the last-good agenda, so this
            // is "we did not refresh", not "the engine is broken" — and §716 keeps a THROWING engine
            // loud precisely so that distinction stays meaningful. The service has already alerted.
            _activity?.ReportInactive(result.FailureReason ?? "The signage agenda was not refreshed.");
            return;
        }

        _activity?.ReportWork();

        _log.LogInformation(
            "SignageAgendaSyncJob: pulled {Pulled}, added {Added}, updated {Updated}, removed {Removed}, skipped {Skipped}.",
            result.Pulled, result.Added, result.Updated, result.Removed, result.SkippedUnusable);
    }
}
