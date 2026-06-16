using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Builds a participant's subscribable calendar feed (the body of
/// <c>GET /calendar/{token}.ics</c>) and one-off single-item .ics downloads.
///
/// The feed is built straight from the existing task / deadline model so it
/// reflects LIVE DB state — when a reminder is added, a deadline moves, or a
/// task is completed, the next fetch by the calendar client picks it up. There
/// is no separate sync store.
///
/// Role coverage falls out of the data, not a per-role code path:
///  - Speaker / MasterclassSpeaker — milestone deadlines (seeded as dated
///    <see cref="ParticipantTask"/> rows by SpeakerDeadlineSeeder).
///  - Volunteer — their shifts (VolunteerAvailability) + any assigned dated tasks.
///  - Organizer — their assigned dated tasks / deadlines.
/// Sponsor-company-scoped tasks (AssignedParticipantId = null, SponsorCompanyId
/// set) are included for a sponsor contact whose company matches, so a sponsor's
/// booth deliverables also appear.
///
/// Every item has a STABLE UID (task:{id} / shift:{id}:{slug}@{host}) so a
/// re-fetch UPDATES the existing entry instead of duplicating it. Deadline-style
/// items carry VALARM reminders 7 and 1 days before the due date.
/// </summary>
public sealed class ParticipantCalendarBuilder
{
    /// <summary>Days-before-due at which the feed emits a VALARM on a deadline.</summary>
    public static readonly int[] AlarmDaysBefore = { 7, 1 };

    private readonly CommunityHubDbContext _db;

