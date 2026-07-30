using System.Text.RegularExpressions;
using CommunityHub.Core.Settings;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔒 §707.8 — THE JOBS PAGE SHOWS THE WORDS, SO THE WORDS MUST BE TRUE.
/// </summary>
/// <remarks>
/// <c>JobDescriptor</c> carries both a human <c>Cadence</c> ("Every 10 minutes") and the <c>Cron</c>
/// actually compiled into the job's <c>[TimerTrigger]</c>. <c>/Organizer/Jobs</c> renders the WORDS —
/// so when the two disagree the page states a schedule the host does not run, and nothing fails.
///
/// <para>Found 2026-07-30 while answering "how often do we check for pending X":
/// <c>SessionChangeDetectionJob</c> read <i>"Every 10 minutes"</i> against a <c>0 */5 * * * *</c>
/// cron — it actually runs twice as often as the page claimed. Exactly the §326bx shape (a control
/// or a readout describing something the system does not do), on the operations page rather than
/// the settings page.</para>
///
/// <para>Only the mechanically checkable forms are asserted — <c>*/N</c> minute intervals, hourly
/// <c>at :MM</c>, and daily <c>HH:MM</c>. Anything else (multi-slot lists, "manual only") is left to
/// prose, so this stays a guard rather than a straitjacket on how a cadence may be worded.</para>
/// </remarks>
public class JobCadenceWordsMatchCronTests
{
    [Fact]
    public void Cadence_words_match_the_cron_for_every_interval_job()
    {
        var mismatches = new List<string>();

        foreach (var job in JobCatalog.All)
        {
            // §510: on an interval-driven job the Cron is only the BASE TICK — the real spacing is
            // the operator's configured minimum interval, so the words legitimately differ.
            if (job.IsIntervalDriven) continue;

            var cron = job.Cron?.Trim() ?? string.Empty;
            var words = job.Cadence?.Trim() ?? string.Empty;
            if (cron.Length == 0 || words.Length == 0) continue;

            // "0 */N * * * *"  =>  every N minutes
            var everyN = Regex.Match(cron, @"^0 \*/(\d+) \* \* \* \*$");
            if (everyN.Success)
            {
                var n = everyN.Groups[1].Value;
                var claimed = Regex.Match(words, @"Every (\d+) min", RegexOptions.IgnoreCase);
                if (!claimed.Success || claimed.Groups[1].Value != n)
                {
                    mismatches.Add($"{job.FunctionName}: cron runs every {n} min but says '{words}'");
                }
                continue;
            }

            // "0 MM HH * * *"  =>  daily at HH:MM
            var daily = Regex.Match(cron, @"^0 (\d{1,2}) (\d{1,2}) \* \* \*$");
            if (daily.Success)
            {
                var hh = int.Parse(daily.Groups[2].Value);
                var mm = int.Parse(daily.Groups[1].Value);
                var stamp = $"{hh:00}:{mm:00}";
                if (!words.Contains(stamp, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"{job.FunctionName}: cron is daily {stamp} but says '{words}'");
                }
                continue;
            }

            // "0 MM * * * *"  =>  hourly at :MM
            var hourly = Regex.Match(cron, @"^0 (\d{1,2}) \* \* \* \*$");
            if (hourly.Success)
            {
                var mm = int.Parse(hourly.Groups[1].Value);
                if (!words.Contains($":{mm:00}", StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"{job.FunctionName}: cron is hourly at :{mm:00} but says '{words}'");
                }
            }
        }

        Assert.True(
            mismatches.Count == 0,
            "The Jobs page would state a schedule the host does not run:\n  "
            + string.Join("\n  ", mismatches));
    }
}
