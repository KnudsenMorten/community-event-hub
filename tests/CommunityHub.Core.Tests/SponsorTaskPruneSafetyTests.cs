using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1081 — THE PRUNE THAT DELETED 15 PRODUCTION ROWS, FOUR OF THEM COMPLETED (2026-08-13).
/// </summary>
/// <remarks>
/// <para><b>What happened.</b> <c>sponsor.initial-onboarding</c> was retired from the task registry.
/// Its key therefore stopped appearing in the pull's <c>desiredKeys</c>, and
/// <see cref="SponsorOrderPullService"/>'s orphan prune hard-deleted every
/// <c>sponsor:{co}:initial-onboarding-of-sponsor</c> row on the first pull after the deploy — 11 open
/// and <b>4 completed</b>. The operator's instruction for that retirement was explicit:
/// <i>"auto-close the task (never delete)"</i>. A retirement sweep did exactly that, but it runs
/// AFTER the per-company loop, so the prune got there first.</para>
///
/// <para>🔑 <b>Why no test caught it: this service had none at all.</b> The prune was a clause inside
/// a large query, so the decision to delete production data was not addressable by a test. The rule
/// is now <see cref="SponsorOrderPullService.MayPrune"/> — extracted precisely so it can be pinned
/// here.</para>
///
/// <para>⚠️ <b>The latent half.</b> Deleting completed rows was ALWAYS possible: any config RENAME
/// changes a title's slug, hence its SourceKey, orphaning the old row — including a Done one, with
/// its <c>CompletedAt</c> and <c>CompletedByParticipantId</c>. The sibling prune in
/// <c>WizardStepTaskSeeder</c> had guarded against this in words for a long time (<i>"Only OPEN rows
/// go: a completed form's Done task keeps its audit trail"</i>); this one never did.</para>
/// </remarks>
public sealed class SponsorTaskPruneSafetyTests
{
    private const string Prefix = "sponsor:4242:";
    private static readonly string[] Desired =
    {
        "sponsor:4242:upload-sponsor-wall-design-in-vector-format",
        "sponsor:4242:register-booth-members",
    };

    /// <summary>
    /// 🔴 THE REGRESSION. A COMPLETED task the config no longer produces is HISTORY, not an orphan.
    /// This is the assertion whose absence cost four completion records.
    /// </summary>
    [Fact]
    public void A_completed_task_is_never_pruned_even_when_the_config_dropped_it()
    {
        Assert.False(SponsorOrderPullService.MayPrune(
            "sponsor:4242:some-retired-thing", TaskState.Done, Prefix, Desired));
    }

    /// <summary>
    /// 🔴 THE EXACT ROW THAT WAS LOST: the retired legacy key, still OPEN, in the window before the
    /// retirement sweep closes it. The sweep runs after this loop, so without this guard the prune
    /// wins the race — which is precisely what happened in production.
    /// </summary>
    [Fact]
    public void The_retired_onboarding_key_is_never_pruned_even_while_still_open()
    {
        Assert.False(SponsorOrderPullService.MayPrune(
            "sponsor:4242:initial-onboarding-of-sponsor", TaskState.Open, Prefix, Desired));
    }

    /// <summary>…and not when InProgress either — the same row, mid-flight.</summary>
    [Fact]
    public void The_retired_onboarding_key_is_never_pruned_when_in_progress()
    {
        Assert.False(SponsorOrderPullService.MayPrune(
            "sponsor:4242:initial-onboarding-of-sponsor", TaskState.InProgress, Prefix, Desired));
    }

    /// <summary>
    /// ✅ The prune still does its actual job: an OPEN row whose title was renamed in config leaves an
    /// orphan behind under the old slug, and that genuinely should go. Removing the guard's teeth
    /// entirely would trade one bug for another.
    /// </summary>
    [Fact]
    public void An_open_orphan_from_a_renamed_title_is_still_pruned()
    {
        Assert.True(SponsorOrderPullService.MayPrune(
            "sponsor:4242:old-title-slug", TaskState.Open, Prefix, Desired));
    }

    /// <summary>A task the config still produces is never an orphan, open or not.</summary>
    [Theory]
    [InlineData(TaskState.Open)]
    [InlineData(TaskState.InProgress)]
    [InlineData(TaskState.Done)]
    public void A_task_the_config_still_wants_is_never_pruned(TaskState state)
    {
        Assert.False(SponsorOrderPullService.MayPrune(
            "sponsor:4242:register-booth-members", state, Prefix, Desired));
    }

    /// <summary>
    /// 🔒 Scope guard: the prune only ever touches THIS company's pull-managed keys. A null key, or
    /// another company's row, is never a candidate — otherwise one company's config change would
    /// delete another's work.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("sponsor:9999:old-title-slug")]
    [InlineData("speakerdl:12:submit-deck")]
    [InlineData("adhoc:4242:something")]
    public void Only_this_companys_pull_managed_keys_are_candidates(string? sourceKey)
    {
        Assert.False(SponsorOrderPullService.MayPrune(sourceKey, TaskState.Open, Prefix, Desired));
    }
}
