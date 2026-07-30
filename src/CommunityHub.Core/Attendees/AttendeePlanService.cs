using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Attendees;

/// <summary>
/// One saved talk in a participant's personal plan ("My plan"). A flattened,
/// read-only projection carrying everything the plan page renders: the session
/// title, type/room/track, its schedule, the speaker name(s) joined for display,
/// and the public detail deep-link. <see cref="HasTime"/> is false for a saved
/// talk that has not been scheduled yet (it still appears so the participant
/// remembers they wanted it, grouped under a "time to be announced" heading).
/// </summary>
public sealed record SavedSessionRow(
    int SessionId,
    string Title,
    SessionType Type,
    string? Room,
    string? Track,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    string Speakers,
    string DetailUrl)
{
    /// <summary>True when the talk has a confirmed start time (so it sorts onto the timeline).</summary>
    public bool HasTime => StartsAt is not null;
}

/// <summary>
/// The whole "My plan" view-model: the participant's saved talks (scheduled ones
/// first, in start-time order; unscheduled ones after) plus convenience counts for
/// the empty / partial states. Built by <see cref="AttendeePlanBuilder"/> from raw
/// session rows so the ordering is unit-testable without a DbContext.
/// </summary>
public sealed class AttendeePlan
{
    /// <summary>The saved talks, scheduled-first then start-time ordered (ties → room, then title).</summary>
    public IReadOnlyList<SavedSessionRow> Sessions { get; init; } = Array.Empty<SavedSessionRow>();

    /// <summary>True when the participant has saved nothing yet (empty state).</summary>
    public bool IsEmpty => Sessions.Count == 0;

    /// <summary>How many saved talks have a confirmed time (vs. still to be announced).</summary>
    public int ScheduledCount => Sessions.Count(s => s.HasTime);
}

/// <summary>
/// A raw saved-session row as read from the database, before ordering / display
/// shaping. The speaker names arrive unsorted; the builder joins + alphabetises them.
/// </summary>
public sealed record RawSavedSession(
    int SessionId,
    string Title,
    SessionType Type,
    string? Room,
    string? Track,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    IReadOnlyList<string> SpeakerNames);

