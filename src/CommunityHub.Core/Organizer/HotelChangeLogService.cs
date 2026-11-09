using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// §1386 — writes and reads the hotel change log (<see cref="HotelChangeLogEntry"/>).
/// </summary>
/// <remarks>
/// 🔑 <see cref="Add"/> only STAGES the entry on the context — the caller's own SaveChanges commits
/// it together with the change it describes, so a change can never be saved without its log line
/// (or the other way round).
/// </remarks>
public sealed class HotelChangeLogService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public HotelChangeLogService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public HotelChangeLogEntry Add(
        int eventId, HotelChangeSource source, string? actor, string kind, string summary,
        int? hotelId = null, int? participantId = null, string? participantName = null,
        bool affectsGuestList = false, bool notify = true)
    {
        var entry = new HotelChangeLogEntry
        {
            EventId = eventId,
            HotelId = hotelId,
            ParticipantId = participantId,
            ParticipantName = Cap(participantName, 200),
            At = _clock.GetUtcNow(),
            Source = source,
            Actor = Cap(string.IsNullOrWhiteSpace(actor) ? "system" : actor.Trim(), 320),
            Kind = kind,
            Summary = Cap(summary, 1000) ?? string.Empty,
            AffectsGuestList = affectsGuestList,
            // notify:false = a change nobody needs mailing about (an import the organizer just ran,
            // a forecast, a seed): it is logged, and born already "notified".
            NotifiedAt = notify ? null : _clock.GetUtcNow(),
        };
        _db.HotelChangeLog.Add(entry);
        return entry;
    }

    /// <summary>Newest first. <paramref name="hotelId"/> narrows to one hotel.</summary>
    public Task<List<HotelChangeLogEntry>> ListAsync(
        int eventId, int? hotelId = null, int take = 500, CancellationToken ct = default)
    {
        var q = _db.HotelChangeLog.AsNoTracking().Where(e => e.EventId == eventId);
        if (hotelId is int h) q = q.Where(e => e.HotelId == h);
        return q.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Take(take).ToListAsync(ct);
    }

    private static string? Cap(string? s, int max) =>
        s is null ? null : (s.Length <= max ? s : s[..(max - 1)] + "…");
}
