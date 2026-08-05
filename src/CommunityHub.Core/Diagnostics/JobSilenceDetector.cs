using CommunityHub.Core.Data;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Diagnostics;

/// <summary>
/// §545 — detects jobs that are SILENT: running green, or not running at all, while doing nothing.
/// </summary>
/// <remarks>
/// <para><b>Why this exists, in the operator's words.</b> §545: *"organizer log viewer + alerting for
/// INACTIVE / SILENT / NO-OP / CREDENTIAL, not just failures. Every incident today was visible in
/// logs and invisible to him."*</para>
///
/// <para><b>The night of 2026-07-28 is the proof.</b> THREE separate features were inert while
/// looking built and green, and nothing alerted on any of them:</para>
/// <list type="number">
/// <item>§576 — both change-detection engines demanded a sync stage that had been DELETED, so they
/// no-opped every 5 minutes for months while the Jobs page showed a healthy run.</item>
/// <item>§621 — a config flag nobody had set made the speaker read return "unavailable" without
/// ever calling Zoho. <c>BackstageChangeCheckedAt</c> was NULL on all 28 speakers: the engine had
/// never once looked. <i>(🗑 §754.5 — that flag is DELETED. It existed to wait for a Backstage
/// OAuth scope the credentials had all along, so it could only ever have blocked.)</i></item>
/// <item>§585 — <c>halls</c> was missing from the <c>?page</c> reject list, so every hall read came
/// back EMPTY and no session could get a room. No error, no failure count.</item>
/// </list>
///
/// <para><b>The existing <see cref="JobFailureTracker"/> cannot see any of these</b> — it alerts on
/// consecutive FAILURES, and none of them failed. That is precisely the gap: a job that succeeds at
/// doing nothing is invisible.</para>
///
/// <para>🔒 <b>Restraint is the design.</b> An alert that cries wolf is worse than none — §609 was
/// exactly that, a red mail every run for a by-design condition. So: rare-cadence jobs are never
/// flagged, the staleness bar is deliberately generous (3× the expected gap), and the result is a
/// digest, not one mail per job.</para>
/// </remarks>
public sealed class JobSilenceDetector
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public JobSilenceDetector(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db; _clock = clock;
    }

    /// <summary>Why a job was flagged.</summary>
    public enum SilenceKind
    {
        /// <summary>Enabled and scheduled, but has NEVER recorded a success.</summary>
        NeverSucceeded = 0,
        /// <summary>Has not succeeded for far longer than its own cadence allows.</summary>
        Stale = 1,
        /// <summary>A health marker with no matching job — left behind by a rename.</summary>
        OrphanedMarker = 2,
    }

    /// <summary>One flagged job.</summary>
    public sealed record Silent(string JobKey, SilenceKind Kind, string Detail);

    /// <summary>
    /// §707.27 E — functions that legitimately report health but are NOT timer jobs, so an absent
    /// <see cref="JobCatalog"/> entry is correct rather than an orphan.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>An allow-list, deliberately — NOT a <c>JobDescriptor</c>.</b> <c>ZohoOrderWebhook</c> is
    /// HTTP-triggered, and <c>JobCatalogCompletenessTests</c> asserts the catalog equals the real
    /// <c>[TimerTrigger]</c> set, so cataloguing it to silence the alert would break the guarantee
    /// that makes the Jobs page trustworthy.
    ///
    /// <para>It was reported as *"most likely left behind by a rename"*, which is a specific and
    /// wrong diagnosis. A recurring alert that names an innocent cause is how a real orphan later
    /// gets waved through.</para>
    ///
    /// <para>These are NOT checked for staleness either: an HTTP function fires when an external
    /// system calls it, so a quiet day is not evidence of anything. Only a timer has a cadence to
    /// measure against.</para>
    /// </remarks>
    public static readonly IReadOnlySet<string> NonTimerHealthReporters =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ZohoOrderWebhook",
        };

    /// <summary>
    /// Jobs that look asleep. Pure over the passed-in state so it is fully testable — no clock or
    /// database reads inside the rule itself.
    /// </summary>
    public static IReadOnlyList<Silent> Evaluate(
        IReadOnlyDictionary<string, DateTimeOffset?> lastSuccessByKey, DateTimeOffset now)
    {
        var flagged = new List<Silent>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var job in JobCatalog.All)
        {
            known.Add(job.FunctionName);
            if (job.HealthKey is { Length: > 0 }) known.Add(job.HealthKey);

            var expected = ExpectedGap(job);
            // 🔒 A rare-cadence job (annual placeholder, manual-only) is NEVER stale. SessionPushPilotJob
            // runs "0 0 0 1 1 *" and is deliberately inert — flagging it would teach him to ignore this.
            if (expected is null) continue;

            if (!lastSuccessByKey.TryGetValue(job.FunctionName, out var last)
                && !lastSuccessByKey.TryGetValue(job.HealthKey ?? job.FunctionName, out last))
            {
                continue;   // no marker at all — this job simply does not report health.
            }

            if (last is null)
            {
                flagged.Add(new Silent(job.FunctionName, SilenceKind.NeverSucceeded,
                    $"{job.Title} has never recorded a success, though it is scheduled {job.Cadence.ToLowerInvariant()}."));
                continue;
            }

            // 3× the expected gap, floored at 2 hours: generous on purpose. A job that is merely a
            // little late must not page him; one that has stopped must.
            var tolerance = expected.Value * 3;
            if (tolerance < TimeSpan.FromHours(2)) tolerance = TimeSpan.FromHours(2);

            var since = now - last.Value;
            if (since > tolerance)
            {
                flagged.Add(new Silent(job.FunctionName, SilenceKind.Stale,
                    $"{job.Title} last succeeded {(int)since.TotalHours}h ago, but runs {job.Cadence.ToLowerInvariant()}."));
            }
        }

        // Markers with no job behind them. Tonight's rename (§595 ErpWebshopReconcileJob →
        // ErpSyncCustomerContactJob) left one, and an orphan quietly stops being watched.
        foreach (var key in lastSuccessByKey.Keys
                     .Where(k => !known.Contains(k) && !NonTimerHealthReporters.Contains(k)))
        {
            flagged.Add(new Silent(key, SilenceKind.OrphanedMarker,
                $"'{key}' reports health but matches no job in the catalog — most likely left behind "
                + "by a rename. Nothing is watching it."));
        }

        return flagged;
    }

    /// <summary>
    /// How long this job may reasonably go between successes, or null when its cadence is too rare
    /// to judge (annual placeholders, manual-only jobs).
    /// </summary>
    public static TimeSpan? ExpectedGap(JobDescriptor job)
    {
        if (job.IsIntervalDriven) return TimeSpan.FromMinutes(job.DefaultIntervalMinutes!.Value);

        var f = job.Cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 6) return null;

        // "0 0 0 1 1 *" — pinned day AND month ⇒ annual/inert. Never judged.
        if (f[3] != "*" || f[4] != "*") return null;

        if (f[1].StartsWith("*/") && int.TryParse(f[1][2..], out var everyMin))
            return TimeSpan.FromMinutes(everyMin);
        if (f[2].StartsWith("*/") && int.TryParse(f[2][2..], out var everyHour))
            return TimeSpan.FromHours(everyHour);
        if (f[1] == "*") return TimeSpan.FromMinutes(1);
        if (f[2] == "*") return TimeSpan.FromHours(1);   // hourly at a fixed minute

        return TimeSpan.FromDays(1);                      // daily at a fixed time
    }

    /// <summary>Load the live health state and evaluate it.</summary>
    public async Task<IReadOnlyList<Silent>> DetectAsync(CancellationToken ct = default)
    {
        var markers = await _db.JobHealthMarkers.AsNoTracking()
            .Select(m => new { m.JobKey, m.LastSuccessAt })
            .ToListAsync(ct);

        var byKey = markers
            .Where(m => !string.IsNullOrWhiteSpace(m.JobKey))
            .GroupBy(m => m.JobKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(x => x.LastSuccessAt), StringComparer.OrdinalIgnoreCase);

        return Evaluate(byKey, _clock.GetUtcNow());
    }
}
