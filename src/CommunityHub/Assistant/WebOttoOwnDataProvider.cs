using CommunityHub.Core.Assistant;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Assistant;

/// <summary>
/// Web implementation of <see cref="IOttoOwnDataProvider"/>: assembles the signed-in
/// participant's OWN data as grounding for Otto. Every query is filtered by the
/// server-resolved <c>participantId</c> (own rows only) — read-only, no writes.
///
/// Covered (REQUIREMENTS §129): their tasks (personal + their sponsor company's
/// company-scoped tasks, like the checklist), their sessions (speakers only), and
/// their readiness summary (open vs done / overdue). A volunteer never receives
/// another person's rows because every filter pins <c>participantId</c>.
/// </summary>
public sealed class WebOttoOwnDataProvider : IOttoOwnDataProvider
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public WebOttoOwnDataProvider(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<IReadOnlyList<OttoGroundingSection>> GetOwnDataAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default)
    {
        var sections = new List<OttoGroundingSection>();
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        // Sponsor contacts also own their company-scoped tasks (mirrors the checklist).
        var sponsorCompanyId = await _db.Participants
            .Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);

        // --- Their tasks (own rows only) ---------------------------------------
        var tasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && (t.AssignedParticipantId == participantId
                            || (sponsorCompanyId != null
                                && t.SponsorCompanyId == sponsorCompanyId)))
            .Select(t => new { t.Title, t.DueDate, t.State, t.IsMandatory })
            .ToListAsync(ct);

        if (tasks.Count > 0)
        {
            var open = tasks.Where(t => t.State != TaskState.Done).ToList();
            var done = tasks.Count(t => t.State == TaskState.Done);
            var overdue = open.Count(t => t.DueDate is { } d && d < today);

            var lines = new List<string>
            {
                $"You have {open.Count} task(s) still open, {done} completed" +
                (overdue > 0 ? $", and {overdue} overdue." : "."),
            };
            foreach (var t in open
                         .OrderBy(t => t.DueDate ?? DateOnly.MaxValue)
                         .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase))
            {
                var due = t.DueDate is { } d ? $" (due {d:yyyy-MM-dd})" : "";
                var flag = t.DueDate is { } dd && dd < today ? " [OVERDUE]" : "";
                lines.Add($"- {t.Title}{due}{flag} — {t.State}");
            }
            sections.Add(new OttoGroundingSection("Your tasks", string.Join("\n", lines)));
        }
        else
        {
            sections.Add(new OttoGroundingSection(
                "Your tasks", "You have no outstanding tasks right now."));
        }

        // --- Their sessions (speakers only) ------------------------------------
        if (role == ParticipantRole.Speaker)
        {
            var sessions = await _db.SessionSpeakers
                .Where(ss => ss.ParticipantId == participantId && ss.Session.EventId == eventId)
                .Select(ss => new
                {
                    ss.Session.Title,
                    ss.Session.Room,
                    ss.Session.StartsAt,
                    ss.Session.Type,
                })
                .ToListAsync(ct);

            if (sessions.Count > 0)
            {
                var lines = sessions
                    .OrderBy(s => s.StartsAt ?? DateTimeOffset.MaxValue)
                    .Select(s =>
                    {
                        var when = s.StartsAt is { } st ? st.ToString("yyyy-MM-dd HH:mm") : "time TBD";
                        var room = string.IsNullOrWhiteSpace(s.Room) ? "room TBD" : s.Room;
                        return $"- {s.Title} ({s.Type}) — {when}, {room}";
                    });
                sections.Add(new OttoGroundingSection(
                    "Your sessions",
                    "These are the sessions you are speaking at:\n" + string.Join("\n", lines)));
            }
        }

        return sections;
    }
}
