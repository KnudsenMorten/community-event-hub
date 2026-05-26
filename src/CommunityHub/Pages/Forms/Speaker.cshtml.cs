using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
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
    private readonly TimeProvider _clock;

    public SpeakerModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
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

    public string FullName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public ParticipantRole Role { get; private set; }
    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    // Sessionize-imported preview fields (read-only on this page).
    public string? Tagline { get; private set; }
    public string? Biography { get; private set; }
    public string? Blog { get; private set; }
    public string? LinkedIn { get; private set; }
    public string? Twitter { get; private set; }
    public DateTimeOffset? LastSessionizeImportAt { get; private set; }

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

            Tagline = profile.Tagline;
            Biography = profile.Biography;
            Blog = profile.Blog;
            LinkedIn = profile.LinkedIn;
            Twitter = profile.Twitter;
            LastSessionizeImportAt = profile.LastSessionizeImportAt;
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

        var profile = await _db.SpeakerProfiles.FirstOrDefaultAsync(
            sp => sp.EventId == me.EventId && sp.ParticipantId == me.ParticipantId, ct);

        if (profile is null)
        {
            profile = new SpeakerProfile
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.SpeakerProfiles.Add(profile);
        }
        else
        {
            profile.UpdatedAt = _clock.GetUtcNow();
        }

        // Only touch the Hub-collected fields here. Sessionize-imported
        // fields are written exclusively by SessionizeImportService.
        profile.Accreditation = AccreditationOptions.Contains(Accreditation)
            ? Accreditation : null;
        profile.IsFirstTimeSpeaker = IsFirstTimeSpeaker;
        profile.Country = string.IsNullOrWhiteSpace(Country) ? null : Country.Trim();
        profile.Gender = GenderOptions.Contains(Gender) ? Gender : null;
        profile.SpeakingPreDay = SpeakingPreDay;
        profile.SpeakingMainDay = SpeakingMainDay;

        // Re-populate sessionize preview for the post-save render.
        Tagline = profile.Tagline;
        Biography = profile.Biography;
        Blog = profile.Blog;
        LinkedIn = profile.LinkedIn;
        Twitter = profile.Twitter;
        LastSessionizeImportAt = profile.LastSessionizeImportAt;

        await _db.SaveChangesAsync(ct);
        Message = "Your speaker details have been saved.";
        return Page();
    }
}
