using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// The render + edit model for the Signal step (REQUIREMENTS §148, §109). Shared by the
/// standalone <c>/Forms/Signal</c> page AND the inline wizard step, and is the model the
/// <c>_SignalFields</c> partial binds to. The step has NO posted form fields — joining is
/// external — so every property is display-only (<see cref="BindNeverAttribute"/>),
/// populated by <see cref="SignalFormService"/>; completion is a MANUAL mark-done.
/// </summary>
public sealed class SignalFormModel
{
    /// <summary>The participant's role (drives which Signal links resolve).</summary>
    [BindNever] public ParticipantRole Role { get; set; }

    /// <summary>True when the role has no Signal groups (GetForRole null) — the step is not relevant.</summary>
    [BindNever] public bool OutOfScope { get; set; }

    /// <summary>The role-appropriate chat + broadcast links, or null when out of scope.</summary>
    [BindNever] public SignalGroupLinks? Links { get; set; }

    /// <summary>True once the per-participant <c>signal:</c> task is marked Done.</summary>
    [BindNever] public bool Done { get; set; }
}

/// <summary>
/// Shared submit-service for the Signal step (REQUIREMENTS §148, §109). It encapsulates the
/// form's ENTIRE behavior — the OnGet load (resolve role-appropriate Signal chat + broadcast
/// links from <see cref="SignalGroupsProvider.GetForRole"/> and ensure the per-participant
/// <c>signal:</c> task exists, idempotent), the standalone page's manual on/off toggle, and the
/// wizard's Save&amp;next mark-done — so that BOTH the standalone <c>/Forms/Signal</c> page and
/// the inline <see cref="SignalStepHandler"/> call the exact same logic and stay identical.
/// Out-of-scope roles (<see cref="SignalGroupsProvider.GetForRole"/> null) are NotRelevant.
/// Completion detection: a <see cref="ParticipantTask"/> with SourceKey
/// <see cref="WizardStepTasks.Signal(int)"/> in state Done (mirrors the wizard services).
/// Implements the <see cref="IWizardFormService"/> marker so it self-registers by concrete type.
/// </summary>
public sealed class SignalFormService : IWizardFormService
{
    private readonly CommunityHubDbContext _db;
    private readonly SignalGroupsProvider _signal;
    private readonly TimeProvider _clock;

    public SignalFormService(
        CommunityHubDbContext db,
        SignalGroupsProvider signal,
        TimeProvider clock)
    {
        _db = db;
        _signal = signal;
        _clock = clock;
    }

    /// <summary>Relevance gate (REQUIREMENTS §148) — the role has Signal groups in scope.</summary>
    public bool IsRelevant(ParticipantRole role) => _signal.GetForRole(role) is not null;

    /// <summary>Completion detection (REQUIREMENTS §148) — the <c>signal:</c> task is Done.
    /// Mirrors SpeakerWizardService / RoleWizardService.</summary>
    public Task<bool> IsDoneAsync(int eventId, int participantId, CancellationToken ct) =>
        _db.Tasks.AnyAsync(
            t => t.EventId == eventId && t.AssignedParticipantId == participantId
                 && t.SourceKey == WizardStepTasks.Signal(participantId) && t.State == TaskState.Done, ct);

    /// <summary>
    /// Load the step's current state — the SAME load the standalone page's OnGet used: resolve
    /// the role-appropriate links, and when in scope ensure the <c>signal:</c> task exists
    /// (idempotent) so it also surfaces in the task list + reminders, then read its done state.
    /// </summary>
    public async Task<SignalFormModel> LoadAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        var model = new SignalFormModel { Role = role };

        var links = _signal.GetForRole(role);
        if (links is null) { model.OutOfScope = true; return model; }
        model.Links = links;

        var task = await EnsureTaskAsync(eventId, participantId, links, ct);
        model.Done = task.State == TaskState.Done;
        return model;
    }

    /// <summary>
    /// The standalone page's manual on/off toggle (mark done / not done) — unchanged behavior.
    /// Out-of-scope roles are a no-op. Returns the refreshed model.
    /// </summary>
    public async Task<SignalFormModel> ToggleAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        var model = new SignalFormModel { Role = role };

        var links = _signal.GetForRole(role);
        if (links is null) { model.OutOfScope = true; return model; }
        model.Links = links;

        var task = await EnsureTaskAsync(eventId, participantId, links, ct);
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
        model.Done = task.State == TaskState.Done;
        return model;
    }

    /// <summary>
    /// The wizard's Save&amp;next (REQUIREMENTS §148): the Save acts as MARK-DONE — ensure the
    /// task exists (idempotent) and mark it Done. There are no posted fields to validate, so this
    /// always <see cref="WizardStepOutcome.Advance"/>s when in scope; an out-of-scope role
    /// (<see cref="SignalGroupsProvider.GetForRole"/> null) returns <see cref="WizardStepOutcome.NotRelevant"/>.
    /// The relevance gate is re-derived server-side here, so a crafted POST can never bypass it.
    /// </summary>
    public async Task<WizardStepOutcome> SaveAsync(
        SignalFormModel model, int eventId, int participantId, ParticipantRole role,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        model.Role = role;

        var links = _signal.GetForRole(role);
        if (links is null) { model.OutOfScope = true; return WizardStepOutcome.NotRelevant; }
        model.Links = links;

        var task = await EnsureTaskAsync(eventId, participantId, links, ct);
        if (task.State != TaskState.Done)
        {
            task.State = TaskState.Done;
            task.CompletedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }
        model.Done = true;
        return WizardStepOutcome.Advance;
    }

    /// <summary>Ensure the "Join Signal groups" task exists (idempotent), per-participant scoped.</summary>
    private async Task<ParticipantTask> EnsureTaskAsync(
        int eventId, int participantId, SignalGroupLinks links, CancellationToken ct)
    {
        var sourceKey = WizardStepTasks.Signal(participantId);
        // §162: embed the actual signal.group join URLs in the description — the task row's
        // linkifier turns them into clickable join buttons, so the person can join straight from
        // the task (they were missing the links before).
        var description = BuildSignalTaskDescription(links);
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (task is null)
        {
            task = new ParticipantTask
            {
                EventId = eventId,
                AssignedParticipantId = participantId,
                Title = "Join Signal groups",
                Description = description,
                State = TaskState.Open,
                IsMandatory = false,
                SourceKey = sourceKey,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync(ct);
        }
        else if (task.Description != description)
        {
            // Refresh existing tasks so they pick up the join links (idempotent).
            task.Description = description;
            await _db.SaveChangesAsync(ct);
        }
        return task;
    }

    private static string BuildSignalTaskDescription(SignalGroupLinks links)
    {
        var sb = new System.Text.StringBuilder(
            "Join the ELDK27 Signal group(s) below, then mark this done.");
        if (links.HasChat) sb.Append("\n\nChat group: ").Append(links.ChatUrl);
        if (links.HasBroadcast) sb.Append("\n\nBroadcast group: ").Append(links.BroadcastUrl);
        return sb.ToString();
    }
}
