using CommunityHub.Core.Assistant;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Assistant;

/// <summary>
/// Web implementation of <see cref="IAiHelperPublicInfoProvider"/> (REQUIREMENTS §149) —
/// the PUBLIC, all-roles grounding: published speakers + their skills + sessions, the
/// session catalogue, and the event schedule / key times (doors, lunch, party, dinner).
///
/// <para>Read-only and PUBLIC by construction:</para>
/// <list type="bullet">
///   <item>Speakers honour the SAME publish HARD GATE as the public <c>/Speakers</c> page
///   (<see cref="SpeakerProfile.SelectedForPublish"/> + <see cref="Participant.IsActive"/>
///   + a speaker role) — an unselected/withdrawn/non-speaker profile can never leak.</item>
///   <item>The schedule is intentionally NOT role-filtered: the operator made dates &amp;
///   times relevant to everyone (general event logistics, not personal data), so anyone
///   can ask "when does lunch start / doors open?".</item>
///   <item>No per-person rows are ever emitted here (those stay in
///   <see cref="IAiHelperOwnDataProvider"/>, scoped to the signed-in participant).</item>
/// </list>
/// </summary>
public sealed class WebAiHelperPublicInfoProvider : IAiHelperPublicInfoProvider
{
    private readonly CommunityHubDbContext _db;

    public WebAiHelperPublicInfoProvider(CommunityHubDbContext db) => _db = db;

