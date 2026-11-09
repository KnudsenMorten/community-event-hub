using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1379.8 — computes <see cref="WordPressPostData"/> for an edition from CEH data, reusing the rules the
/// rest of the hub already applies (nothing re-invented):
/// <list type="bullet">
/// <item>sessions out of scope = service sessions + <see cref="SoMeSubjectScope"/> (§1178: test data,
///   TEST sessions, SoMe-excluded, excluded title patterns, all-test-speaker sessions);</item>
/// <item>test speakers = <see cref="Participant.IsTestUser"/> (§905);</item>
/// <item>track order = <see cref="SoMeBundleBuildService.AllTracksOrder"/> (§1384);</item>
/// <item>sponsor names + websites = <see cref="PublicSponsorsService"/> (the public /Sponsors page,
///   DESIGN §6 name chain); not-announceable companies = <see cref="TestDataScope"/> (§1347/§1348);</item>
/// <item>speaker link = the public speaker page <c>{HubUrl}/Speakers/{participantId}</c>, only for a
///   speaker that page actually shows (selected for publish, active, Speaker role) — otherwise the name
///   is plain text rather than a link to a 404.</item>
/// </list>
/// </summary>
public sealed class WordPressPostDataService
{
    private readonly CommunityHubDbContext _db;
    private readonly string _hubUrl;

    public WordPressPostDataService(CommunityHubDbContext db, IOptions<EmailTemplateOptions>? email = null)
    {
        _db = db;
        _hubUrl = (email?.Value.HubUrl ?? string.Empty).Trim().TrimEnd('/');
    }

