using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §1273 — the event's feedback, broken down: per DAY, per session TYPE, per TRACK, with the number of
/// responses by CHANNEL, and the moment it was calculated.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-21: *"improve this statistics so i can see stats per day, type of session
/// (master class, technical session), track status, number of responses: total, kiosk, qr code - when
/// calculation was made"*.</para>
///
/// <para>🔑 <b>Pooled within each group</b> — every response counts once — which is the event
/// headline's method, so a row reads the same way as the figure above it. The per-session floor
/// (<see cref="SatisfactionScore.MinimumResponses"/>) is not applied to a GROUP, for the reason
/// §754.1 dropped it at event level: a group pools many rooms, and "no score yet" on a day with 40
/// responses would help nobody. The response count is always shown beside the score.</para>
///
/// <para>🔒 Groups are built from ATTRIBUTED responses (a group is a set of sessions). The event
/// channel line counts every response, attributed or not — the same total as the pooled headline.</para>
/// </remarks>
public sealed class EvaluationStatisticsService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public EvaluationStatisticsService(CommunityHubDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The label for sessions with no track.</summary>
    public const string NoTrack = "No track";

    private sealed record Item(
        int SessionId, SessionType? Type, string? Track, DateTimeOffset Start, int Rating, string Source);

    /// <summary>Responses by channel. "Box" is a built unit, "Kiosk" an iPad, "Qr" the printed code.</summary>
    public sealed record Channels(int Total, int Box, int Kiosk, int Qr);

    /// <summary>One line of a breakdown table.</summary>
    public sealed record Row(string Label, int Sessions, SatisfactionScore.Result Score, Channels Channels);

    public sealed record Breakdown(
        DateTimeOffset CalculatedAt,
        Channels Event,
        IReadOnlyList<Row> ByDay,
        IReadOnlyList<Row> ByType,
        IReadOnlyList<Row> ByTrack);

    /// <param name="typeLabel">How a session type is named on screen (the web layer owns the words).</param>
    public async Task<Breakdown> BuildAsync(
        int eventId, Func<SessionType?, string> typeLabel, CancellationToken ct = default)
    {
        var sessions = await _db.EvaluationSessions
            .Where(s => s.EventId == eventId)
            .Select(s => new { s.Id, s.CehSessionId, s.TrackName, s.ScheduledStart })
            .ToListAsync(ct);

        var cehIds = sessions.Where(s => s.CehSessionId != null).Select(s => s.CehSessionId!.Value).ToList();
        var types = await _db.Sessions
            .Where(s => cehIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Type })
            .ToDictionaryAsync(s => s.Id, s => (SessionType?)s.Type, ct);

        var responses = await _db.EvaluationResponses
            .Where(r => r.EventId == eventId)
            .Select(r => new { r.SessionId, r.Rating, r.Source })
            .ToListAsync(ct);

        static Channels Count<T>(IEnumerable<T> rows, Func<T, string> source)
        {
            int box = 0, kiosk = 0, qr = 0, total = 0;
            foreach (var r in rows)
            {
                total++;
                switch (source(r))
                {
                    case EvaluationResponseSources.Qr: qr++; break;
                    case EvaluationResponseSources.Kiosk: kiosk++; break;
                    default: box++; break;
                }
            }
            return new Channels(total, box, kiosk, qr);
        }

        var bySession = sessions.ToDictionary(s => s.Id);
        var items = responses
            .Where(r => r.SessionId is int id && bySession.ContainsKey(id))
            .Select(r =>
            {
                var s = bySession[r.SessionId!.Value];
                SessionType? type = s.CehSessionId is int cid && types.TryGetValue(cid, out var t) ? t : null;
                return new Item(s.Id, type, s.TrackName, s.ScheduledStart, r.Rating, r.Source);
            })
            .ToList();

        IReadOnlyList<Row> Group<TKey>(
            Func<Item, TKey> keyOf, Func<TKey, string> label,
            Func<IEnumerable<IGrouping<TKey, Item>>, IEnumerable<IGrouping<TKey, Item>>> order)
            where TKey : notnull =>
            order(items.GroupBy(keyOf))
                .Select(g => new Row(
                    label(g.Key),
                    g.Select(x => x.SessionId).Distinct().Count(),
                    SatisfactionScore.Compute(
                        SatisfactionScore.Distribution.From(g.Select(x => x.Rating)), requireMinimum: false),
                    Count(g, x => x.Source)))
                .ToList();

        // Day = the session's scheduled start in DANISH time — the day the organizers mean.
        var byDay = Group(
            x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.Start, EventTimezone.Tz).DateTime),
            d => d.ToString("ddd d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
            gs => gs.OrderBy(g => g.Key));

        var byType = Group(
            x => typeLabel(x.Type),
            s => s,
            gs => gs.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal));

        var byTrack = Group(
            x => string.IsNullOrWhiteSpace(x.Track) ? NoTrack : x.Track.Trim(),
            s => s,
            gs => gs.OrderBy(g => g.Key == NoTrack ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase));

        return new Breakdown(
            _clock.GetUtcNow(),
            Count(responses, r => r.Source),
            byDay, byType, byTrack);
    }
}
