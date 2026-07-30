using System.Reflection;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §327 — the operator asked for a jobs page AND, pointedly, to "verify also the page contains
/// all the jobs i actually have". A hand-written catalog that nobody checks is exactly the kind
/// of thing that drifts and then quietly lies (see §326bm, where a stale label claimed a stage
/// was unimplemented). So the guarantee is mechanical: these tests REFLECT over the real
/// <c>CommunityHub.Jobs</c> assembly and fail the build if the catalog and the deployed
/// functions disagree in either direction.
/// </summary>
public sealed class JobCatalogCompletenessTests
{
    /// <summary>Every <c>[Function]</c> method that has a <c>[TimerTrigger]</c> parameter,
    /// paired with the cron expression actually compiled into it.</summary>
    private static IReadOnlyList<(string Function, string Cron)> RealTimerJobs()
    {
        var asm = typeof(CommunityHub.Jobs.ReminderJob).Assembly;
        var found = new List<(string, string)>();

        foreach (var type in asm.GetTypes())
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                var fn = m.GetCustomAttribute<FunctionAttribute>();
                if (fn is null) continue;

                foreach (var p in m.GetParameters())
                {
                    var timer = p.GetCustomAttribute<TimerTriggerAttribute>();
                    if (timer is null) continue;
                    found.Add((fn.Name, timer.Schedule));
                }
            }
        }

        return found;
    }

    [Fact]
    public void Every_timer_job_that_exists_is_listed_in_the_catalog()
    {
        var missing = RealTimerJobs()
            .Select(j => j.Function)
            .Where(f => JobCatalog.Find(f) is null)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These timer jobs exist but are NOT on the organizer jobs page — an organizer would "
            + "have no idea they run: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_catalog_never_lists_a_job_that_does_not_exist()
    {
        var real = RealTimerJobs().Select(j => j.Function).ToHashSet(StringComparer.Ordinal);
        var ghosts = JobCatalog.All
            .Select(j => j.FunctionName)
            .Where(f => !real.Contains(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.True(ghosts.Count == 0,
            "The jobs page lists jobs that no longer exist (renamed or deleted): "
            + string.Join(", ", ghosts));
    }

    [Fact]
    public void The_catalog_cron_matches_the_cron_the_job_is_actually_bound_to()
    {
        // The page states a schedule as fact. If the catalog and the attribute drift, the page
        // tells the operator the job runs at a time it does not — worse than showing nothing.
        var mismatched = new List<string>();
        foreach (var (fn, cron) in RealTimerJobs())
        {
            var d = JobCatalog.Find(fn);
            if (d is null) continue;   // covered by the completeness test above
            if (!string.Equals(d.Cron, cron, StringComparison.Ordinal))
            {
                mismatched.Add($"{fn}: catalog '{d.Cron}' vs actual '{cron}'");
            }
        }

        Assert.True(mismatched.Count == 0,
            "The jobs page would state the wrong schedule: " + string.Join(" | ", mismatched));
    }

    [Fact]
    public void Every_feature_key_a_job_claims_to_depend_on_is_a_real_catalog_key()
    {
        // A job filed under a key that is not in FeatureCatalog would render as "off because
        // its feature is off" against a switch the organizer cannot find.
        var bad = JobCatalog.All
            .Where(j => j.FeatureKey is not null && FeatureCatalog.Find(j.FeatureKey!) is null)
            .Select(j => $"{j.FunctionName} -> '{j.FeatureKey}'")
            .ToList();

        Assert.True(bad.Count == 0,
            "Jobs reference feature keys that are not in the feature catalog: " + string.Join(", ", bad));
    }

    [Fact]
    public void Function_names_are_unique_so_the_join_key_is_unambiguous()
    {
        var dupes = JobCatalog.All
            .GroupBy(j => j.FunctionName, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(dupes.Count == 0, "Duplicate job entries: " + string.Join(", ", dupes));
    }
}
