using System.ComponentModel.DataAnnotations;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// The consolidated "Speaker Details" page (§26c) — ONE place for everything a speaker
/// owns: name, bio + socials, photo, MS accreditation / skills, country, and contact
/// preferences. Replaces the split between the old /Forms/Speaker (bio) and the speaker
/// fields on /Profile. Bio fields seeded from Sessionize are marked speaker-edited on
/// change so the delta re-import never overwrites them. "Save &amp; Sync to Zoho" pushes
/// the speaker to Backstage (gated by the publish + ring rules; a no-op until a live
/// Zoho speaker writer is configured).
/// </summary>
[Authorize]
public class DetailsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerBioBackstageSyncService _sync;
    private readonly TimeProvider _clock;

    public DetailsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SpeakerBioBackstageSyncService sync,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _sync = sync;
        _clock = clock;
    }

    public static readonly string[] AccreditationOptions = Forms.SpeakerModel.AccreditationOptions;
    public static readonly string[] GenderOptions = Forms.SpeakerModel.GenderOptions;

    public string FullName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public bool IsError { get; private set; }
    public DateTimeOffset? LastSessionizeImportAt { get; private set; }
    public string? PhotoStoredPath { get; private set; }
    /// <summary>When this speaker profile was last saved (UTC), for the "Last saved …" line (§51).</summary>
    public DateTimeOffset? LastSavedAt { get; private set; }

    // --- Identity / name ---
    [BindProperty][StringLength(200)] public string? FirstName { get; set; }
    [BindProperty][StringLength(200)] public string? LastName { get; set; }

    // --- Bio + socials (seeded from Sessionize, owned by the speaker) ---
    [BindProperty][StringLength(500, ErrorMessage = "Tagline is too long (max 500).")] public string? Tagline { get; set; }
    [BindProperty][StringLength(4000, ErrorMessage = "Bio is too long (max 4000).")] public string? Biography { get; set; }
    [BindProperty][StringLength(500)] public string? Blog { get; set; }
    [BindProperty][StringLength(500)] public string? LinkedIn { get; set; }
    [BindProperty][StringLength(200)] public string? Twitter { get; set; }
    [BindProperty][StringLength(1000)] public string? PhotoUrl { get; set; }

    // --- Details ---
    // Microsoft accreditation is MULTI-select (operator 2026-06-24): a person can hold
    // several (e.g. MVP + Regional Director). Stored as a CSV in SpeakerProfile.Accreditation.
    [BindProperty] public List<string> SelectedAccreditations { get; set; } = new();
    [BindProperty][StringLength(2, MinimumLength = 0, ErrorMessage = "Use the 2-letter country code (e.g. DK).")]
    public string? Country { get; set; }
    [BindProperty] public string? Gender { get; set; }
    [BindProperty] public bool? IsFirstTimeSpeaker { get; set; }
    [BindProperty][StringLength(320)] public string? ContactEmailOverride { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) { AccessDenied = true; return Page(); }

        FullName = me.FullName; Email = me.Email;
        var p = await Load(me, ct);
        if (p is not null) Bind(p);
        return Page();
    }

    public Task<IActionResult> OnPostSaveAsync(CancellationToken ct) => SaveAsync(sync: false, ct);
    public Task<IActionResult> OnPostSaveAndSyncAsync(CancellationToken ct) => SaveAsync(sync: true, ct);

    private async Task<IActionResult> SaveAsync(bool sync, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) { AccessDenied = true; return Page(); }
        FullName = me.FullName; Email = me.Email;

        // Normalise the country to an upper 2-letter code.
        Country = string.IsNullOrWhiteSpace(Country) ? null : Country.Trim().ToUpperInvariant();
        if (!ModelState.IsValid) { var pp = await Load(me, ct); if (pp is not null) { LastSessionizeImportAt = pp.LastSessionizeImportAt; PhotoStoredPath = pp.PhotoSharePointPath; LastSavedAt = pp.UpdatedAt; } return Page(); }

        var now = _clock.GetUtcNow();
        var profile = await _db.SpeakerProfiles.FirstOrDefaultAsync(
            sp => sp.EventId == me.EventId && sp.ParticipantId == me.ParticipantId, ct);
        if (profile is null)
        {
            profile = new SpeakerProfile { EventId = me.EventId, ParticipantId = me.ParticipantId, CreatedAt = now, UpdatedAt = now };
            _db.SpeakerProfiles.Add(profile);
        }
        else { profile.UpdatedAt = now; }

        // Track whether anything that maps to Zoho Backstage actually changed, so the
        // "Save & sync" path only re-emails the organizers' manual-update alert when
        // there's a real change (dedupe — it used to fire on EVERY save).
        var syncRelevantChanged = false;

        // Bio fields: mark speaker-edited on change so the delta re-import won't flush them.
        syncRelevantChanged |= ApplyBio(profile, SpeakerProfile.BioFields.Tagline,   profile.Tagline,   N(Tagline),   v => profile.Tagline = v,   now);
        syncRelevantChanged |= ApplyBio(profile, SpeakerProfile.BioFields.Biography, profile.Biography, N(Biography), v => profile.Biography = v, now);
        syncRelevantChanged |= ApplyBio(profile, SpeakerProfile.BioFields.Blog,      profile.Blog,      N(Blog),      v => profile.Blog = v,      now);
        syncRelevantChanged |= ApplyBio(profile, SpeakerProfile.BioFields.LinkedIn,  profile.LinkedIn,  N(LinkedIn),  v => profile.LinkedIn = v,  now);
        syncRelevantChanged |= ApplyBio(profile, SpeakerProfile.BioFields.Twitter,   profile.Twitter,   N(Twitter),   v => profile.Twitter = v,   now);
        // PhotoUrl is a speaker-owned bio field but is NOT pushed to Backstage, so a
        // photo-only change must not count as a Zoho-sync change (no re-alert).
        ApplyBio(profile, SpeakerProfile.BioFields.PhotoUrl,  profile.PhotoUrl,  N(PhotoUrl),  v => profile.PhotoUrl = v,  now);

        // Identity + details (hub-collected; not part of the Sessionize delta dirty set).
        // FirstName/LastName/Country/Skills(Accreditation) DO map to Backstage, so a change
        // to any of them is also a real Zoho-sync change.
        var accred = SelectedAccreditations.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct().ToList();
        var accredCsv = accred.Count > 0 ? string.Join(", ", accred) : null;
        syncRelevantChanged |=
            !string.Equals(profile.FirstName, N(FirstName), StringComparison.Ordinal)
            || !string.Equals(profile.LastName, N(LastName), StringComparison.Ordinal)
            || !string.Equals(profile.Country, Country, StringComparison.Ordinal)
            || !string.Equals(profile.Accreditation, accredCsv, StringComparison.Ordinal);

        profile.FirstName = N(FirstName);
        profile.LastName = N(LastName);
        // Accreditation is multi-select; the joined CSV IS the speaker's "skills" that
        // sync to Zoho as comma-separated Skills (operator 2026-06-24).
        profile.Accreditation = accredCsv;
        profile.Country = Country;
        profile.Gender = N(Gender);
        profile.IsFirstTimeSpeaker = IsFirstTimeSpeaker;
        profile.ContactEmailOverride = N(ContactEmailOverride);

        await _db.SaveChangesAsync(ct);
        Bind(profile);
        Message = "Your speaker details have been saved.";

        if (sync)
        {
            try
            {
                // DEDUPE: only let the sync re-email the organizers' manual-update alert
                // when something the speaker owns actually changed. A no-change re-sync of
                // an already-in-Backstage speaker stays silent.
                var r = await _sync.SyncOneAsync(
                    me.EventId, me.ParticipantId, alertOnExisting: syncRelevantChanged, ct: ct);
                if (r.Outcome == SpeakerBioSyncOutcome.BlockedNeedsManualUpdate && !syncRelevantChanged)
                {
                    Message += " You're already in Zoho Backstage and nothing changed since your last save, so the organizers were not re-notified.";
                }
                else
                {
                    Message += r.Outcome switch
                    {
                        SpeakerBioSyncOutcome.PushedPublic => " Created in Zoho Backstage (public).",
                        SpeakerBioSyncOutcome.PushedDraft  => " Created in Zoho Backstage (not featured — awaiting publish approval).",
                        SpeakerBioSyncOutcome.BlockedNeedsManualUpdate =>
                            " You're already in Zoho Backstage — its API can't update an existing speaker, so the organizers were emailed to update you there by hand.",
                        SpeakerBioSyncOutcome.RingGated    => " Saved. Zoho sync is held for you until the organizers enable it for your group.",
                        SpeakerBioSyncOutcome.Disabled     => " Saved. (Zoho speaker sync is off for this edition.)",
                        SpeakerBioSyncOutcome.BuiltOnly    => " Saved. (Zoho speaker sync isn't configured yet.)",
                        SpeakerBioSyncOutcome.Failed       => " Saved, but the Zoho sync failed: " + (r.Error ?? "unknown error") + ".",
                        _ => string.Empty,
                    };
                }
                if (r.Outcome == SpeakerBioSyncOutcome.Failed) IsError = true;
            }
            catch (Exception ex) { IsError = true; Message += " Zoho sync error: " + ex.Message; }
        }

        return Page();
    }

    private async Task<SpeakerProfile?> Load(CurrentParticipant me, CancellationToken ct) =>
        await _db.SpeakerProfiles.FirstOrDefaultAsync(
            sp => sp.EventId == me.EventId && sp.ParticipantId == me.ParticipantId, ct);

    private void Bind(SpeakerProfile p)
    {
        FirstName = p.FirstName; LastName = p.LastName;
        Tagline = p.Tagline; Biography = p.Biography; Blog = p.Blog; LinkedIn = p.LinkedIn; Twitter = p.Twitter;
        PhotoUrl = p.PhotoUrl; PhotoStoredPath = p.PhotoSharePointPath;
        SelectedAccreditations = (p.Accreditation ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Country = p.Country;
        Gender = p.Gender; IsFirstTimeSpeaker = p.IsFirstTimeSpeaker; ContactEmailOverride = p.ContactEmailOverride;
        LastSessionizeImportAt = p.LastSessionizeImportAt;
        LastSavedAt = p.UpdatedAt;
    }

    private static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Applies a bio field if it changed; returns true when a change was made.</summary>
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
