using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// The render + edit model for the Profile step (REQUIREMENTS §148). It is shared by the
/// standalone <c>/Profile</c> page AND the inline wizard step, and is the model the
/// <c>_ProfileFields</c> partial binds to. The EDITABLE fields (top of the class) are the
/// only ones model binding fills; the DISPLAY fields are <see cref="BindNeverAttribute"/>
/// and are populated by <see cref="ProfileFormService"/> (load + save), never from the POST.
/// </summary>
public sealed class ProfileFormModel
{
    // ----- editable (bound from the POST) --------------------------------
    public string? FullName { get; set; }
    public string? Phone { get; set; }

    /// <summary>Optional ALTERNATE login email (§26d) — sign in with this OR the primary.</summary>
    public string? AlternateEmail { get; set; }

    // ----- display-only (set by the service; never bound) -----------------
    /// <summary>Read-only sign-in identity (set by the service, never bound).</summary>
    [BindNever] public string Email { get; set; } = string.Empty;

    /// <summary>Read-only organizer-set role (drives the speaker-details notice).</summary>
    [BindNever] public ParticipantRole Role { get; set; }

    /// <summary>True for speaker roles — drives the speaker-details notice in the partial.</summary>
    [BindNever] public bool IsSpeaker { get; set; }

    /// <summary>Optional extra address — preserved as-is (the Profile UI no longer edits it,
    /// operator 2026-06-25); kept on the model for parity with the standalone page.</summary>
    [BindNever] public string? SecondaryEmail { get; set; }

    /// <summary>User-facing banner for the standalone page (success or error text).</summary>
    [BindNever] public string? Message { get; set; }

    /// <summary>True when the last save succeeded (drives the info/error banner style).</summary>
    [BindNever] public bool Saved { get; set; }

    /// <summary>Optional secondary notice line (parity with the original page; currently unused).</summary>
    [BindNever] public string? SpeakerNotice { get; set; }
}