    public async Task<WordPressPostData> LoadAsync(int eventId, CancellationToken ct = default)
    {
        // 🔴 Operator 2026-11-07: "everyone who speaks" incl. the 4 organizers and sponsor speakers — "both are and must be
        // included" (Kent Agerlund, Morten Knudsen: MVP/RD organizers on the Welcome/Closing keynotes). Those keynotes and
        // the sponsor technical sessions carry ExcludeFromSoMeAnnouncements, which is a SoMe-campaign rule, not a "does
        // this person speak" rule — so the counts use the §1384 variant: every exclusion (test data, TEST sessions,
        // title patterns, all-test sessions) EXCEPT the announcement flag.
        var excluded = await new SoMeSubjectScope(_db).ExcludedSessionIdsIgnoringAnnouncementFlagAsync(eventId, ct);

        var sessions = (await _db.Sessions
                .AsNoTracking()
                .Where(s => s.EventId == eventId && !s.IsServiceSession)
                .Select(s => new { s.Id, s.Type, s.Track })
                .ToListAsync(ct))
            .Where(s => !excluded.Contains(s.Id))
            .ToList();
        var sessionById = sessions.ToDictionary(s => s.Id);

        var links = (await _db.SessionSpeakers
                .AsNoTracking()
                .Where(ss => ss.Session.EventId == eventId)
                .Select(ss => new { ss.SessionId, ss.ParticipantId })
                .ToListAsync(ct))
            .Where(l => sessionById.ContainsKey(l.SessionId))
            .ToList();
        var linkedIds = links.Select(l => l.ParticipantId).Distinct().ToList();

        // Operator 2026-11-07: "speaker count = everyone who speaks" — the Speaker role, OR linked to any
        // in-scope session whatever the role (organizers, sponsors, volunteers). Active, never a test user.
        // 🔴 §940 keeps every RING-1 account flagged as a test user (it cannot be cleared there) — and the organizers'
        // own speaker accounts are Ring 1. PROD 2026-11-07: Kent Agerlund + Morten Knudsen (MVP/RD, on the Welcome and
        // Closing keynotes) were dropped, so {{MvpRdCount}} said 57 instead of 59; operator: "both are and must be
        // included". A Ring-1 test-flagged account that speaks on a REAL (in-scope, non-test) session is an organizer
        // speaking and counts; every other test user stays out (genuine test accounts sit on test sessions).
        var people = await _db.Participants
            .AsNoTracking()
            .Where(p => p.EventId == eventId && p.IsActive
                        && (!p.IsTestUser || (p.Ring == CommunityHub.Core.Settings.Ring.Ring1 && linkedIds.Contains(p.Id)))
                        && (p.Role == ParticipantRole.Speaker || linkedIds.Contains(p.Id)))
            .Select(p => new { p.Id, p.FullName, p.Email, p.Role, p.SamePersonAsId })
            .ToListAsync(ct);

        // One person counts once: a §OPEN-22 SamePersonAs pair, or two rows with one e-mail, are one speaker.
        var distinctPeople = people
            .GroupBy(p => p.SamePersonAsId ?? p.Id)
            .Select(g => g.OrderByDescending(p => p.Role == ParticipantRole.Speaker).First())
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Email) ? $"#{p.Id}" : p.Email.Trim().ToLowerInvariant())
            .Select(g => g.OrderByDescending(p => p.Role == ParticipantRole.Speaker).First())
            .ToList();
        var countedIds = people.Select(p => p.Id).ToHashSet();

        var profiles = await _db.SpeakerProfiles
            .AsNoTracking()
            .Where(sp => sp.EventId == eventId)
            .Select(sp => new
            {
                sp.ParticipantId,
                sp.Accreditation,
                sp.MvpCategories,
                sp.Country,
                sp.BackstageCountry,
                sp.SelectedForPublish,
                sp.Gender,
            })
            .ToListAsync(ct);
        var profileById = profiles
            .GroupBy(p => p.ParticipantId)
            .ToDictionary(g => g.Key, g => g.First());
        var published = profiles.Where(p => p.SelectedForPublish).Select(p => p.ParticipantId).ToHashSet();

        var counted = distinctPeople
            .Select(p => profileById.TryGetValue(p.Id, out var pr) ? pr : null)
            .ToList();

        // 🔴 Accreditation is a COMMA-SEPARATED list ("Microsoft Expert, Microsoft MVP, Microsoft MCT" — the speaker form
        // saves it that way). Comparing the whole string counted only one-accreditation speakers: PROD 2026-11-07 gave
        // {{MvpCount}} = 9 and {{RdCount}} = 0 against ~55 MVPs and 6 RDs. A speaker counts for every value they hold.
        static bool Is(string? accreditation, string value) =>
            (accreditation ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));

        var countryCount = counted
            .Select(pr => (pr?.Country ?? pr?.BackstageCountry)?.Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var mvp = counted.Count(pr => pr is not null
            && (Is(pr.Accreditation, "Microsoft MVP") || !string.IsNullOrWhiteSpace(pr.MvpCategories)));
        var rd = counted.Count(pr => pr is not null && Is(pr.Accreditation, "Microsoft Regional Director"));
        // the merged figure the operator shows ("61 MVPs/RDs"): MVP OR RD, each person once
        var mvpOrRd = counted.Count(pr => pr is not null
            && (Is(pr.Accreditation, "Microsoft MVP") || !string.IsNullOrWhiteSpace(pr.MvpCategories)
                || Is(pr.Accreditation, "Microsoft Regional Director")));
        var msEmployee = counted.Count(pr => pr is not null && Is(pr.Accreditation, "Microsoft Employee"));
        var msExpert = counted.Count(pr => pr is not null && Is(pr.Accreditation, "Microsoft Expert"));
        // Gender as the speaker gave it on the speaker form ("Male" / "Female" / "Non-binary" / "Prefer not to say").
        var female = counted.Count(pr => string.Equals(pr?.Gender?.Trim(), "Female", StringComparison.OrdinalIgnoreCase));
        var male = counted.Count(pr => string.Equals(pr?.Gender?.Trim(), "Male", StringComparison.OrdinalIgnoreCase));

        // Per-track lists: the same line-up the §1384 all-tracks graphic draws (sessions with a track).
        var nameById = people.ToDictionary(p => p.Id, p => p.FullName);
        var roleById = people.ToDictionary(p => p.Id, p => p.Role);
        // A track's line-up is its technical sessions + master classes (the §1384 rule): a keynote or panel that carries
        // a Track (PROD: Welcome/Closing were "Security") must not list the organizers under that track.
        var trackRows = links
            .Where(l => countedIds.Contains(l.ParticipantId)
                        && !string.IsNullOrWhiteSpace(sessionById[l.SessionId].Track)
                        && sessionById[l.SessionId].Type is SessionType.TechnicalSession or SessionType.MasterClass)
            .Select(l => new { Track = sessionById[l.SessionId].Track!.Trim(), l.ParticipantId })
            .ToList();
        var order = SoMeBundleBuildService.AllTracksOrder(trackRows.Select(r => r.Track));
        var byTrack = order
            .Select(track => new WordPressPostTrack(
                track,
                trackRows
                    .Where(r => string.Equals(r.Track, track, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.ParticipantId)
                    .Distinct()
                    .Select(id => new WordPressPostSpeaker(nameById[id], SpeakerUrl(id, roleById[id], published)))
                    // One person with two rows (organizer + speaker) is listed once, linked when either row is.
                    .GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(s => s.Url is not null).First())
                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();

        var technical = sessions.Where(s => s.Type == SessionType.TechnicalSession).ToList();

        var (sponsors, exhibitors) = await LoadCompaniesAsync(eventId, ct);

        return new WordPressPostData(
            SpeakerCount: distinctPeople.Count,
            CountryCount: countryCount,
            MvpCount: mvp,
            RdCount: rd,
            MicrosoftEmployeeCount: msEmployee,
            MicrosoftExpertCount: msExpert,
            SessionCount: technical.Count,
            MasterClassCount: sessions.Count(s => s.Type == SessionType.MasterClass),
            PanelCount: sessions.Count(s => s.Type == SessionType.PanelDiscussion),
            TrackCount: technical
                .Select(s => s.Track?.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            SponsorCount: sponsors.Count,
            ExhibitorCount: exhibitors.Count,
            SpeakersByTrack: byTrack,
            Sponsors: sponsors,
            Exhibitors: exhibitors,
            MvpOrRdCount: mvpOrRd,
            FemaleSpeakerCount: female,
            MaleSpeakerCount: male);
    }

    /// <summary>The public speaker page applies this same gate (PublicSpeakersService.GetByIdAsync).</summary>
    private string? SpeakerUrl(int participantId, ParticipantRole role, IReadOnlySet<int> published) =>
        !string.IsNullOrEmpty(_hubUrl) && role == ParticipantRole.Speaker && published.Contains(participantId)
            ? $"{_hubUrl}/Speakers/{participantId}"
            : null;

    /// <summary>
    /// Sponsors and exhibitors as the public /Sponsors page names them, minus test / withdrawn / lapsed
    /// companies. A sponsor = a company bought a sponsorship (<see cref="SponsorInfo.IsSponsor"/>); an
    /// exhibitor = it bought a booth (<see cref="SponsorInfo.IsExhibitor"/>).
    /// </summary>
    private async Task<(List<WordPressPostCompany> Sponsors, List<WordPressPostCompany> Exhibitors)>
        LoadCompaniesAsync(int eventId, CancellationToken ct)
    {
        var view = await new PublicSponsorsService(_db).BuildAsync(ct);
        if (view is null) return (new(), new());

        var flags = await _db.SponsorInfos
            .AsNoTracking()
            .Where(s => s.EventId == eventId && s.Status == SponsorStatus.Active)
            .Select(s => new { s.SponsorCompanyId, s.IsSponsor, s.IsExhibitor })
            .ToListAsync(ct);
        var sponsorIds = flags.Where(f => f.IsSponsor).Select(f => f.SponsorCompanyId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exhibitorIds = flags.Where(f => f.IsExhibitor).Select(f => f.SponsorCompanyId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notAnnounceable = await TestDataScope.NotAnnounceableSponsorCompanyIdsAsync(_db, eventId, ct);

        var rows = view.Groups
            .SelectMany(g => g.Sponsors)
            .Where(r => !notAnnounceable.Contains(r.CompanyId))
            .GroupBy(r => r.CompanyId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (
            rows.Where(r => sponsorIds.Contains(r.CompanyId))
                .Select(r => new WordPressPostCompany(r.Name, r.WebsiteUrl)).ToList(),
            rows.Where(r => exhibitorIds.Contains(r.CompanyId))
                .Select(r => new WordPressPostCompany(r.Name, r.WebsiteUrl)).ToList());
    }
}