    public ParticipantCalendarBuilder(CommunityHubDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Build the full VCALENDAR for one participant, scoped to their own items
    /// in their edition. <paramref name="uidHost"/> is the public host (e.g.
    /// "hub.expertslive.dk") used to make UIDs globally unique + stable.
    /// </summary>
    public async Task<string> BuildFeedAsync(
        int participantId, string uidHost, CancellationToken ct = default)
    {
        var p = await _db.Participants
            .AsNoTracking()
            .Where(x => x.Id == participantId)
            .Select(x => new
            {
                x.Id,
                x.EventId,
                x.Email,
                x.FullName,
                x.SponsorCompanyId,
                // Speaker contact-email override (null for non-speakers / unset).
                ContactEmailOverride = _db.SpeakerProfiles
                    .Where(sp => sp.ParticipantId == x.Id)
                    .Select(sp => sp.ContactEmailOverride)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (p is null)
        {
            // Unknown participant: empty but valid calendar.
            return IcsCalendarBuilder.BuildFeed("Community Hub", "", "", Array.Empty<CalendarItem>());
        }

        // ALL calendar invites use the effective address: override ?? Sessionize.
        var ownerEmail = SpeakerProfile.EffectiveEmailFor(p.Email, p.ContactEmailOverride);

        var ev = await _db.Events
            .AsNoTracking()
            .Where(e => e.Id == p.EventId)
            .Select(e => new { e.DisplayName, e.VenueName })
            .FirstOrDefaultAsync(ct);
        var calendarName = ev is null
            ? "Community Hub"
            : $"{ev.DisplayName} — my deadlines";

        var items = await BuildItemsAsync(
            p.Id, p.EventId, p.SponsorCompanyId, uidHost, ev?.VenueName, ct);

        return IcsCalendarBuilder.BuildFeed(
            calendarName, ownerEmail, p.FullName, items);
    }

    /// <summary>
    /// Build a single-item .ics for the "Download .ics" button on one task. The
    /// UID matches the feed's UID for the same task, so downloading then later
    /// subscribing does not create a duplicate. Returns null if the task is not
    /// the participant's (or has no due date — nothing to put on a calendar).
    /// </summary>
    public async Task<string?> BuildSingleTaskAsync(
        int participantId, int taskId, string uidHost, CancellationToken ct = default)
    {
        var p = await _db.Participants
            .AsNoTracking()
            .Where(x => x.Id == participantId)
            .Select(x => new
            {
                x.Id,
                x.EventId,
                x.Email,
                x.FullName,
                x.SponsorCompanyId,
                ContactEmailOverride = _db.SpeakerProfiles
                    .Where(sp => sp.ParticipantId == x.Id)
                    .Select(sp => sp.ContactEmailOverride)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (p is null) return null;

        var ownerEmail = SpeakerProfile.EffectiveEmailFor(p.Email, p.ContactEmailOverride);

        var task = await _db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId
                        && t.EventId == p.EventId
                        && t.DueDate != null
                        && (t.AssignedParticipantId == p.Id
                            || (p.SponsorCompanyId != null
                                && t.SponsorCompanyId == p.SponsorCompanyId)))
            .Select(t => new { t.Id, t.Title, t.Description, t.DueDate })
            .FirstOrDefaultAsync(ct);
        if (task is null || task.DueDate is null) return null;

        var ev = await _db.Events
            .AsNoTracking()
            .Where(e => e.Id == p.EventId)
            .Select(e => new { e.DisplayName, e.VenueName })
            .FirstOrDefaultAsync(ct);

        var item = TaskToItem(task.Id, task.Title, task.Description, task.DueDate.Value, uidHost, ev?.VenueName);
        return IcsCalendarBuilder.BuildFeed(
            ev?.DisplayName ?? "Community Hub", ownerEmail, p.FullName, new[] { item });
    }

    private async Task<List<CalendarItem>> BuildItemsAsync(
        int participantId, int eventId, string? sponsorCompanyId,
        string uidHost, string? venueName, CancellationToken ct)
    {
        var items = new List<CalendarItem>();

        // --- Dated tasks the participant owns (or their sponsor company owns) ---
        var tasks = await _db.Tasks
            .AsNoTracking()
            .Where(t => t.EventId == eventId
                        && t.DueDate != null
                        && (t.AssignedParticipantId == participantId
                            || (sponsorCompanyId != null
                                && t.SponsorCompanyId == sponsorCompanyId)))
            .Select(t => new { t.Id, t.Title, t.Description, t.DueDate, t.State })
            .ToListAsync(ct);

        foreach (var t in tasks)
        {
            // Even completed deadlines stay on the calendar as a record — the
            // SUMMARY marks them done so the user sees the history.
            var title = t.State == TaskState.Done ? $"[Done] {t.Title}" : t.Title;
            items.Add(TaskToItem(t.Id, title, t.Description, t.DueDate!.Value, uidHost, venueName));
        }

        // --- Volunteer shifts ---------------------------------------------------
        // Shift names are a config-driven free-text list; we render each picked
        // shift as an all-day informational entry on the edition's days so the
        // volunteer sees what they signed up for. Times are not modelled per
        // shift, so these are all-day with a stable per-shift UID.
        var vol = await _db.VolunteerAvailabilities
            .AsNoTracking()
            .Where(v => v.EventId == eventId && v.ParticipantId == participantId)
            .Select(v => new { v.Id, v.SelectedShifts, v.PreferredRole })
            .FirstOrDefaultAsync(ct);

        if (vol is not null && !string.IsNullOrWhiteSpace(vol.SelectedShifts))
        {
            var ev = await _db.Events
                .AsNoTracking()
                .Where(e => e.Id == eventId)
                .Select(e => new { e.StartDate, e.PreDayDate })
                .FirstOrDefaultAsync(ct);

            // Anchor to pre-day if present, else the event start date.
            var anchor = ev?.PreDayDate ?? ev?.StartDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var anchorStart = new DateTimeOffset(anchor.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            var shifts = vol.SelectedShifts
                .Split(new[] { ',', ';', '|', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var shift in shifts)
            {
                var slug = Slug(shift);
                var desc = string.IsNullOrWhiteSpace(vol.PreferredRole)
                    ? "Volunteer shift"
                    : $"Volunteer shift — preferred role: {vol.PreferredRole}";
                items.Add(new CalendarItem(
                    Uid: $"shift:{vol.Id}:{slug}@{uidHost}",
                    Summary: $"Volunteer: {shift}",
                    Description: desc,
                    Location: venueName,
                    Start: anchorStart,
                    End: anchorStart.AddDays(1),       // all-day DTEND is exclusive
                    AllDay: true,
                    AlarmsDaysBefore: new[] { 1 }));
            }
        }

        // --- Assigned volunteer tasks (the volunteer "My schedule") -------------
        // A volunteer's concrete assigned work (VolunteerTask via assignment) with
        // a due date becomes a calendar entry too, so the personal feed and the
        // "Add to my calendar" button on /Volunteer/MySchedule carry their shifts.
        // Stable UID voltask:{id} so a re-fetch updates, never duplicates.
        var volTaskIds = await _db.VolunteerTaskAssignments
            .AsNoTracking()
            .Where(a => a.EventId == eventId && a.ParticipantId == participantId)
            .Select(a => a.TaskId)
            .ToListAsync(ct);

        if (volTaskIds.Count > 0)
        {
            var volTasks = await _db.VolunteerTasks
                .AsNoTracking()
                .Where(t => t.EventId == eventId
                            && volTaskIds.Contains(t.Id)
                            && t.DueDate != null
                            && t.Status != VolunteerTaskStatus.Cancelled)
                .Select(t => new { t.Id, t.Title, t.Shift, t.TimeEnd, t.DueDate, t.Status })
                .ToListAsync(ct);

            foreach (var t in volTasks)
            {
                items.Add(VolunteerTaskToItem(
                    t.Id, t.Title, t.Shift, t.TimeEnd, t.DueDate!.Value, t.Status, uidHost, venueName));
            }
        }

        return items
            .OrderBy(i => i.Start)
            .ThenBy(i => i.Summary, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Build a single-item .ics for one ASSIGNED volunteer task. The UID matches
    /// the feed's UID for the same task (voltask:{id}), so downloading then later
    /// subscribing does not duplicate it. Returns null if the task is not assigned
    /// to the participant in this edition, has no due date, or is cancelled.
    /// </summary>
    public async Task<string?> BuildSingleVolunteerTaskAsync(
        int participantId, int taskId, string uidHost, CancellationToken ct = default)
    {
        var p = await _db.Participants
            .AsNoTracking()
            .Where(x => x.Id == participantId)
            .Select(x => new
            {
                x.Id,
                x.EventId,
                x.Email,
                x.FullName,
                ContactEmailOverride = _db.SpeakerProfiles
                    .Where(sp => sp.ParticipantId == x.Id)
                    .Select(sp => sp.ContactEmailOverride)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (p is null) return null;

        var assigned = await _db.VolunteerTaskAssignments
            .AsNoTracking()
            .AnyAsync(a => a.EventId == p.EventId
                           && a.ParticipantId == p.Id
                           && a.TaskId == taskId, ct);
        if (!assigned) return null;

        var task = await _db.VolunteerTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId
                        && t.EventId == p.EventId
                        && t.DueDate != null
                        && t.Status != VolunteerTaskStatus.Cancelled)
            .Select(t => new { t.Id, t.Title, t.Shift, t.TimeEnd, t.DueDate, t.Status })
            .FirstOrDefaultAsync(ct);
        if (task is null || task.DueDate is null) return null;

        var ownerEmail = SpeakerProfile.EffectiveEmailFor(p.Email, p.ContactEmailOverride);
        var ev = await _db.Events
            .AsNoTracking()
            .Where(e => e.Id == p.EventId)
            .Select(e => new { e.DisplayName, e.VenueName })
            .FirstOrDefaultAsync(ct);

        var item = VolunteerTaskToItem(
            task.Id, task.Title, task.Shift, task.TimeEnd, task.DueDate.Value, task.Status, uidHost, ev?.VenueName);
        return IcsCalendarBuilder.BuildFeed(
            ev?.DisplayName ?? "Community Hub", ownerEmail, p.FullName, new[] { item });
    }

    private static CalendarItem VolunteerTaskToItem(
        int taskId, string title, string? shift, string? timeEnd,
        DateOnly dueDate, VolunteerTaskStatus status, string uidHost, string? venueName)
    {
        var start = new DateTimeOffset(dueDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        // The shift / time-end fields are free text (the catalogue is config-driven
        // elsewhere), so they go into the description rather than parsed times — the
        // entry is an all-day item on the due date, like the other deadlines.
        var summary = status == VolunteerTaskStatus.Done ? $"[Done] {title}" : title;
        var parts = new List<string> { "Volunteer task." };
        if (!string.IsNullOrWhiteSpace(shift))
        {
            parts.Add(string.IsNullOrWhiteSpace(timeEnd) ? $"When: {shift}" : $"When: {shift}-{timeEnd}");
        }
        return new CalendarItem(
            Uid: $"voltask:{taskId}@{uidHost}",
            Summary: $"Volunteer: {summary}",
            Description: string.Join(" ", parts),
            Location: venueName,
            Start: start,
            End: start.AddDays(1),                  // all-day DTEND is exclusive
            AllDay: true,
            AlarmsDaysBefore: new[] { 1 });
    }

    private static CalendarItem TaskToItem(
        int taskId, string title, string? description,
        DateOnly dueDate, string uidHost, string? venueName)
    {
        var start = new DateTimeOffset(dueDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var desc = string.IsNullOrWhiteSpace(description)
            ? "Deadline from your Community Hub. Open the hub to update this item."
            : description;
        return new CalendarItem(
            Uid: $"task:{taskId}@{uidHost}",
            Summary: title,
            Description: desc,
            Location: venueName,
            Start: start,
            End: start.AddDays(1),                  // all-day DTEND is exclusive
            AllDay: true,
            AlarmsDaysBefore: AlarmDaysBefore);
    }

    private static string Slug(string text)
    {
        var chars = text.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray();
        var slug = new string(chars).Trim().Replace(' ', '-');
        return slug.Length > 40 ? slug[..40] : (slug.Length == 0 ? "shift" : slug);
    }
}
