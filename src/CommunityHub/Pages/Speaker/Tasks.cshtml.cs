using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// Speaker "My tasks" (operator 2026-06-23) — the speaker deadline tasks rendered
/// in the sponsor-style collapse/expand list. Tasks are the config-seeded
/// <c>speakerdl:</c> <see cref="ParticipantTask"/> rows (one set per speaker);
/// completion is per-speaker via <see cref="SpeakerMilestoneService.ToggleAsync"/>.
/// Action + upload buttons are authored as [label](url) markdown in each task's
/// description (rendered by TaskTextLinkifier), so this page needs no per-task
/// upload widget.
/// </summary>
[Authorize]
public class TasksModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerDeadlineSeeder _seeder;
    private readonly FormTaskReconciler _formTaskReconciler;
    private readonly SpeakerMilestoneService _milestones;

    public TasksModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SpeakerDeadlineSeeder seeder,
        FormTaskReconciler formTaskReconciler,
        SpeakerMilestoneService milestones)
    {
        _db = db;
        _participant = participant;
        _seeder = seeder;
        _formTaskReconciler = formTaskReconciler;
        _milestones = milestones;
    }

    public bool NotSpeaker { get; private set; }
    public List<ParticipantTask> Tasks { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) { NotSpeaker = true; return Page(); }

        // Ensure this speaker's deadline tasks exist (idempotent), then load them.
        try { await _seeder.SeedAsync(me.EventId, ct); } catch { /* tolerate seed hiccup */ }

        // Mark any OPEN logistics deadline tasks Done where the speaker has already
        // submitted the matching form data (hotel/dinner/lunch/swag/travel), so the
        // list reflects real completion. Idempotent + no-op when nothing changed.
        await _formTaskReconciler.ReconcileAsync(me.EventId, me.ParticipantId, ct);

        Tasks = await _db.Tasks
            .Where(t => t.EventId == me.EventId
                        && t.AssignedParticipantId == me.ParticipantId
                        && t.SourceKey != null
                        && t.SourceKey.StartsWith(SpeakerMilestoneService.SourceKeyPrefix))
            .OrderBy(t => t.State)
            .ThenBy(t => t.DueDate)
            .ThenBy(t => t.Title)
            .ToListAsync(ct);
        return Page();
    }

    /// <summary>Toggle one of the speaker's own tasks done/open (per-speaker scoped).</summary>
    public async Task<IActionResult> OnPostToggleAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        await _milestones.ToggleAsync(me.EventId, me.ParticipantId, taskId, ct);
        return RedirectToPage();
    }
}
