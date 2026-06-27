using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// Speaker "Help to promote your session(s)" Get-Started step (REQUIREMENTS §116).
/// Drives LinkedIn promotion of the speaker's session(s): the wired publish path
/// lives on <see cref="GraphicsModel"/> (Speaker/Graphics →
/// <c>SpeakerLinkedInPublishService</c>, §52) using the pulled session graphics, and
/// this step surfaces a prefilled LinkedIn share + the #ELDK27 #ExpertsLiveDK tags
/// (§115). Completion is a MANUAL mark-done (promoting is external/optional), tracked
/// on a per-speaker <c>promote:</c> <see cref="ParticipantTask"/>. Speaker-only.
/// </summary>
[Authorize]
public class PromoteModel : PageModel
{
    /// <summary>The hashtags every speaker promo should carry (§115).</summary>
    public const string Hashtags = "#ELDK27 #ExpertsLiveDK";

    /// <summary>The public event site the prefilled LinkedIn share points at.</summary>
    public const string ShareTargetUrl = "https://eldk27.expertslive.dk";

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;

    public PromoteModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    public bool NotSpeaker { get; private set; }
    public bool Done { get; private set; }

    /// <summary>A prefilled LinkedIn "share to feed" URL for the event site.</summary>
    public string LinkedInShareUrl =>
        "https://www.linkedin.com/sharing/share-offsite/?url=" + Uri.EscapeDataString(ShareTargetUrl);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) { NotSpeaker = true; return Page(); }

        var task = await EnsureTaskAsync(me.EventId, me.ParticipantId, ct);
        Done = task.State == TaskState.Done;
        return Page();
    }

    /// <summary>Toggle the manual "promote your session(s)" completion.</summary>
    public async Task<IActionResult> OnPostToggleAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) { NotSpeaker = true; return Page(); }

        var task = await EnsureTaskAsync(me.EventId, me.ParticipantId, ct);
        if (task.State == TaskState.Done)
        {
            task.State = TaskState.Open;
            task.CompletedAt = null;
        }
        else
        {
            task.State = TaskState.Done;
            task.CompletedAt = _clock.GetUtcNow();
        }
        await _db.SaveChangesAsync(ct);
        return RedirectToPage();
    }

    private async Task<ParticipantTask> EnsureTaskAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = WizardStepTasks.Promote(participantId);
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (task is null)
        {
            task = new ParticipantTask
            {
                EventId = eventId,
                AssignedParticipantId = participantId,
                Title = "Help to promote your session(s)",
                Description = "Share your session(s) on LinkedIn with " + Hashtags + ", then mark this done.",
                State = TaskState.Open,
                IsMandatory = false,
                SourceKey = sourceKey,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync(ct);
        }
        return task;
    }
}
