using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

/// <summary>
/// The participant's own profile page (REQUIREMENTS §1): every signed-in role
/// can view and edit their own basics — display name and phone — and see the
/// read-only facts that identify them (email = login identity; role = admin-set).
///
/// No schema change: this reuses the existing <see cref="Participant"/> fields
/// (<c>FullName</c>, <c>Phone</c>). Editing is scoped to the signed-in
/// participant's own row only — a participant can never load or save another
/// person's profile.
/// </summary>
[Authorize]
public class ProfileModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHub.Core.Integrations.SpeakerEmailPropagationService _backstageEmail;
    private readonly TimeProvider _clock;

    public ProfileModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        CommunityHub.Core.Integrations.SpeakerEmailPropagationService backstageEmail,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _backstageEmail = backstageEmail;
        _clock = clock;
    }

    // --- Editable basics ----------------------------------------------------
    [BindProperty] public string? FullName { get; set; }
    [BindProperty] public string? Phone { get; set; }

    /// <summary>Optional extra address; when set, mail to this person is also
    /// CC'd here (10a-5). Blank clears it.</summary>
    [BindProperty] public string? SecondaryEmail { get; set; }

    /// <summary>Optional ALTERNATE login email (§26d) — sign in with this OR the primary.</summary>
    [BindProperty] public string? AlternateEmail { get; set; }

    // --- Speaker details (moved here from /Forms/Speaker, operator 2026-06-21).
    //     Shown + editable only for speaker roles; persisted on SpeakerProfile. ---
    [BindProperty]
    [System.ComponentModel.DataAnnotations.EmailAddress(ErrorMessage = "Please enter a valid email address (or leave blank to use your Sessionize address).")]
    [System.ComponentModel.DataAnnotations.StringLength(320, ErrorMessage = "That email address is too long.")]
    public string? SpeakerContactEmail { get; set; }
    [BindProperty] public string? SpeakerAccreditation { get; set; }
    [BindProperty] public string? SpeakerCountry { get; set; }
    [BindProperty] public string? SpeakerGender { get; set; }
    [BindProperty] public bool? SpeakerFirstTime { get; set; }

    /// <summary>True for speaker roles — drives the speaker-details section in the view.</summary>
    public bool IsSpeaker { get; private set; }
    public static string[] AccreditationOptions => CommunityHub.Pages.Forms.SpeakerModel.AccreditationOptions;
    public static string[] GenderOptions => CommunityHub.Pages.Forms.SpeakerModel.GenderOptions;

    // --- Read-only identity facts (shown, not editable) ---------------------
    public string Email { get; private set; } = string.Empty;
    public ParticipantRole Role { get; private set; }

    public string? Message { get; private set; }
    public string? SpeakerNotice { get; private set; }
    public bool Saved { get; private set; }

    private static bool IsSpeakerRole(ParticipantRole r) =>
        r is ParticipantRole.Speaker;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var p = await _db.Participants
            .Where(p => p.Id == me.ParticipantId && p.EventId == me.EventId)
            .Select(p => new { p.FullName, p.Phone, p.Email, p.Role, p.SecondaryEmail, p.AlternateEmail })
            .FirstOrDefaultAsync(ct);
        if (p is null) return RedirectToPage("/Login");

        FullName = p.FullName;
        Phone = p.Phone;
        AlternateEmail = p.AlternateEmail;
        SecondaryEmail = p.SecondaryEmail;
        Email = p.Email;
        Role = p.Role;
        IsSpeaker = IsSpeakerRole(p.Role);

        if (IsSpeaker)
        {
            var sp = await _db.SpeakerProfiles.AsNoTracking().FirstOrDefaultAsync(
                x => x.EventId == me.EventId && x.ParticipantId == me.ParticipantId, ct);
            if (sp is not null)
            {
                SpeakerContactEmail = sp.ContactEmailOverride;
                SpeakerAccreditation = sp.Accreditation;
                SpeakerCountry = sp.Country;
                SpeakerGender = sp.Gender;
                SpeakerFirstTime = sp.IsFirstTimeSpeaker;
            }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Always scope the write to the signed-in participant's OWN row.
        var p = await _db.Participants.FirstOrDefaultAsync(
            x => x.Id == me.ParticipantId && x.EventId == me.EventId, ct);
        if (p is null) return RedirectToPage("/Login");

        // Read-only facts for the redisplay regardless of validation outcome.
        Email = p.Email;
        Role = p.Role;
        IsSpeaker = IsSpeakerRole(p.Role);


        var trimmedName = (FullName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            Message = "Please enter your name.";
            FullName = p.FullName; // restore so the field is not blanked
            Phone = p.Phone;
            return Page();
        }
        if (trimmedName.Length > 200)
        {
            Message = "That name is too long (max 200 characters).";
            return Page();
        }

        var trimmedPhone = string.IsNullOrWhiteSpace(Phone) ? null : Phone.Trim();
        if (trimmedPhone is { Length: > 40 })
        {
            Message = "That phone number is too long (max 40 characters).";
            return Page();
        }

        var trimmedSecondary = string.IsNullOrWhiteSpace(SecondaryEmail)
            ? null : SecondaryEmail.Trim();
        if (trimmedSecondary is not null)
        {
            if (trimmedSecondary.Length > 320)
            {
                Message = "That secondary email is too long (max 320 characters).";
                return Page();
            }
            // Cheap shape check: one @ with text either side, no spaces.
            var at = trimmedSecondary.IndexOf('@');
            if (at <= 0 || at >= trimmedSecondary.Length - 1
                || trimmedSecondary.Contains(' '))
            {
                Message = "Please enter a valid secondary email (or leave it blank).";
                FullName = p.FullName;
                Phone = p.Phone;
                return Page();
            }
        }

        // Alternate LOGIN email (§26d): normalized; must be a valid shape, must not equal
        // your own primary, and must not collide with anyone else's primary/alt in the edition.
        var altEmail = string.IsNullOrWhiteSpace(AlternateEmail)
            ? null : CommunityHub.Core.Auth.PinLoginService.NormalizeEmail(AlternateEmail);
        if (altEmail is not null)
        {
            var atA = altEmail.IndexOf('@');
            if (altEmail.Length > 320 || atA <= 0 || atA >= altEmail.Length - 1 || altEmail.Contains(' '))
            {
                Message = "Please enter a valid alternate email (or leave it blank).";
                FullName = p.FullName; Phone = p.Phone; return Page();
            }
            if (altEmail == p.Email)
            {
                Message = "Your alternate email can't be the same as your sign-in email.";
                FullName = p.FullName; Phone = p.Phone; return Page();
            }
            var clash = await _db.Participants.AnyAsync(
                x => x.EventId == me.EventId && x.Id != p.Id
                     && (x.Email == altEmail || x.AlternateEmail == altEmail), ct);
            if (clash)
            {
                Message = "That alternate email is already used by another participant in this event.";
                FullName = p.FullName; Phone = p.Phone; return Page();
            }
        }

        p.FullName = trimmedName;
        p.Phone = trimmedPhone;
        p.SecondaryEmail = trimmedSecondary;
        p.AlternateEmail = altEmail;

        // --- Speaker details (operator 2026-06-21): persisted on SpeakerProfile.
        //     Speaking days are NOT speaker-set — they're auto-derived from the
        //     speaker's accepted sessions (master class => pre-day; session => main
        //     day) for organizer hotel/food planning. ---
        // Speaker DETAIL fields (preferred email / accreditation / country / gender /
        // first-time) moved to the consolidated /Speaker/Details page (operator
        // 2026-06-24) — Profile no longer edits or writes them. We still keep the
        // organizer-only speaking-days auto-derivation here so it refreshes on save.
        if (IsSpeaker)
        {
            var now = _clock.GetUtcNow();
            var sp = await _db.SpeakerProfiles.FirstOrDefaultAsync(
                x => x.EventId == me.EventId && x.ParticipantId == me.ParticipantId, ct);
            if (sp is null)
            {
                sp = new SpeakerProfile { EventId = me.EventId, ParticipantId = me.ParticipantId, CreatedAt = now };
                _db.SpeakerProfiles.Add(sp);
            }
            else { sp.UpdatedAt = now; }

            var types = await _db.Sessions
                .Where(s => s.EventId == me.EventId && !s.IsServiceSession
                            && s.SessionSpeakers.Any(ss => ss.ParticipantId == me.ParticipantId))
                .Select(s => s.Type).ToListAsync(ct);
            sp.SpeakingPreDay = types.Any(t => t == SessionType.MasterClass);
            sp.SpeakingMainDay = types.Any(t => t != SessionType.MasterClass);
        }

        await _db.SaveChangesAsync(ct);

        FullName = p.FullName;
        Phone = p.Phone;
        SecondaryEmail = p.SecondaryEmail;
        Saved = true;
        // The header greeting reads the name from the session cookie, which is
        // stamped at login — so a changed name shows everywhere after the next
        // sign-in. The profile page itself always shows the saved value.
        Message = "Your profile has been saved.";
        return Page();
    }
}
