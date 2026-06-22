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

    // Speaker DETAILS fields (Accreditation / first-time / Country / Gender / Speaking
    // days / preferred email) moved to My Profile (operator 2026-06-21). This form is
    // now the speaker's BIO editor only. Speaking days are auto-derived on the Profile
    // save. The option lists stay here (public static) — Profile references them.

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
            Tagline = profile.Tagline;
            Biography = profile.Biography;
            Blog = profile.Blog;
            LinkedIn = profile.LinkedIn;
            Twitter = profile.Twitter;
            PhotoUrl = profile.PhotoUrl;
            LastSessionizeImportAt = profile.LastSessionizeImportAt;
            BioLastEditedBySpeakerAt = profile.BioLastEditedBySpeakerAt;
        }

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

        // BIO ONLY (operator 2026-06-21): the speaker details fields moved to My
        // Profile; this form edits the bio. When a field's value actually changes,
        // mark it speaker-edited so the delta Sessionize sync never overwrites it.
        ApplyBioField(profile, SpeakerProfile.BioFields.Tagline,   profile.Tagline,   Normalize(Tagline),   v => profile.Tagline = v,   now);
        ApplyBioField(profile, SpeakerProfile.BioFields.Biography, profile.Biography, Normalize(Biography), v => profile.Biography = v, now);
        ApplyBioField(profile, SpeakerProfile.BioFields.Blog,      profile.Blog,      Normalize(Blog),      v => profile.Blog = v,      now);
        ApplyBioField(profile, SpeakerProfile.BioFields.LinkedIn,  profile.LinkedIn,  Normalize(LinkedIn),  v => profile.LinkedIn = v,  now);
        ApplyBioField(profile, SpeakerProfile.BioFields.Twitter,   profile.Twitter,   Normalize(Twitter),   v => profile.Twitter = v,   now);
        ApplyBioField(profile, SpeakerProfile.BioFields.PhotoUrl,  profile.PhotoUrl,  Normalize(PhotoUrl),  v => profile.PhotoUrl = v,  now);

        // Re-populate the bound bio fields with the saved (normalized) values.
        Tagline = profile.Tagline;
        Biography = profile.Biography;
        Blog = profile.Blog;
        LinkedIn = profile.LinkedIn;
        Twitter = profile.Twitter;
        PhotoUrl = profile.PhotoUrl;
        LastSessionizeImportAt = profile.LastSessionizeImportAt;
        BioLastEditedBySpeakerAt = profile.BioLastEditedBySpeakerAt;

        await _db.SaveChangesAsync(ct);
        Message = "Your bio has been saved.";
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
