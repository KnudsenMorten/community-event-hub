using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer.SponsorAdmin;

/// <summary>
/// Sponsor tasks management. The sponsor task CATALOG (canonical list of
/// tasks every sponsor has to complete) currently lives in the
/// SponsorTaskExpander integration -- expanded per sponsor at task-list
/// hydration time. This page is the UI surface for editing that catalog:
/// add a new task, remove obsolete entries, and shift the per-task
/// deadline when a milestone moves.
///
/// SCAFFOLD: the create / delete / deadline-change handlers are wired
/// but write to a stub list. Real backend lives in a follow-up commit
/// that promotes the catalog from SponsorTaskExpander's config-driven
/// model to a DbSet&lt;SponsorTaskTemplate&gt;. Doing that here would
/// require a DB migration which we can't validate against the production
/// DB schema mid-conversation.
/// </summary>
[Authorize]
public class TasksModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;

    public TasksModel(ICurrentParticipantAccessor participant)
    {
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>
    /// Stand-in row type used by the page table. Will be replaced by
    /// the actual <c>SponsorTaskTemplate</c> domain class once the
    /// DbSet migration lands.
    /// </summary>
    public record TaskRow(string Id, string Title, DateOnly? Deadline, string Notes);

    public List<TaskRow> Tasks { get; private set; } = new();

    public IActionResult OnGet()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        // SCAFFOLD seed data so the table renders. Replace with a query
        // against DbSet<SponsorTaskTemplate> once that's introduced.
        Tasks = new List<TaskRow>
        {
            new("logo",         "Upload company logo (PNG, 512x512)",   new DateOnly(2026, 11,  1), "Used on the sponsor page + badge"),
            new("attendees",    "Submit booth attendee list",            new DateOnly(2027,  1, 15), "Needed for badge printing"),
            new("dinner",       "Pick speaker dinner attendees",         new DateOnly(2027,  1, 28), "1 seat per Diamond, +1 per Platinum"),
            new("swag",         "Confirm swag inventory + ship date",    new DateOnly(2027,  1, 20), "Optional for Silver tier"),
            new("session-meta", "Approve sponsored session title + bio", new DateOnly(2026, 12, 10), "Only Diamond / Platinum"),
        };
        return Page();
    }

    /// <summary>SCAFFOLD: stub for create. No DB write yet.</summary>
    public IActionResult OnPostCreate(string title, DateOnly? deadline, string? notes)
    {
        // TODO: write to DbSet<SponsorTaskTemplate>; until then, redirect
        // back to the page with a notice.
        TempData["Notice"] = "Create scaffold: would have created task '" + title + "'. Backend pending.";
        return RedirectToPage();
    }

    /// <summary>SCAFFOLD: stub for delete. No DB write yet.</summary>
    public IActionResult OnPostDelete(string id)
    {
        TempData["Notice"] = "Delete scaffold: would have removed task id '" + id + "'. Backend pending.";
        return RedirectToPage();
    }

    /// <summary>SCAFFOLD: stub for deadline change. No DB write yet.</summary>
    public IActionResult OnPostChangeDeadline(string id, DateOnly deadline)
    {
        TempData["Notice"] = $"ChangeDeadline scaffold: would have set task id '{id}' deadline to {deadline:yyyy-MM-dd}. Backend pending.";
        return RedirectToPage();
    }
}
