using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>The outcome of a Sessionize SESSIONS import run.</summary>
public sealed record SessionImportResult(
    int Fetched,
    int Created,
    int Updated,
    int Skipped,
    int LinksCreated,
    int LinksRemoved,
    IReadOnlyList<string> Warnings,
    string? Error);

/// <summary>
/// Imports Sessionize SESSIONS (the v2 <c>All</c>/<c>Sessions</c> view, alongside
/// speakers) as <see cref="Session"/> rows and links each session to its
/// speaker(s) through <see cref="SessionSpeaker"/>.
///
/// Rules (consistent with the speaker import - see
/// <see cref="SessionizeImportService"/>):
///  - <b>Upsert by the Sessionize session id</b> within the edition: a known id
///    refreshes the existing row in place; a new id creates one. This is the same
///    new/changed-upsert semantics the speaker delta uses.
///  - <b>Never delete</b>: a session removed in Sessionize is left for an organizer
///    to remove, never auto-deleted (same as speakers).
///  - <b>Speaker links are matched on the Sessionize speaker id</b>, mapped through
///    the parsed speakers (id -> email) to the participant the speaker import
///    created (email -> Participant within the edition). A session speaker whose id
///    has no matching participant is reported and left unlinked, not dropped.
///  - <b>Sessions are import-driven and in-hub only</b> (NOT a Backstage/public
///    concern), so the importer overwrites the imported fields each run; the link
///    set is reconciled to exactly the current Sessionize speaker set per session
///    (stale links removed, missing links added) - this is hub-side import state,
///    not an editable hub field, so reconciling it flushes nothing the organizer
///    owns.
///
/// Run the SPEAKER import first (so the participants exist to link to); the
/// combined <c>SessionizeApiImportService</c> already does this.
/// </summary>
public sealed class SessionImportService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public SessionImportService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Upsert the supplied sessions for an edition and reconcile their speaker
    /// links. <paramref name="speakers"/> carries the parsed Sessionize speakers
    /// (id -> email) so a session's speaker ids resolve to participants; pass the
    /// same list the speaker import consumed. Never throws for bad data - errors
    /// are in the result. <paramref name="warnings"/> from the source are carried
    /// through.
    /// </summary>
    public async Task<SessionImportResult> ImportSessionsAsync(
        int eventId,
        IReadOnlyList<SessionizeSession> sessions,
        IReadOnlyList<SessionizeSpeaker> speakers,
        IReadOnlyList<string> warnings,
        CancellationToken ct = default)
    {
        var carriedWarnings = new List<string>(warnings);

        // Sessionize speaker id -> email (from the parsed speakers).
        var idToEmail = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in speakers)
        {
            if (!string.IsNullOrWhiteSpace(s.SessionizeId)
                && !string.IsNullOrWhiteSpace(s.Email))
            {
                idToEmail[s.SessionizeId] = s.Email;
            }
        }

        // email -> participant id (within the edition).
        var emailToParticipantId = await _db.Participants
            .Where(p => p.EventId == eventId)
            .ToDictionaryAsync(p => p.Email, p => p.Id, StringComparer.OrdinalIgnoreCase, ct);

        // Existing sessions for this edition, by Sessionize id, with their links.
        var existing = await _db.Sessions
            .Where(s => s.EventId == eventId)
            .Include(s => s.SessionSpeakers)
            .ToListAsync(ct);
        var bySessionizeId = existing
            .Where(s => !string.IsNullOrWhiteSpace(s.SessionizeId))
            .GroupBy(s => s.SessionizeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var now = _clock.GetUtcNow();
        int created = 0, updated = 0, skipped = 0, linksCreated = 0, linksRemoved = 0;

        foreach (var src in sessions)
        {
            if (string.IsNullOrWhiteSpace(src.SessionizeId))
            {
                carriedWarnings.Add(
                    $"Session '{src.Title}': skipped - no Sessionize id.");
                skipped++;
                continue;
            }

            if (!bySessionizeId.TryGetValue(src.SessionizeId, out var session))
            {
                session = new Session
                {
                    EventId = eventId,
                    SessionizeId = src.SessionizeId,
                    CreatedAt = now,
                };
                _db.Sessions.Add(session);
                bySessionizeId[src.SessionizeId] = session;
                created++;
            }
            else
            {
                updated++;
            }

            session.Title = src.Title;
            session.Abstract = src.Abstract;
            session.Room = src.Room;
            session.Track = src.Track;
            session.StartsAt = src.StartsAt;
            session.EndsAt = src.EndsAt;
            session.IsServiceSession = src.IsServiceSession;
            // Imported sessions are NEVER hub-added; derive Type + Length defaults
            // from the source category/format + duration (these are import-owned,
            // refreshed each run).
            session.IsHubAdded = false;
            session.Length = SessionDefaultsMapper.MapLength(src.StartsAt, src.EndsAt);
            // Respect an organizer's MANUAL type override — a re-import never clobbers
            // it. Otherwise derive the type from the source category/format + duration.
            if (!session.TypeIsManualOverride)
                session.Type = SessionDefaultsMapper.MapType(src.Category, session.Length);
            session.UpdatedAt = now;
            session.LastSessionizeImportAt = now;

            // Resolve the session's Sessionize speaker ids to participant ids.
            var desiredParticipantIds = new HashSet<int>();
            foreach (var spkId in src.SpeakerIds)
            {
                if (idToEmail.TryGetValue(spkId, out var email)
                    && emailToParticipantId.TryGetValue(email, out var pid))
                {
                    desiredParticipantIds.Add(pid);
                }
                else
                {
                    carriedWarnings.Add(
                        $"Session '{src.Title}': speaker id '{spkId}' has no "
                        + "matching imported speaker (emailless or not yet "
                        + "imported) - left unlinked.");
                }
            }

            // Reconcile links: add missing, remove stale. (Import state, not a
            // hub-editable field, so reconciling flushes nothing the organizer owns.)
            var currentLinks = session.SessionSpeakers.ToList();
            foreach (var link in currentLinks)
            {
                if (!desiredParticipantIds.Contains(link.ParticipantId))
                {
                    session.SessionSpeakers.Remove(link);
                    _db.Set<SessionSpeaker>().Remove(link);
                    linksRemoved++;
                }
            }
            var existingPids = currentLinks
                .Where(l => desiredParticipantIds.Contains(l.ParticipantId))
                .Select(l => l.ParticipantId)
                .ToHashSet();
            foreach (var pid in desiredParticipantIds)
            {
                if (existingPids.Contains(pid)) continue;
                session.SessionSpeakers.Add(new SessionSpeaker
                {
                    Session = session,
                    ParticipantId = pid,
                });
                linksCreated++;
            }
        }

        await _db.SaveChangesAsync(ct);

        return new SessionImportResult(
            sessions.Count, created, updated, skipped,
            linksCreated, linksRemoved, carriedWarnings, null);
    }
}
