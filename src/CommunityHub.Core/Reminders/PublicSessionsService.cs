using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// One row in the PUBLIC sessions overview (<c>/Sessions</c>). Read-only projection
/// of a <see cref="Session"/> with everything the public page renders: title,
/// type/length (display + raw enum for the filters), room, schedule, the linked
/// speaker name(s), and the two public deep-links a session may expose — its
/// master-class logistics page (<see cref="PublicSlug"/>, master-class only) and
/// its attendee-question ask page (<see cref="AskToken"/>).
/// </summary>
public sealed record PublicSessionRow(
    int Id,
    string Title,
    string? Abstract,
    SessionType Type,
    SessionLength Length,
    string? Room,
    string? Track,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    IReadOnlyList<PublicSessionSpeaker> Speakers,
    string? PublicSlug,
    string? AskToken,
    // §154: audience level ("Expert (400)") + the exact numeric length in minutes
    // (preferred over the coarse Length bucket for the "60 min" label when present).
    string? Level = null,
    int? LengthMinutes = null);

/// <summary>The whole public overview: the rows plus the filter facets to render.</summary>
public sealed record PublicSessionsView(
    string EventDisplayName,
    IReadOnlyList<PublicSessionRow> Sessions,
    IReadOnlyList<string> Rooms,
    int TotalCount,
    int MatchCount,
    IReadOnlyList<TimeslotFacet> Timeslots,
    // §154: distinct Track + Level values across the edition, for the filter dropdowns
    // (built like Rooms — stable across filtering).
    IReadOnlyList<string> Tracks,
    IReadOnlyList<string> Levels);

/// <summary>One date/timeslot the Sessions overview can filter by (§154). <paramref name="Key"/> is
/// the stable filter value (start, invariant); <paramref name="Label"/> is the human "ddd dd MMM, HH:mm–HH:mm".</summary>
public sealed record TimeslotFacet(string Key, string Label);

/// <summary>
/// One speaker linked to a session on the PUBLIC session-detail page. Carries the
/// participant id (so a row can deep-link to the speaker on <c>/Speakers</c>) and
/// whether that speaker is currently <b>published</b> — the same HARD GATE the
/// public speakers page uses. The detail page links the name ONLY when
/// <see cref="IsPublished"/> is true (never expose an unselected speaker); an
/// unpublished co-speaker still shows as plain text so the line-up reads honestly.
/// </summary>
public sealed record PublicSessionSpeaker(
    int ParticipantId, string Name, bool IsPublished, string? LinkedIn = null);

/// <summary>
/// The PUBLIC, no-login detail view of a single session (<c>/Sessions/{id}</c>):
/// everything the overview row has plus the full speaker list with per-speaker
/// publish state for cross-linking, the master-class logistics slug (master class
/// only) and the ask-a-question token. Read-only.
/// </summary>
public sealed record PublicSessionDetail(
    int Id,
    string EventDisplayName,
    string Title,
    string? Abstract,
    SessionType Type,
    SessionLength Length,
    string? Room,
    string? Track,
    string? VenueName,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    IReadOnlyList<PublicSessionSpeaker> Speakers,
    string? PublicSlug,
    string? AskToken,
    // §154: audience level + exact numeric length in minutes (see PublicSessionRow).
    string? Level = null,
    int? LengthMinutes = null);

/// <summary>
/// Builds the data for the PUBLIC, no-login sessions overview page
/// (REQUIREMENTS § session management — "Filters in the session views on Type and
/// Length"). Scoped to the currently <b>active</b> event (the same active-event
/// resolution the public volunteer-signup page uses), so an anonymous visitor sees
/// only the live edition's published sessions.
///
/// Read-only: it never writes. Service sessions (breaks/lunch) are excluded — the
/// overview lists talks/workshops/sponsor sessions only. The page filters by
/// <see cref="SessionType"/> and <see cref="SessionLength"/> (and optionally room),
/// plus a free-text search over title / abstract / speaker / room / track.
/// </summary>
public sealed class PublicSessionsService
{
    private readonly CommunityHubDbContext _db;

    public PublicSessionsService(CommunityHubDbContext db) => _db = db;