/// <summary>
/// Pure ordering / display shaping for the attendee personal plan — no DbContext,
/// no clock — so the sort order and the speaker-name join are unit-testable.
/// Scheduled talks come first (by start time, then room, then title); unscheduled
/// talks follow (alphabetical) so a saved-but-not-yet-timed talk is never lost.
/// </summary>
public static class AttendeePlanBuilder
{
    public static AttendeePlan Build(IEnumerable<RawSavedSession> rows)
    {
        var ordered = (rows ?? Array.Empty<RawSavedSession>())
            .Select(ToRow)
            .OrderBy(r => r.StartsAt is null)                         // scheduled first
            .ThenBy(r => r.StartsAt)
            .ThenBy(r => r.Room ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AttendeePlan { Sessions = ordered };
    }

    private static SavedSessionRow ToRow(RawSavedSession s)
    {
        var speakers = string.Join(", ", s.SpeakerNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        return new SavedSessionRow(
            s.SessionId,
            s.Title,
            s.Type,
            s.Room,
            s.Track,
            s.StartsAt,
            s.EndsAt,
            speakers,
            DetailUrl: $"/Sessions/{s.SessionId}");
    }
}

/// <summary>
/// The personal session-plan service backing the attendee self-service "My plan"
/// page and the "Save to my plan" toggle on the public sessions list. A participant
/// curates their own running order by saving the talks they want to attend; the plan
/// is private to them, edition-scoped, and never books a seat (booking stays in Zoho
/// Bookings — this is a personal bookmark list only).
///
/// <b>Own-row scoped, server-enforced:</b> every read and write is filtered to
/// <c>EventId == eventId</c> AND <c>ParticipantId == participantId</c>, so one
/// participant can never see or change another's plan. The toggle is idempotent
/// (the unique <c>(Event, Participant, Session)</c> index guarantees one row), and a
/// save is refused when the target session is not in the participant's own edition
/// (so a stale page can't save a foreign / cross-edition session id).
///
/// Every query is SQL-translatable (flat filters + a key projection); the read path
/// hands the rows to the pure <see cref="AttendeePlanBuilder"/> for ordering.
/// </summary>
public sealed class AttendeePlanService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public AttendeePlanService(CommunityHubDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// The set of session ids the participant has saved in this edition — used to
    /// render the "Saved ✓ / Save" state on the public sessions list without N
    /// queries. Empty when nothing is saved.
    /// </summary>
    public async Task<IReadOnlySet<int>> GetSavedSessionIdsAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var ids = await _db.SavedSessions
            .Where(x => x.EventId == eventId && x.ParticipantId == participantId)
            .Select(x => x.SessionId)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    /// <summary>True when the participant has this session in their plan.</summary>
    public async Task<bool> IsSavedAsync(
        int eventId, int participantId, int sessionId, CancellationToken ct = default) =>
        await _db.SavedSessions.AnyAsync(
            x => x.EventId == eventId && x.ParticipantId == participantId && x.SessionId == sessionId, ct);

    /// <summary>
    /// Toggle a session in the participant's plan: save it if not saved, remove it
    /// if already saved. Idempotent and self-correcting. Returns the new saved
    /// state (<c>true</c> = now saved, <c>false</c> = now removed). A save is
    /// REFUSED — returns <c>false</c> with no write — when the session does not
    /// exist in the participant's own edition (defends against a stale / forged
    /// session id), so the toggle can never create a cross-edition bookmark.
    /// </summary>
    public async Task<bool> ToggleAsync(
        int eventId, int participantId, int sessionId, CancellationToken ct = default)
    {
        var existing = await _db.SavedSessions.FirstOrDefaultAsync(
            x => x.EventId == eventId && x.ParticipantId == participantId && x.SessionId == sessionId, ct);

        if (existing is not null)
        {
            _db.SavedSessions.Remove(existing);
            await _db.SaveChangesAsync(ct);
            return false;
        }

        // Only allow saving a session that really belongs to this edition.
        var belongs = await _db.Sessions.AnyAsync(
            s => s.Id == sessionId && s.EventId == eventId && !s.IsServiceSession, ct);
        if (!belongs) return false;

        _db.SavedSessions.Add(new SavedSession
        {
            EventId = eventId,
            ParticipantId = participantId,
            SessionId = sessionId,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Remove a session from the participant's plan (the "Remove" action on the My
    /// plan page). Idempotent — removing one that is not saved is a no-op. Returns
    /// <c>true</c> when a row was actually removed.
    /// </summary>
    public async Task<bool> RemoveAsync(
        int eventId, int participantId, int sessionId, CancellationToken ct = default)
    {
        var existing = await _db.SavedSessions.FirstOrDefaultAsync(
            x => x.EventId == eventId && x.ParticipantId == participantId && x.SessionId == sessionId, ct);
        if (existing is null) return false;

        _db.SavedSessions.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Build the participant's "My plan" view for an edition: their saved
    /// (non-service) talks, ordered scheduled-first then by start time. Self-healing
    /// — a saved session that has since been deleted simply drops out (the join
    /// returns nothing for it), so the plan never shows a dangling entry. Read-only.
    /// </summary>
    public async Task<AttendeePlan> BuildPlanAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        // Flat, SQL-translatable projection: the saved sessions joined to their
        // (non-service) Session rows in this edition, with each talk's speaker
        // id/name pairs materialized for client-side join + ordering.
        var raw = await _db.SavedSessions
            .Where(x => x.EventId == eventId && x.ParticipantId == participantId)
            .Select(x => x.Session)
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.Type,
                s.Room,
                s.Track,
                s.StartsAt,
                s.EndsAt,
                SpeakerNames = s.SessionSpeakers
                    .Select(ss => ss.Participant.FullName)
                    .ToList(),
            })
            .ToListAsync(ct);

        var rows = raw.Select(s => new RawSavedSession(
            s.Id, s.Title, s.Type, s.Room, s.Track, s.StartsAt, s.EndsAt, s.SpeakerNames));

        return AttendeePlanBuilder.Build(rows);
    }

    // §193: the "download my plan as .ics" export (BuildPlanIcsAsync) was removed —
    // attendees no longer download a calendar file. Calendar entries arrive as e-mailed
    // invitations via CalendarInviteEmailService.
}
