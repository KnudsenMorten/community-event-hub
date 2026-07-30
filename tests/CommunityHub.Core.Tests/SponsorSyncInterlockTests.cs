using System.Reflection;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §640 — only ONE sponsor/exhibitor sync may write to Zoho.
/// </summary>
/// <remarks>
/// <para>Operator decision 2026-07-29: <i>"add the timer over SponsorZohoSyncService"</i>, after
/// §637 found PROD had no scheduled sponsor sync at all.</para>
///
/// <para>🔒 <b>Two sponsor syncs writing the same records is DATA LOSS, not inefficiency.</b> Zoho
/// hard-caps contact-e-mail updates at 3, and his warning was explicit: <i>"the sponsor object and
/// exhibitor goes into a stale state so we cannot update it anymore and have to delete it, which is
/// a disaster as leads will be lost"</i>. The legacy <c>BackstageSyncJob</c> is disabled by a config
/// switch today, but "today" is exactly the assumption that rots — so the interlock is structural
/// and these tests keep it that way.</para>
/// </remarks>
public class SponsorSyncInterlockTests
{
    private static Type ReconcileJob =>
        typeof(CommunityHub.Jobs.SponsorZohoReconcileJob);

    /// <summary>
    /// 🔒 The interlock only works if the new job can SEE the legacy switch. A refactor that drops
    /// this dependency would silently remove the guard while leaving the code that reads it.
    /// </summary>
    [Fact]
    public void The_reconcile_job_can_see_the_LEGACY_switch_so_it_can_stand_down()
    {
        var takesLegacyOptions = ReconcileJob.GetConstructors()
            .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(BackstageSyncOptions)));

        Assert.True(takesLegacyOptions,
            "SponsorZohoReconcileJob must depend on BackstageSyncOptions, or it cannot detect that "
            + "the legacy BackstageSyncJob is enabled and would double-write sponsor records.");
    }

    /// <summary>
    /// 🔒 §641 — the legacy job is RETIRED, so there must be exactly ONE sponsor sync in the
    /// catalog. A second one reappearing is the failure this whole file exists to prevent.
    /// </summary>
    [Fact]
    public void There_is_exactly_ONE_sponsor_sync_left_and_the_legacy_one_is_gone()
    {
        Assert.Null(JobCatalog.Find("BackstageSyncJob"));

        var reconcile = JobCatalog.Find("SponsorZohoReconcileJob");
        Assert.NotNull(reconcile);
        Assert.Equal(CommunityHub.Jobs.SponsorZohoReconcileJob.FeatureKey, reconcile!.FeatureKey);
    }

    /// <summary>
    /// 🔒 §641 — retiring a job means removing its <c>[Function]</c> attribute, NOT just its catalog
    /// row. A class that still carries the attribute would keep being scheduled by the host while
    /// being invisible on the Jobs page — strictly worse than before.
    /// </summary>
    [Fact]
    public void The_retired_legacy_job_can_no_longer_be_SCHEDULED()
    {
        var run = typeof(CommunityHub.Jobs.BackstageSyncJob)
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(run);
        Assert.Null(run!.GetCustomAttribute<Microsoft.Azure.Functions.Worker.FunctionAttribute>());
        Assert.DoesNotContain(run.GetParameters(),
            p => p.GetCustomAttribute<Microsoft.Azure.Functions.Worker.TimerTriggerAttribute>() is not null);
    }

    /// <summary>
    /// §542: <i>"it should run every 10 min"</i>. That cadence was applied to the legacy job in §510
    /// and achieved nothing because the job was off (§637); it now has to hold on the job that runs.
    /// </summary>
    [Fact]
    public void The_reconcile_runs_at_the_cadence_he_actually_asked_for()
    {
        var job = JobCatalog.Find("SponsorZohoReconcileJob")!;

        Assert.True(job.IsIntervalDriven);
        Assert.Equal(10, job.DefaultIntervalMinutes);
    }

    /// <summary>
    /// 🔒 The interlock is kept even though the legacy job is retired — it is now a TRIPWIRE for
    /// anyone who re-adds the <c>[Function]</c> attribute. §637 is the reason to distrust "it is
    /// switched off" as a safety property: that job was off for its whole life and still caused a
    /// problem, because a switch being off is not the same as a thing being unable to run.
    /// </summary>
    [Fact]
    public void The_interlock_survives_the_retirement_as_a_tripwire()
    {
        var readsLegacySwitch = ReconcileJob.GetConstructors()
            .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(BackstageSyncOptions)));

        Assert.True(readsLegacySwitch,
            "The interlock must remain: if anyone revives BackstageSyncJob by re-adding its "
            + "[Function] attribute, this is what stops the two syncs double-writing.");
    }

    /// <summary>
    /// The description has to name what it actually does, because it is the row he looks at when
    /// asking "did my sponsor change reach Zoho?".
    /// </summary>
    [Fact]
    public void The_sponsor_sync_describes_the_three_things_it_does()
    {
        var what = JobCatalog.Find("SponsorZohoReconcileJob")!.What;

        Assert.Contains("coordinator", what, StringComparison.OrdinalIgnoreCase);   // §626
        Assert.Contains("deleted in Zoho", what, StringComparison.OrdinalIgnoreCase); // §632 self-heal
        Assert.Contains("exhibitor", what, StringComparison.OrdinalIgnoreCase);     // §596
    }

    /// <summary>
    /// 🔒 §642 — <b>"reconcile" is not a word he uses</b> (operator 2026-07-29: *"reconsile is not a
    /// word i use myself"*), and §595 already renamed one job off it. This pins the preference on
    /// everything he can actually SEE, so it cannot creep back in through a new job.
    /// </summary>
    [Fact]
    public void No_job_the_operator_can_SEE_uses_the_word_reconcile()
    {
        var offenders = JobCatalog.All
            .Where(j => j.Title.Contains("reconcil", StringComparison.OrdinalIgnoreCase)
                        || j.What.Contains("reconcil", StringComparison.OrdinalIgnoreCase))
            .Select(j => $"{j.FunctionName} ({j.Title})")
            .ToList();

        Assert.True(offenders.Count == 0,
            "These jobs show the word 'reconcile' on the Jobs page. Say what the job DOES instead — "
            + "e.g. \"Zoho sync: sponsors + exhibitors\": " + string.Join(", ", offenders));
    }

    /// <summary>
    /// 🔒 The counterpart: FUNCTION NAMES and health keys may still contain it, and MUST be left
    /// alone. They are live DB keys — §595 renamed one and §634 had to clean up the orphaned health
    /// marker it left behind. Cosmetic preference never justifies touching a key with history.
    /// </summary>
    [Fact]
    public void The_function_name_is_NOT_renamed_because_it_is_a_live_database_key()
    {
        Assert.NotNull(JobCatalog.Find("SponsorZohoReconcileJob"));
    }
}
