using CommunityHub.Forms;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1312 — operator 2026-09-27: "the code you added in sponsors get started with missing mandatory tasks marked as red in
/// menu, can we implement that for the other roles". The personal wizards mark optional steps done themselves; only the
/// optional-but-saved calendar step is exempt from the red marking.
/// </summary>
public sealed class GetStartedMandatoryStepsTests
{
    [Fact]
    public void An_open_mandatory_step_marks_the_menu()
    {
        Assert.True(GetStartedSteps.AnyMandatoryOpen(new[] { ("details", true), ("accept", false) }));
        Assert.False(GetStartedSteps.AnyMandatoryOpen(new[] { ("details", true), ("accept", true) }));
    }

    [Fact]
    public void The_optional_calendar_email_step_never_marks_anything()
    {
        Assert.False(GetStartedSteps.IsMandatory("calendar"));
        Assert.False(GetStartedSteps.AnyMandatoryOpen(new[] { ("calendar", false), ("party", true) }));
        Assert.True(GetStartedSteps.IsMandatory("party"));
    }
}
