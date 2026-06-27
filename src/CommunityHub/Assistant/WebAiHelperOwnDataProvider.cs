using CommunityHub.Core.Assistant;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Sponsors;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Assistant;

/// <summary>
/// Web implementation of <see cref="IAiHelperOwnDataProvider"/>: assembles the signed-in
/// participant's OWN data as grounding for the assistant. Every query is filtered by the
/// server-resolved <c>participantId</c> (own rows only) — read-only, no writes.
///
/// Covered (REQUIREMENTS §129): their tasks (personal + their sponsor company's
/// company-scoped tasks, like the checklist), their sessions (speakers only), the
/// SPONSOR's own-company deliverables rollup (§135), the VOLUNTEER's own shifts /
/// availability, and their readiness summary (open vs done / overdue). A volunteer
/// never receives another person's rows, and a sponsor never another company's rows,
/// because every filter pins <c>participantId</c> / that participant's own
/// <c>SponsorCompanyId</c>.
/// </summary>
public sealed class WebAiHelperOwnDataProvider : IAiHelperOwnDataProvider
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly SponsorDeliverablesService _deliverables;

    public WebAiHelperOwnDataProvider(
        CommunityHubDbContext db, TimeProvider clock, SponsorDeliverablesService deliverables)
    {
        _db = db;
        _clock = clock;
        _deliverables = deliverables;
    }

    public async Task<IReadOnlyList<AiHelperGroundingSection>> GetOwnDataAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default)
    {
        var sections = new List<AiHelperGroundingSection>();
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
            sections.Add(new AiHelperGroundingSection("Your tasks", string.Join("\n", lines)));
        }
        else
        {
            sections.Add(new AiHelperGroundingSection(
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
                sections.Add(new AiHelperGroundingSection(
                    "Your sessions",
                    "These are the sessions you are speaking at:\n" + string.Join("\n", lines)));
            }
        }

        // --- Sponsor: your own company's deliverables (§135) --------------------
        // Scoped strictly to the signed-in sponsor's OWN company (their
        // SponsorCompanyId, resolved above); never another company's rollup.
        if (role == ParticipantRole.Sponsor && sponsorCompanyId != null)
        {
            var d = await _deliverables.BuildForCompanyAsync(
                eventId, sponsorCompanyId, today, companyName: null, ct);

            if (d.ApplicableCount > 0)
            {
                var lines = new List<string>
                {
                    $"Your sponsor deliverables are {d.Percent}% complete ({d.Summary})" +
                    (d.OverdueCount > 0 ? $", with {d.OverdueCount} overdue." : "."),
                };

                var missing = d.MissingStages;
                if (missing.Count > 0)
                {
                    lines.Add("Still to do:");
                    foreach (var s in missing)
                    {
                        var due = s.Deadline is { } dl ? $" (due {dl:yyyy-MM-dd})" : "";
                        var flag = s.Overdue ? " [OVERDUE]" : "";
                        lines.Add($"- {s.Label}{due}{flag}");
                    }
                }

                var doneStages = d.DoneStages;
                if (doneStages.Count > 0)
                {
                    lines.Add("Already done: " +
                              string.Join(", ", doneStages.Select(s => s.Label)) + ".");
                }

                sections.Add(new AiHelperGroundingSection(
                    "Your deliverables", string.Join("\n", lines)));
            }
        }

        // --- Volunteer: your own shifts / assignments + availability -----------
        // Every query is pinned to participantId — a volunteer never sees another
        // volunteer's shifts or availability.
        if (role == ParticipantRole.Volunteer)
        {
            var shifts = await _db.VolunteerTaskAssignments
                .Where(a => a.EventId == eventId && a.ParticipantId == participantId)
                .Select(a => new
                {
                    a.Task.Title,
                    Category = a.Task.Subcategory.Category.Name,
                    Subcategory = a.Task.Subcategory.Name,
                    a.Task.DueDate,
                    a.Task.Shift,
                    a.Task.TimeEnd,
                    a.Task.Status,
                    a.DecisionStatus,
                })
                .ToListAsync(ct);

            if (shifts.Count > 0)
            {
                var lines = new List<string>
                {
                    $"You have {shifts.Count} shift/assignment(s):",
                };
                foreach (var s in shifts
                             .OrderBy(s => s.DueDate is null)
                             .ThenBy(s => s.DueDate ?? DateOnly.MaxValue)
                             .ThenBy(s => s.Shift ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase))
                {
                    var parts = new List<string>();
                    if (s.DueDate is { } dd) parts.Add(dd.ToString("yyyy-MM-dd"));
                    if (!string.IsNullOrWhiteSpace(s.Shift)) parts.Add(s.Shift!);
                    if (!string.IsNullOrWhiteSpace(s.TimeEnd)) parts.Add("to " + s.TimeEnd);
                    var when = parts.Count > 0 ? string.Join(" ", parts) : "time TBD";
                    var decision = s.DecisionStatus != ShiftDecisionStatus.None
                        ? $" — you {s.DecisionStatus} this shift"
                        : "";
                    lines.Add($"- {s.Title} ({s.Category} / {s.Subcategory}) — {when}; {s.Status}{decision}");
                }
                sections.Add(new AiHelperGroundingSection(
                    "Your shifts / assignments", string.Join("\n", lines)));
            }
            else
            {
                sections.Add(new AiHelperGroundingSection(
                    "Your shifts / assignments",
                    "You have no volunteer shifts assigned yet."));
            }

            // Availability: per-day windows (§ VolunteerDayAvailability) + the
            // preferred shifts/role/max-hours the volunteer submitted.
            var days = await _db.VolunteerDayAvailabilities
                .Where(v => v.EventId == eventId && v.ParticipantId == participantId)
                .Select(v => new { v.Day, v.Level, v.Note })
                .OrderBy(v => v.Day)
                .ToListAsync(ct);

            var prefs = await _db.VolunteerAvailabilities
                .Where(v => v.EventId == eventId && v.ParticipantId == participantId)
                .Select(v => new { v.SelectedShifts, v.PreferredRole, v.MaxHoursPerDay })
                .FirstOrDefaultAsync(ct);

            if (days.Count > 0 || prefs is not null)
            {
                var lines = new List<string>();
                if (prefs is not null)
                {
                    if (!string.IsNullOrWhiteSpace(prefs.PreferredRole))
                        lines.Add($"Preferred role: {prefs.PreferredRole}.");
                    if (!string.IsNullOrWhiteSpace(prefs.SelectedShifts))
                        lines.Add($"Available for shifts: {prefs.SelectedShifts}.");
                    lines.Add($"Max hours per day: {prefs.MaxHoursPerDay}.");
                }
                if (days.Count > 0)
                {
                    lines.Add("Per-day availability:");
                    foreach (var d in days)
                    {
                        var note = string.IsNullOrWhiteSpace(d.Note) ? "" : $" — {d.Note}";
                        lines.Add($"- {d.Day:yyyy-MM-dd}: {d.Level}{note}");
                    }
                }
                if (lines.Count > 0)
                {
                    sections.Add(new AiHelperGroundingSection(
                        "Your availability", string.Join("\n", lines)));
                }
            }
        }

        return sections;
    }
}
