using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §299.8/b7 — the REQUIRED day choice when an organizer adds a session manually
/// in the hub (imported sessions never prompt; their day comes from the source
/// schedule). Drives which edition date the hub-add stamps onto StartsAt.
/// </summary>
public enum HubSessionDay
{
    /// <summary>The edition's pre-day (master classes) — PreDayDate (falls back to StartDate).</summary>
    PreDay = 0,

    /// <summary>The main conference day — StartDate.</summary>
    MainDay = 1,
}

/// <summary>The outcome of provisioning a room's QR code across its sessions.</summary>
/// <param name="Provisioned">True when the QR seam actually stored a QR.</param>
/// <param name="SessionsUpdated">How many sessions in the room got the QR URL.</param>
/// <param name="ImageUrl">The stored SharePoint image URL (null when not provisioned).</param>
/// <param name="Message">Human-readable status / reason.</param>
public sealed record RoomQrProvisionResult(
    bool Provisioned,
    int SessionsUpdated,
    string? ImageUrl,
    string Message);

/// <summary>
/// Organizer-side session management (REQUIREMENTS § hub-only sessions, type/length,
/// room, QR). Adds hub-only sessions (e.g. sponsor sessions) alongside imported ones,
/// edits the editable fields, and drives the per-room QR provisioning seam.
///
/// All writes are edition-scoped (EventId) and never touch the Sessionize import path:
/// hub-added sessions get a synthetic <c>hub-&lt;guid&gt;</c> id the import never
/// matches, so a re-import never overwrites or deletes them.
/// </summary>
public sealed class SessionManagementService
{
    /// <summary>Synthetic-id prefix marking a hub-added (non-Sessionize) session.</summary>
    public const string HubSessionizeIdPrefix = "hub-";

    /// <summary>Default local start time (09:00) stamped onto a hub-added session's
    /// chosen day (§299.8/b7 — the manual-path day prompt).</summary>
    private static readonly TimeOnly HubSessionDefaultStartTime = new(9, 0);

    private readonly CommunityHubDbContext _db;
    private readonly IRoomQrProvider _qr;
    private readonly TimeProvider _clock;

    // §299.8/b7: the length config (quick-picks + max custom minutes). Optional so
    // legacy constructions/tests stay valid; null falls back to the shipped
    // default max (any positive int ≤ 600 validates).
    private readonly Config.SessionOptionsService? _options;

    public SessionManagementService(
        CommunityHubDbContext db,
        IRoomQrProvider qr,
        TimeProvider clock,
        Config.SessionOptionsService? options = null)
    {
        _db = db;
        _qr = qr;
        _clock = clock;
        _options = options;
    }

    /// <summary>The inclusive max for a custom session length in minutes (config;
    /// shipped default 600 when no config service is wired).</summary>
    public int MaxLengthMinutes =>
        _options?.MaxMinutes ?? Config.EventEditionConfig.DefaultSessionLengthMaxMinutes;

    /// <summary>§299.8/b7 — validate an organizer-entered session length: any POSITIVE
    /// integer of minutes up to <see cref="MaxLengthMinutes"/> (quick-pick or custom;
    /// 37 is as valid as 60, and 420 must pass). Throws <see cref="ArgumentException"/>
    /// with an honest message otherwise.</summary>
    private void ValidateLengthMinutes(int lengthMinutes)
    {
        if (lengthMinutes <= 0)
        {
            throw new ArgumentException(
                "The session length must be a positive number of minutes.",
                nameof(lengthMinutes));
        }
        if (lengthMinutes > MaxLengthMinutes)
        {
            throw new ArgumentException(
                $"The session length must be at most {MaxLengthMinutes} minutes.",
                nameof(lengthMinutes));
        }
    }

