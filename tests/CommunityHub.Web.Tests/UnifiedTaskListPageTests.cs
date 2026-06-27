using System;
using System.IO;
using System.Linq;
using CommunityHub.Core.Domain;
using CommunityHub.Pages.Shared;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §147: the three task pages (volunteer/media/eventpartner <c>/Tasks</c>,
/// <c>/Speaker/Tasks</c>, <c>/Sponsor/Tasks</c>) are UNIFIED onto the SAME shared
/// rendering — the <c>_TaskListPanel</c> partial: a completion-% header, pending tasks
/// at the top, completed tasks collapsed at the bottom, each task rendered ONCE.
///
/// Two guards:
///   1. Static Razor-source checks (same approach as <see cref="BreadcrumbWiringTests"/>)
///      prove all three pages render the shared panel with the right per-role row partial,
///      that the panel orders pending-before-completed and carries the % header, and that
///      the /Tasks page no longer ALSO renders the shared _ChecklistCard (the old duplicate).
///   2. Direct unit tests of <see cref="TaskListPanelView"/> prove the pending/completed
///      split, the ordering, and the completion maths.
/// FAKE data only.
/// </summary>
public sealed class UnifiedTaskListPageTests
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

    private static string Page(params string[] parts) =>
        File.ReadAllText(Path.Combine(PagesDir(), Path.Combine(parts)));

    // ---- 1. shared-rendering wiring ---------------------------------------

    [Fact]
    public void All_three_task_pages_render_the_shared_TaskListPanel()
    {
        Assert.Contains("Shared/_TaskListPanel", Page("Tasks", "Index.cshtml"));
        Assert.Contains("Shared/_TaskListPanel", Page("Speaker", "Tasks.cshtml"));
        Assert.Contains("Shared/_TaskListPanel", Page("Sponsor", "Tasks.cshtml"));
    }

    [Fact]
    public void Each_page_passes_its_own_row_partial_to_the_shared_panel()
    {
        Assert.Contains("RowPartialName = \"_ParticipantTaskRow\"", Page("Tasks", "Index.cshtml"));
        Assert.Contains("RowPartialName = \"_SpeakerTaskRow\"", Page("Speaker", "Tasks.cshtml"));
        Assert.Contains("RowPartialName = \"_SponsorTaskRow\"", Page("Sponsor", "Tasks.cshtml"));
    }

    [Fact]
    public void Tasks_page_no_longer_renders_the_checklist_card_so_tasks_show_once()
    {
        var page = Page("Tasks", "Index.cshtml");
        // The DUPLICATE source: the page used to render BOTH _ChecklistCard AND a task
        // list. The checklist card render is gone, leaving the single shared panel.
        Assert.DoesNotContain("_ChecklistCard", page);
        Assert.Contains("Shared/_TaskListPanel", page);
    }

    [Fact]
    public void Pages_do_not_render_the_row_partial_directly_only_via_the_shared_panel()
    {
        // A row must be rendered ONCE — through the shared panel, never also by a direct
        // per-page PartialAsync(row) loop (that would double-render).
        Assert.DoesNotContain("PartialAsync(\"_SpeakerTaskRow\"", Page("Speaker", "Tasks.cshtml"));
        Assert.DoesNotContain("PartialAsync(\"_SponsorTaskRow\"", Page("Sponsor", "Tasks.cshtml"));
    }

    [Fact]
    public void Shared_panel_orders_pending_before_completed_and_collapses_completed()
    {
        var panel = Page("Shared", "_TaskListPanel.cshtml");

        var pendingIdx   = panel.IndexOf("Model.Pending", StringComparison.Ordinal);
        var completedIdx = panel.IndexOf("Model.Completed", StringComparison.Ordinal);
        Assert.True(pendingIdx >= 0 && completedIdx > pendingIdx,
            "The pending list must render before the completed list.");

        // Completed sits inside a collapsed <details> at the bottom.
        Assert.Contains("<details", panel);
    }

    [Fact]
    public void Shared_panel_renders_a_completion_percent_header_with_a_progress_bar()
    {
        var panel = Page("Shared", "_TaskListPanel.cshtml");

        Assert.Contains("Model.Completion", panel);
        Assert.Contains("TaskList.Summary", panel);     // "X of N done"
        Assert.Contains("role=\"progressbar\"", panel); // a11y progress bar
        Assert.Contains(".Percent%", panel);            // the big percent number

        // The % header renders BEFORE the task lists so it leads the panel.
        var headerIdx  = panel.IndexOf("TaskList.Summary", StringComparison.Ordinal);
        var pendingIdx = panel.IndexOf("Model.Pending", StringComparison.Ordinal);
        Assert.True(headerIdx >= 0 && pendingIdx > headerIdx,
            "The completion % header must render above the task lists.");
    }

    [Fact]
    public void Task_completion_header_is_labelled_distinct_from_the_readiness_deliverables_rollups()
    {
        // §147 coherence: the new % must NOT compete with the speaker readiness (§144)
        // / sponsor deliverables (§145) rollup. The panel labels its own measure
        // "Task checklist"; the rollups keep their own headings.
        Assert.Contains("TaskList.ProgressHeading", Page("Shared", "_TaskListPanel.cshtml"));
        Assert.Contains("Am I ready?", Page("Speaker", "Tasks.cshtml"));
        Assert.Contains("Your deliverables", Page("Sponsor", "Tasks.cshtml"));
    }

    // ---- 2. TaskListPanelView behaviour -----------------------------------

    private static ParticipantTask T(
        string title, TaskState state, DateOnly? due = null, DateTimeOffset? completed = null) =>
        new() { Title = title, State = state, DueDate = due, CompletedAt = completed };

    [Fact]
    public void Pending_excludes_done_and_completed_contains_only_done()
    {
        var view = new TaskListPanelView
        {
            RowPartialName = "_X",
            Tasks = new[]
            {
                T("a", TaskState.Open),
                T("b", TaskState.Done),
                T("c", TaskState.InProgress),
            },
        };

        Assert.Equal(new[] { "a", "c" }, view.Pending.Select(t => t.Title).OrderBy(s => s));
        Assert.Equal(new[] { "b" }, view.Completed.Select(t => t.Title));
    }

    [Fact]
    public void Pending_is_ordered_overdue_first_then_by_due_date()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var view = new TaskListPanelView
        {
            RowPartialName = "_X",
            Tasks = new[]
            {
                T("future",  TaskState.Open, due: today.AddDays(10)),
                T("overdue", TaskState.Open, due: today.AddDays(-3)),
                T("soon",    TaskState.Open, due: today.AddDays(2)),
            },
        };

        // Overdue leads; the rest by ascending due date.
        Assert.Equal(new[] { "overdue", "soon", "future" }, view.Pending.Select(t => t.Title));
    }

    [Fact]
    public void Completion_is_computed_from_the_tasks()
    {
        var view = new TaskListPanelView
        {
            RowPartialName = "_X",
            Tasks = new[]
            {
                T("a", TaskState.Done),
                T("b", TaskState.Done),
                T("c", TaskState.Open),
                T("d", TaskState.Open),
            },
        };

        Assert.Equal(2, view.Completion.Done);
        Assert.Equal(4, view.Completion.Total);
        Assert.Equal(50, view.Completion.Percent);
    }
}
