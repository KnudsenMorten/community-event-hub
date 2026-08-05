using System.ComponentModel.DataAnnotations;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
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

    /// <summary>§302b: the speaker's company — optional; Sessionize has no such field, so
    /// the hub collects it and pushes it to the Zoho speaker's "Company Name" when filled.</summary>
    [StringLength(200)] public string? CompanyName { get; set; }

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
    // Display/legacy only — no longer edited on this step (the input was removed; the
    // get-started Step 1 "Calendar email" owns the alternate email). BindNever so a POST
    // can never null it out.
    [BindNever][StringLength(320)] public string? ContactEmailOverride { get; set; }

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

/// <summary>One Backstage-mapped field a speaker changed on the Details form (old → new).</summary>
public sealed record SpeakerDetailFieldChange(string Field, string? OldValue, string? NewValue);

/// <summary>
/// The result of the shared Speaker Details save: the wizard <see cref="WizardStepOutcome"/>
/// PLUS whether anything that maps to Zoho Backstage actually changed. The standalone page's
/// "Save &amp; sync to Zoho" path uses <see cref="SyncRelevantChanged"/> to drive the
/// manual-update alert dedupe; the wizard ignores it (the wizard uses the plain Save path).
/// <see cref="ManualUpdateMailSent"/> is true when the save itself already mailed the
/// operator the field-level changes (speaker already in Backstage) — callers must then
/// suppress their own generic manual-update alert (no double mail).
/// </summary>
public readonly record struct SpeakerDetailsSaveResult(
    WizardStepOutcome Outcome, bool SyncRelevantChanged,
    IReadOnlyList<SpeakerDetailFieldChange>? Changes = null,
    bool ManualUpdateMailSent = false);

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

    // Operator 2026-07-24: when a speaker who ALREADY exists in Backstage edits their
    // bio/profile (get-started task or standalone page), info@expertslive.dk must get a
    // mail WITH the changes — the Backstage speakers API is create-only, so the operator
    // applies them manually. Ring-exempt via ZohoChangeNotifier; optional so tests/legacy
    // constructions keep compiling (null ⇒ no mail).
    private readonly CommunityHub.Core.Email.ZohoChangeNotifier? _zohoChanges;

    public SpeakerDetailsFormService(
        CommunityHubDbContext db, TimeProvider clock,
        CommunityHub.Core.Email.ZohoChangeNotifier? zohoChanges = null)
    {
        _db = db;
        _clock = clock;
        _zohoChanges = zohoChanges;
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
        // §306 (operator 2026-07-24): accreditation is mandatory and "Microsoft Expert"
        // is PRE-SELECTED when nothing is chosen yet — the speaker just unticks/reticks
        // as needed and the mandatory rule is satisfied by default.
        if (model.SelectedAccreditations.Count == 0)
            model.SelectedAccreditations.Add("Microsoft Expert");
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

        // §306 (operator 2026-07-24): Microsoft accreditation, Country and Gender are
        // MANDATORY — "something is chosen for all 3". Enforced HERE (the shared persist
        // path) so the wizard step and the standalone page behave identically. "None" /
        // "Prefer not to say" are valid choices — mandatory means chosen, not disclosed.
        if (!model.SelectedAccreditations.Any(a => !string.IsNullOrWhiteSpace(a)))
            modelState.AddModelError("SelectedAccreditations",
                "Pick at least one Microsoft accreditation.");
        if (string.IsNullOrWhiteSpace(model.Country))
            modelState.AddModelError("Country", "Pick your country.");
        if (string.IsNullOrWhiteSpace(model.Gender))
            modelState.AddModelError("Gender", "Pick a gender option (\"Prefer not to say\" counts).");

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

        // Track WHICH Backstage-mapped fields actually changed (old → new), so the
        // standalone "Save & sync" path only re-emails the organizers' manual-update alert
        // when there's a real change (dedupe — it used to fire on EVERY save) AND the
        // operator-2026-07-24 change mail can list the concrete edits to apply manually.
        // §302: field names are the ZOHO GUI names from the Backstage "Edit Speaker"
        // panel (operator: "you must show the gui field") — e.g. CEH "Microsoft
        // accreditation" is Zoho's "Skills". Blog has NO Zoho field, so a blog-only
        // edit is not a Zoho change (no mail).
        var changes = new List<SpeakerDetailFieldChange>();
        void Track(string zohoGuiField, string? old, string? incoming)
        {
            if (!string.Equals(old, incoming, StringComparison.Ordinal))
                changes.Add(new SpeakerDetailFieldChange(zohoGuiField, old, incoming));
        }

        // Bio fields: mark speaker-edited on change so the delta re-import won't flush them.
        // §302b: GUI labels come from the ONE ZohoFieldMap.
        Track(ZohoFieldMap.Speaker.Tagline.GuiLabel, profile.Tagline, N(model.Tagline));
        Track(ZohoFieldMap.Speaker.Biography.GuiLabel, profile.Biography, N(model.Biography));
        Track(ZohoFieldMap.Speaker.LinkedIn.GuiLabel, profile.LinkedIn, N(model.LinkedIn));
        Track(ZohoFieldMap.Speaker.Twitter.GuiLabel, profile.Twitter, N(model.Twitter));
        ApplyBio(profile, SpeakerProfile.BioFields.Tagline,   profile.Tagline,   N(model.Tagline),   v => profile.Tagline = v,   now);
        ApplyBio(profile, SpeakerProfile.BioFields.Biography, profile.Biography, N(model.Biography), v => profile.Biography = v, now);
        ApplyBio(profile, SpeakerProfile.BioFields.Blog,      profile.Blog,      N(model.Blog),      v => profile.Blog = v,      now);
        ApplyBio(profile, SpeakerProfile.BioFields.LinkedIn,  profile.LinkedIn,  N(model.LinkedIn),  v => profile.LinkedIn = v,  now);
        ApplyBio(profile, SpeakerProfile.BioFields.Twitter,   profile.Twitter,   N(model.Twitter),   v => profile.Twitter = v,   now);
        // PhotoUrl is a speaker-owned bio field but is NOT pushed to Backstage, so a
        // photo-only change must not count as a Zoho-sync change (no re-alert).
        ApplyBio(profile, SpeakerProfile.BioFields.PhotoUrl,  profile.PhotoUrl,  N(model.PhotoUrl),  v => profile.PhotoUrl = v,  now);

        // Identity + details. FirstName/LastName/Country/Skills(Accreditation) DO map to
        // Backstage, so a change to any of them is also a real Zoho-sync change.
        var accred = model.SelectedAccreditations.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct().ToList();
        var accredCsv = accred.Count > 0 ? string.Join(", ", accred) : null;
        Track(ZohoFieldMap.Speaker.FirstName.GuiLabel, profile.FirstName, N(model.FirstName));
        Track(ZohoFieldMap.Speaker.LastName.GuiLabel, profile.LastName, N(model.LastName));
        Track(ZohoFieldMap.Speaker.Company.GuiLabel, profile.CompanyName, N(model.CompanyName));
        Track(ZohoFieldMap.Speaker.Country.GuiLabel, profile.Country, model.Country);
        Track(ZohoFieldMap.Speaker.Skills.GuiLabel, profile.Accreditation, accredCsv);
        var syncRelevantChanged = changes.Count > 0;

        profile.CompanyName = N(model.CompanyName);
        profile.FirstName = N(model.FirstName);
        profile.LastName = N(model.LastName);
        // Accreditation is multi-select; the joined CSV IS the speaker's "skills".
        profile.Accreditation = accredCsv;
        profile.Country = model.Country;
        profile.Gender = N(model.Gender);
        profile.IsFirstTimeSpeaker = model.IsFirstTimeSpeaker;
        // ContactEmailOverride is no longer edited on this step (operator 2026-06-28 — the
        // get-started Step 1 "Calendar email" is the single alternate-email field). Do NOT
        // write it here: the input is gone, so model binding leaves it null and a save would
        // WIPE any existing override. The persisted value is left untouched.

        // Completion marker (operator 2026-07-11): the ACT of saving this step IS the speaker
        // confirming their details — so mark it even when nothing changed (e.g. the Sessionize-
        // imported bio/links were already correct and the speaker just reviewed + saved). Without
        // this, ApplyBio only stamps on a field CHANGE, so "Save & next" on an already-complete
        // profile left the step/task PENDING. Per-field speaker-edit flags stay change-driven
        // above, so a delta re-import still updates fields the speaker never touched.
        profile.BioLastEditedBySpeakerAt = now;

        await _db.SaveChangesAsync(ct);
        BindFromProfile(model, profile);
        model.Message = "Your speaker details have been saved.";

        // Operator 2026-07-24: the speaker ALREADY exists in Backstage (create-only API ⇒
        // CEH cannot push the edit) — mail info@expertslive.dk the concrete field changes
        // to apply manually. Fires for BOTH entry points (get-started wizard step AND the
        // standalone page) because this shared service is the single persist path.
        var mailSent = false;
        if (syncRelevantChanged
            && !string.IsNullOrWhiteSpace(profile.BackstageSpeakerId)
            && _zohoChanges is not null)
        {
            var who = $"{profile.FirstName} {profile.LastName}".Trim();
            // 🔒 §745 — the heading is an INTRO, not a change. It used to be the first ENTRY of the
            // list below, and the subject counts that list: two edited fields were announced as
            // "3 change(s)" (operator 2026-07-31: *"it says 3 changes in subject but mention 2,
            // why. is skill conuted as 2"* — it was neither; Skills is one field).
            var intro =
                $"ACTION NEEDED: speaker '{(who.Length > 0 ? who : email)}' ({email}) edited their "
                + $"hub profile — apply these changes in Backstage (Speakers → Edit Speaker, "
                + $"id {profile.BackstageSpeakerId}; the speakers API is create-only). "
                + "Field names below are the ZOHO GUI fields:";
            var lines = new List<string>();
            // §533 — this mail exists to be COPY-PASTED into Zoho, so the new value must arrive
            // WHOLE (operator 2026-07-28: "you cannot do that, as i wnt be able to copy/paste to
            // zoho - i need all text … newer cut off text, newer !"). It clipped BOTH values at
            // 300 chars, which makes a 1,500-character biography useless for the one job the mail
            // has.
            //
            // It also printed 'old' → 'new' for every field, so a long bio appeared TWICE ("why do
            // you mention it twice here ?"). For pasting, the old value is noise. Short fields keep
            // the before/after — genuinely useful for Country or Company Name — while long ones
            // print only the new text, in full, on its own line.
            lines.AddRange(changes.Select(c => IsShort(c.OldValue) && IsShort(c.NewValue)
                ? $"  {c.Field}: '{Show(c.OldValue)}' → '{Show(c.NewValue)}'"
                : $"  {c.Field} — set it to exactly this:\n{Show(c.NewValue)}"));
            // §763 — manualOnly: the Backstage speakers API is CREATE-ONLY, so CEH wrote nothing
            // here. The mail must not claim it did, nor ask him to "publish" a change that does not
            // exist over there; every line below is hand-work.
            await _zohoChanges.NotifyAsync("Speakers", lines, ct, intro: intro, manualOnly: true);
            mailSent = true;
        }

        return new SpeakerDetailsSaveResult(WizardStepOutcome.Advance, syncRelevantChanged, changes, mailSent);
    }

    /// <summary>
    /// §533 — a change value for the ops mail, ALWAYS in full. Never clipped: the operator has to
    /// paste this into Zoho by hand, and a truncated biography cannot be pasted.
    /// </summary>
    private static string Show(string? v) =>
        string.IsNullOrEmpty(v) ? "(empty)" : v;

    /// <summary>
    /// §533 — short enough to read as a before → after on one line. Longer values get the new text
    /// on its own line instead, so a bio is never printed twice.
    /// </summary>
    private static bool IsShort(string? v) => (v?.Length ?? 0) <= 120;

    /// <summary>Map a persisted profile back onto the render model (the standalone page's Bind()).</summary>
    private static void BindFromProfile(SpeakerDetailsFormModel model, SpeakerProfile p)
    {
        model.FirstName = p.FirstName; model.LastName = p.LastName;
        model.CompanyName = p.CompanyName;
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
