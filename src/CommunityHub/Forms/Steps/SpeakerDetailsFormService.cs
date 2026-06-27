using System.ComponentModel.DataAnnotations;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// The render + edit model for the Speaker Details step (REQUIREMENTS §148, §26c). It is
/// shared by the standalone <c>/Speaker/Details</c> page AND the inline wizard step, and is
/// the model the <c>_DetailsFields</c> partial binds to. The EDITABLE fields (top of the
/// class) are the only ones model binding fills — they carry the SAME StringLength/email-style
/// validation the standalone page's <c>[BindProperty]</c> set did; the DISPLAY fields are
/// <see cref="BindNeverAttribute"/> and are populated by <see cref="SpeakerDetailsFormService"/>
/// (load + save), never from the POST.
/// </summary>
public sealed class SpeakerDetailsFormModel
{
    // ----- editable (bound from the POST) — mirrors DetailsModel's [BindProperty] set -----
    [StringLength(200)] public string? FirstName { get; set; }
    [StringLength(200)] public string? LastName { get; set; }

    [StringLength(500, ErrorMessage = "Tagline is too long (max 500).")] public string? Tagline { get; set; }
    [StringLength(4000, ErrorMessage = "Bio is too long (max 4000).")] public string? Biography { get; set; }
    [StringLength(500)] public string? Blog { get; set; }
    [StringLength(500)] public string? LinkedIn { get; set; }
    [StringLength(200)] public string? Twitter { get; set; }
    [StringLength(1000)] public string? PhotoUrl { get; set; }

    // Microsoft accreditation is MULTI-select (operator 2026-06-24); stored as a CSV in
    // SpeakerProfile.Accreditation. The flat name "SelectedAccreditations" matches the
    // partial's manual checkboxes, so empty-prefix binding fills the list unchanged.
    public List<string> SelectedAccreditations { get; set; } = new();

    [StringLength(2, MinimumLength = 0, ErrorMessage = "Use the 2-letter country code (e.g. DK).")]
    public string? Country { get; set; }
    public string? Gender { get; set; }
    public bool? IsFirstTimeSpeaker { get; set; }
    [StringLength(320)] public string? ContactEmailOverride { get; set; }

    // ----- display-only (set by the service; never bound) -----------------
    /// <summary>The sign-in / Sessionize match email — read-only identity key, shown but never edited here.</summary>
    [BindNever] public string Email { get; set; } = string.Empty;

    /// <summary>The SharePoint-stored copy of the speaker photo (relative path), read-only.</summary>
    [BindNever] public string? PhotoStoredPath { get; set; }

    /// <summary>When the Sessionize import last ran for this speaker (read-only note).</summary>
    [BindNever] public DateTimeOffset? LastSessionizeImportAt { get; set; }

    /// <summary>REQUIREMENTS §51 — when this profile was last saved (UpdatedAt); null = never.</summary>
    [BindNever] public DateTimeOffset? LastSavedAt { get; set; }

    /// <summary>Plain-save confirmation message (the standalone "Save &amp; sync" path appends to it).</summary>
    [BindNever] public string? Message { get; set; }
}

/// <summary>
/// The result of the shared Speaker Details save: the wizard <see cref="WizardStepOutcome"/>
/// PLUS whether anything that maps to Zoho Backstage actually changed. The standalone page's
/// "Save &amp; sync to Zoho" path uses <see cref="SyncRelevantChanged"/> to drive the
/// manual-update alert dedupe; the wizard ignores it (the wizard uses the plain Save path).
/// </summary>
public readonly record struct SpeakerDetailsSaveResult(WizardStepOutcome Outcome, bool SyncRelevantChanged);