    /// <summary>
    /// LEGACY bucket overload — bridges older callers/tests still holding a
    /// <see cref="SessionLength"/> bucket: converts it to representative minutes
    /// (FullDay → 420) and adds WITHOUT a day choice (no schedule stamp).
    /// The organizer UI path uses the minutes + day overload below.
    /// </summary>
    public Task<Session> AddHubSessionAsync(
        int eventId,
        string title,
        SessionType type,
        SessionLength length,
        string? room = null,
        string? @abstract = null,
        IReadOnlyList<int>? speakerParticipantIds = null,
        CancellationToken ct = default) =>
        AddHubSessionAsync(
            eventId, title, type, SessionDefaultsMapper.MinutesFromBucket(length),
            day: null, room, @abstract, speakerParticipantIds, ct);

    /// <summary>
    /// Add a hub-only session to an edition (§299.8/b7). Title is required; the
    /// LENGTH is the source-of-truth integer minutes (any positive value up to the
    /// configured max — quick-pick or custom; the legacy <see cref="SessionLength"/>
    /// bucket is derived for display). <paramref name="day"/> is the REQUIRED day
    /// choice on the organizer's manual path (imported sessions never prompt): the
    /// session's StartsAt is stamped to the edition's PreDayDate/StartDate at 09:00
    /// (EndsAt = start + minutes) and <see cref="Session.IsDateOverridden"/> is SET so
    /// a Sessionize re-import never touches the stamped schedule (❓OPEN-20). A null
    /// day (legacy callers) skips the stamp. Optionally links the given speaker
    /// participant ids (must belong to the same edition).
    /// </summary>
    public async Task<Session> AddHubSessionAsync(
        int eventId,
        string title,
        SessionType type,
        int lengthMinutes,
        HubSessionDay? day,
        string? room = null,
        string? @abstract = null,
        IReadOnlyList<int>? speakerParticipantIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A session title is required.", nameof(title));
        }
        ValidateLengthMinutes(lengthMinutes);

