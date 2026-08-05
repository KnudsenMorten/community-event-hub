using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Settings;

/// <summary>Where the Functions host lives, and the admin key that may start one of its jobs.</summary>
public sealed class JobTriggerOptions
{
    public const string SectionName = "JobTrigger";

    /// <summary>e.g. <c>https://eldk27hub-fn-prodpdrq.azurewebsites.net</c>. Blank ⇒ feature off.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The Functions host MASTER key. Blank ⇒ feature off (the button reports why).</summary>
    public string MasterKey { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(MasterKey);
}

/// <summary>The outcome of a manual trigger, in words the organizer page can print.</summary>
public sealed record JobTriggerResult(bool Accepted, string Message);

/// <summary>
/// §543 — RUN ONE BACKGROUND JOB NOW, from the organizer Jobs page.
///
/// <para>Operator 2026-07-28, with 1,500 attendees waiting on an agenda that had not synced:
/// <i>"you must do this by triggering - i need a trigger now option for each of the
/// functions/jobs"</i>, and <i>"i cannot trigger now, i cannot change frequency"</i>. Until now the
/// only way to make a job run was to wait for its cron, so a stuck sync could not be nudged at all
/// — the operator could watch the problem but not act on it.</para>
///
/// <para><b>How.</b> The Azure Functions host exposes <c>POST /admin/functions/{name}</c>, which
/// starts a function immediately — including a timer job — using the host's master key. That runs
/// the EXACT production routine, which is why the project guardrail permits manual triggering while
/// forbidding hand-rolled syncs: nothing is duplicated here, the real job is simply started early.
/// The timers remain the system of record for cadence.</para>
///
/// <para><b>Fire-and-forget by contract.</b> The endpoint returns 202 as soon as the run is
/// accepted, not when it finishes, so the page promises only "started" — never "succeeded". The
/// job's own Last run / health columns report the outcome, which keeps this honest about a sync
/// that starts and then fails.</para>
/// </summary>
public sealed class JobTriggerService
{
    private readonly HttpClient _http;
    private readonly JobTriggerOptions _options;
    private readonly ILogger<JobTriggerService>? _log;

    // §766 — needed to clear the §510 interval stamp so a manual run is not silently throttled.
    // Optional so existing constructions/tests are unchanged; without it the old (lying) behaviour
    // would return, so production wiring passes it.
    private readonly Data.CommunityHubDbContext? _db;

    public JobTriggerService(
        HttpClient http, JobTriggerOptions options, ILogger<JobTriggerService>? log = null,
        Data.CommunityHubDbContext? db = null)
    {
        _http = http;
        _options = options;
        _log = log;
        _db = db;
    }

    /// <summary>
    /// §766 — let ONE manual run through the §510 interval gate. Never throws: failing to clear the
    /// stamp must not stop the trigger, it just means the run may be throttled as before.
    /// </summary>
    private async Task ClearIntervalStampAsync(string functionName, CancellationToken ct)
    {
        if (_db is null) return;
        try
        {
            var state = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstOrDefaultAsync(_db.JobRunStates, s => s.FunctionName == functionName, ct);
            if (state?.LastRunAt is null) return;   // never run ⇒ already un-throttled

            state.LastRunAt = null;
            await _db.SaveChangesAsync(ct);
            _log?.LogInformation(
                "Job {Function}: interval stamp cleared for a manual run (§766).", functionName);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex,
                "Job {Function}: could not clear the interval stamp; the manual run may be throttled.",
                functionName);
        }
    }

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>
    /// Start <paramref name="functionName"/> now. The name must be a real catalog job, so a typo
    /// or a crafted post cannot reach an arbitrary endpoint on the Functions host.
    /// </summary>
    public async Task<JobTriggerResult> TriggerAsync(string functionName, CancellationToken ct = default)
    {
        if (JobCatalog.Find(functionName) is null)
            return new JobTriggerResult(false, $"'{functionName}' is not a known job.");

        if (!_options.IsConfigured)
        {
            return new JobTriggerResult(false,
                "Manual triggering is not configured for this environment "
                + "(JobTrigger:BaseUrl / JobTrigger:MasterKey).");
        }

        // 🔴 §766 — CLEAR THE INTERVAL STAMP FIRST, or this button lies.
        //
        // JobsPauseMiddleware applies the §510 interval to EVERY invocation and cannot tell a timer
        // tick from a deliberate manual start. So on an interval-driven job the admin endpoint
        // answered 202, the middleware short-circuited the body, and this method reported "STARTED"
        // for a run that never happened:
        //     SpeakerPhotoArchiveJob skipped: runs every 1440 min; last run 2026-08-01 12:50:00Z.
        //
        // 🔑 §543 gave him "trigger now" because he said he could not; §510 then gave him the
        // frequency dial and silently took the trigger away for exactly the jobs the dial governs.
        // The two features cancelled each other, and a DAILY job is precisely the one you most need
        // to run early — waiting 24 hours to see whether it works is not a debugging loop.
        //
        // 🔒 Nulling LastRunAt introduces no new rule: ShouldSkip already treats a job that has
        // never run as never skippable. The middleware re-stamps it as this run starts, so the
        // cadence resumes from the manual run and nothing accumulates. The INTERVAL is untouched —
        // this forces ONE run, it does not widen the schedule.
        //
        // ⚠️ Done HERE rather than in the middleware on purpose: teaching the middleware to detect
        // "programmatically called via the host APIs" would exempt anything else that reaches the
        // admin endpoint, and that reason string is a host implementation detail, not a contract.
        await ClearIntervalStampAsync(functionName, ct);

        var url = $"{_options.BaseUrl.TrimEnd('/')}/admin/functions/{functionName}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("x-functions-key", _options.MasterKey);
            // The admin endpoint requires a body; a timer job ignores the payload itself.
            req.Content = JsonContent.Create(new { input = string.Empty });

            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                _log?.LogInformation("Job {Function} triggered manually.", functionName);
                return new JobTriggerResult(true,
                    $"{JobCatalog.Find(functionName)!.Title} was STARTED. It runs in the background — "
                    + "watch its Last run and Health columns for the outcome.");
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            _log?.LogWarning(
                "Manual trigger of {Function} refused: HTTP {Status} {Body}",
                functionName, (int)resp.StatusCode, body);

            // 404 is the one worth naming: it means the deployed host has no such function, which
            // is a REAL finding (a job in the catalog that is not actually deployed), not a glitch.
            return new JobTriggerResult(false, resp.StatusCode switch
            {
                System.Net.HttpStatusCode.NotFound =>
                    $"The Functions host does not have a job called '{functionName}' — "
                    + "it may not be deployed.",
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                    "The Functions host rejected the admin key — it may have been rotated.",
                _ => $"The Functions host refused the request (HTTP {(int)resp.StatusCode}).",
            });
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Manual trigger of {Function} failed.", functionName);
            return new JobTriggerResult(false, $"Could not reach the Functions host: {ex.Message}");
        }
    }
}