/// <summary>
/// Shared submit-service for the Speaker Details form (REQUIREMENTS §148, §26c). It
/// encapsulates the form's plain-save behavior — the OnGet load, the OnPost validate/persist,
/// and the side-effects that MUST be preserved: upsert <see cref="SpeakerProfile"/> and mark
/// the speaker-edited bio markers (<see cref="SpeakerProfile.MarkSpeakerEdited"/> /
/// <see cref="SpeakerProfile.BioLastEditedBySpeakerAt"/> via <c>ApplyBio</c>) so a Sessionize
/// delta re-import never overwrites the speaker's own edits. BOTH the standalone
/// <c>/Speaker/Details</c> page AND the inline <see cref="SpeakerDetailsStepHandler"/> call
/// this same logic, so they stay identical.
///
/// <para>This is the PLAIN Save path only — it does NOT push to Zoho. The standalone page's
/// "Save &amp; sync to Zoho" action keeps the <c>SpeakerBioBackstageSyncService</c> call on the
/// page (gated by <see cref="SpeakerDetailsSaveResult.SyncRelevantChanged"/>); the wizard's
/// Save&amp;next never syncs. Implements the <see cref="IWizardFormService"/> marker so it
/// self-registers by concrete type.</para>
/// </summary>
public sealed class SpeakerDetailsFormService : IWizardFormService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public SpeakerDetailsFormService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Relevance gate (REQUIREMENTS §148): the Speaker Details step is for SPEAKERS only.</summary>
    public bool IsRelevant(ParticipantRole role) => role == ParticipantRole.Speaker;

    /// <summary>
    /// Completion detection (REQUIREMENTS §148) — the SPEAKER has actually edited their own
    /// details: <see cref="SpeakerProfile.BioLastEditedBySpeakerAt"/> is set (the true
    /// "speaker acted" marker; a Sessionize import never sets it). Mirrors SpeakerWizardService.
    /// </summary>
    public Task<bool> IsDoneAsync(int eventId, int participantId, CancellationToken ct) =>
        _db.SpeakerProfiles.AnyAsync(
            p => p.EventId == eventId && p.ParticipantId == participantId
                 && p.BioLastEditedBySpeakerAt != null, ct);

    /// <summary>
    /// Load the form's current state — the SAME load the standalone page's OnGet used: hydrate
    /// from any existing speaker profile. Returns a fully-populated model (empty when no profile
    /// exists yet). The sign-in <paramref name="email"/> is the read-only identity key.
    /// </summary>
    public async Task<SpeakerDetailsFormModel> LoadAsync(int eventId, int participantId, string email, CancellationToken ct)
    {
        var model = new SpeakerDetailsFormModel { Email = email };
        var p = await _db.SpeakerProfiles.FirstOrDefaultAsync(
            sp => sp.EventId == eventId && sp.ParticipantId == participantId, ct);
        if (p is not null) BindFromProfile(model, p);
        return model;
    }

    /// <summary>
    /// Validate + persist + run the speaker-edit side-effects (REQUIREMENTS §148) — the SAME
    /// logic the standalone page's plain Save ran. The country is normalised to an upper
    /// 2-letter code (exactly as the standalone page did, BEFORE the ModelState check), field
    /// errors re-render the SAME step (=> <see cref="WizardStepOutcome.Invalid"/>), and on
    /// success the profile is upserted with the bio fields marked speaker-edited so the delta
    /// re-import never overwrites them. Relevance is RE-DERIVED here, so a crafted POST can
    /// never bypass the speaker-only gate. Returns whether a Zoho-sync-relevant field changed
    /// (the standalone "Save &amp; sync" path consumes it; the wizard ignores it).
    /// </summary>
    public async Task<SpeakerDetailsSaveResult> SaveAsync(
        SpeakerDetailsFormModel model, int eventId, int participantId, string email,
        ParticipantRole role, ModelStateDictionary modelState, CancellationToken ct)
    {
        model.Email = email;

        // Relevance is re-checked server-side (never trusted from the post).
        if (!IsRelevant(role))
            return new SpeakerDetailsSaveResult(WizardStepOutcome.NotRelevant, false);

        // Normalise the country to an upper 2-letter code (same as the standalone page did,
        // before the ModelState check — StringLength already ran on the raw bound value).
        model.Country = string.IsNullOrWhiteSpace(model.Country) ? null : model.Country.Trim().ToUpperInvariant();

        if (!modelState.IsValid)
        {
            // Re-render with field errors; keep the posted editable values + restore the
            // read-only stamps from the existing profile (identical to the standalone page).
            var pp = await _db.SpeakerProfiles.FirstOrDefaultAsync(
                sp => sp.EventId == eventId && sp.ParticipantId == participantId, ct);
            if (pp is not null)
            {
                model.LastSessionizeImportAt = pp.LastSessionizeImportAt;
                model.PhotoStoredPath = pp.PhotoSharePointPath;
                model.LastSavedAt = pp.UpdatedAt;
            }
            return new SpeakerDetailsSaveResult(WizardStepOutcome.Invalid, false);
        }

        var now = _clock.GetUtcNow();
        var profile = await _db.SpeakerProfiles.FirstOrDefaultAsync(
            sp => sp.EventId == eventId && sp.ParticipantId == participantId, ct);
        if (profile is null)
        {
            profile = new SpeakerProfile { EventId = eventId, ParticipantId = participantId, CreatedAt = now, UpdatedAt = now };
            _db.SpeakerProfiles.Add(profile);
        }
        else { profile.UpdatedAt = now; }

        // Track whether anything that maps to Zoho Backstage actually changed, so the
        // standalone "Save & sync" path only re-emails the organizers' manual-update alert
        // when there's a real change (dedupe — it used to fire on EVERY save).
        var syncRelevantChanged = false;

        // Bio fields: mark speaker-edited on change so the delta re-import won't flush them.
        syncRelevantChanged |= ApplyBio(profile, SpeakerProfile.BioFields.Tagline,   profile.Tagline,   N(model.Tagline),   v => profile.Tagline = v,   now);
        syncRelevantChanged |= ApplyBio(profile, SpeakerProfile.BioFields.Biography, profile.Biography, N(model.Biography), v => profile.Biography = v, now);
        syncRelevantChanged |= ApplyBio(profile, SpeakerProfile.BioFields.Blog,      profile.Blog,      N(model.Blog),      v => profile.Blog = v,      now);
        syncRelevantChanged |= ApplyBio(profile, SpeakerProfile.BioFields.LinkedIn,  profile.LinkedIn,  N(model.LinkedIn),  v => profile.LinkedIn = v,  now);
        syncRelevantChanged |= ApplyBio(profile, SpeakerProfile.BioFields.Twitter,   profile.Twitter,   N(model.Twitter),   v => profile.Twitter = v,   now);
        // PhotoUrl is a speaker-owned bio field but is NOT pushed to Backstage, so a
        // photo-only change must not count as a Zoho-sync change (no re-alert).
        ApplyBio(profile, SpeakerProfile.BioFields.PhotoUrl,  profile.PhotoUrl,  N(model.PhotoUrl),  v => profile.PhotoUrl = v,  now);

        // Identity + details. FirstName/LastName/Country/Skills(Accreditation) DO map to
        // Backstage, so a change to any of them is also a real Zoho-sync change.
        var accred = model.SelectedAccreditations.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct().ToList();
        var accredCsv = accred.Count > 0 ? string.Join(", ", accred) : null;
        syncRelevantChanged |=
            !string.Equals(profile.FirstName, N(model.FirstName), StringComparison.Ordinal)
            || !string.Equals(profile.LastName, N(model.LastName), StringComparison.Ordinal)
            || !string.Equals(profile.Country, model.Country, StringComparison.Ordinal)
            || !string.Equals(profile.Accreditation, accredCsv, StringComparison.Ordinal);

        profile.FirstName = N(model.FirstName);
        profile.LastName = N(model.LastName);
        // Accreditation is multi-select; the joined CSV IS the speaker's "skills".
        profile.Accreditation = accredCsv;
        profile.Country = model.Country;
        profile.Gender = N(model.Gender);
        profile.IsFirstTimeSpeaker = model.IsFirstTimeSpeaker;
        profile.ContactEmailOverride = N(model.ContactEmailOverride);

        await _db.SaveChangesAsync(ct);
        BindFromProfile(model, profile);
        model.Message = "Your speaker details have been saved.";
        return new SpeakerDetailsSaveResult(WizardStepOutcome.Advance, syncRelevantChanged);
    }

    /// <summary>Map a persisted profile back onto the render model (the standalone page's Bind()).</summary>
    private static void BindFromProfile(SpeakerDetailsFormModel model, SpeakerProfile p)
    {
        model.FirstName = p.FirstName; model.LastName = p.LastName;
        model.Tagline = p.Tagline; model.Biography = p.Biography; model.Blog = p.Blog;
        model.LinkedIn = p.LinkedIn; model.Twitter = p.Twitter;
        model.PhotoUrl = p.PhotoUrl; model.PhotoStoredPath = p.PhotoSharePointPath;
        model.SelectedAccreditations = (p.Accreditation ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        model.Country = p.Country;
        model.Gender = p.Gender; model.IsFirstTimeSpeaker = p.IsFirstTimeSpeaker;
        model.ContactEmailOverride = p.ContactEmailOverride;
        model.LastSessionizeImportAt = p.LastSessionizeImportAt;
        model.LastSavedAt = p.UpdatedAt;
    }

    private static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Applies a bio field if it changed; returns true when a change was made
    /// (and stamps the speaker-edited marker so the delta re-import never overwrites it).</summary>
    private static bool ApplyBio(
        SpeakerProfile profile, string field, string? current, string? incoming,
        Action<string?> setter, DateTimeOffset now)
    {
        if (string.Equals(current, incoming, StringComparison.Ordinal)) return false;
        setter(incoming);
        profile.MarkSpeakerEdited(field, now);
        return true;
    }
}
