using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// The render model for the Promote step (REQUIREMENTS §148 / §116). It is shared by the
/// standalone <c>/Speaker/Promote</c> page AND the inline wizard step, and is the model the
/// <c>_PromoteFields</c> partial binds to. The Promote step carries NO editable inputs — it
/// is a MANUAL "mark done" step — so every member here is display-only, populated by
/// <see cref="PromoteFormService"/>.
/// </summary>
public sealed class PromoteFormModel
{
    /// <summary>The hashtags every speaker promo should carry (§115).</summary>
    public const string Hashtags = "#ELDK27 #ExpertsLiveDK";

    /// <summary>The public event site the prefilled LinkedIn share points at.</summary>
    public const string ShareTargetUrl = "https://eldk27.expertslive.dk";

    /// <summary>True when the participant is not a speaker — the step does not apply.</summary>
    public bool NotSpeaker { get; set; }

    /// <summary>True once the per-speaker <c>promote:</c> task is marked Done.</summary>
    public bool Done { get; set; }

    /// <summary>A prefilled LinkedIn "share to feed" URL for the event site.</summary>
    public string LinkedInShareUrl =>
        "https://www.linkedin.com/sharing/share-offsite/?url=" + Uri.EscapeDataString(ShareTargetUrl);
}

/// <summary>
/// Shared submit-service for the speaker "Help to promote your session(s)" form
/// (REQUIREMENTS §148 / §116). It encapsulates the form's ENTIRE behavior — the OnGet load
/// (ensure the per-speaker auto-task + read its Done state + the prefilled LinkedIn share)
/// and the manual completion side-effects (EnsureTask idempotent + mark/toggle the
/// <c>promote:{pid}</c> task) — so that BOTH the standalone <c>/Speaker/Promote</c> page and
/// the inline <see cref="PromoteStepHandler"/> call the exact same logic and stay identical.
/// Implements the <see cref="IWizardFormService"/> marker so it self-registers by concrete type.
///
/// <para>Completion detection (REQUIREMENTS §148): a <see cref="ParticipantTask"/> whose
/// <see cref="ParticipantTask.SourceKey"/> equals <c>WizardStepTasks.Promote(pid)</c> in
/// state <see cref="TaskState.Done"/> — mirrors SpeakerWizardService.</para>
/// </summary>
public sealed class PromoteFormService : IWizardFormService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public PromoteFormService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Relevance gate (REQUIREMENTS §148) — speaker-only; non-speakers => NotRelevant.</summary>
    public static bool IsRelevant(ParticipantRole role) => role == ParticipantRole.Speaker;

    /// <summary>Completion detection (REQUIREMENTS §148) — the <c>promote:{pid}</c> task is Done.
    /// Mirrors SpeakerWizardService.</summary>
    public Task<bool> IsDoneAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = WizardStepTasks.Promote(participantId);
        return _db.Tasks.AnyAsync(
            t => t.EventId == eventId && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey && t.State == TaskState.Done, ct);
    }

    /// <summary>
    /// Load the form's current state — the SAME load the standalone page's OnGet used:
    /// non-speakers get a "not for you" model with no task touched; speakers get the auto-task
    /// ensured (idempotent) and the read-only Done + prefilled-share state surfaced.
    /// </summary>
    public async Task<PromoteFormModel> LoadAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        var model = new PromoteFormModel();
        if (!IsRelevant(role)) { model.NotSpeaker = true; return model; }

        var task = await EnsureTaskAsync(eventId, participantId, ct);
        model.Done = task.State == TaskState.Done;
        return model;
    }

    /// <summary>
    /// MANUAL completion for the inline wizard's Save&amp;next (REQUIREMENTS §148): EnsureTask
    /// (idempotent) + mark the <c>promote:{pid}</c> task Done. Non-speaker => NotRelevant
    /// (re-checked server-side so a crafted POST can never bypass the gate). There are no
    /// editable fields to validate, so this never returns <see cref="WizardStepOutcome.Invalid"/>.
    /// </summary>
    public async Task<WizardStepOutcome> SaveAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        if (!IsRelevant(role)) return WizardStepOutcome.NotRelevant;

        var task = await EnsureTaskAsync(eventId, participantId, ct);
        if (task.State != TaskState.Done)
        {
            task.State = TaskState.Done;
            task.CompletedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }
        return WizardStepOutcome.Advance;
    }

    /// <summary>
    /// Toggle the manual "promote your session(s)" completion — the SAME logic the standalone
    /// page's OnPostToggle ran (Done ⇄ Open). Returns the post-toggle render model. Non-speakers
    /// get a NotSpeaker model with no task touched.
    /// </summary>
    public async Task<PromoteFormModel> ToggleAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        if (!IsRelevant(role)) return new PromoteFormModel { NotSpeaker = true };

        var task = await EnsureTaskAsync(eventId, participantId, ct);
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
        return new PromoteFormModel { Done = task.State == TaskState.Done };
    }

    /// <summary>Ensure the per-speaker "Help to promote your session(s)" auto-task exists (§116), idempotent.</summary>
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
                Description = "Share your session(s) on LinkedIn with " + PromoteFormModel.Hashtags + ", then mark this done.",
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
