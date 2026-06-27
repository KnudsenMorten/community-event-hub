using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Resources;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// The render + edit model for the Swag step (REQUIREMENTS §148). It is shared by the
/// standalone <c>/Forms/Swag</c> page AND the inline wizard step, and is the model the
/// <c>_SwagFields</c> partial binds to. The EDITABLE fields (top of the class) are the
/// only ones model binding fills; the DISPLAY fields are <see cref="BindNeverAttribute"/>
/// and are populated by <see cref="SwagFormService"/> (load + save), never from the POST.
/// </summary>
public sealed class SwagFormModel
{
    // ----- editable (bound from the POST) --------------------------------
    /// <summary>A polo size from <see cref="SwagOptions.PoloSizes"/> OR the "no polo" sentinel.
    /// Required — a blank choice is rejected (Swag.ErrPickPolo).</summary>
    public string? PoloChoice { get; set; }

    /// <summary>A jacket size from <see cref="SwagOptions.JacketSizes"/> OR the "no jacket" sentinel.
    /// Not surfaced in the current form markup, so it stays null unless posted.</summary>
    public string? JacketChoice { get; set; }

    public bool WantsGift { get; set; } = true;
    public bool WantsCredlyBadge { get; set; } = true;
    public string? Notes { get; set; }

    // ----- display-only (set by the service; never bound) -----------------
    [BindNever] public ParticipantRole Role { get; set; }
    [BindNever] public string FullName { get; set; } = string.Empty;
    [BindNever] public string Email { get; set; } = string.Empty;
    [BindNever] public string? Message { get; set; }

    /// <summary>REQUIREMENTS §51 — when these swag preferences were last saved (UpdatedAt); null = never.</summary>
    [BindNever] public DateTimeOffset? LastSavedAt { get; set; }
}

/// <summary>
/// Shared submit-service for the Swag form (REQUIREMENTS §148). It encapsulates the form's
/// ENTIRE behavior — the OnGet load, the OnPost validate/persist, and ALL side-effects
/// (required polo choice, SwagPreference upsert from the polo/jacket catalogs, the
/// auto-task ensure+done) — so that BOTH the standalone <c>/Forms/Swag</c> page and the
/// inline <see cref="SwagStepHandler"/> call the exact same logic and stay identical.
/// Implements the <see cref="IWizardFormService"/> marker so it self-registers by concrete type.
/// </summary>
public sealed class SwagFormService : IWizardFormService
{
    /// <summary>SourceKey prefix for the "complete the swag form" auto-task — <c>swag-form:{pid}</c>.</summary>
    public const string SwagTaskKey = "swag-form";

    /// <summary>Roles that historically receive swag (Volunteer / Speaker / Organizer).</summary>
    public static readonly ParticipantRole[] EligibleRoles =
    {
        ParticipantRole.Volunteer,
        ParticipantRole.Speaker,
        ParticipantRole.Organizer,
    };

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IStringLocalizer<SharedResource> _loc;

    public SwagFormService(
        CommunityHubDbContext db,
        TimeProvider clock,
        IStringLocalizer<SharedResource> loc)
    {
        _db = db;
        _clock = clock;
        _loc = loc;
    }

