using CommunityHub.Core.Participants;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §410 — which tasks belong on the §400 "Your tasks &amp; deadlines" wizard step.
///
/// <para><b>The bug this closes.</b> §173e mirrors every wizard STEP with a matching task, and §400
/// built its step from the participant checklist unfiltered. For a role whose tasks are ALL mirrors
/// — an attendee has exactly <c>party-form</c> and <c>masterclass-form</c> — the step listed the
/// person's own wizard steps back at them, one screen after they had filled them in. Operator
/// 2026-07-27: <i>"for any roles, that does NOT have any tasks outside the get started, you must
/// remove the step 3 'your tasks &amp; deadlines'"</i>.</para>
///
/// <para><b>Why the rule is data-driven and not a role list.</b> His own conclusion — "only
/// speakers, organizers, sponsors" — is right today and would go stale the first time a volunteer
/// or media deadline is added. Keying on the task DATA makes the step appear exactly when the
/// person has something outside the wizard, whoever they are.</para>
/// </summary>
public sealed class OutsideWizardTasksTests
{
    [Theory]
    // Every wizard-step mirror seen on production.
    [InlineData("party-form:42")]
    [InlineData("masterclass-form:42")]
    [InlineData("hotel-form:42")]
    [InlineData("dinner-form:42")]
    [InlineData("lunch-form:42")]
    [InlineData("swag-form:42")]
    [InlineData("volunteer-form:42")]
    [InlineData("accept:42")]
    [InlineData("profile:42")]
    [InlineData("signal:42")]
    [InlineData("availability:42")]
    [InlineData("speaker-details:42")]
    public void A_wizard_step_MIRROR_never_appears_on_the_deadlines_step(string sourceKey)
    {
        Assert.False(OutsideWizardTasks.IsOutsideWizard(sourceKey));
    }

    [Fact]
    public void The_TRAVEL_task_is_a_mirror_despite_its_odd_key()
    {
        // travel is a real wizard step (§399 gates it by country), but its key is
        // "travel:submit-ticket-invoice" rather than "travel-form" — easy to miss, and missing it
        // would put a wizard step back on the deadlines list for every non-Danish speaker.
        Assert.False(OutsideWizardTasks.IsOutsideWizard("travel:submit-ticket-invoice:42"));
    }

    [Theory]
    // The genuinely-outside families, all dated, from production.
    [InlineData("speakerdl:42:upload-final-presentation")]
    [InlineData("speakerdl:42:help-to-promote-your-sessions")]
    [InlineData("sponsor:acme:booth-materials")]
    [InlineData("woo:12345")]
    public void A_deadline_OUTSIDE_the_wizard_does_appear(string sourceKey)
    {
        Assert.True(OutsideWizardTasks.IsOutsideWizard(sourceKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_UNKEYED_task_is_treated_as_outside_the_wizard(string? sourceKey)
    {
        // Deliberate direction. An unkeyed task is almost certainly one an organizer created by
        // hand for one person, and HIDING a real obligation is a worse failure than showing one row
        // too many: somebody can act on a duplicate, but cannot act on something they never saw.
        Assert.True(OutsideWizardTasks.IsOutsideWizard(sourceKey));
    }

    [Fact]
    public void Matching_is_on_the_whole_key_HEAD_not_a_loose_prefix()
    {
        // "profile" is wizard-owned; a hypothetical "profile-review" deadline must NOT be swallowed
        // by it. Splitting on ':' and comparing the whole head is what prevents that.
        Assert.False(OutsideWizardTasks.IsOutsideWizard("profile:42"));
        Assert.True(OutsideWizardTasks.IsOutsideWizard("profile-review:42"));
    }

    [Fact]
    public void An_ATTENDEES_whole_task_set_yields_NOTHING_outside_the_wizard()
    {
        // The operator's exact case: this is why the step must disappear for them entirely.
        var attendeeTasks = new[] { "party-form:81", "masterclass-form:81" };
        Assert.DoesNotContain(attendeeTasks, OutsideWizardTasks.IsOutsideWizard);
    }

    [Fact]
    public void A_SPEAKERS_task_set_yields_something_outside_the_wizard()
    {
        var speakerTasks = new[]
        {
            "hotel-form:73", "dinner-form:73", "accept:73",       // mirrors
            "speakerdl:73:upload-final-presentation",             // a real deadline
        };
        Assert.Contains(speakerTasks, OutsideWizardTasks.IsOutsideWizard);
    }
}