        var now = _clock.GetUtcNow();
        var session = new Session
        {
            EventId = eventId,
            // Synthetic id the Sessionize import never matches → import-safe.
            SessionizeId = HubSessionizeIdPrefix + Guid.NewGuid().ToString("N"),
            Title = title.Trim(),
            Abstract = string.IsNullOrWhiteSpace(@abstract) ? null : @abstract.Trim(),
            Room = string.IsNullOrWhiteSpace(room) ? null : room.Trim(),
            Type = type,
            LengthMinutes = lengthMinutes,
            // The legacy bucket is DERIVED from the minutes (display only).
            Length = SessionDefaultsMapper.ToLengthBucket(lengthMinutes),
            IsHubAdded = true,
            IsServiceSession = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (day is { } chosenDay)
        {
            // Stamp the chosen day (Pre-day / Main day) from the edition's dates at
            // the default 09:00 (UTC-stamped wall time, same convention as the
            // agenda push) and mark the schedule as manually owned so re-imports
            // never re-derive it (❓OPEN-20).
            var ev = await _db.Events
                .Where(e => e.Id == eventId)
                .Select(e => new { e.StartDate, e.PreDayDate })
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException($"Event {eventId} not found.");
            var date = chosenDay == HubSessionDay.PreDay
                ? (ev.PreDayDate ?? ev.StartDate)
                : ev.StartDate;
            session.StartsAt = new DateTimeOffset(
                date.ToDateTime(HubSessionDefaultStartTime), TimeSpan.Zero);
            session.EndsAt = session.StartsAt.Value.AddMinutes(lengthMinutes);
            session.IsDateOverridden = true;
        }

        _db.Sessions.Add(session);

        if (speakerParticipantIds is { Count: > 0 })
        {
            var valid = await _db.Participants
                .Where(p => p.EventId == eventId && speakerParticipantIds.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(ct);
            foreach (var pid in valid.Distinct())
            {
                session.SessionSpeakers.Add(new SessionSpeaker
                {
                    Session = session,
                    ParticipantId = pid,
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>
    /// Update the editable session fields (§299.8/b7). The LENGTH is the
    /// source-of-truth integer minutes (positive, ≤ the configured max; the legacy
    /// <see cref="SessionLength"/> bucket is derived for display). For a hub-added
    /// session every field is editable; for an imported session the import owns
    /// Title/Abstract/times, so only the hub-managed Type/Length/Room override +
    /// evaluation form url are applied here.
    ///
    /// <b>Schedule (❓OPEN-20, answered 2026-07-23):</b> when
    /// <paramref name="applySchedule"/> is true AND the posted
    /// <paramref name="startsAt"/>/<paramref name="endsAt"/> DIFFER from the stored
    /// values, the schedule is updated and <see cref="Session.IsDateOverridden"/> is
    /// SET — a Sessionize re-import then skips the StartsAt/EndsAt refresh so the
    /// manual date survives (mirroring <see cref="Session.TypeIsManualOverride"/>).
    /// Unchanged posted values leave the flag alone. Returns the session.
    /// </summary>
    public async Task<Session> UpdateSessionAsync(
        int eventId,
        int sessionId,
        SessionType type,
        int lengthMinutes,
        string? room,
        string? evaluationFormUrl,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null,
        bool applySchedule = false,
        CancellationToken ct = default)
    {
        ValidateLengthMinutes(lengthMinutes);

        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.EventId == eventId, ct)
            ?? throw new InvalidOperationException(
                $"Session {sessionId} not found in event {eventId}.");

        session.Type = type;
        // An organizer manually setting the type is a manual override: a later
        // Sessionize / Backstage re-import must NOT clobber it (FEATURE 1).
        session.TypeIsManualOverride = true;
        session.LengthMinutes = lengthMinutes;
        // The legacy bucket is DERIVED from the minutes (display only).
        session.Length = SessionDefaultsMapper.ToLengthBucket(lengthMinutes);
        session.Room = string.IsNullOrWhiteSpace(room) ? null : room.Trim();
        session.EvaluationFormUrl =
            string.IsNullOrWhiteSpace(evaluationFormUrl) ? null : evaluationFormUrl.Trim();

        // ❓OPEN-20: a REAL manual schedule change stamps the override flag so the
        // import never re-derives the date. Posting back the unchanged values is
        // NOT an override (the edit form always re-posts the current schedule).
        if (applySchedule && (session.StartsAt != startsAt || session.EndsAt != endsAt))
        {
            session.StartsAt = startsAt;
            session.EndsAt = endsAt;
            session.IsDateOverridden = true;
        }

        session.UpdatedAt = _clock.GetUtcNow();

        await _db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>
    /// Provision (or refresh) the QR code for a room and stamp the stored SharePoint
    /// image URL onto every session in that room within the edition. The QR encodes the
    /// supplied room deep-link (<paramref name="roomTargetUrl"/>). When the QR seam is
    /// not wired (<see cref="IRoomQrProvider.CanProvision"/> = false), no SharePoint
    /// call is made and the result explains the ◻ pending wiring — nothing is faked.
    /// </summary>
    public async Task<RoomQrProvisionResult> ProvisionRoomQrAsync(
        int eventId,
        string room,
        string roomTargetUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(room))
        {
            return new RoomQrProvisionResult(false, 0, null, "A room name is required.");
        }

        if (!_qr.CanProvision)
        {
            return new RoomQrProvisionResult(
                false, 0, null,
                "Room-QR storage is not configured (SharePoint site/creds are operator "
                + "config, pending ◻). No QR was generated.");
        }

        var evt = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct)
            ?? throw new InvalidOperationException($"Event {eventId} not found.");

        var qr = await _qr.EnsureRoomQrAsync(evt.Code, room.Trim(), roomTargetUrl, ct);

        var now = _clock.GetUtcNow();
        var sessions = await _db.Sessions
            .Where(s => s.EventId == eventId && s.Room == room.Trim())
            .ToListAsync(ct);
        foreach (var s in sessions)
        {
            s.RoomQrUrl = qr.ImageUrl;
            s.RoomQrGeneratedAt = now;
            s.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);

        return new RoomQrProvisionResult(
            true, sessions.Count, qr.ImageUrl,
            $"QR provisioned for room '{room.Trim()}'; {sessions.Count} session(s) updated.");
    }
}