/// <summary>
/// Shared submit-service for the Profile form (REQUIREMENTS §148). It encapsulates the
/// form's ENTIRE behavior — the OnGet load, the OnPost validate/persist, and the
/// speaker-only speaking-days auto-derivation side-effect — so that BOTH the standalone
/// <c>/Profile</c> page and the inline <see cref="ProfileStepHandler"/> call the exact same
/// logic and stay identical. Implements the <see cref="IWizardFormService"/> marker so it
/// self-registers by concrete type.
///
/// <para>Every write is scoped to the signed-in participant's OWN row — a participant can
/// never load or save another person's profile. Speaker details (bio/photo/accreditation/…)
/// live on <c>/Speaker/Details</c>; the only speaker-specific work here is refreshing the
/// organizer-only speaking-days flags, gated to the speaker role and therefore never
/// exercised by the generic role wizard (speakers use the 'details' step instead).</para>
/// </summary>
public sealed class ProfileFormService : IWizardFormService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public ProfileFormService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    private static bool IsSpeakerRole(ParticipantRole r) => r is ParticipantRole.Speaker;

    /// <summary>
    /// Profile applies to every role the generic wizard serves — there is no
    /// entitlement/relevance gate ("tell us how to reach you" is universal). Kept for
    /// symmetry with the other step services and the handler's NotRelevant path.
    /// </summary>
    public Task<bool> IsRelevantAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
        => Task.FromResult(true);

    /// <summary>Completion detection (REQUIREMENTS §148) — the participant has filled in a
    /// phone number. Mirrors RoleWizardService's 'profile' done rule.</summary>
    public Task<bool> IsDoneAsync(int eventId, int participantId, CancellationToken ct) =>
        _db.Participants.AnyAsync(
            p => p.Id == participantId && p.EventId == eventId
                 && p.Phone != null && p.Phone != "", ct);

    /// <summary>
    /// Load the form's current state — the SAME load the standalone page's OnGet used:
    /// hydrate the editable basics + read-only identity facts from the participant's own row.
    /// Returns <c>null</c> when the signed-in participant has no row (the standalone page
    /// then redirects to Login, mirroring the original behavior).
    /// </summary>
    public async Task<ProfileFormModel?> LoadAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        var p = await _db.Participants
            .Where(p => p.Id == participantId && p.EventId == eventId)
            .Select(p => new { p.FullName, p.Phone, p.Email, p.Role, p.SecondaryEmail, p.AlternateEmail })
            .FirstOrDefaultAsync(ct);
        if (p is null) return null;

        return new ProfileFormModel
        {
            FullName = p.FullName,
            Phone = p.Phone,
            AlternateEmail = p.AlternateEmail,
            SecondaryEmail = p.SecondaryEmail,
            Email = p.Email,
            Role = p.Role,
            IsSpeaker = IsSpeakerRole(p.Role),
        };
    }

    /// <summary>
    /// Validate + persist + run the speaker side-effect (REQUIREMENTS §148) — the SAME logic
    /// the standalone page's OnPost ran. Field errors are written into <paramref name="modelState"/>
    /// AND surfaced on <see cref="ProfileFormModel.Message"/> (so the standalone banner is
    /// unchanged) and the call returns <see cref="WizardStepOutcome.Invalid"/>. On success the
    /// own row is updated (FullName / Phone / AlternateEmail), the speaker speaking-days flags
    /// are refreshed for speaker roles, and the call returns <see cref="WizardStepOutcome.Advance"/>.
    /// The read-only identity facts are always re-populated for the redisplay.
    /// </summary>
    public async Task<WizardStepOutcome> SaveAsync(
        ProfileFormModel model, int eventId, int participantId,
        ParticipantRole role, ModelStateDictionary modelState, CancellationToken ct)
    {
        // Always scope the write to the signed-in participant's OWN row.
        var p = await _db.Participants.FirstOrDefaultAsync(
            x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null)
        {
            // Extreme edge (signed in but no row): surface a generic error rather than throw.
            model.Message = "We couldn't find your profile. Please sign in again.";
            model.Saved = false;
            return WizardStepOutcome.Invalid;
        }

        // Read-only facts for the redisplay regardless of validation outcome.
        model.Email = p.Email;
        model.Role = p.Role;
        model.IsSpeaker = IsSpeakerRole(p.Role);
        model.SecondaryEmail = p.SecondaryEmail;

        var trimmedName = (model.FullName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            model.FullName = p.FullName; // restore so the field is not blanked
            model.Phone = p.Phone;
            return Fail(model, modelState, nameof(model.FullName), "Please enter your name.");
        }
        if (trimmedName.Length > 200)
            return Fail(model, modelState, nameof(model.FullName), "That name is too long (max 200 characters).");

        var trimmedPhone = string.IsNullOrWhiteSpace(model.Phone) ? null : model.Phone.Trim();
        if (trimmedPhone is { Length: > 40 })
            return Fail(model, modelState, nameof(model.Phone), "That phone number is too long (max 40 characters).");

        // The secondary CC email field was removed from the Profile UI as redundant
        // (operator 2026-06-25). Any existing stored value is left untouched (not wiped).

        // Alternate LOGIN email (§26d): normalized; must be a valid shape, must not equal
        // your own primary, and must not collide with anyone else's primary/alt in the edition.
        var altEmail = string.IsNullOrWhiteSpace(model.AlternateEmail)
            ? null : PinLoginService.NormalizeEmail(model.AlternateEmail);
        if (altEmail is not null)
        {
            var atA = altEmail.IndexOf('@');
            if (altEmail.Length > 320 || atA <= 0 || atA >= altEmail.Length - 1 || altEmail.Contains(' '))
            {
                model.FullName = p.FullName; model.Phone = p.Phone;
                return Fail(model, modelState, nameof(model.AlternateEmail),
                    "Please enter a valid alternate email (or leave it blank).");
            }
            if (altEmail == p.Email)
            {
                model.FullName = p.FullName; model.Phone = p.Phone;
                return Fail(model, modelState, nameof(model.AlternateEmail),
                    "Your alternate email can't be the same as your sign-in email.");
            }
            var clash = await _db.Participants.AnyAsync(
                x => x.EventId == eventId && x.Id != p.Id
                     && (x.Email == altEmail || x.AlternateEmail == altEmail), ct);
            if (clash)
            {
                model.FullName = p.FullName; model.Phone = p.Phone;
                return Fail(model, modelState, nameof(model.AlternateEmail),
                    "That alternate email is already used by another participant in this event.");
            }
        }

        p.FullName = trimmedName;
        p.Phone = trimmedPhone;
        p.AlternateEmail = altEmail;

        // --- Speaker speaking-days auto-derivation (operator 2026-06-21): speaking days are
        //     NOT speaker-set — they're auto-derived from the speaker's accepted sessions
        //     (master class => pre-day; session => main day) for organizer hotel/food
        //     planning. Speaker DETAIL fields moved to /Speaker/Details (operator 2026-06-24).
        //     Gated to the speaker role, so the generic role wizard never exercises this. ---
        if (model.IsSpeaker)
        {
            var now = _clock.GetUtcNow();
            var sp = await _db.SpeakerProfiles.FirstOrDefaultAsync(
                x => x.EventId == eventId && x.ParticipantId == participantId, ct);
            if (sp is null)
            {
                sp = new SpeakerProfile { EventId = eventId, ParticipantId = participantId, CreatedAt = now };
                _db.SpeakerProfiles.Add(sp);
            }
            else { sp.UpdatedAt = now; }

            var types = await _db.Sessions
                .Where(s => s.EventId == eventId && !s.IsServiceSession
                            && s.SessionSpeakers.Any(ss => ss.ParticipantId == participantId))
                .Select(s => s.Type).ToListAsync(ct);
            sp.SpeakingPreDay = types.Any(t => t == SessionType.MasterClass);
            sp.SpeakingMainDay = types.Any(t => t != SessionType.MasterClass);
        }

        await _db.SaveChangesAsync(ct);

        model.FullName = p.FullName;
        model.Phone = p.Phone;
        model.SecondaryEmail = p.SecondaryEmail;
        model.Saved = true;
        // The header greeting reads the name from the session cookie, which is stamped at
        // login — so a changed name shows everywhere after the next sign-in. The profile page
        // itself always shows the saved value.
        model.Message = "Your profile has been saved.";
        return WizardStepOutcome.Advance;
    }

    /// <summary>Record a field error on both ModelState (wizard validation summary + per-field
    /// spans) and the model's banner (standalone page), and return <see cref="WizardStepOutcome.Invalid"/>.</summary>
    private static WizardStepOutcome Fail(
        ProfileFormModel model, ModelStateDictionary modelState, string field, string message)
    {
        modelState.AddModelError(field, message);
        model.Message = message;
        model.Saved = false;
        return WizardStepOutcome.Invalid;
    }
}
