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
    private readonly SpeakerReadinessService _readiness;

    public TasksModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SpeakerDeadlineSeeder seeder,
        FormTaskReconciler formTaskReconciler,
        SpeakerMilestoneService milestones,
        SpeakerReadinessService readiness)
    {
        _db = db;
        _participant = participant;
        _seeder = seeder;
        _formTaskReconciler = formTaskReconciler;
        _milestones = milestones;
        _readiness = readiness;
    }

    public bool NotSpeaker { get; private set; }
    public List<ParticipantTask> Tasks { get; private set; } = new();

    /// <summary>
    /// §138: the speaker's "am I ready?" readiness rollup (score + the what's-missing
    /// list), surfaced at the TOP of My Tasks now that the standalone /Speaker/Readiness
    /// nav item is removed. A pure read-only AGGREGATE of existing data via
    /// <see cref="SpeakerReadinessService"/>; null when the speaker has no
    /// <see cref="SpeakerProfile"/> yet (the view then omits the rollup).
    /// </summary>
    public SpeakerReadiness? Readiness { get; private set; }

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

        // §138: build the readiness rollup AFTER seeding + reconcile, so the score
        // reflects already-submitted form data on first visit. Read-only; null when the
        // speaker has no SpeakerProfile (the view omits the rollup card).
        Readiness = await _readiness.BuildForSpeakerAsync(me.EventId, me.ParticipantId, ct);
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
