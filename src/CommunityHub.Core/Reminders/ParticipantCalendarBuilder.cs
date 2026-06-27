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
///  - Speaker — milestone deadlines (seeded as dated
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

    private readonly ScheduleService? _schedule;

    // ScheduleService is optional so existing unit tests can construct the builder
    // with just the DbContext; DI always supplies it, so the live feed includes the
    // role-relevant schedule (sync-all).
    public ParticipantCalendarBuilder(CommunityHubDbContext db, ScheduleService? schedule = null)
    {
        _db = db;
        _schedule = schedule;
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
                // Speaker email overrides (null for non-speakers / unset). Calendar
                // mail prefers the calendar-specific override (wizard step 1).
                // Resolve the profile ONCE via a single ordered subquery: two
                // separate correlated scalar subqueries over the same table get
                // mis-bound by the provider (the sibling CalendarEmail came back
                // null while ContactEmailOverride resolved), so project both fields
                // from one deterministic row.
                SpeakerEmails = _db.SpeakerProfiles
                    .Where(sp => sp.ParticipantId == x.Id)
                    .OrderBy(sp => sp.Id)
                    .Select(sp => new { sp.CalendarEmail, sp.ContactEmailOverride })
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (p is null)
        {
            // Unknown participant: empty but valid calendar.
            return IcsCalendarBuilder.BuildFeed("Community Hub", "", "", Array.Empty<CalendarItem>());
        }

        // Calendar feed owner: calendar override ?? contact override ?? Sessionize.
        var ownerEmail = SpeakerProfile.CalendarEmailFor(
            p.Email, p.SpeakerEmails?.CalendarEmail, p.SpeakerEmails?.ContactEmailOverride);

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

        // SYNC-ALL: append the role-relevant SCHEDULE / key dates (move-in … party …
        // appreciation dinner) so the one subscription also carries them. Filtered to
        // the participant's role via ScheduleService; each gets a stable UID.
        if (_schedule is not null)
        {
            var role = await _db.Participants.AsNoTracking()
                .Where(x => x.Id == p.Id).Select(x => (ParticipantRole?)x.Role)
                .FirstOrDefaultAsync(ct);
            foreach (var s in await _schedule.GetForRoleAsync(p.EventId, role, ct))
            {
                items.Add(ToCalendarItem(s, uidHost));
            }
        }

        // SPEAKER SESSION sync: the participant's own Backstage / Sessionize sessions
        // (title + room) as timed entries, so a speaker's calendar shows when + where
        // they present. Linked via SessionSpeaker; only scheduled sessions are emitted.
        var sessions = await _db.SessionSpeakers.AsNoTracking()
            .Where(ss => ss.ParticipantId == p.Id
                      && ss.Session.EventId == p.EventId
                      && ss.Session.StartsAt != null)
            .Select(ss => new
            {
                ss.Session.Id,
                ss.Session.Title,
                ss.Session.Room,
                ss.Session.StartsAt,
                ss.Session.EndsAt,
            })
            .ToListAsync(ct);
        foreach (var s in sessions)
        {
            items.Add(new CalendarItem(
                Uid: $"session:{s.Id}@{uidHost}",
                Summary: $"My session: {s.Title}",
                Description: null,
                Location: s.Room,
                Start: s.StartsAt!.Value,
                End: s.EndsAt ?? s.StartsAt!.Value.AddHours(1),
                AllDay: false,
                AlarmsDaysBefore: new[] { 1 }));
        }

        return IcsCalendarBuilder.BuildFeed(
            calendarName, ownerEmail, p.FullName, items);
    }

    /// <summary>Convert a schedule entry to a calendar item (stable UID, all-day or timed).</summary>
    public static CalendarItem ToCalendarItem(ScheduleEntry s, string uidHost)
    {
        var key = s.Id > 0
            ? s.Id.ToString()
            : $"{s.StartsAt.UtcTicks}-{new string(s.Title.Where(char.IsLetterOrDigit).ToArray())}";
        var end = s.EndsAt ?? (s.AllDay ? s.StartsAt.AddDays(1) : s.StartsAt.AddHours(1));
        return new CalendarItem(
            Uid: $"sched:{key}@{uidHost}",
            Summary: s.Title,
            Description: s.Notes,
            Location: s.Location,
            Start: s.StartsAt,
            End: end,
            AllDay: s.AllDay,
            AlarmsDaysBefore: System.Array.Empty<int>());
    }

    /// <summary>
    /// One human-readable row of a participant's calendar feed, for the organizer
    /// "preview my feed" panel on the Calendar settings page. It mirrors exactly the
    /// items <see cref="BuildFeedAsync"/> would emit (same query, same scope), but
    /// returns plain values for the UI rather than RFC 5545 text — so an organizer
    /// can SEE what the subscribable feed contains without parsing an .ics file.
    /// </summary>
    public sealed record CalendarPreviewRow(
        string Summary, DateOnly Date, bool AllDay, string? Location);

    /// <summary>
    /// Build the human-readable preview of one participant's calendar feed (the
    /// same items as <see cref="BuildFeedAsync"/>, date-ordered). Returns an empty
    /// list for an unknown participant or one with no dated items. Read-only.
    /// </summary>
    public async Task<IReadOnlyList<CalendarPreviewRow>> BuildPreviewAsync(
        int participantId, CancellationToken ct = default)
    {
        var p = await _db.Participants
            .AsNoTracking()
            .Where(x => x.Id == participantId)
            .Select(x => new { x.Id, x.EventId, x.SponsorCompanyId })
            .FirstOrDefaultAsync(ct);
        if (p is null) return Array.Empty<CalendarPreviewRow>();

        var venueName = await _db.Events
            .AsNoTracking()
            .Where(e => e.Id == p.EventId)
            .Select(e => e.VenueName)
            .FirstOrDefaultAsync(ct);

        // "preview" is a stable, non-routable host: the preview never produces a
        // subscribable URL, so the UID host is irrelevant — it only keeps UIDs
        // well-formed for the shared item builder.
        var items = await BuildItemsAsync(
            p.Id, p.EventId, p.SponsorCompanyId, "preview", venueName, ct);

        return items
            .Select(i => new CalendarPreviewRow(
                Summary: i.Summary,
                Date: DateOnly.FromDateTime(i.Start.UtcDateTime),
                AllDay: i.AllDay,
                Location: i.Location))
            .ToList();
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
                // Single ordered subquery (see BuildFeedAsync) so both override
                // fields resolve from one deterministic row.
                SpeakerEmails = _db.SpeakerProfiles
                    .Where(sp => sp.ParticipantId == x.Id)
                    .OrderBy(sp => sp.Id)
                    .Select(sp => new { sp.CalendarEmail, sp.ContactEmailOverride })
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (p is null) return null;

        var ownerEmail = SpeakerProfile.CalendarEmailFor(
            p.Email, p.SpeakerEmails?.CalendarEmail, p.SpeakerEmails?.ContactEmailOverride);

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
                // Single ordered subquery (see BuildFeedAsync) so both override
                // fields resolve from one deterministic row.
                SpeakerEmails = _db.SpeakerProfiles
                    .Where(sp => sp.ParticipantId == x.Id)
                    .OrderBy(sp => sp.Id)
                    .Select(sp => new { sp.CalendarEmail, sp.ContactEmailOverride })
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

        var ownerEmail = SpeakerProfile.CalendarEmailFor(
            p.Email, p.SpeakerEmails?.CalendarEmail, p.SpeakerEmails?.ContactEmailOverride);
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
            // Strip the **bold**/__underline__/[label](url) markup to plain text so
            // the markers don't leak literally into the calendar entry.
            : CommunityHub.Core.Email.TaskMarkup.ToPlainText(description);
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
