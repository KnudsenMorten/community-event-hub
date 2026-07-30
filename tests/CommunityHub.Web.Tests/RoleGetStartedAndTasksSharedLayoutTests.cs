using System;
using System.IO;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §214: every role's Get-Started wizard AND task-management view render through the SAME
/// shared layout components speakers use — the §161 <c>_WizardStepper</c> for the wizard and
/// the shared <c>_TaskListPanel</c> for the task list — so the look is consistent across roles
/// (only the steps/tasks CONTENT differs). This static test pins that each role's page invokes
/// the shared partial, catching any role that re-introduces a divergent layout.
/// </summary>
public sealed class RoleGetStartedAndTasksSharedLayoutTests
{
    private static string PagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate src/CommunityHub/Pages from " + AppContext.BaseDirectory);
    }

    // Each role's Get-Started wizard page → must use the shared _WizardStepper (§161).
    public static TheoryData<string> GetStartedPages() => new()
    {
        Path.Combine("Forms", "GetStarted.cshtml"),    // Organizer / Media / EventPartner / Volunteer / Attendee
        Path.Combine("Forms", "SpeakerWizard.cshtml"), // Speaker (§28)
        Path.Combine("Sponsor", "GetStarted.cshtml"),  // Sponsor (§32)
    };

    // Each role's task-management view → must use the shared _TaskListPanel.
    public static TheoryData<string> TaskPages() => new()
    {
        Path.Combine("Tasks", "Index.cshtml"),  // Organizer / Media / EventPartner / Volunteer
        Path.Combine("Speaker", "Tasks.cshtml"),
        Path.Combine("Sponsor", "Tasks.cshtml"),
    };

    [Theory]
    [MemberData(nameof(GetStartedPages))]
    public void Every_role_get_started_page_uses_the_shared_wizard_stepper(string relativePath)
    {
        var file = Path.Combine(PagesDir(), relativePath);
        Assert.True(File.Exists(file), $"§214: expected Get-Started page missing: {file}");
        var html = File.ReadAllText(file);
        Assert.Contains("_WizardStepper", html);
    }

    [Theory]
    [MemberData(nameof(TaskPages))]
    public void Every_role_task_page_uses_the_shared_task_list_panel(string relativePath)
    {
        var file = Path.Combine(PagesDir(), relativePath);
        Assert.True(File.Exists(file), $"§214: expected task page missing: {file}");
        var html = File.ReadAllText(file);
        Assert.Contains("_TaskListPanel", html);
    }
}
