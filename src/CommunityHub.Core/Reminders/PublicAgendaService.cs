using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// One talk on the public day-by-day agenda. A read-only projection of a scheduled
/// <see cref="Session"/> carrying just what the agenda timetable renders: the time
/// window, title, type/length, room/track, the joined speaker name(s), and the
/// always-public detail link (<c>/Sessions/{id}</c>) so a row deep-links to the full
/// session page. Unlike the flat <see cref="PublicSessionRow"/> the agenda only ever
/// contains <b>scheduled</b> talks (a session with no start time has no place on a
/// timetable), so <see cref="StartsAt"/> is non-nullable here.
/// </summary>
public sealed record PublicAgendaItem(
    int Id,
    string Title,
    SessionType Type,
    SessionLength Length,
    string? Room,
    string? Track,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    string Speakers);

/// <summary>
/// One day of the public agenda: the day's date (venue-local) and its talks in
/// start-time order (ties broken by room then title for a deterministic render).
/// </summary>
public sealed record PublicAgendaDay(
    DateOnly Date,
    IReadOnlyList<PublicAgendaItem> Items);

/// <summary>
/// The whole public agenda view: the edition display name, the per-day groups in
/// chronological order, and convenience counts for the empty/teaser states.
/// </summary>
public sealed record PublicAgendaView(
    string EventDisplayName,
    IReadOnlyList<PublicAgendaDay> Days,
    int ScheduledCount,
    int UnscheduledCount)
{
    /// <summary>True when the edition has no scheduled talks yet (agenda empty state).</summary>
    public bool IsEmpty => Days.Count == 0;
}

/// <summary>
/// Builds the data for the PUBLIC, no-login day-by-day agenda / timetable page
/// (<c>/Agenda</c>) — REQUIREMENTS §21 Public site "agenda/grid view". The flat
/// <c>/Sessions</c> list answers "what sessions exist + filter them"; the agenda
/// answers "what is the running order, day by day". It is scoped to the currently
/// <b>active</b> edition (same active-event resolution the public <c>/Sessions</c>
/// page uses), so an anonymous visitor sees only the live edition's programme.
///
/// Read-only: it never writes. Service sessions (breaks/lunch) are excluded, and only
/// <b>scheduled</b> talks (those with a start time) appear — an unscheduled talk has
/// no place on a timetable (it is still reachable from the flat <c>/Sessions</c>
/// list). The DB query is a flat, SQL-translatable projection; the day grouping +
/// ordering are pure (<see cref="PublicAgendaBuilder"/>) so they are unit-testable
/// without a DbContext.
/// </summary>
public sealed class PublicAgendaService
{
    private readonly CommunityHubDbContext _db;

    public PublicAgendaService(CommunityHubDbContext db) => _db = db;

    /// <summary>
    /// Build the public agenda for the active edition. Returns <c>null</c> when there
    /// is no active event (the page then renders a friendly "no event" empty state).
    /// </summary>
    public async Task<PublicAgendaView?> BuildAsync(CancellationToken ct = default)
    {
        var active = await _db.Events
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.Id)
            .Select(e => new { e.Id, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (active is null) return null;

        var eventId = active.Id;

        // Flat, translatable projection of this edition's non-service sessions. The
        // speaker names come back as a raw list; the join + day grouping + ordering
        // happen client-side in the pure builder (after this materialization
        // boundary) so no un-translatable shape reaches the relational provider.
        var raw = await _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .Select(s => new RawAgendaSession(
                s.Id,
                s.Title,
                s.Type,
                s.Length,
                s.Room,
                s.Track,
                s.StartsAt,
                s.EndsAt,
                s.SessionSpeakers
                    .Select(ss => ss.Participant.FullName)
                    .ToList()))
            .ToListAsync(ct);

        return PublicAgendaBuilder.Build(active.DisplayName, raw);
    }
}

/// <summary>
/// The flat DB row the agenda query materializes, before the pure day-grouping pass.
/// Carries the nullable <c>StartsAt</c> (the builder drops the unscheduled ones) and
/// the raw speaker name list (the builder joins + orders them for display).
/// </summary>
public sealed record RawAgendaSession(
    int Id,
    string Title,
    SessionType Type,
    SessionLength Length,
    string? Room,
    string? Track,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    IReadOnlyList<string> SpeakerNames);

/// <summary>
/// Pure day-by-day grouping for the public agenda. No DbContext / clock — every input
/// is explicit so the "group by day, order within a day, drop the unscheduled,
/// join+order speakers" logic is unit-testable in isolation. The day key is the
/// venue-local date of each talk's <see cref="DateTimeOffset.Date"/> (i.e. the date as
/// seen in the talk's own offset, the way Sessionize publishes the grid), so a talk
/// lands on the calendar day a visitor at the venue would expect.
/// </summary>
public static class PublicAgendaBuilder
{
    public static PublicAgendaView Build(string eventDisplayName, IReadOnlyList<RawAgendaSession> sessions)
    {
        sessions ??= Array.Empty<RawAgendaSession>();

        var scheduled = sessions.Where(s => s.StartsAt is not null).ToList();
        var unscheduledCount = sessions.Count - scheduled.Count;

        var days = scheduled
            .Select(s => new
            {
                Day = DateOnly.FromDateTime(s.StartsAt!.Value.Date),
                Item = ToItem(s),
            })
            .GroupBy(x => x.Day)
            .OrderBy(g => g.Key)
            .Select(g => new PublicAgendaDay(
                g.Key,
                g.Select(x => x.Item)
                    .OrderBy(i => i.StartsAt)
                    .ThenBy(i => i.Room ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();

        return new PublicAgendaView(eventDisplayName, days, scheduled.Count, unscheduledCount);
    }

    private static PublicAgendaItem ToItem(RawAgendaSession s)
    {
        var speakers = string.Join(", ", s.SpeakerNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        return new PublicAgendaItem(
            s.Id,
            s.Title,
            s.Type,
            s.Length,
            s.Room,
            s.Track,
            s.StartsAt!.Value,
            s.EndsAt,
            speakers);
    }
}
