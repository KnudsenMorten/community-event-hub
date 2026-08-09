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

    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;

    public JobSilenceAlertJob(
        JobSilenceDetector detector, EngineAlertSender alerts, ILogger<JobSilenceAlertJob> log,
        Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _detector = detector; _alerts = alerts; _log = log; _config = config;
    }

    [Function("JobSilenceAlertJob")]
    public async Task Run(
        // §878 — BASE TICK ONLY; the cadence is the operator's interval on /Organizer/Jobs.
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        // 🛑 §977 — OPERATOR OFF SWITCH. 2026-08-09: *"i dont want any emails which tells me 'i did
        // nothing' or 'i did not find anything in last 300 runs' or whatever it sends. it is just
        // noice"*.
        //
        // ⚠️ §545 deliberately made this watchdog un-gateable by a FEATURE flag, on the reasoning
        // that the thing which notices silence must not be silenceable by the same switch that
        // silences everything else. That reasoning still holds for feature gates — this is a
        // separate, explicit operator switch, and he is the audience the mail exists for. An alert
        // its only reader has asked to stop is not protection; it is noise that trains him to ignore
        // the inbox the REAL alerts arrive in.
        //
        // 🔒 Default TRUE, so no other deployment changes behaviour; ELDK27 PROD sets it false.
        // The detector still runs and still logs — only the MAIL stops, so the signal is in App
        // Insights for anyone who goes looking.
        var alertsEnabled = !string.Equals(
            _config["Alerts:JobSilenceEnabled"], "false", StringComparison.OrdinalIgnoreCase);

        var silent = await _detector.DetectAsync(ct);

        if (silent.Count == 0)
        {
            _log.LogInformation("JobSilenceAlertJob: every scheduled job is reporting healthy.");
            return;
        }

        if (!alertsEnabled)
        {
            _log.LogInformation(
                "JobSilenceAlertJob: {Count} job(s) look asleep, but Alerts:JobSilenceEnabled=false — logged, not mailed.",
                silent.Count);
            foreach (var s in silent)
                _log.LogInformation("JobSilenceAlertJob (not mailed): {Kind} — {Detail}", s.Kind, s.Detail);
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
            // §752.9 — DEV-SILENT. On DEV most of these jobs are idle BY CONFIGURATION: their
            // features are switched off on purpose, so "4 look asleep" is the operator's own setup
            // read back to him. In PROD a job that has quietly stopped doing anything is precisely
            // the failure nothing else reports, which is why the alert exists at all.
            ct, throttleKey: "JobSilenceAlertJob", devSilent: true);

        _log.LogInformation("JobSilenceAlertJob: reported {Count} silent job(s).", silent.Count);
    }

    private static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
}