    /// <summary>
    /// FEATURE B eligibility (REQUIREMENTS §148 relevance gate): the swag/polo form is gated by
    /// ENTITLEMENT (<see cref="OrderItem.Swag"/> OR <see cref="OrderItem.Polo"/> — the form covers
    /// both), so a sponsor-self-funded speaker (entitled to neither) is excluded. A non-speaker role
    /// keeps its historical access (Volunteer / Organizer) even if a future rule change dropped its
    /// entitlement, so access is never silently removed; speakers are gated purely by entitlement.
    /// </summary>
    public async Task<bool> IsRelevantAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        var entitled = await FormEntitlementGate.IsEntitledToAnyAsync(
            _db, eventId, participantId, ct, OrderItem.Swag, OrderItem.Polo);
        var historicalNonSpeaker = role != ParticipantRole.Speaker && EligibleRoles.Contains(role);
        return entitled || historicalNonSpeaker;
    }

    /// <summary>Completion detection (REQUIREMENTS §148) — a <see cref="SwagPreference"/> row exists.
    /// Mirrors SpeakerWizardService / RoleWizardService.</summary>
    public Task<bool> IsDoneAsync(int eventId, int participantId, CancellationToken ct) =>
        _db.SwagPreferences.AnyAsync(s => s.EventId == eventId && s.ParticipantId == participantId, ct);

    /// <summary>
    /// Load the form's current state — the SAME load the standalone page's OnGet used: ensure the
    /// auto-task exists, then hydrate from any existing preference. Returns a fully-populated model.
    /// </summary>
    public async Task<SwagFormModel> LoadAsync(
        int eventId, int participantId, ParticipantRole role, string fullName, string email, CancellationToken ct)
    {
        var model = new SwagFormModel { Role = role, FullName = fullName, Email = email };

        await EnsureSwagTaskExistsAsync(eventId, participantId, ct);

        var existing = await _db.SwagPreferences.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.ParticipantId == participantId, ct);
        if (existing is not null)
        {
            model.PoloChoice = existing.WantsPolo ? existing.PoloSize : SwagOptions.NoPoloLabel;
            model.JacketChoice = existing.WantsJacket ? existing.JacketSize : SwagOptions.NoJacketLabel;
            model.WantsGift = existing.WantsGift;
            model.WantsCredlyBadge = existing.WantsCredlyBadge;
            model.Notes = existing.Notes;
            model.LastSavedAt = existing.UpdatedAt;
        }
        return model;
    }

    /// <summary>
    /// Validate + persist + run all side-effects (REQUIREMENTS §148) — the SAME logic the standalone
    /// page's OnPost ran. Requires an explicit polo choice (a size OR the "no polo" sentinel); a blank
    /// choice adds a field error (=> <see cref="WizardStepOutcome.Invalid"/>) and persists nothing. On
    /// success the preference is upserted (polo/jacket resolved from the shared catalogs) and the
    /// auto-task is marked done. Relevance is RE-DERIVED from the DB here, so a crafted POST can never
    /// bypass the gate.
    /// </summary>
    public async Task<WizardStepOutcome> SaveAsync(
        SwagFormModel model, int eventId, int participantId, string fullName, string email,
        ParticipantRole role, ModelStateDictionary modelState, CancellationToken ct)
    {
        model.Role = role;
        model.FullName = fullName;
        model.Email = email;

        // Relevance is re-checked server-side (never trusted from the post).
        if (!await IsRelevantAsync(eventId, participantId, role, ct))
            return WizardStepOutcome.NotRelevant;

        // Field-level validation (REQUIREMENTS §21 shared validation pattern):
        // require an explicit polo choice — a size OR the "no polo" sentinel.
        if (string.IsNullOrWhiteSpace(model.PoloChoice))
        {
            modelState.AddModelError(nameof(model.PoloChoice), _loc["Swag.ErrPickPolo"]);
        }
        if (!modelState.IsValid)
        {
            // Re-render with field errors; nothing is persisted.
            return WizardStepOutcome.Invalid;
        }

        var pref = await _db.SwagPreferences.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.ParticipantId == participantId, ct);

        if (pref is null)
        {
            pref = new SwagPreference
            {
                EventId = eventId,
                ParticipantId = participantId,
                CreatedAt = _clock.GetUtcNow(),
                UpdatedAt = _clock.GetUtcNow(),
            };
            _db.SwagPreferences.Add(pref);
        }
        else
        {
            pref.UpdatedAt = _clock.GetUtcNow();
        }

        if (string.IsNullOrWhiteSpace(model.PoloChoice) || model.PoloChoice == SwagOptions.NoPoloLabel)
        {
            pref.WantsPolo = false;
            pref.PoloSize = null;
        }
        else
        {
            pref.WantsPolo = true;
            pref.PoloSize = SwagOptions.PoloSizes.Contains(model.PoloChoice) ? model.PoloChoice : null;
        }

        if (string.IsNullOrWhiteSpace(model.JacketChoice) || model.JacketChoice == SwagOptions.NoJacketLabel)
        {
            pref.WantsJacket = false;
            pref.JacketSize = null;
        }
        else
        {
            pref.WantsJacket = true;
            pref.JacketSize = SwagOptions.JacketSizes.Contains(model.JacketChoice) ? model.JacketChoice : null;
        }

        pref.WantsGift = model.WantsGift;
        pref.WantsCredlyBadge = model.WantsCredlyBadge;
        pref.Notes = model.Notes;

        await _db.SaveChangesAsync(ct);
        await MarkSwagTaskDoneAsync(eventId, participantId, ct);
        model.LastSavedAt = pref.UpdatedAt;
        model.Message = "Your swag preferences have been saved.";
        return WizardStepOutcome.Advance;
    }

    // ----- auto-task: "Complete the Swag preferences form" ----------------
    private async Task EnsureSwagTaskExistsAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{SwagTaskKey}:{participantId}";
        var exists = await _db.Tasks.AnyAsync(
            t => t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (exists) return;

        var due = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => (DateOnly?)e.StartDate.AddDays(-21))
            .FirstOrDefaultAsync(ct);

        _db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId,
            AssignedParticipantId = participantId,
            Title = "Complete the Swag preferences form",
            Description = "Pick your polo size (or 'I wear my own clothes'). " +
                          "Saving the form marks this task Done.",
            DueDate = due,
            State = TaskState.Open,
            SourceKey = sourceKey,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkSwagTaskDoneAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{SwagTaskKey}:{participantId}";
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (task is null || task.State == TaskState.Done) return;
        task.State = TaskState.Done;
        task.CompletedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }
}