    /// <summary>
    /// Build the public overview for the active edition, applying the optional
    /// filters. Returns <c>null</c> when there is no active event (the page then
    /// renders a friendly "no event" empty state).
    /// </summary>
    /// <param name="type">Narrow to one session type, or null for all.</param>
    /// <param name="length">Narrow to one session length, or null for all.</param>
    /// <param name="room">Narrow to one room (exact, case-insensitive), or null for all.</param>
    /// <param name="search">Free-text search over title/abstract/speaker/room/track.</param>
    /// <param name="track">Narrow to one track (exact, case-insensitive), or null for all (§154).</param>
    /// <param name="level">Narrow to one level (exact, case-insensitive), or null for all (§154).</param>
    public async Task<PublicSessionsView?> BuildAsync(
        SessionType? type = null,
        SessionLength? length = null,
        string? room = null,
        string? search = null,
        string? timeslot = null,
        string? track = null,
        string? level = null,
        CancellationToken ct = default)
    {
        var active = await _db.Events
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.Id)
            .Select(e => new { e.Id, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (active is null) return null;

        var eventId = active.Id;

        // The hard publish gate, resolved as a SINGLE flat, fully-translatable query:
        // the set of participant ids in this edition that are published speakers
        // (selected + active + speaker role). Doing this once (instead of a correlated
        // per-speaker Any() subquery nested inside the session projection) keeps the
        // session query translatable on RELATIONAL providers (SQL Server / SQLite) —
        // the nested Select(...).Any(...) shape throws "could not be translated".
        var publishedSpeakerIds = await _db.SpeakerProfiles
            .Where(sp =>
                sp.EventId == eventId
                && sp.SelectedForPublish
                && sp.Participant.IsActive
                && sp.Participant.Role == ParticipantRole.Speaker)
            .Select(sp => sp.ParticipantId)
            .Distinct()
            .ToListAsync(ct);
        var publishedSpeakers = new HashSet<int>(publishedSpeakerIds);

        // §156: each session speaker's LinkedIn (from SpeakerProfile, imported from Sessionize),
        // so the "With {speaker}" line can hyperlink the name. Resolved once as a flat id->url map.
        var linkedInById = (await _db.SpeakerProfiles
                .Where(sp => sp.EventId == eventId && sp.LinkedIn != null && sp.LinkedIn != "")
                .Select(sp => new { sp.ParticipantId, sp.LinkedIn })
                .ToListAsync(ct))
            .GroupBy(x => x.ParticipantId)
            .ToDictionary(g => g.Key, g => NormalizeLinkedIn(g.First().LinkedIn));

        // Base set: this edition's non-service sessions, projected to a flat,
        // translatable shape (the speakers come back as raw id/name pairs). The
        // per-speaker publish gate + ordering are applied CLIENT-SIDE below, after
        // this materialization boundary, so no un-translatable nested projection
        // reaches the relational provider.
        var raw = await _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.Abstract,
                s.Type,
                s.Length,
                s.Room,
                s.Track,
                s.Level,
                s.LengthMinutes,
                s.StartsAt,
                s.EndsAt,
                Speakers = s.SessionSpeakers
                    .Select(ss => new { ss.ParticipantId, ss.Participant.FullName })
                    .ToList(),
                s.PublicSlug,
                s.PublicToken,
            })
            .ToListAsync(ct);

        var all = raw
            .Select(s => new PublicSessionRow(
                s.Id,
                s.Title,
                s.Abstract,
                s.Type,
                s.Length,
                s.Room,
                s.Track,
                s.StartsAt,
                s.EndsAt,
                s.Speakers
                    .Select(ss => new PublicSessionSpeaker(
                        ss.ParticipantId,
                        ss.FullName,
                        // Same hard gate as the public speakers page — published iff a
                        // selected, active, speaker-role profile exists in this edition.
                        publishedSpeakers.Contains(ss.ParticipantId),
                        linkedInById.TryGetValue(ss.ParticipantId, out var li) ? li : null))
                    .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                // Only a master class exposes a public logistics page; for any other
                // type the slug is irrelevant to the public overview.
                s.Type == SessionType.MasterClass ? s.PublicSlug : null,
                s.PublicToken,
                s.Level,
                s.LengthMinutes))
            .ToList();

        var total = all.Count;

