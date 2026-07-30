using CommunityHub.Core.Diagnostics;
using CommunityHub.Core.Email;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §545 — the daily "is anything quietly asleep?" check. Alerts on SILENT jobs, which the existing
/// failure tracker cannot see because they do not fail.
/// </summary>
/// <remarks>
/// <para>Operator: *"organizer log viewer + alerting for INACTIVE / SILENT / NO-OP / CREDENTIAL, not
/// just failures. Every incident today was visible in logs and invisible to him."*</para>
///
/// <para>On 2026-07-28 three features were inert while green — a deleted sync stage still being
/// demanded (§576), a config flag never set (§621), and a resource missing from the paging reject
/// list so every read came back empty (§585). None failed, so nothing alerted. This job exists to
/// make that class visible.</para>
///
/// <para>🔒 <b>ONE DIGEST, and only when the picture CHANGES.</b> A daily mail listing the same
/// jobs would be ignored inside a week — §609 is the cautionary tale, where a red failure mail fired
/// every run for a by-design condition. The alert sender throttles on the digest content, so an
/// unchanged set stays quiet.</para>
/// </remarks>
public sealed class JobSilenceAlertJob
{
    private readonly JobSilenceDetector _detector;
    private readonly EngineAlertSender _alerts;
    private readonly ILogger<JobSilenceAlertJob> _log;

    public JobSilenceAlertJob(
        JobSilenceDetector detector, EngineAlertSender alerts, ILogger<JobSilenceAlertJob> log)
    {
        _detector = detector; _alerts = alerts; _log = log;
    }

    [Function("JobSilenceAlertJob")]
    public async Task Run(
        // Daily at 06:40 UTC — after the other nightly jobs have had their chance to run, so a job
        // that legitimately runs at 05:00 is not reported as silent at 04:00.
        [TimerTrigger("0 40 6 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        var silent = await _detector.DetectAsync(ct);

        if (silent.Count == 0)
        {
            _log.LogInformation("JobSilenceAlertJob: every scheduled job is reporting healthy.");
            return;
        }

        foreach (var s in silent)
            _log.LogWarning("JobSilenceAlertJob: {Kind} — {Detail}", s.Kind, s.Detail);

        var lines = string.Join("", silent
            .OrderBy(s => s.Kind)
            .Select(s => $"<li><b>{Enc(s.JobKey)}</b> — {Enc(s.Detail)}</li>"));

        // Throttled on a STABLE key so an unchanged picture does not re-mail daily.
        await _alerts.AlertAsync(
            $"Background jobs: {silent.Count} look asleep [ELDK27]",
            "<p>These background jobs are not failing — they are simply not doing anything, which is "
            + "why nothing else has told you about them.</p>"
            + $"<ul>{lines}</ul>"
            + "<p>A job can report a healthy run while being blocked by a setting, a deleted "
            + "configuration value, or an upstream read that silently returns nothing.</p>",
            ct, throttleKey: "JobSilenceAlertJob");

        _log.LogInformation("JobSilenceAlertJob: reported {Count} silent job(s).", silent.Count);
    }

    private static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
}
