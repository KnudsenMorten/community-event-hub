using System.Collections.Generic;
using CommunityHub.Core.Participants;
using CommunityHub.Forms;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §161 shared Get-Started STEPPER view-model — the one shape the speaker, role and sponsor
/// "Get started" pages all render through <c>_WizardStepper</c>. These prove the progress math
/// (mirrors the original sponsor/speaker views), that EVERY step stays a card with an
/// edit/open link even at 100% (no dead-end), and that an undeterminable (null) step is shown
/// but never counted nor lets the wizard go "all done".
/// </summary>
public sealed class WizardStepperVmTests
{
    private static WizardStepperCard Card(int n, bool? done) =>
        new(n, $"Step {n}", $"Desc {n}", done, $"/Forms/Step{n}");

    private static WizardStepperVm Vm(params WizardStepperCard[] cards) =>
        new("Get started", "Intro", cards, ContinueUrl: null, ContinueLabel: null,
            AllDoneMessage: "All set", EditLabel: "Edit", OpenLabel: "Open");

    [Fact]
    public void Progress_counts_only_confirmed_done_steps()
    {
        var vm = Vm(Card(1, true), Card(2, false), Card(3, true), Card(4, false));

        Assert.Equal(4, vm.TotalSteps);
        Assert.Equal(2, vm.DoneCount);
        Assert.Equal(50, vm.Percent);
        Assert.False(vm.AllDone);
    }

    [Fact]
    public void Every_step_is_kept_as_an_editable_card_when_all_done()
    {
        // The whole point of §161: at 100% the cards are STILL listed (so each is editable),
        // not collapsed into an "all done — go to hub" dead-end.
        var vm = Vm(Card(1, true), Card(2, true), Card(3, true));

        Assert.True(vm.AllDone);
        Assert.Equal(100, vm.Percent);
        Assert.Equal(3, vm.Cards.Count);                 // nothing dropped
        Assert.All(vm.Cards, c => Assert.True(c.Done));  // each rendered (with an Edit link)
    }

    [Fact]
    public void Null_state_step_is_shown_but_never_counted_and_blocks_all_done()
    {
        // Mirrors the sponsor ERP-contacts step: undeterminable state is a guided link.
        var vm = Vm(Card(1, true), Card(2, null), Card(3, true));

        Assert.Equal(3, vm.TotalSteps);
        Assert.Equal(2, vm.DoneCount);    // the null step is not "done"
        Assert.False(vm.AllDone);         // ... and keeps the wizard not-all-done
        Assert.Equal(67, vm.Percent);     // round(100*2/3)
    }

    [Fact]
    public void Empty_plan_is_zero_percent_not_all_done()
    {
        var vm = Vm();
        Assert.Equal(0, vm.Percent);
        Assert.False(vm.AllDone);
    }

    [Theory]
    // §161 step↔task consistency: the manual mark-done Get-Started steps deep-link to the
    // SAME page the matching My-Tasks row links to (ParticipantChecklistBuilder), so the two
    // surfaces never disagree on where to act.
    [InlineData("/Forms/Signal", "signal:42")]
    [InlineData("/Speaker/Promote", "promote:42")]
    [InlineData("/Party", "party-form:42")]
    public void Manual_step_route_matches_my_tasks_link(string stepRoute, string sourceKey)
    {
        Assert.Equal(stepRoute, ParticipantChecklistBuilder.LinkForSourceKey(sourceKey));
    }
}
