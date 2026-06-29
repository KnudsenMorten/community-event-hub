using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// The render + edit model for the Party sign-up step (REQUIREMENTS §164). Shared by the
/// inline wizard step (<see cref="PartyStepHandler"/>) and bound by the <c>_PartyFields</c>
/// partial. The EDITABLE fields (top) are the only ones model binding fills; the DISPLAY
/// fields are <see cref="BindNeverAttribute"/> and are populated by <see cref="PartyFormService"/>.
/// </summary>
public sealed class PartyFormModel
{
    // ----- editable (bound from the POST) --------------------------------
    /// <summary>The YES/NO opt-in. Defaults to YES (most people come).</summary>
    public bool Attending { get; set; } = true;

    /// <summary>Sponsor head count ("how many from your company") — bound only for sponsors.</summary>
    public int? HeadCount { get; set; }

    // ----- display-only (set by the service; never bound) -----------------
    [BindNever] public ParticipantRole Role { get; set; }

    /// <summary>True for a sponsor — the head-count input is shown only then.</summary>
    [BindNever] public bool IsSponsor { get; set; }

    /// <summary>The "16:00–18:30" window text, data-driven from <see cref="PartyRsvpService"/>.</summary>
    [BindNever] public string TimeWindow { get; set; } = string.Empty;

    /// <summary>The party date line (e.g. "Tuesday, 9 February 2027"), or empty when no party.</summary>
    [BindNever] public string DateLine { get; set; } = string.Empty;

    /// <summary>True once the participant has saved an RSVP (Yes or No).</summary>
    [BindNever] public bool Done { get; set; }

    /// <summary>True when there is no active party window (the step then renders a neutral note).</summary>
    [BindNever] public bool NoParty { get; set; }
}

/// <summary>
/// Shared submit-service for the Party sign-up step (REQUIREMENTS §164, §148). It wraps the
/// SAME <see cref="PartyRsvpService"/> the standalone <c>/Party</c> page uses — so a signed-in
/// participant's inline wizard RSVP and their /Party RSVP are identical — and reconciles the
/// per-participant party task (mark Done on save) via <see cref="FormTaskReconciler"/>.
/// Implements the <see cref="IWizardFormService"/> marker so it self-registers by concrete type.
/// </summary>
public sealed class PartyFormService : IWizardFormService
{
    private readonly PartyRsvpService _party;
    private readonly FormTaskReconciler _reconciler;

    public PartyFormService(PartyRsvpService party, FormTaskReconciler reconciler)
    {
        _party = party;
        _reconciler = reconciler;
    }

    /// <summary>Completion detection (§148) — the participant has a saved Party RSVP row.
    /// Mirrors SpeakerWizardService / RoleWizardService.</summary>
    public async Task<bool> IsDoneAsync(int eventId, int participantId, CancellationToken ct) =>
        await _party.GetForParticipantAsync(eventId, participantId, ct) is not null;

    /// <summary>Load the step's current state: the party window text + any prior answer.</summary>
    public async Task<PartyFormModel> LoadAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        var model = new PartyFormModel { Role = role, IsSponsor = role == ParticipantRole.Sponsor };
        await PopulateWindowAsync(model, ct);

        var existing = await _party.GetForParticipantAsync(eventId, participantId, ct);
        if (existing is not null)
        {
            model.Attending = existing.Attending;
            model.HeadCount = existing.HeadCount;
            model.Done = true;
        }
        return model;
    }

    /// <summary>
    /// Validate + persist (§148) — upsert the RSVP via the shared service (name + email come
    /// from the signed-in participant, never the post) and reconcile the party task to Done.
    /// </summary>
    public async Task<WizardStepOutcome> SaveAsync(
        PartyFormModel model, int eventId, int participantId, string email, string fullName,
        ParticipantRole role, ModelStateDictionary modelState, CancellationToken ct)
    {
        model.Role = role;
        model.IsSponsor = role == ParticipantRole.Sponsor;
        await PopulateWindowAsync(model, ct);

        if (model.NoParty)
        {
            // Nothing to RSVP to — skip it rather than error.
            return WizardStepOutcome.NotRelevant;
        }

        var headCount = model.IsSponsor ? model.HeadCount : null;
        var result = await _party.SubmitAsync(
            fullName, email, model.Attending, ipHash: null, headCount, participantId, ct);
        if (!result.Ok)
        {
            modelState.AddModelError(string.Empty, result.Error ?? "Could not save your RSVP.");
            return WizardStepOutcome.Invalid;
        }

        // Close (or reopen) the party task to match the new answer.
        await _reconciler.ReconcileAsync(eventId, participantId, ct);
        model.Done = true;
        return WizardStepOutcome.Advance;
    }

    private async Task PopulateWindowAsync(PartyFormModel model, CancellationToken ct)
    {
        var p = await _party.GetActivePartyAsync(ct);
        if (p is null) { model.NoParty = true; return; }
        model.TimeWindow = $"{p.StartHour:00}:{p.StartMinute:00}–{p.EndHour:00}:{p.EndMinute:00}";
        model.DateLine = p.Date.ToDateTime(new TimeOnly(0, 0)).ToString("dddd, d MMMM yyyy");
    }
}
