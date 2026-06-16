using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

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

    private readonly CommunityHubDbContext _db;
    private readonly IRoomQrProvider _qr;
    private readonly TimeProvider _clock;

    public SessionManagementService(
        CommunityHubDbContext db,
        IRoomQrProvider qr,
        TimeProvider clock)
    {
        _db = db;
        _qr = qr;
        _clock = clock;
    }

    /// <summary>
    /// Add a hub-only session to an edition. Title is required; Type/Length/Room are
    /// organizer-set. Returns the created session. Optionally links the given speaker
    /// participant ids (must belong to the same edition).
    /// </summary>
    public async Task<Session> AddHubSessionAsync(
        int eventId,
        string title,
        SessionType type,
        SessionLength length,
        string? room = null,
        string? @abstract = null,
        IReadOnlyList<int>? speakerParticipantIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A session title is required.", nameof(title));
        }

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
            Length = length,
            IsHubAdded = true,
            IsServiceSession = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
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
    /// Update the editable session fields. For a hub-added session every field is
    /// editable; for an imported session the import owns Title/Abstract/Room/times, so
    /// only the hub-managed Type/Length/Room override + evaluation form url are applied
    /// here (Room is editable for hub-added; for imported it is refreshed by the import,
    /// but a manual organizer edit is allowed between imports). Returns the session.
    /// </summary>
    public async Task<Session> UpdateSessionAsync(
        int eventId,
        int sessionId,
        SessionType type,
        SessionLength length,
        string? room,
        string? evaluationFormUrl,
        CancellationToken ct = default)
    {
        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.EventId == eventId, ct)
            ?? throw new InvalidOperationException(
                $"Session {sessionId} not found in event {eventId}.");

        session.Type = type;
        session.Length = length;
        session.Room = string.IsNullOrWhiteSpace(room) ? null : room.Trim();
        session.EvaluationFormUrl =
            string.IsNullOrWhiteSpace(evaluationFormUrl) ? null : evaluationFormUrl.Trim();
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
