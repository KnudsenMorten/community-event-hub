using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>The outcome of a Sessionize SESSIONS import run.</summary>
/// <summary>
/// §999 — ONE Sessionize field that differs from the CEH value CEH now owns.
/// </summary>
/// <param name="SessionTitle">The CEH session it belongs to, for the mail.</param>
/// <param name="Field">The organizer-facing field name ("Date / time", "Room", "Track", "Tags").</param>
/// <param name="InCeh">What CEH holds — the value that WINS and is left untouched.</param>
/// <param name="InSessionize">What Sessionize says, for the operator to judge.</param>
/// <param name="SessionId">
/// §1024 — which CEH session this belongs to, so the report can be deduped PER SESSION. A new
/// disagreement on session B must not be swallowed because session A's was already mailed.
/// </param>
public sealed record SessionizeFieldDeviation(
    string SessionTitle, string Field, string? InCeh, string? InSessionize, int SessionId = 0);

public sealed record SessionImportResult(
    int Fetched,
    int Created,
    int Updated,
    int Skipped,
    int LinksCreated,
    int LinksRemoved,
    IReadOnlyList<string> Warnings,
    string? Error,
    /// <summary>
    /// §999 — the CEH-owned fields where Sessionize now disagrees. Reported, never applied.
    /// </summary>
    IReadOnlyList<SessionizeFieldDeviation>? Deviations = null)
{
    public IReadOnlyList<SessionizeFieldDeviation> DeviationsOrEmpty =>
        Deviations ?? Array.Empty<SessionizeFieldDeviation>();
}

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

    // §299.6/b5: the config room registry (warn-only validation) — optional so
    // legacy constructions/tests stay valid; null or an empty registry keeps the
    // unknown-room warning quiet.
    private readonly Config.RoomRegistryService? _rooms;

    // §299.8/b7: the configured level list, for deriving Session.LevelCode from
    // the source Level label. Optional; null falls back to the "(NNN)" parse only.
    private readonly Config.SessionOptionsService? _options;

    public SessionImportService(
        CommunityHubDbContext db,
        TimeProvider clock,
        Config.RoomRegistryService? rooms = null,
        Config.SessionOptionsService? options = null)
    {
        _db = db;
        _clock = clock;
        _rooms = rooms;
        _options = options;
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
        // §999 — CEH-owned fields where Sessionize now disagrees. Collected, never applied.
        var deviations = new List<SessionizeFieldDeviation>();

        foreach (var src in sessions)
        {
            if (string.IsNullOrWhiteSpace(src.SessionizeId))
            {
                carriedWarnings.Add(
                    $"Session '{src.Title}': skipped - no Sessionize id.");
                skipped++;
                continue;
            }

            bool isNew;
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
                isNew = true;
            }
            else
            {
                updated++;
                isNew = false;
            }

            // 🔑 §999 — TWO CLASSES OF FIELD, AND THE SPLIT IS THE WHOLE DESIGN.
            //
            // Operator 2026-08-09: *"date/time, location/room and tags are at this point owned by
            // CEH and should not be overwritten automatically, but the comparison must state
            // exactly which fields differ"* — plus track, which is *"initially coming from
            // sessionize in the create in ceh, hereafter ceh owns the track field"*.
            //
            // ⇒ CONTENT (title, abstract, speakers) stays Sessionize-owned and is copied every run.
            //   SCHEDULING (date/time, room, track, tags) is CEH-owned ON AN EXISTING ROW and is
            //   only ever REPORTED. On CREATE everything comes across 1:1 — there is no CEH value
            //   to protect yet, and seeding from Sessionize is exactly what create is for.
            //
            // ⚠️ This REVERSES the previous behaviour, where every field was overwritten hourly.
            // It is also what resolves the two-owner conflict on Room: Zoho→CEH is disabled (§1000)
            // and Sessionize no longer writes it, so CEH is now the single owner of the schedule.
            session.Title = src.Title;
            session.Abstract = src.Abstract;
            session.Level = src.Level;

            if (isNew)
            {
                // CREATE — seed 1:1 from Sessionize. Nothing to protect.
                session.Room = src.Room;
                session.Track = src.Track;
                session.Tags = src.Tags;
            }
            else
            {
                // EXISTING — CEH owns these. Compare and report; never write.
                AddDeviation(deviations, session, "Room", session.Room, src.Room);
                // 🔴 §1011 — a COMMON-FOR-ALL-TRACKS session has no track by definition, so
                // Sessionize's track is not a disagreement, it is the thing the tick box overrules.
                // Reporting it would mail him hourly about a field he deliberately switched off —
                // the §594 failure wearing a different hat.
                if (!session.IsCommonForAllTracks)
                    AddDeviation(deviations, session, "Track", session.Track, src.Track);
                AddDeviation(deviations, session, "Tags", session.Tags, src.Tags);
            }
            // §299.8/b7: derive the NUMERIC level code from the label against the
            // configured sessionLevels (label match, else "(NNN)" digits). Unknown
            // labels stay null (string-only behaviour). Re-derived each pull.
            session.LevelCode = _options is not null
                ? _options.DeriveLevelCode(src.Level)
                : Config.SessionOptionsService.DeriveLevelCode(
                    src.Level, Array.Empty<Config.SessionLevelOption>());
            // §999 — DATE/TIME is CEH-owned on an existing row. On create it seeds 1:1.
            // 🔒 `IsDateOverridden` is now redundant for the import (nothing overwrites the
            // schedule any more) but is LEFT IN PLACE: other code reads it, and removing a flag
            // whose absence is invisible is how a silent regression gets shipped.
            if (isNew)
            {
                session.StartsAt = src.StartsAt;
                session.EndsAt = src.EndsAt;
            }
            else if (session.StartsAt != src.StartsAt || session.EndsAt != src.EndsAt)
            {
                AddDeviation(deviations, session, "Date / time",
                    FormatRange(session.StartsAt, session.EndsAt),
                    FormatRange(src.StartsAt, src.EndsAt));
            }
            session.IsServiceSession = src.IsServiceSession;
            // Imported sessions are NEVER hub-added; derive Type + Length defaults
            // from the source category/format + duration (these are import-owned,
            // refreshed each run).
            session.IsHubAdded = false;
            // Length prefers the scheduled times, then the Format label's duration hint
            // ("(60 min)" / "Master Class") — the times are empty until the Sessionize grid
            // is published, so without the label every imported session looked like 60 min.
            // 🔒 §999 — derived from the SCHEDULE THAT WON, not from Sessionize's. On an existing
            // row CEH owns the times, so deriving the length from Sessionize would print a duration
            // that contradicts the start/end shown right next to it.
            session.Length = SessionDefaultsMapper.MapLength(
                session.StartsAt, session.EndsAt, src.Category);
            // §154: the exact numeric minutes for display ("60 min"). On create the parser's value
            // is the best available; afterwards the CEH schedule decides, falling back to the
            // parser's hint when CEH has no times yet.
            session.LengthMinutes = isNew
                ? src.LengthMinutes
                : (session.StartsAt is { } cs && session.EndsAt is { } ce && ce > cs
                    ? (int)Math.Round((ce - cs).TotalMinutes)
                    : src.LengthMinutes);
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
            // §326ax GUARD: the speaker→participant join is keyed on e-mail, and e-mail comes
            // from Sessionize's token-protected "emails" SIDE-VIEW — whose failure is only a
            // WARNING (SessionizeApiClient), unlike the main speakers view which fails closed.
            // When that side-view is down, idToEmail is empty for EVERY speaker, so
            // desiredParticipantIds is empty for EVERY session and this loop would delete
            // every session→speaker link in the edition: the public agenda loses all its
            // speakers and the speaker portal loses their sessions. An empty join is never
            // evidence that speakers were unassigned — only prune when the roster resolved.
            if (idToEmail.Count > 0)
            {
                foreach (var link in currentLinks)
                {
                    if (!desiredParticipantIds.Contains(link.ParticipantId))
                    {
                        session.SessionSpeakers.Remove(link);
                        _db.Set<SessionSpeaker>().Remove(link);
                        linksRemoved++;
                    }
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

        // §299.6/b5 — WARN-ONLY room validation (option A): after the upsert,
        // surface every imported room name that is not in the configured registry
        // (sessionRooms). Names must be byte-identical across systems, so this is
        // an exact (ordinal) check; it NEVER blocks the import. Quiet when no
        // registry is configured.
        if (_rooms is { HasEntries: true })
        {
            var unknownRooms = sessions
                .Select(s => s.Room)
                .Where(r => !string.IsNullOrWhiteSpace(r) && !_rooms.IsKnown(r))
                .Select(r => r!.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToList();
            foreach (var room in unknownRooms)
            {
                carriedWarnings.Add(
                    $"Unknown room '{room}': not in the configured room registry "
                    + "(sessionRooms). Room names must be byte-identical across "
                    + "systems — check for a typo/rename. The import was NOT blocked.");
            }
        }

        // 🔴 §1024 — ONE MAIL PER DISTINCT DISAGREEMENT. Filtered BEFORE the save, so the hash
        // stamps and the report are written in the same transaction: a crash between them would
        // otherwise either mail twice or never mail again.
        //
        // 🔒 Scoped to the sessions this run actually touched. A session Sessionize no longer
        // returns keeps its stamp rather than being silently "cleared and re-mailed" the day it
        // reappears.
        var toReport = KeepUnreported(deviations, bySessionizeId.Values);

        await _db.SaveChangesAsync(ct);

        return new SessionImportResult(
            sessions.Count, created, updated, skipped,
            linksCreated, linksRemoved, carriedWarnings, null, toReport);
    }

    /// <summary>§999 — record a CEH-vs-Sessionize difference, ignoring pure formatting.</summary>
    /// <remarks>
    /// <para>🔒 Compared through <see cref="Integrations.RichTextCompare"/> so a trailing space, an
    /// HTML entity or a curly quote never reports as a difference. An unclosable deviation line is
    /// the §594 failure, and this one would arrive hourly.</para>
    ///
    /// <para>⚠️ <b>A BLANK Sessionize value is NOT a deviation.</b> Sessionize simply not carrying a
    /// room or a track is the normal case, not a disagreement — reporting it would flag every
    /// session for ever and the mail would be ignored inside a week.</para>
    /// </remarks>
    private static void AddDeviation(
        List<SessionizeFieldDeviation> into, Session session, string field,
        string? inCeh, string? inSessionize)
    {
        if (string.IsNullOrWhiteSpace(inSessionize)) return;
        if (string.Equals(
                Integrations.RichTextCompare.Comparable(inCeh),
                Integrations.RichTextCompare.Comparable(inSessionize),
                StringComparison.OrdinalIgnoreCase))
            return;
        into.Add(new SessionizeFieldDeviation(session.Title, field, inCeh, inSessionize, session.Id));
    }

    /// <summary>
    /// 🔴 §1024 — keep only the deviations worth MAILING: those whose set has changed for that
    /// session since the last mail. Stamps the new hash and clears it for sessions that now agree.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-09: *"lets leave it for now, as it is a good reminder to validate
    /// again, but only 1 time mail"*. The mail stays — it is a useful prompt — but a standing
    /// disagreement CEH is deliberately winning is not news on every pass.</para>
    ///
    /// <para>🔑 <b>Deliberately not §1002's hourly re-send.</b> That one chases a WRONG PUBLIC
    /// AGENDA somebody must fix. This one describes a state that is correct by design, so once is
    /// right — and again only if the disagreement itself changes.</para>
    ///
    /// <para>🔒 A session that no longer deviates has its hash CLEARED, so the same disagreement
    /// recurring later mails again instead of being swallowed by a stale stamp.</para>
    /// </remarks>
    private static List<SessionizeFieldDeviation> KeepUnreported(
        List<SessionizeFieldDeviation> all, IEnumerable<Session> touched)
    {
        var bySession = all.Where(d => d.SessionId > 0)
            .GroupBy(d => d.SessionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var report = new List<SessionizeFieldDeviation>();
        foreach (var session in touched)
        {
            if (!bySession.TryGetValue(session.Id, out var mine))
            {
                session.SessionizeDeviationNotifiedHash = null;   // agrees again ⇒ forget it
                continue;
            }

            var hash = DeviationHash(mine);
            if (string.Equals(hash, session.SessionizeDeviationNotifiedHash, StringComparison.Ordinal))
                continue;                                          // same disagreement, already mailed

            session.SessionizeDeviationNotifiedHash = hash;
            report.AddRange(mine);
        }
        return report;
    }

    /// <summary>The stable key of one session's deviation set (same set ⇒ same hash ⇒ no re-mail).</summary>
    private static string DeviationHash(IEnumerable<SessionizeFieldDeviation> d) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n",
                d.OrderBy(x => x.Field, StringComparer.Ordinal)
                 .Select(x => $"{x.Field}{x.InCeh}{x.InSessionize}")))))[..32];

    /// <summary>A start–end pair as the deviation mail prints it, in Danish time (§305).</summary>
    private static string FormatRange(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null) return "(not set)";
        var tz = Integrations.EventTimezone.Tz;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var text = TimeZoneInfo.ConvertTime(start.Value, tz).ToString("ddd dd MMM yyyy, HH:mm", ci);
        return end is { } e
            ? text + "–" + TimeZoneInfo.ConvertTime(e, tz).ToString("HH:mm", ci)
            : text;
    }
}
