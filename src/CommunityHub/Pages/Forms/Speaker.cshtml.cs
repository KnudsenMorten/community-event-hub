using System.ComponentModel.DataAnnotations;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Forms;

[Authorize]
public class SpeakerModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerEmailPropagationService _backstageEmail;
    private readonly TimeProvider _clock;

    public SpeakerModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SpeakerEmailPropagationService backstageEmail,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _backstageEmail = backstageEmail;
        _clock = clock;
    }

    public static readonly ParticipantRole[] EligibleRoles =
    {
        ParticipantRole.Speaker,
        ParticipantRole.MasterclassSpeaker,
    };

    public static readonly string[] AccreditationOptions =
    {
        "Microsoft Employee",
        "Microsoft Expert",
        "Microsoft MVP",
        "Microsoft Regional Director",
        "None / other",
    };

    public static readonly string[] GenderOptions =
    {
        "Male", "Female", "Non-binary", "Prefer not to say",
    };

    [BindProperty] public string? Accreditation { get; set; }
    [BindProperty] public bool? IsFirstTimeSpeaker { get; set; }
    [BindProperty] public string? Country { get; set; }
    [BindProperty] public string? Gender { get; set; }
    [BindProperty] public bool SpeakingPreDay { get; set; }
    [BindProperty] public bool SpeakingMainDay { get; set; }

    /// <summary>Structured dietary/allergy capture for day catering (REQUIREMENTS §21) — shared with the Dinner form.</summary>
    [BindProperty] public CommunityHub.Pages.Shared.DietaryInput Dietary { get; set; } = new();

    /// <summary>
    /// Speaker-chosen preferred address for calendar invites + messages. Blank =
    /// use the Sessionize/community address. Never changes the login identity.
    /// </summary>
    [BindProperty]
    [EmailAddress(ErrorMessage = "Please enter a valid email address (or leave blank to use your Sessionize address).")]
    [StringLength(320, ErrorMessage = "That email address is too long.")]
    public string? ContactEmailOverride { get; set; }

    /// <summary>Confirmation/notice shown after the contact-email override changes.</summary>
    public string? EmailNotice { get; private set; }

    public string FullName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public ParticipantRole Role { get; private set; }
    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    // --- Speaker bio fields: seeded from Sessionize, EDITABLE by the speaker --
    // Saving any of these marks it speaker-edited so the delta Sessionize sync
    // never overwrites the speaker's own change.
    [BindProperty]
    [StringLength(500, ErrorMessage = "Tagline is too long (max 500 characters).")]
    public string? Tagline { get; set; }

    [BindProperty]
    [StringLength(4000, ErrorMessage = "Bio is too long (max 4000 characters).")]
    public string? Biography { get; set; }

    [BindProperty]
    [StringLength(500, ErrorMessage = "That link is too long.")]
    public string? Blog { get; set; }

    [BindProperty]
    [StringLength(500, ErrorMessage = "That link is too long.")]
    public string? LinkedIn { get; set; }

    [BindProperty]
    [StringLength(200, ErrorMessage = "That link is too long.")]
    public string? Twitter { get; set; }

    [BindProperty]
    [StringLength(1000, ErrorMessage = "That photo URL is too long.")]
    public string? PhotoUrl { get; set; }

    public DateTimeOffset? LastSessionizeImportAt { get; private set; }
    public DateTimeOffset? BioLastEditedBySpeakerAt { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        FullName = me.FullName;
        Email = me.Email;
        Role = me.Role;
        if (!EligibleRoles.Contains(me.Role))
        {
            AccessDenied = true;
            return Page();
        }

        var profile = await _db.SpeakerProfiles.FirstOrDefaultAsync(
            sp => sp.EventId == me.EventId && sp.ParticipantId == me.ParticipantId, ct);
        if (profile is not null)
        {
            Accreditation = profile.Accreditation;
            IsFirstTimeSpeaker = profile.IsFirstTimeSpeaker;
            Country = profile.Country;
            Gender = profile.Gender;
            SpeakingPreDay = profile.SpeakingPreDay;
            SpeakingMainDay = profile.SpeakingMainDay;
            ContactEmailOverride = profile.ContactEmailOverride;

            Tagline = profile.Tagline;
            Biography = profile.Biography;
            Blog = profile.Blog;
            LinkedIn = profile.LinkedIn;
            Twitter = profile.Twitter;
            PhotoUrl = profile.PhotoUrl;
            LastSessionizeImportAt = profile.LastSessionizeImportAt;
            BioLastEditedBySpeakerAt = profile.BioLastEditedBySpeakerAt;
        }

        var diet = await _db.DietaryRequirements.FirstOrDefaultAsync(
            d => d.EventId == me.EventId && d.ParticipantId == me.ParticipantId
                 && d.Surface == DietarySurface.SpeakerCatering, ct);
        Dietary.LoadFrom(diet);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        FullName = me.FullName;
        Email = me.Email;
        Role = me.Role;
        if (!EligibleRoles.Contains(me.Role))
        {
            AccessDenied = true;
            return Page();
        }

        // Format-validate the contact-email override before any save. A bad
        // address (other than blank) blocks the save with a field error.
        var newOverride = string.IsNullOrWhiteSpace(ContactEmailOverride)
            ? null
            : ContactEmailOverride.Trim();
        if (!ModelState.IsValid)
        {
            await PopulatePreviewAsync(me.EventId, me.ParticipantId, ct);
            return Page();
        }

        var now = _clock.GetUtcNow();
        var profile = await _db.SpeakerProfiles.FirstOrDefaultAsync(
            sp => sp.EventId == me.EventId && sp.ParticipantId == me.ParticipantId, ct);

        if (profile is null)
        {
            profile = new SpeakerProfile
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                CreatedAt = now,
            };
            _db.SpeakerProfiles.Add(profile);
        }
        else
        {
            profile.UpdatedAt = now;
        }

        // Only touch the Hub-collected fields here. (Sessionize import writes
        // the bio fields; the speaker edits them below.)
        profile.Accreditation = AccreditationOptions.Contains(Accreditation)
            ? Accreditation : null;
        profile.IsFirstTimeSpeaker = IsFirstTimeSpeaker;
        profile.Country = string.IsNullOrWhiteSpace(Country) ? null : Country.Trim();
        profile.Gender = GenderOptions.Contains(Gender) ? Gender : null;
        profile.SpeakingPreDay = SpeakingPreDay;
        profile.SpeakingMainDay = SpeakingMainDay;

        // --- Speaker bio fields (seeded from Sessionize, owned by the speaker) -
        // The speaker edits their OWN bio here (own-row scoped). When a field's
        // value actually changes, mark it speaker-edited so the delta Sessionize
        // sync never flushes it. (The organizer "Full import" override is the
        // only path that re-seeds + clears these markers.)
        ApplyBioField(profile, SpeakerProfile.BioFields.Tagline,   profile.Tagline,   Normalize(Tagline),   v => profile.Tagline = v,   now);
        ApplyBioField(profile, SpeakerProfile.BioFields.Biography, profile.Biography, Normalize(Biography), v => profile.Biography = v, now);
        ApplyBioField(profile, SpeakerProfile.BioFields.Blog,      profile.Blog,      Normalize(Blog),      v => profile.Blog = v,      now);
        ApplyBioField(profile, SpeakerProfile.BioFields.LinkedIn,  profile.LinkedIn,  Normalize(LinkedIn),  v => profile.LinkedIn = v,  now);
        ApplyBioField(profile, SpeakerProfile.BioFields.Twitter,   profile.Twitter,   Normalize(Twitter),   v => profile.Twitter = v,   now);
        ApplyBioField(profile, SpeakerProfile.BioFields.PhotoUrl,  profile.PhotoUrl,  Normalize(PhotoUrl),  v => profile.PhotoUrl = v,  now);

        // Contact-email override: detect a change so we only notify + propagate
        // to Backstage when the effective address actually moved.
        var previousOverride = string.IsNullOrWhiteSpace(profile.ContactEmailOverride)
            ? null : profile.ContactEmailOverride.Trim();
        var overrideChanged = !string.Equals(
            previousOverride, newOverride, StringComparison.OrdinalIgnoreCase);
        profile.ContactEmailOverride = newOverride;
        ContactEmailOverride = newOverride;

        // Re-populate the bound bio fields with the saved (normalized) values.
        Tagline = profile.Tagline;
        Biography = profile.Biography;
        Blog = profile.Blog;
        LinkedIn = profile.LinkedIn;
        Twitter = profile.Twitter;
        PhotoUrl = profile.PhotoUrl;
        LastSessionizeImportAt = profile.LastSessionizeImportAt;
        BioLastEditedBySpeakerAt = profile.BioLastEditedBySpeakerAt;

        await SaveDietaryAsync(me.EventId, me.ParticipantId, ct);

        await _db.SaveChangesAsync(ct);
        Message = "Your speaker details have been saved.";

        if (overrideChanged)
        {
            // Confirmation/notice + propagate the effective address to Backstage.
            var effective = SpeakerProfile.EffectiveEmailFor(me.Email, newOverride);
            EmailNotice = newOverride is null
                ? $"Your preferred email was cleared. Calendar invites and "
                  + $"messages will now use your Sessionize address ({me.Email})."
                : $"Calendar invites and messages will now be sent to "
                  + $"{effective}. Your sign-in still uses {me.Email}.";

            try
            {
                await _backstageEmail.QueueAsync(
                    me.EventId, me.ParticipantId, me.Email, newOverride, ct);
            }
            catch
            {
                // Propagation to the external event system must never break the
                // form save; the hub's own mail/calendar already use the override.
            }
        }

        return Page();
    }

    /// <summary>
    /// Re-populate the read-only context fields (last-import / last-edit stamps)
    /// for a re-render when validation fails. The editable bio fields keep the
    /// user's submitted values so they aren't lost on a validation error.
    /// </summary>
    private async Task PopulatePreviewAsync(int eventId, int participantId, CancellationToken ct)
    {
        var profile = await _db.SpeakerProfiles.FirstOrDefaultAsync(
            sp => sp.EventId == eventId && sp.ParticipantId == participantId, ct);
        if (profile is null) return;
        LastSessionizeImportAt = profile.LastSessionizeImportAt;
        BioLastEditedBySpeakerAt = profile.BioLastEditedBySpeakerAt;
    }

    // ----- Structured dietary capture (shared with the Dinner form) ------
    private async Task SaveDietaryAsync(int eventId, int participantId, CancellationToken ct)
    {
        var row = await _db.DietaryRequirements.FirstOrDefaultAsync(
            d => d.EventId == eventId && d.ParticipantId == participantId
                 && d.Surface == DietarySurface.SpeakerCatering, ct);
        if (row is null)
        {
            row = new DietaryRequirement
            {
                EventId = eventId,
                ParticipantId = participantId,
                Surface = DietarySurface.SpeakerCatering,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.DietaryRequirements.Add(row);
        }
        else
        {
            row.UpdatedAt = _clock.GetUtcNow();
        }
        Dietary.ApplyTo(row);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Apply a speaker-edited bio field: when the value actually changes, write
    /// it and mark the field speaker-edited (stamping the last-edit time) so the
    /// delta Sessionize sync won't overwrite it. No-op marking when unchanged.
    /// </summary>
    private static void ApplyBioField(
        SpeakerProfile profile, string field,
        string? current, string? incoming,
        Action<string?> setter, DateTimeOffset now)
    {
        if (string.Equals(current, incoming, StringComparison.Ordinal)) return;
        setter(incoming);
        profile.MarkSpeakerEdited(field, now);
    }
}