    public async Task<IReadOnlyList<AiHelperGroundingSection>> GetPublicInfoAsync(
        int eventId, CancellationToken ct = default)
    {
        var sections = new List<AiHelperGroundingSection>();

        // --- (1) Published speakers + their skills + the sessions they present --------
        // HARD GATE in the Where clause (selected-for-publish + active + speaker role),
        // mirroring PublicSpeakersService — never an unselected speaker.
        var speakers = await _db.SpeakerProfiles
            .Where(sp => sp.EventId == eventId
                         && sp.SelectedForPublish
                         && sp.Participant.IsActive
                         && sp.Participant.Role == ParticipantRole.Speaker)
            .Select(sp => new
            {
                sp.Participant.FullName,
                sp.Tagline,
                sp.Biography,
                Sessions = _db.SessionSpeakers
                    .Where(ss => ss.ParticipantId == sp.ParticipantId
                                 && ss.Session.EventId == eventId
                                 && !ss.Session.IsServiceSession)
                    .OrderBy(ss => ss.Session.StartsAt).ThenBy(ss => ss.Session.Title)
                    .Select(ss => ss.Session.Title)
                    .ToList(),
            })
            .ToListAsync(ct);

        if (speakers.Count > 0)
        {
            var lines = speakers
                .OrderBy(s => s.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(s =>
                {
                    // "skills" = the tagline when present, else a trimmed bio snippet.
                    var skills = !string.IsNullOrWhiteSpace(s.Tagline)
                        ? s.Tagline!.Trim()
                        : (!string.IsNullOrWhiteSpace(s.Biography) ? Truncate(s.Biography!.Trim(), 220) : null);
                    var skillsPart = string.IsNullOrWhiteSpace(skills) ? "" : $" — {skills}";
                    var sess = s.Sessions.Count > 0 ? $" (sessions: {string.Join("; ", s.Sessions)})" : "";
                    return $"- {s.FullName}{skillsPart}{sess}";
                });
            sections.Add(new AiHelperGroundingSection(
                "Speakers (public lineup)",
                "Published speakers, their expertise, and the session(s) they present. " +
                "Anyone may ask about these:\n" + string.Join("\n", lines)));
        }

        // --- (2) The session programme (title / type / time / room / speakers) --------
        // Privacy: mirror the speaker publish HARD GATE — a session appears ONLY if it
        // has at least one PUBLISHED speaker, and only published speaker names are shown.
        // So an unselected speaker's talk never leaks via the programme.
        var publishedSpeakerIds = (await _db.SpeakerProfiles
            .Where(sp => sp.EventId == eventId
                         && sp.SelectedForPublish
                         && sp.Participant.IsActive
                         && sp.Participant.Role == ParticipantRole.Speaker)
            .Select(sp => sp.ParticipantId)
            .ToListAsync(ct)).ToHashSet();

        var sessionsRaw = await _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .OrderBy(s => s.StartsAt ?? DateTimeOffset.MaxValue).ThenBy(s => s.Title)
            .Select(s => new
            {
                s.Title,
                s.Type,
                s.StartsAt,
                s.Room,
                s.Abstract,
                Speakers = s.SessionSpeakers
                    .OrderBy(ss => ss.Participant.FullName)
                    .Select(ss => new { ss.ParticipantId, ss.Participant.FullName })
                    .ToList(),
            })
            .ToListAsync(ct);

        var sessions = sessionsRaw
            .Select(s => new
            {
                s.Title,
                s.Type,
                s.StartsAt,
                s.Room,
                s.Abstract,
                Speakers = s.Speakers.Where(x => publishedSpeakerIds.Contains(x.ParticipantId))
                                     .Select(x => x.FullName).ToList(),
            })
            .Where(s => s.Speakers.Count > 0)   // only sessions with >=1 published speaker
            .ToList();

        if (sessions.Count > 0)
        {
            var lines = sessions.Select(s =>
            {
                var when = s.StartsAt is { } st ? st.ToString("ddd dd MMM HH:mm") : "time TBD";
                var room = string.IsNullOrWhiteSpace(s.Room) ? "room TBD" : s.Room!;
                var who = s.Speakers.Count > 0 ? $" — {string.Join(", ", s.Speakers)}" : "";
                var summary = string.IsNullOrWhiteSpace(s.Abstract) ? "" : $"\n    {Truncate(s.Abstract!.Trim(), 240)}";
                return $"- {s.Title} ({s.Type}){who} — {when}, {room}{summary}";
            });
            sections.Add(new AiHelperGroundingSection(
                "Session programme",
                "All sessions on the programme (anyone may ask about these):\n" + string.Join("\n", lines)));
        }

        // --- (3) Event schedule / key times for ALL roles ----------------------------
        // Doors open, lunch, breaks, party, appreciation dinner, etc. NOT role-filtered:
        // the operator made dates+times relevant to everyone (general logistics, not
        // personal data). Sourced from ScheduleEntry + Sessionize service sessions
        // (lunch/breaks carried as IsServiceSession).
        var schedule = await _db.ScheduleEntries
            .Where(e => e.EventId == eventId)
            .OrderBy(e => e.StartsAt)
            .Select(e => new { e.Title, e.StartsAt, e.EndsAt, e.AllDay, e.Location, e.Notes })
            .ToListAsync(ct);

        var serviceTimes = await _db.Sessions
            .Where(s => s.EventId == eventId && s.IsServiceSession)
            .OrderBy(s => s.StartsAt ?? DateTimeOffset.MaxValue)
            .Select(s => new { s.Title, s.StartsAt, s.EndsAt, s.Room })
            .ToListAsync(ct);

        if (schedule.Count > 0 || serviceTimes.Count > 0)
        {
            var lines = new List<string>();
            foreach (var e in schedule)
            {
                string when;
                if (e.AllDay)
                {
                    when = e.StartsAt.ToString("ddd dd MMM") + " (all day)";
                }
                else
                {
                    when = e.StartsAt.ToString("ddd dd MMM HH:mm")
                           + (e.EndsAt is { } end ? "–" + end.ToString("HH:mm") : "");
                }
                var loc = string.IsNullOrWhiteSpace(e.Location) ? "" : $", {e.Location}";
                var note = string.IsNullOrWhiteSpace(e.Notes) ? "" : $" — {e.Notes}";
                lines.Add($"- {e.Title}: {when}{loc}{note}");
            }
            foreach (var s in serviceTimes)
            {
                var when = s.StartsAt is { } st
                    ? st.ToString("ddd dd MMM HH:mm") + (s.EndsAt is { } en ? "–" + en.ToString("HH:mm") : "")
                    : "time TBD";
                var room = string.IsNullOrWhiteSpace(s.Room) ? "" : $", {s.Room}";
                lines.Add($"- {s.Title}: {when}{room}");
            }
            sections.Add(new AiHelperGroundingSection(
                "Event schedule & key times",
                "Key dates and times for everyone — doors open, lunch, breaks, party, " +
                "appreciation dinner, etc.:\n" + string.Join("\n", lines)));
        }

        // --- (4) §490 PUBLIC sponsors & exhibitors -----------------------------------
        // Operator: "it is also relevant with any information that is also synced to zoho
        // like sponsor & exhibitor names, information, etc."
        //
        // The gate is the SAME as the public /Sponsors page (PublicSponsorsService):
        // Status == Active, so a withdrawn company (§253 G8b) drops out of the helper the
        // moment it drops off the public wall — one rule, not two that can drift apart.
        //
        // COMPANY-LEVEL FIELDS ONLY. Everything below is marketing copy the sponsor wrote
        // FOR publication, already rendered publicly and synced to Zoho. Deliberately
        // EXCLUDED as personal data (GDPR): contact names, e-mails, phone numbers, signer /
        // coordinator identities, booth-member lists, check-in slots and head counts. Those
        // are not "sponsor information" — they are people.
        var sponsors = await _db.SponsorInfos
            .Where(s => s.EventId == eventId && s.Status == SponsorStatus.Active)
            .Select(s => new
            {
                s.SponsorCompanyId,
                s.Tier,
                s.BoothLabel,
                s.WebsiteUrl,
                s.LinkedInUrl,
                s.CompanyDescription,
                s.CompanyDescriptionShort,
            })
            .ToListAsync(ct);

        if (sponsors.Count > 0)
        {
            // The hub has no company entity — the public NAME lives on SponsorUploadLocation
            // and resolves through the shared fallback chain, so the helper says exactly the
            // same company name as every other sponsor surface (§443).
            var names = await _db.SponsorUploadLocations
                .Where(l => l.EventId == eventId)
                .Select(l => new { l.SponsorCompanyId, l.CompanyName })
                .ToListAsync(ct);
            var nameByCompany = names
                .Where(n => !string.IsNullOrWhiteSpace(n.CompanyName))
                .GroupBy(n => n.SponsorCompanyId)
                .ToDictionary(g => g.Key, g => g.First().CompanyName!, StringComparer.OrdinalIgnoreCase);

            var lines = new List<string>();
            foreach (var s in sponsors)
            {
                nameByCompany.TryGetValue(s.SponsorCompanyId, out var publicName);
                var name = CommunityHub.Core.Integrations.SponsorCompanyName.Resolve(
                    publicName, legalName: null, billingName: null, companyId: s.SponsorCompanyId);

                var bits = new List<string> { $"tier: {s.Tier}" };
                if (!string.IsNullOrWhiteSpace(s.BoothLabel)) bits.Add($"booth: {s.BoothLabel!.Trim()}");
                if (!string.IsNullOrWhiteSpace(s.WebsiteUrl)) bits.Add($"web: {s.WebsiteUrl!.Trim()}");
                if (!string.IsNullOrWhiteSpace(s.LinkedInUrl)) bits.Add($"LinkedIn: {s.LinkedInUrl!.Trim()}");

                var about = s.CompanyDescriptionShort ?? s.CompanyDescription;
                var blurb = string.IsNullOrWhiteSpace(about) ? "" : " — " + Truncate(about!.Trim(), 600);

                lines.Add($"- {name} ({string.Join(", ", bits)}){blurb}");
            }

            sections.Add(new AiHelperGroundingSection(
                "Sponsors & exhibitors",
                "The event's sponsor and exhibitor companies — the same public information " +
                "shown on the sponsors page and synced to Zoho. Booth labels say where to find " +
                "an exhibitor on the expo floor. Company information only; no contact people.\n"
                + string.Join("\n", lines)));
        }

        return sections;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
