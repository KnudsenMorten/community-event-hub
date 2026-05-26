using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// The sponsor area (CONTEXT.md section 9 / 11g). Shows the signed-in sponsor
/// contact their company's onboarding tasks and lets them mark a task
/// complete or reopen it.
///
/// Tasks are scoped to the contact's company: a sponsor task carries the
/// WooCommerce order's company id (_cm_company_id) and the contact's
/// Participant row carries the same SponsorCompanyId. A sponsor therefore
/// sees and edits ONLY their own company's tasks - not other sponsors'.
/// Any contact of a company may complete/reopen any of that company's tasks
/// (the WooCommerce pull creates them unassigned - they are company-level).
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;

    public IndexModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    public List<ParticipantTask> SponsorTasks { get; private set; } = new();
    public string? Message { get; private set; }

    /// <summary>True when this sponsor has no company id set (see the view).</summary>
    public bool NoCompanyLink { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        await LoadAsync(me, ct);
        return Page();
    }

    /// <summary>Mark one of this company's sponsor tasks done, or reopen it.</summary>
    public async Task<IActionResult> OnPostToggleAsync(
        int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null)
        {
            NoCompanyLink = true;
            await LoadAsync(me, ct);
            return Page();
        }

        // The task must be in this edition, sponsor-sourced (woo:), and belong
        // to THIS contact's company. This guard means a sponsor can never
        // alter a non-sponsor task or another company's task, even by
        // tampering with the posted taskId.
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.Id == taskId
                 && t.EventId == me.EventId
                 && t.SourceKey != null
                 && t.SourceKey.StartsWith("woo:")
                 && t.SponsorCompanyId == companyId,
            ct);

        if (task is not null)
        {
            if (task.State == TaskState.Done)
            {
                task.State = TaskState.Open;
                task.CompletedAt = null;
                Message = "Task reopened.";
            }
            else
            {
                task.State = TaskState.Done;
                task.CompletedAt = _clock.GetUtcNow();
                Message = "Task marked complete.";
            }
            await _db.SaveChangesAsync(ct);
        }

        await LoadAsync(me, ct);
        return Page();
    }

    private async Task LoadAsync(CurrentParticipant me, CancellationToken ct)
    {
        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null)
        {
            // The contact has no company link yet - show nothing rather than
            // every sponsor's tasks. The view explains how to fix it.
            NoCompanyLink = true;
            SponsorTasks = new List<ParticipantTask>();
            return;
        }

        SponsorTasks = await _db.Tasks
            .Where(t => t.EventId == me.EventId
                        && t.SourceKey != null
                        && t.SourceKey.StartsWith("woo:")
                        && t.SponsorCompanyId == companyId)
            .OrderBy(t => t.State)
            .ThenBy(t => t.DueDate)
            .ToListAsync(ct);
    }

    /// <summary>The signed-in sponsor's company id, or null if not set.</summary>
    private async Task<string?> GetCompanyIdAsync(
        int participantId, CancellationToken ct) =>
        await _db.Participants
            .Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
}
