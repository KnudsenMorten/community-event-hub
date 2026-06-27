using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// The render + edit model for the Accept step (REQUIREMENTS §148/§119). It is shared by
/// the standalone <c>/Forms/Accept</c> page AND the inline wizard step, and is the model the
/// <c>_AcceptFields</c> partial binds to. The single EDITABLE field (<see cref="Accept"/>) is
/// the only one model binding fills; the DISPLAY fields are <see cref="BindNeverAttribute"/>
/// and are populated by <see cref="AcceptFormService"/> (load + save), never from the POST.
/// </summary>
public sealed class AcceptFormModel
{
    // ----- editable (bound from the POST) --------------------------------
    /// <summary>The required "I accept" checkbox value.</summary>
    public bool Accept { get; set; }

    // ----- display-only (set by the service; never bound) -----------------
    /// <summary>True once a <see cref="ParticipantPolicyAcceptance"/> row exists (who/when).</summary>
    [BindNever] public bool AlreadyAccepted { get; set; }

    /// <summary>When the acceptance was recorded (read-only confirmation).</summary>
    [BindNever] public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>The login email that recorded the acceptance (read-only confirmation).</summary>
    [BindNever] public string? AcceptedByEmail { get; set; }

    /// <summary>Flash message — success or, with <see cref="IsError"/>, the validation error.</summary>
    [BindNever] public string? Message { get; set; }

    /// <summary>True when <see cref="Message"/> is a validation error (vs a success notice).</summary>
    [BindNever] public bool IsError { get; set; }

    /// <summary>The Code of Conduct URL rendered in the partial.</summary>
    [BindNever] public string CodeOfConductUrl { get; set; } = AcceptFormService.CodeOfConductUrl;

    /// <summary>The Privacy Policy URL rendered in the partial.</summary>
    [BindNever] public string PrivacyPolicyUrl { get; set; } = AcceptFormService.PrivacyPolicyUrl;
}

/// <summary>
/// Shared submit-service for the Accept form (REQUIREMENTS §148/§119). It encapsulates the
/// form's ENTIRE behavior — the OnGet load, the OnPost validate/persist, and the side-effect
/// of recording a durable, auditable acceptance — so that BOTH the standalone
/// <c>/Forms/Accept</c> page and the inline <see cref="AcceptStepHandler"/> call the exact
/// same logic and stay identical. Implements the <see cref="IWizardFormService"/> marker so
/// it self-registers by concrete type.
///
/// <para>This step is the ALWAYS-LAST step across every role's wizard (SpeakerWizardService /
/// RoleWizardService both append "accept" last) and applies to ALL roles, so relevance is
/// unconditional. Completion = a <see cref="ParticipantPolicyAcceptance"/> row exists for
/// (eventId, participantId). The persist is idempotent: a second submit when a row already
/// exists is a no-op (the original acceptance who/when/URLs are never overwritten).</para>
/// </summary>
public sealed class AcceptFormService : IWizardFormService
{
    public const string CodeOfConductUrl = "https://expertslive.dk/code-of-conduct/";
    public const string PrivacyPolicyUrl = "https://expertslive.dk/privacy-policy/";

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public AcceptFormService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Relevance gate (REQUIREMENTS §148): the policy-acceptance step applies to ALL roles
    /// and is the always-last step in every wizard, so it is unconditionally relevant.
    /// </summary>
    public Task<bool> IsRelevantAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct) =>
        Task.FromResult(true);

    /// <summary>Completion detection (REQUIREMENTS §148) — a <see cref="ParticipantPolicyAcceptance"/>
    /// row exists. Mirrors SpeakerWizardService / RoleWizardService.</summary>
    public Task<bool> IsDoneAsync(int eventId, int participantId, CancellationToken ct) =>
        _db.ParticipantPolicyAcceptances.AnyAsync(
            a => a.EventId == eventId && a.ParticipantId == participantId, ct);

    /// <summary>
    /// Load the form's current state — the SAME load the standalone page's OnGet used:
    /// hydrate the read-only "already accepted" confirmation (who/when) from any existing row.
    /// </summary>
    public async Task<AcceptFormModel> LoadAsync(int eventId, int participantId, CancellationToken ct)
    {
        var model = new AcceptFormModel();
        await PopulateExistingAsync(model, eventId, participantId, ct);
        return model;
    }

    /// <summary>
    /// Validate + persist (REQUIREMENTS §148) — the SAME logic the standalone page's OnPost ran.
    /// The required checkbox must be ticked: otherwise a field error is written into
    /// <paramref name="modelState"/> (=> <see cref="WizardStepOutcome.Invalid"/>) and the flash
    /// message is set so the standalone page renders identically. On success the acceptance is
    /// persisted ONCE (idempotent if a row already exists) and the step advances.
    /// </summary>
    public async Task<WizardStepOutcome> SaveAsync(
        AcceptFormModel model, int eventId, int participantId, string email,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        // ALREADY ACCEPTED first (idempotent): once a row exists the partial shows the
        // read-only "Accepted on …" confirmation with NO checkbox — so there is nothing to
        // tick. Without this short-circuit, re-finishing an already-accepted step bound
        // Accept=false and looped on "Please tick to continue" with no checkbox to tick.
        var existing = await _db.ParticipantPolicyAcceptances.FirstOrDefaultAsync(
            a => a.EventId == eventId && a.ParticipantId == participantId, ct);
        if (existing is not null)
        {
            await PopulateExistingAsync(model, eventId, participantId, ct);
            model.Message = "Your acceptance is already on file.";
            return WizardStepOutcome.Advance;
        }

        // New acceptance: the checkbox MUST be ticked.
        if (!model.Accept)
        {
            // Re-render with the field error + flash; nothing is persisted.
            await PopulateExistingAsync(model, eventId, participantId, ct);
            const string err = "Please tick \"I accept\" to continue.";
            model.IsError = true;
            model.Message = err;
            modelState.AddModelError(nameof(model.Accept), err);
            return WizardStepOutcome.Invalid;
        }

        // Record the acceptance once (who/when/URLs are durable + never overwritten later).
        _db.ParticipantPolicyAcceptances.Add(new ParticipantPolicyAcceptance
        {
            EventId = eventId,
            ParticipantId = participantId,
            AcceptedByEmail = email,
            AcceptedAt = _clock.GetUtcNow(),
            CodeOfConductUrl = CodeOfConductUrl,
            PrivacyPolicyUrl = PrivacyPolicyUrl,
        });
        await _db.SaveChangesAsync(ct);

        await PopulateExistingAsync(model, eventId, participantId, ct);
        model.Message = "Thank you — your acceptance has been recorded.";
        return WizardStepOutcome.Advance;
    }

    /// <summary>Hydrate the read-only acceptance confirmation from the persisted row (if any).
    /// Identical to the standalone page's old private <c>LoadAsync</c>.</summary>
    private async Task PopulateExistingAsync(AcceptFormModel model, int eventId, int participantId, CancellationToken ct)
    {
        var row = await _db.ParticipantPolicyAcceptances.AsNoTracking().FirstOrDefaultAsync(
            a => a.EventId == eventId && a.ParticipantId == participantId, ct);
        if (row is not null)
        {
            model.AlreadyAccepted = true;
            model.AcceptedAt = row.AcceptedAt;
            model.AcceptedByEmail = row.AcceptedByEmail;
            model.Accept = true;
        }
    }
}
