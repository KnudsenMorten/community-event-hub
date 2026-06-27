using System.ComponentModel.DataAnnotations;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// The render + edit model for the Calendar-email step (REQUIREMENTS §148). It is shared by
/// the standalone <c>/Forms/CalendarEmail</c> page AND the inline wizard step, and is the model
/// the <c>_CalendarEmailFields</c> partial binds to. Only the EDITABLE field
/// (<see cref="CalendarEmail"/>) is model-bound — and validated as an email ONLY when provided;
/// the DISPLAY fields are <see cref="BindNeverAttribute"/> and are populated by
/// <see cref="CalendarEmailFormService"/> (load + save), never from the POST.
/// </summary>
public sealed class CalendarEmailFormModel
{
    // ----- editable (bound from the POST) --------------------------------
    /// <summary>The optional alternate calendar address. Validated as an email only when provided.</summary>
    [StringLength(320)]
    [EmailAddress(ErrorMessage = "Enter a valid email address, or leave blank to use your primary email.")]
    public string? CalendarEmail { get; set; }

    // ----- display-only (set by the service; never bound) -----------------
    [BindNever] public ParticipantRole Role { get; set; }
    [BindNever] public string PrimaryEmail { get; set; } = string.Empty;
    [BindNever] public string? Message { get; set; }
    [BindNever] public bool Saved { get; set; }
}

/// <summary>
/// Shared submit-service for the Calendar-email form (REQUIREMENTS §148). It encapsulates the
/// form's ENTIRE behavior — the OnGet load and the OnPost validate/persist — so that BOTH the
/// standalone <c>/Forms/CalendarEmail</c> page and the inline <see cref="CalendarEmailStepHandler"/>
/// call the exact same logic and stay byte-for-byte identical. Implements the
/// <see cref="IWizardFormService"/> marker so it self-registers by concrete type.
///
/// <para>This is the FIRST speaker step and is OPTIONAL: an alternate calendar address used only
/// for CALENDAR invites / notifications. Left blank, calendar mail falls back to the speaker's
/// primary address. Saving the step ALWAYS stamps <see cref="SpeakerProfile.CalendarEmailSetAt"/>
/// — even when the field is left blank or skipped — so the optional step still counts as DONE.
/// Completion detection: <see cref="SpeakerProfile.CalendarEmailSetAt"/> != null. Speaker-only;
/// any other role is NotRelevant.</para>
/// </summary>
public sealed class CalendarEmailFormService : IWizardFormService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public CalendarEmailFormService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Relevance gate (REQUIREMENTS §148): this step is speaker-only — any other role is NotRelevant.</summary>
    public static bool IsRelevant(ParticipantRole role) => role == ParticipantRole.Speaker;

    /// <summary>Completion detection (REQUIREMENTS §148) — the speaker has SAVED the step
    /// (<see cref="SpeakerProfile.CalendarEmailSetAt"/> stamped), even if the field was left blank.</summary>
    public Task<bool> IsDoneAsync(int eventId, int participantId, CancellationToken ct) =>
        _db.SpeakerProfiles.AnyAsync(
            sp => sp.EventId == eventId && sp.ParticipantId == participantId
                  && sp.CalendarEmailSetAt != null, ct);

    /// <summary>
    /// Load the form's current state — the SAME load the standalone page's OnGet used: hydrate the
    /// optional override from any existing <see cref="SpeakerProfile"/> and surface the primary email
    /// (the fallback shown as the field placeholder). Returns a fully-populated model.
    /// </summary>
    public async Task<CalendarEmailFormModel> LoadAsync(
        int eventId, int participantId, ParticipantRole role, string primaryEmail, CancellationToken ct)
    {
        var model = new CalendarEmailFormModel { Role = role, PrimaryEmail = primaryEmail };

        var profile = await Load(eventId, participantId, ct);
        if (profile is not null)
        {
            model.CalendarEmail = profile.CalendarEmail;
            model.Saved = profile.CalendarEmailSetAt != null;
        }
        return model;
    }

    /// <summary>
    /// Validate + persist (REQUIREMENTS §148) — the SAME logic the standalone page's OnPost ran.
    /// The alternate address is validated as an email only when provided (DataAnnotations on the
    /// model, already applied during binding); on a binding/validation error this returns
    /// <see cref="WizardStepOutcome.Invalid"/> so the SAME step re-renders with the posted value +
    /// messages. On success the <see cref="SpeakerProfile"/> override is upserted (blank => null
    /// fallback to the primary) and <see cref="SpeakerProfile.CalendarEmailSetAt"/> is ALWAYS
    /// stamped so this OPTIONAL step counts as Done even when left blank (a "Skip that still saves").
    /// Relevance is re-checked server-side, so a crafted POST can never bypass it.
    /// </summary>
    public async Task<WizardStepOutcome> SaveAsync(
        CalendarEmailFormModel model, int eventId, int participantId, string email, string fullName,
        ParticipantRole role, ModelStateDictionary modelState, CancellationToken ct)
    {
        model.Role = role;
        model.PrimaryEmail = email;

        // Relevance is re-checked server-side (never trusted from the post).
        if (!IsRelevant(role)) return WizardStepOutcome.NotRelevant;

        // Field-level validation (the email/length DataAnnotations ran during binding).
        if (!modelState.IsValid) return WizardStepOutcome.Invalid;

        var now = _clock.GetUtcNow();
        var profile = await Load(eventId, participantId, ct);
        if (profile is null)
        {
            profile = new SpeakerProfile
            {
                EventId = eventId,
                ParticipantId = participantId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.SpeakerProfiles.Add(profile);
        }
        else
        {
            profile.UpdatedAt = now;
        }

        // Normalise: blank => no override (calendar mail falls back to the primary).
        profile.CalendarEmail = string.IsNullOrWhiteSpace(model.CalendarEmail) ? null : model.CalendarEmail.Trim();
        // ALWAYS stamp the "speaker acted" marker so this OPTIONAL step counts as done even when
        // left blank / skipped (mirrors the details step's BioLastEditedBySpeakerAt marker).
        profile.CalendarEmailSetAt = now;

        await _db.SaveChangesAsync(ct);

        model.CalendarEmail = profile.CalendarEmail;
        model.Saved = true;
        model.Message = string.IsNullOrWhiteSpace(profile.CalendarEmail)
            ? "Saved. Calendar invites and notifications will go to your primary email."
            : "Saved. Calendar invites and notifications will go to " + profile.CalendarEmail + ".";
        return WizardStepOutcome.Advance;
    }

    private Task<SpeakerProfile?> Load(int eventId, int participantId, CancellationToken ct) =>
        _db.SpeakerProfiles.FirstOrDefaultAsync(
            sp => sp.EventId == eventId && sp.ParticipantId == participantId, ct);
}
