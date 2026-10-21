using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Volunteers;

/// <summary>One active volunteer as the roster page shows them: the toggle plus what they answered.</summary>
/// <param name="Availability">Day → the label they chose, in day order. Shown NEXT TO the toggle on purpose.</param>
public sealed record RosterRow(
    int ParticipantId,
    string Name,
    string Email,
    bool InRoster,
    string? Note,
    IReadOnlyList<(DateOnly Day, string Label, bool Working)> Availability,
    int AssignedTasks);

/// <summary>
/// §1297 — server-side authority for the WORKER ROSTER: the curated pool
/// <see cref="VolunteerPlanner"/> auto-assigns from, and the per-Bucket preferred volunteers.
/// </summary>
/// <remarks>
/// Organizer-only, like every other allocation mutation. The roster is a list an organizer
/// curates (same shape as the bucket-supervisor join, for the same reason) — membership is the
/// flag, so adding and removing a row is the whole API.
/// </remarks>
public sealed class VolunteerPlanningRosterService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public VolunteerPlanningRosterService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    private static void RequireOrganizer(VolunteerStructureService.ActorContext actor)
    {
        if (!actor.Role.HasOrganizerAccess())
            throw new VolunteerAccessDeniedException("Organizer role required to curate the volunteer planning roster.");
    }

    /// <summary>
    /// Every ACTIVE volunteer in the edition with their roster membership and their stated
    /// availability per day.
    /// </summary>
    /// <remarks>
    /// 🔑 Showing the availability beside the toggle is the point of the page, not decoration:
    /// adding somebody who answered "Morning only" to a full-day pool is a contradiction, and
    /// this is where it should be visible rather than discovered later by the planner.
    /// </remarks>
    public async Task<IReadOnlyList<RosterRow>> LoadAsync(
        VolunteerStructureService.ActorContext actor, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        var eventId = actor.EventId;

        var people = await _db.Participants
            .Where(p => p.EventId == eventId && p.Role == ParticipantRole.Volunteer && p.IsActive)
            .Select(p => new { p.Id, p.FullName, p.Email })
            .ToListAsync(ct);

        var roster = await _db.VolunteerPlanningRoster
            .Where(r => r.EventId == eventId)
            .Select(r => new { r.ParticipantId, r.Note })
            .ToListAsync(ct);
        var rosterById = roster.ToDictionary(r => r.ParticipantId, r => r.Note);

        var availability = await _db.VolunteerDayAvailabilities
            .Where(v => v.EventId == eventId)
            .Select(v => new { v.ParticipantId, v.Day, v.Level, v.Note })
            .ToListAsync(ct);

        var assignedCounts = (await _db.VolunteerTaskAssignments
                .Where(a => a.EventId == eventId)
                .Select(a => a.ParticipantId)
                .ToListAsync(ct))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var byPid = availability.GroupBy(a => a.ParticipantId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => x.Day)
                      .Select(x =>
                      {
                          var option = VolunteerDayOptions.Resolve(x.Day, x.Level, x.Note);
                          var window = VolunteerDayOptions.WindowFor(x.Day, x.Level, x.Note);
                          var label = window.Window is { } w ? $"{option.Title} ({w})" : option.Title;
                          return (x.Day, Label: label, Working: window.Present);
                      })
                      .ToList());

        return people
            .Select(p => new RosterRow(
                p.Id,
                string.IsNullOrWhiteSpace(p.FullName) ? p.Email : p.FullName,
                p.Email,
                rosterById.ContainsKey(p.Id),
                rosterById.GetValueOrDefault(p.Id),
                byPid.TryGetValue(p.Id, out var days)
                    ? days
                    : Array.Empty<(DateOnly, string, bool)>(),
                assignedCounts.GetValueOrDefault(p.Id)))
            .OrderByDescending(r => r.InRoster)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Put a volunteer in scope for auto-planning. Idempotent.</summary>
    public async Task<bool> AddAsync(
        VolunteerStructureService.ActorContext actor, int participantId, string? note = null,
        CancellationToken ct = default)
    {
        RequireOrganizer(actor);

        var person = await _db.Participants
            .FirstOrDefaultAsync(p => p.Id == participantId && p.EventId == actor.EventId, ct);
        if (person is null)
            throw new VolunteerValidationException("That volunteer is not in this edition.");
        if (person.Role != ParticipantRole.Volunteer)
            throw new VolunteerValidationException("Only volunteers can be put on the worker roster.");

        var exists = await _db.VolunteerPlanningRoster
            .AnyAsync(r => r.EventId == actor.EventId && r.ParticipantId == participantId, ct);
        if (exists) return false;

        _db.VolunteerPlanningRoster.Add(new VolunteerPlanningRosterEntry
        {
            EventId = actor.EventId,
            ParticipantId = participantId,
            AddedByEmail = actor.Email,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Take a volunteer out of scope. ⚠️ Removes them from FUTURE planning runs only —
    /// assignments they already hold are untouched, because un-assigning somebody is a
    /// decision with a mail attached and it belongs to the allocation queue, not to a scope list.
    /// </summary>
    public async Task<bool> RemoveAsync(
        VolunteerStructureService.ActorContext actor, int participantId, CancellationToken ct = default)
    {
        RequireOrganizer(actor);

        var row = await _db.VolunteerPlanningRoster
            .FirstOrDefaultAsync(r => r.EventId == actor.EventId && r.ParticipantId == participantId, ct);
        if (row is null) return false;

        _db.VolunteerPlanningRoster.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ── Preferred volunteers per Bucket ─────────────────────────────────────────────────

    /// <summary>Record that a Bucket prefers a volunteer. Idempotent; re-adding updates the rank.</summary>
    public async Task<bool> AddPreferredAsync(
        VolunteerStructureService.ActorContext actor, int categoryId, int participantId, int rank = 0,
        CancellationToken ct = default)
    {
        RequireOrganizer(actor);

        var category = await _db.VolunteerCategories
            .FirstOrDefaultAsync(c => c.Id == categoryId && c.EventId == actor.EventId, ct);
        if (category is null) throw new VolunteerValidationException("That bucket is not in this edition.");

        var person = await _db.Participants
            .FirstOrDefaultAsync(p => p.Id == participantId && p.EventId == actor.EventId, ct);
        if (person is null || person.Role != ParticipantRole.Volunteer)
            throw new VolunteerValidationException("Only volunteers in this edition can be preferred for a bucket.");

        var existing = await _db.VolunteerCategoryPreferences
            .FirstOrDefaultAsync(p => p.CategoryId == categoryId && p.ParticipantId == participantId, ct);
        if (existing is not null)
        {
            existing.Rank = rank;
            await _db.SaveChangesAsync(ct);
            return false;
        }

        _db.VolunteerCategoryPreferences.Add(new VolunteerCategoryPreference
        {
            EventId = actor.EventId,
            CategoryId = categoryId,
            ParticipantId = participantId,
            Rank = rank,
            AddedByEmail = actor.Email,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Drop a bucket's preference for a volunteer.</summary>
    public async Task<bool> RemovePreferredAsync(
        VolunteerStructureService.ActorContext actor, int categoryId, int participantId,
        CancellationToken ct = default)
    {
        RequireOrganizer(actor);

        var row = await _db.VolunteerCategoryPreferences
            .FirstOrDefaultAsync(p => p.EventId == actor.EventId
                                      && p.CategoryId == categoryId
                                      && p.ParticipantId == participantId, ct);
        if (row is null) return false;

        _db.VolunteerCategoryPreferences.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The preferred volunteers for each bucket in the edition, ranked.</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<(int ParticipantId, string Name, int Rank)>>>
        LoadPreferredAsync(VolunteerStructureService.ActorContext actor, CancellationToken ct = default)
    {
        RequireOrganizer(actor);

        var rows = await _db.VolunteerCategoryPreferences
            .Where(p => p.EventId == actor.EventId)
            .Select(p => new
            {
                p.CategoryId,
                p.ParticipantId,
                p.Rank,
                Name = p.Participant.FullName != "" ? p.Participant.FullName : p.Participant.Email,
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.CategoryId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<(int, string, int)>)g
                    .OrderBy(r => r.Rank).ThenBy(r => r.Name)
                    .Select(r => (r.ParticipantId, r.Name, r.Rank))
                    .ToList());
    }
}