        IEnumerable<PublicSessionRow> q = all;
        if (type is not null) q = q.Where(r => r.Type == type);
        if (length is not null) q = q.Where(r => r.Length == length);
        if (!string.IsNullOrWhiteSpace(timeslot))
            q = q.Where(r => TimeslotKey(r.StartsAt) == timeslot.Trim());
        if (!string.IsNullOrWhiteSpace(room))
        {
            var r = room.Trim();
            q = q.Where(x => string.Equals(x.Room, r, StringComparison.OrdinalIgnoreCase));
        }
        // §154: Track + Level filters (work across ALL session types, incl. master classes).
        if (!string.IsNullOrWhiteSpace(track))
        {
            var t = track.Trim();
            q = q.Where(x => string.Equals(x.Track, t, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(level))
        {
            var l = level.Trim();
            q = q.Where(x => string.Equals(x.Level, l, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(x => Contains(x.Title, term)
                             || Contains(x.Abstract, term)
                             || Contains(x.Room, term)
                             || Contains(x.Track, term)
                             || x.Speakers.Any(sp => Contains(sp.Name, term)));
        }

        // Stable, human-friendly order: scheduled sessions first (by start), then
        // the unscheduled ones, then by room/title so the list is deterministic.
        var rows = q
            .OrderBy(r => r.StartsAt is null)
            .ThenBy(r => r.StartsAt)
            .ThenBy(r => r.Room ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Room facet: every distinct room across the edition (not just the filtered
        // set) so the room dropdown is stable as the user filters.
        var rooms = all
            .Select(r => r.Room)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // §154: Track + Level facets — distinct values across the whole edition (same
        // stable-as-you-filter rule as Rooms), feeding the two new filter dropdowns.
        var tracks = all
            .Select(r => r.Track)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var levels = all
            .Select(r => r.Level)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Date/timeslot facet (§154): every distinct scheduled start across the edition, ordered,
        // so the dropdown is stable as the user filters. Label shows day + start–end.
        var timeslots = all
            .Where(r => r.StartsAt is not null)
            .GroupBy(r => r.StartsAt!.Value)
            .OrderBy(g => g.Key)
            .Select(g => new TimeslotFacet(
                TimeslotKey(g.Key)!,
                g.Key.ToString("ddd dd MMM, HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                    + (g.Where(r => r.EndsAt is not null).Select(r => r.EndsAt!.Value).DefaultIfEmpty().Max() is { } end && end != default
                        ? "–" + end.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                        : "")))
            .ToList();

        return new PublicSessionsView(
            active.DisplayName, rows, rooms, total, rows.Count, timeslots, tracks, levels);
    }

    /// <summary>Stable invariant filter key for a session's start (null when unscheduled).</summary>
    private static string? TimeslotKey(DateTimeOffset? startsAt) =>
        startsAt?.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Resolve one session's PUBLIC detail by its numeric id, scoped to the active
    /// edition (so an old edition's id can't be poked to leak content, and the page
    /// stays in step with the public overview). Returns <c>null</c> when there is no
    /// active event, the id is not in the active edition, or it is a service session
    /// (breaks/lunch are never publicly addressable).
    ///
    /// Each linked speaker carries a <see cref="PublicSessionSpeaker.IsPublished"/>
    /// flag computed from the SAME hard gate the public speakers page uses
    /// (SelectedForPublish + active + speaker role), so the detail page links only to
    /// speakers that are safe to show.
    /// </summary>
    public async Task<PublicSessionDetail?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var active = await _db.Events
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.Id)
            .Select(e => new { e.Id, e.DisplayName, e.VenueName })
            .FirstOrDefaultAsync(ct);
        if (active is null) return null;

        var eventId = active.Id;

        // The publish gate as a SINGLE flat, fully-translatable query (the set of
        // published speaker participant ids for this edition), resolved separately
        // from the session projection. This avoids the correlated per-speaker Any()
        // subquery nested inside Select(...).ToList(), which throws "could not be
        // translated" on relational providers (SQL Server / SQLite).
        var publishedSpeakerIds = await _db.SpeakerProfiles
            .Where(sp =>
                sp.EventId == eventId
                && sp.SelectedForPublish
                && sp.Participant.IsActive
                && sp.Participant.Role == ParticipantRole.Speaker)
            .Select(sp => sp.ParticipantId)
            .Distinct()
            .ToListAsync(ct);
        var publishedSpeakers = new HashSet<int>(publishedSpeakerIds);

        var s = await _db.Sessions
            .Where(x => x.Id == id && x.EventId == eventId && !x.IsServiceSession)
            .Select(x => new
            {
                x.Id, x.Title, x.Abstract, x.Type, x.Length, x.Room, x.Track,
                x.Level, x.LengthMinutes,
                x.StartsAt, x.EndsAt, x.PublicSlug, x.PublicToken,
                // Flat, translatable speaker rows; the publish gate is applied
                // client-side below against the published-id set.
                Speakers = x.SessionSpeakers
                    .Select(ss => new { ss.ParticipantId, ss.Participant.FullName })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (s is null) return null;

        var speakers = s.Speakers
            .Select(sp => new PublicSessionSpeaker(
                sp.ParticipantId, sp.FullName, publishedSpeakers.Contains(sp.ParticipantId)))
            .OrderBy(sp => sp.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PublicSessionDetail(
            s.Id,
            active.DisplayName,
            s.Title,
            s.Abstract,
            s.Type,
            s.Length,
            s.Room,
            s.Track,
            string.IsNullOrWhiteSpace(active.VenueName) ? null : active.VenueName,
            s.StartsAt,
            s.EndsAt,
            speakers,
            // Only a master class exposes a public logistics page.
            s.Type == SessionType.MasterClass ? s.PublicSlug : null,
            s.PublicToken,
            s.Level,
            s.LengthMinutes);
    }

    /// <summary>
    /// Given a set of candidate session ids, return the subset that are PUBLICLY
    /// VIEWABLE — i.e. the exact same gate <see cref="GetByIdAsync"/> applies, so a
    /// caller can decide whether a public <c>/Sessions/{id}</c> link will actually
    /// resolve (rather than guessing from unrelated state such as the speaker's own
    /// profile-publish flag). A session is publicly viewable iff there is an active
    /// event AND the session is in that active edition AND it is not a service
    /// session. Returns an empty set when there is no active event or no candidates.
    ///
    /// Read-only. This is intentionally a SET (not a per-id round-trip) so a list
    /// page can resolve every row's link in one query.
    /// </summary>
    public async Task<HashSet<int>> GetPubliclyViewableSessionIdsAsync(
        IEnumerable<int> candidateIds, CancellationToken ct = default)
    {
        var ids = candidateIds?.Distinct().ToList() ?? new List<int>();
        if (ids.Count == 0) return new HashSet<int>();

        var activeId = await _db.Events
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.Id)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (activeId is null) return new HashSet<int>();

        // Same gate as GetByIdAsync: in the active edition, not a service session.
        var viewable = await _db.Sessions
            .Where(s => s.EventId == activeId.Value
                        && !s.IsServiceSession
                        && ids.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(ct);

        return new HashSet<int>(viewable);
    }

    /// <summary>
    /// Build a single-event RFC 5545 VCALENDAR (one VEVENT, <c>METHOD:PUBLISH</c>) for
    /// one PUBLIC session, so an anonymous visitor can drop the talk straight into a
    /// personal calendar from the session-detail page. Reuses the same
    /// <see cref="IcsCalendarBuilder"/> the per-user feed uses, so the file validates
    /// identically (CRLF, stable UID, folded lines).
    ///
    /// Returns <c>null</c> when the session is not publicly resolvable (no active
    /// event, wrong edition, service session, unknown id — same gate as
    /// <see cref="GetByIdAsync"/>) OR when it has no scheduled start time (an
    /// unscheduled talk has nothing to put on a calendar). The UID is stable
    /// (<c>session:{id}@{host}</c>) so re-downloading UPDATES the entry, never
    /// duplicates it. Location is "Room, Venue" (whichever parts exist); the
    /// description carries the speaker name(s) and (truncated) abstract. No private
    /// data — only the already-public session fields.
    /// </summary>
    public async Task<string?> BuildIcsAsync(int id, string host, CancellationToken ct = default)
    {
        var s = await GetByIdAsync(id, ct);
        if (s is null || s.StartsAt is null) return null;

        var start = s.StartsAt.Value;
        // No explicit end → fall back to a sensible 1-hour block so the calendar
        // entry has a duration rather than a zero-length point.
        var end = s.EndsAt ?? start.AddHours(1);
        if (end <= start) end = start.AddHours(1);

        var location = string.Join(", ", new[] { s.Room, s.VenueName }
            .Where(p => !string.IsNullOrWhiteSpace(p)));

        var descParts = new List<string>();
        var speakerNames = s.Speakers.Select(sp => sp.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (speakerNames.Count > 0) descParts.Add(string.Join(", ", speakerNames));
        if (!string.IsNullOrWhiteSpace(s.Abstract))
        {
            var a = s.Abstract.Trim();
            descParts.Add(a.Length > 500 ? a.Substring(0, 500) + "…" : a);
        }
        var description = string.Join("\n\n", descParts);

        var safeHost = string.IsNullOrWhiteSpace(host) ? "communityhub" : host;
        var item = new CalendarItem(
            Uid: $"session:{s.Id}@{safeHost}",
            Summary: s.Title,
            Description: description.Length == 0 ? null : description,
            Location: location.Length == 0 ? null : location,
            Start: start,
            End: end,
            AllDay: false,
            AlarmsDaysBefore: Array.Empty<int>());

        // METHOD:PUBLISH single-event calendar (no owner — this is a public talk, not
        // a personal invite, so it carries no ORGANIZER/ATTENDEE addresses).
        return IcsCalendarBuilder.BuildFeed(
            calendarName: s.Title,
            ownerEmail: string.Empty,
            ownerName: string.Empty,
            items: new[] { item });
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack)
        && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalise a stored LinkedIn value (Sessionize may give a full URL, a bare
    /// "linkedin.com/in/x", or a handle) to a safe absolute https URL, or null when it is
    /// blank or doesn't look like a LinkedIn address. Only http/https are emitted (no
    /// javascript: etc.), so the value is safe to drop straight into an href.
    /// </summary>
    private static string? NormalizeLinkedIn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();

        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(v, UriKind.Absolute, out var u)
                   && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
                ? v
                : null;
        }

        // Bare host/handle: only accept something that looks like a LinkedIn address.
        if (v.Contains("linkedin.", StringComparison.OrdinalIgnoreCase))
            return "https://" + v.TrimStart('/');

        return null;
    }
}
