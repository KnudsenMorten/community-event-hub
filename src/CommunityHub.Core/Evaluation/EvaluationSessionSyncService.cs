using CommunityHub.Core.Data;
using CommunityHub.Core.Domain.Evaluation;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §743 C3 — mirror CEH's sessions into Session Evaluation, and backfill the attribution of
/// responses that arrived before their session existed.
/// </summary>
/// <remarks>
/// <para>🔒 <b>CEH IS THE MASTER, but it never overwrites silently.</b> Every sync-driven change
/// records the previous and the new value. A room reassignment made during the event is exactly the
/// change that would otherwise vanish, taking device attribution with it — and a wrong figure has
/// to be traceable back to the moment the session moved.</para>
///
/// <para>🔑 <b>Backfill is the point of running this at all right now.</b> Ingest has been storing
/// presses with <c>SessionId = null</c> because no sessions existed (§5.3's "venue and date only"
/// state). Once sessions arrive, those raw records can be attributed — which is precisely why the
/// brief insists on storing raw responses rather than computed scores.</para>
/// </remarks>
public sealed class EvaluationSessionSyncService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public EvaluationSessionSyncService(CommunityHubDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>What one sync pass did. <paramref name="Changes"/> is the human-readable audit.</summary>
    public sealed record Result(
        int RoomsCreated, int SessionsCreated, int SessionsUpdated, int Skipped,
        int Backfilled, IReadOnlyList<string> Changes, IReadOnlyList<string> Warnings,
        int SpeakerLinksAdded = 0, int SpeakerLinksRemoved = 0);

    /// <summary>The room-name match key: trimmed, lower-cased, inner whitespace collapsed.</summary>
    public static string RoomKey(string? name) =>
        string.Join(' ', (name ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    public async Task<Result> SyncAsync(int eventId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var changes = new List<string>();
        var warnings = new List<string>();

        // CEH sessions that can actually be collected against: a real slot, in a room.
        // §428's service-session exclusion applies — a service session is not a talk.
        var cehSessions = await _db.Sessions
            .Where(s => s.EventId == eventId
                        && !s.IsServiceSession
                        && s.StartsAt != null && s.EndsAt != null)
            .Select(s => new
            {
                s.Id, s.Title, s.Room, s.Track, s.StartsAt, s.EndsAt,
            })
            .ToListAsync(ct);

        var rooms = await _db.EvaluationRooms.Where(r => r.EventId == eventId).ToListAsync(ct);
        var roomsByKey = rooms.ToDictionary(r => r.NameKey, StringComparer.Ordinal);
        var existing = await _db.EvaluationSessions
            .Where(s => s.EventId == eventId && s.CehSessionId != null)
            .ToListAsync(ct);
        var byCehId = existing.ToDictionary(s => s.CehSessionId!.Value);

        int roomsCreated = 0, created = 0, updated = 0, skipped = 0;

        foreach (var c in cehSessions)
        {
            var start = c.StartsAt!.Value;
            var end = c.EndsAt!.Value;

            if (end <= start)
            {
                // A zero or negative slot has no window to collect in. Reported, not guessed at.
                warnings.Add($"'{c.Title}' has an end at or before its start — skipped.");
                skipped++;
                continue;
            }

            // --- room -------------------------------------------------------------------------
            EvaluationRoom? room = null;
            var key = RoomKey(c.Room);
            if (key.Length > 0)
            {
                if (!roomsByKey.TryGetValue(key, out room))
                {
                    room = new EvaluationRoom
                    {
                        EventId = eventId, Name = c.Room!.Trim(), NameKey = key, CreatedAt = now,
                    };
                    _db.EvaluationRooms.Add(room);
                    await _db.SaveChangesAsync(ct);   // need the id for the session below
                    roomsByKey[key] = room;
                    roomsCreated++;
                    changes.Add($"Room created: '{room.Name}'.");
                }
            }
            else
            {
                // Attribution needs a room. Say so rather than storing a session nothing can reach.
                warnings.Add($"'{c.Title}' has no room in CEH — it cannot collect feedback until it does.");
            }

            var (opensAt, closesAt) = EvaluationAttribution.WindowFor(start, end);
            var lengthMinutes = (int)Math.Round((end - start).TotalMinutes);

            // --- session ----------------------------------------------------------------------
            if (!byCehId.TryGetValue(c.Id, out var target))
            {
                _db.EvaluationSessions.Add(new EvaluationSession
                {
                    EventId = eventId, CehSessionId = c.Id, RoomId = room?.Id,
                    Title = c.Title, TrackName = c.Track,
                    ScheduledStart = start, ScheduledEnd = end,
                    CollectionWindowOpensAt = opensAt, CollectionWindowClosesAt = closesAt,
                    ScheduledLengthMinutes = lengthMinutes,
                    CreatedAt = now, UpdatedAt = now,
                });
                created++;
                changes.Add($"Session created: '{c.Title}' in '{room?.Name ?? "(no room)"}' "
                            + $"{start:dd MMM HH:mm}–{end:HH:mm}.");
                continue;
            }

            // 🔒 CEH wins — and every replaced value is NAMED. This is the difference between a
            // traceable figure and a mystery.
            var diffs = new List<string>();
            if (target.RoomId != room?.Id)
            {
                var before = rooms.FirstOrDefault(r => r.Id == target.RoomId)?.Name ?? "(none)";
                diffs.Add($"room '{before}' → '{room?.Name ?? "(none)"}'");
                target.RoomId = room?.Id;
            }
            if (target.ScheduledStart != start || target.ScheduledEnd != end)
            {
                diffs.Add($"time {target.ScheduledStart:dd MMM HH:mm}–{target.ScheduledEnd:HH:mm} "
                          + $"→ {start:dd MMM HH:mm}–{end:HH:mm}");
                target.ScheduledStart = start;
                target.ScheduledEnd = end;
                target.CollectionWindowOpensAt = opensAt;
                target.CollectionWindowClosesAt = closesAt;
                target.ScheduledLengthMinutes = lengthMinutes;
            }
            if (!string.Equals(target.Title, c.Title, StringComparison.Ordinal))
            {
                diffs.Add($"title '{target.Title}' → '{c.Title}'");
                target.Title = c.Title;
            }
            if (!string.Equals(target.TrackName, c.Track, StringComparison.Ordinal))
            {
                target.TrackName = c.Track;
            }

            if (diffs.Count > 0)
            {
                target.UpdatedAt = now;
                updated++;
                changes.Add($"Session '{c.Title}': " + string.Join("; ", diffs) + ".");
            }
        }

        await _db.SaveChangesAsync(ct);

        // --- overlap validation (§743 item 32) ------------------------------------------------
        // Two sessions may never overlap in ONE ROOM: attribution would be genuinely ambiguous.
        // We REPORT rather than refuse the sync, because CEH is the master and refusing would just
        // leave us stale — but an organiser must see it, since every press in the overlap is
        // credited to whichever session the tie-break picks.
        var all = await _db.EvaluationSessions
            .Where(s => s.EventId == eventId && s.RoomId != null)
            .ToListAsync(ct);
        foreach (var group in all.GroupBy(s => s.RoomId!.Value))
        {
            var ordered = group.OrderBy(s => s.ScheduledStart).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var prev = ordered[i - 1];
                var cur = ordered[i];
                if (EvaluationAttribution.Overlaps(
                        prev.ScheduledStart, prev.ScheduledEnd, cur.ScheduledStart, cur.ScheduledEnd))
                {
                    warnings.Add(
                        $"OVERLAP in one room: '{prev.Title}' and '{cur.Title}' both run "
                        + $"{cur.ScheduledStart:dd MMM HH:mm}. Presses in the overlap cannot be "
                        + "attributed unambiguously — fix the schedule in CEH.");
                }
            }
        }

        var (linksAdded, linksRemoved) = await SyncSpeakersAsync(eventId, changes, warnings, now, ct);

        var backfilled = await BackfillAsync(eventId, ct);

        return new Result(roomsCreated, created, updated, skipped, backfilled, changes, warnings,
                          linksAdded, linksRemoved);
    }

    /// <summary>
    /// §743 C3 — mirror each session's SPEAKERS. The brief's <c>SessionSpeaker</c>: populated by the
    /// sync, never by hand, so a speaker change in CEH re-points automatically.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Not an authorisation record.</b> §743.3 put the speaker's results inside CEH, so
    /// who may READ a session is decided by CEH's own <c>MineAsSpeaker</c>. These rows feed the C7
    /// speaker-history section, the C0 report-ready notification, and <c>scope=speaker</c>.</para>
    ///
    /// <para>🔒 <b><c>Participant.Email</c> ONLY</b> (§743.2). Not <c>ContactEmailOverride</c>, not
    /// <c>SecondaryEmail</c>, not <c>CalendarEmail</c>, not <c>AlternateEmail</c> — those are
    /// delivery or alternate addresses, and CEH signs a session in with the PRIMARY. Normalised with
    /// the very same <see cref="Auth.PinLoginService.NormalizeEmail"/> the login uses, so the two
    /// cannot drift apart.</para>
    ///
    /// <para>🔑 <b>Removals are logged as loudly as additions.</b> A speaker dropped from a session
    /// in CEH stops receiving that session's report — which is correct, and is also exactly the kind
    /// of silent change that gets reported later as "I never got my results".</para>
    /// </remarks>
    private async Task<(int Added, int Removed)> SyncSpeakersAsync(
        int eventId, List<string> changes, List<string> warnings, DateTimeOffset now,
        CancellationToken ct)
    {
        var mirrored = await _db.EvaluationSessions
            .Where(s => s.EventId == eventId && s.CehSessionId != null)
            .Select(s => new { s.Id, s.Title, CehId = s.CehSessionId!.Value })
            .ToListAsync(ct);
        if (mirrored.Count == 0) return (0, 0);

        var cehIds = mirrored.Select(m => m.CehId).ToList();

        // 🔒 Participant.Email and nothing else — see the remarks. Read as a projection so no other
        // address column is even in scope to be picked by mistake later.
        var cehLinks = await _db.SessionSpeakers
            .Where(ss => cehIds.Contains(ss.SessionId))
            .Select(ss => new
            {
                ss.SessionId,
                ss.ParticipantId,
                Email = ss.Participant.Email,
                Name = ss.Participant.FullName,
            })
            .ToListAsync(ct);

        var wantedBySession = new Dictionary<int, Dictionary<string, (int Pid, string? Name)>>();
        foreach (var m in mirrored) wantedBySession[m.CehId] = new(StringComparer.Ordinal);

        foreach (var link in cehLinks)
        {
            var email = Auth.PinLoginService.NormalizeEmail(link.Email ?? string.Empty);
            if (email.Length == 0)
            {
                // 🔑 Reported, never stored as an empty key. A speaker with no primary address
                // cannot be mailed a report and cannot be grouped — an organiser has to fix it in
                // CEH, and a blank row would hide that rather than surface it.
                warnings.Add(
                    $"Speaker '{link.Name ?? "(unnamed)"}' on a mirrored session has no primary "
                    + "e-mail in CEH — not linked, so they will not receive their report.");
                continue;
            }
            wantedBySession[link.SessionId][email] = (link.ParticipantId, link.Name);
        }

        var sessionIds = mirrored.Select(m => m.Id).ToList();
        var existing = await _db.EvaluationSessionSpeakers
            .Where(x => sessionIds.Contains(x.EvaluationSessionId))
            .ToListAsync(ct);
        var existingBySession = existing
            .GroupBy(x => x.EvaluationSessionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        int added = 0, removed = 0;

        foreach (var m in mirrored)
        {
            var wanted = wantedBySession[m.CehId];
            existingBySession.TryGetValue(m.Id, out var have);
            have ??= new List<Domain.Evaluation.EvaluationSessionSpeaker>();

            foreach (var row in have.Where(r => !wanted.ContainsKey(r.SpeakerEmail)).ToList())
            {
                _db.EvaluationSessionSpeakers.Remove(row);
                removed++;
                changes.Add($"Speaker unlinked from '{m.Title}': {row.SpeakerEmail}.");
            }

            var haveEmails = have.Select(r => r.SpeakerEmail).ToHashSet(StringComparer.Ordinal);
            foreach (var (email, who) in wanted.Where(w => !haveEmails.Contains(w.Key)))
            {
                _db.EvaluationSessionSpeakers.Add(new Domain.Evaluation.EvaluationSessionSpeaker
                {
                    EvaluationSessionId = m.Id,
                    SpeakerEmail = email,
                    DisplayName = who.Name,
                    CehParticipantId = who.Pid,
                    CreatedAt = now,
                });
                added++;
                changes.Add($"Speaker linked to '{m.Title}': {email}.");
            }

            // Display names drift (a rename in CEH). Follow it silently — it is display only and
            // nothing keys on it, so logging every rename would only bury the real changes above.
            foreach (var row in have.Where(r => wanted.ContainsKey(r.SpeakerEmail)))
            {
                var want = wanted[row.SpeakerEmail];
                if (!string.Equals(row.DisplayName, want.Name, StringComparison.Ordinal))
                {
                    row.DisplayName = want.Name;
                }
                row.CehParticipantId = want.Pid;
            }
        }

        if (added > 0 || removed > 0 || _db.ChangeTracker.HasChanges())
        {
            await _db.SaveChangesAsync(ct);
        }

        return (added, removed);
    }

    /// <summary>
    /// Attribute responses that arrived before their session existed — or before their device was
    /// linked to a room. Only ever fills a NULL <c>SessionId</c>: an already-attributed response is
    /// never re-pointed here, because that would silently move a press between speakers.
    /// </summary>
    public async Task<int> BackfillAsync(int eventId, CancellationToken ct = default)
    {
        var unattributed = await _db.EvaluationResponses
            .Where(r => r.EventId == eventId && r.SessionId == null && r.SerialNumber != null)
            .ToListAsync(ct);
        if (unattributed.Count == 0) return 0;

        var deviceRooms = await _db.EvaluationDevices
            .Where(d => d.EventId == eventId && d.RoomId != null)
            .Select(d => new { d.SerialNumber, RoomId = d.RoomId!.Value })
            .ToListAsync(ct);
        if (deviceRooms.Count == 0) return 0;
        var roomOf = deviceRooms.ToDictionary(x => x.SerialNumber, x => x.RoomId, StringComparer.Ordinal);

        var sessions = await _db.EvaluationSessions
            .Where(s => s.EventId == eventId && s.RoomId != null)
            .ToListAsync(ct);
        var byRoom = sessions.GroupBy(s => s.RoomId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var n = 0;
        foreach (var r in unattributed)
        {
            if (!roomOf.TryGetValue(r.SerialNumber!, out var roomId)) continue;
            if (!byRoom.TryGetValue(roomId, out var candidates)) continue;

            var match = EvaluationAttribution.Resolve(candidates, r.CollectionTimestamp);
            if (match is null) continue;   // genuinely outside every window — stays venue-and-date

            r.SessionId = match.Id;
            n++;
        }

        if (n > 0) await _db.SaveChangesAsync(ct);
        return n;
    }
}
