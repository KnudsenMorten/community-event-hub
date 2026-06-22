using System.Security.Cryptography;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>The public view of a master class's logistics page (no auth needed to read).</summary>
/// <param name="SessionId">The master-class session id.</param>
/// <param name="Title">The master-class title (shown as the page heading).</param>
/// <param name="Room">The room, when known.</param>
/// <param name="LogisticsText">The published logistics text (null/blank = nothing yet).</param>
/// <param name="UpdatedAt">When the logistics text was last edited.</param>
/// <param name="Speakers">The involved speaker display names (shown as "presented by").</param>
public sealed record MasterClassLogisticsView(
    int SessionId,
    string Title,
    string? Room,
    string? LogisticsText,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<string> Speakers);

/// <summary>
/// Drives the master-class <b>public logistics page</b> (REQUIREMENTS § 6c):
/// mints the per-session public slug, resolves the slug to the public view (NO
/// auth), and applies edits — gated so only an <b>involved speaker of that
/// session OR an organizer</b> may write.
///
/// The public page itself is anonymous (read-only). Editing requires a
/// signed-in participant whose involvement is re-checked server-side
/// (<see cref="CanEditAsync"/>), so there is no anonymous input to abuse —
/// spam-resistant by construction.
/// </summary>
public sealed class MasterClassLogisticsService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public MasterClassLogisticsService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Return the session's public slug, minting one on first use. Only a
    /// master-class session gets a public page; other types throw. Idempotent:
    /// once a slug exists it is returned unchanged.
    /// </summary>
    public async Task<string> EnsureSlugAsync(
        int eventId, int sessionId, CancellationToken ct = default)
    {
        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.EventId == eventId, ct)
            ?? throw new InvalidOperationException(
                $"Session {sessionId} not found in event {eventId}.");
        if (session.Type != SessionType.MasterClass)
        {
            throw new InvalidOperationException(
                "A public logistics page is only available for a master-class session.");
        }

        if (string.IsNullOrWhiteSpace(session.PublicSlug))
        {
            session.PublicSlug = NewSlug();
            session.UpdatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }
        return session.PublicSlug!;
    }

    /// <summary>
    /// Resolve a public slug to its read-only view, or null when the slug is
    /// empty / unknown / not a master class. Anonymous-safe (no auth). Used by
    /// the public <c>GET /MasterClass/{slug}</c> page.
    /// </summary>
    public async Task<MasterClassLogisticsView?> GetPublicViewAsync(
        string? slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var trimmed = slug.Trim();

        var session = await _db.Sessions
            .AsNoTracking()
            .Include(s => s.SessionSpeakers).ThenInclude(ss => ss.Participant)
            .FirstOrDefaultAsync(
                s => s.PublicSlug == trimmed && s.Type == SessionType.MasterClass, ct);
        if (session is null) return null;

        var speakers = session.SessionSpeakers
            .Select(ss => ss.Participant?.FullName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();

        return new MasterClassLogisticsView(
            session.Id, session.Title, session.Room,
            session.LogisticsText, session.LogisticsUpdatedAt, speakers);
    }

    /// <summary>
    /// Whether the given participant may edit this session's logistics: TRUE for
    /// an <b>Organizer</b> in the edition, or a participant <b>linked as a speaker
    /// of this session</b>. The session must be a master class.
    /// </summary>
    public async Task<bool> CanEditAsync(
        int eventId, int sessionId, int participantId, ParticipantRole role,
        CancellationToken ct = default)
    {
        var isMasterClass = await _db.Sessions.AnyAsync(
            s => s.Id == sessionId && s.EventId == eventId
                 && s.Type == SessionType.MasterClass, ct);
        if (!isMasterClass) return false;

        if (role == ParticipantRole.Organizer) return true;

        return await _db.SessionSpeakers.AnyAsync(
            ss => ss.SessionId == sessionId && ss.ParticipantId == participantId, ct);
    }

    /// <summary>
    /// Apply an edit to the session's logistics text, gated by
    /// <see cref="CanEditAsync"/>. Throws <see cref="UnauthorizedAccessException"/>
    /// when the editor is neither an involved speaker nor an organizer. Stamps
    /// the editor's email + time (audit). Returns the saved session.
    /// </summary>
    public async Task<Session> UpdateLogisticsAsync(
        int eventId, int sessionId, int participantId, ParticipantRole role,
        string editorEmail, string? logisticsText, CancellationToken ct = default)
    {
        if (!await CanEditAsync(eventId, sessionId, participantId, role, ct))
        {
            throw new UnauthorizedAccessException(
                "Only an involved speaker or an organizer may edit this master "
                + "class's logistics.");
        }

        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.EventId == eventId, ct)
            ?? throw new InvalidOperationException(
                $"Session {sessionId} not found in event {eventId}.");

        var now = _clock.GetUtcNow();
        session.LogisticsText =
            string.IsNullOrWhiteSpace(logisticsText) ? null : logisticsText.Trim();
        session.LogisticsUpdatedAt = now;
        session.LogisticsUpdatedByEmail = editorEmail;
        session.UpdatedAt = now;

        // Editing implies the page exists — make sure a slug is minted.
        if (string.IsNullOrWhiteSpace(session.PublicSlug))
        {
            session.PublicSlug = NewSlug();
        }

        await _db.SaveChangesAsync(ct);
        return session;
    }

    private static string NewSlug()
    {
        // 144-bit URL-safe random slug — unguessable, shareable, no padding.
        var bytes = RandomNumberGenerator.GetBytes(18);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
