using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §743 C7 — read the stored responses and DERIVE the figures. Nothing here is persisted.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Every metric is computed at query time from raw responses</b> (§8). No score is
/// stored as authoritative, which is what makes changing the weight profile, revising the bands,
/// correcting an attribution error or recomputing history free rather than a migration.</para>
///
/// <para>Scores come from <see cref="SatisfactionScore"/> and nowhere else — Part 2 forbids a
/// component inventing its own variant, and this service is the only place the database and the
/// metric meet.</para>
/// </remarks>
public sealed class EvaluationScoreService
{
    private readonly CommunityHubDbContext _db;

    public EvaluationScoreService(CommunityHubDbContext db) => _db = db;

    /// <summary>One session's figures, plus what it needs to be displayed.</summary>
    public sealed record SessionScore(
        int SessionId, string Title, string? Room, string? TrackName,
        DateTimeOffset ScheduledStart, SatisfactionScore.Result Score);

    /// <summary>The four counters for a set of responses, by session.</summary>
    private async Task<Dictionary<int, SatisfactionScore.Distribution>> DistributionsAsync(
        int eventId, CancellationToken ct)
    {
        // One grouped query rather than one per session: (session, rating) → count.
        var rows = await _db.EvaluationResponses
            .Where(r => r.EventId == eventId && r.SessionId != null)
            .GroupBy(r => new { SessionId = r.SessionId!.Value, r.Rating })
            .Select(g => new { g.Key.SessionId, g.Key.Rating, Count = g.Count() })
            .ToListAsync(ct);

        var map = new Dictionary<int, (int f, int t, int tw, int o)>();
        foreach (var row in rows)
        {
            map.TryGetValue(row.SessionId, out var c);
            c = row.Rating switch
            {
                4 => (c.f + row.Count, c.t, c.tw, c.o),
                3 => (c.f, c.t + row.Count, c.tw, c.o),
                2 => (c.f, c.t, c.tw + row.Count, c.o),
                1 => (c.f, c.t, c.tw, c.o + row.Count),
                _ => c,
            };
            map[row.SessionId] = c;
        }

        return map.ToDictionary(
            kv => kv.Key,
            kv => new SatisfactionScore.Distribution(kv.Value.f, kv.Value.t, kv.Value.tw, kv.Value.o));
    }

    /// <summary>Every session in the edition with its current figures, earliest first.</summary>
    public async Task<IReadOnlyList<SessionScore>> ListAsync(
        int eventId, string profile = SatisfactionScore.ProfileLinear, CancellationToken ct = default)
    {
        var dists = await DistributionsAsync(eventId, ct);

        var sessions = await _db.EvaluationSessions
            .Where(s => s.EventId == eventId)
            .OrderBy(s => s.ScheduledStart)
            .Select(s => new
            {
                s.Id, s.Title, s.TrackName, s.ScheduledStart,
                Room = s.Room != null ? s.Room.Name : null,
            })
            .ToListAsync(ct);

        return sessions.Select(s => new SessionScore(
                s.Id, s.Title, s.Room, s.TrackName, s.ScheduledStart,
                SatisfactionScore.Compute(
                    dists.TryGetValue(s.Id, out var d) ? d : new SatisfactionScore.Distribution(0, 0, 0, 0),
                    profile)))
            .ToList();
    }

    /// <summary>One session's figures, or null when the session is not in this edition.</summary>
    public async Task<SessionScore?> ForSessionAsync(
        int eventId, int sessionId, string profile = SatisfactionScore.ProfileLinear,
        CancellationToken ct = default) =>
        (await ListAsync(eventId, profile, ct)).FirstOrDefault(s => s.SessionId == sessionId);

    /// <summary>The event-level figures: POOLED (the headline) and MEAN SESSION, named separately.</summary>
    /// <remarks>
    /// 🔒 <b>Pooled includes responses that belong to NO session</b> — the §5.3 "venue and date only"
    /// rows. They are real feedback from real attendees on a real day; only the attribution is
    /// missing, so excluding them from the event total would understate it. Mean-session cannot
    /// include them, because they belong to no session to average.
    /// </remarks>
    public async Task<(SatisfactionScore.Result Pooled, double? MeanSession, int Unattributed)>
        ForEventAsync(int eventId, string profile = SatisfactionScore.ProfileLinear,
            CancellationToken ct = default)
    {
        var perSession = (await DistributionsAsync(eventId, ct)).Values.ToList();

        var unattributedRatings = await _db.EvaluationResponses
            .Where(r => r.EventId == eventId && r.SessionId == null)
            .Select(r => r.Rating)
            .ToListAsync(ct);
        var unattributed = SatisfactionScore.Distribution.From(unattributedRatings);

        var pooled = SatisfactionScore.Pooled(perSession.Append(unattributed), profile);
        var mean = SatisfactionScore.MeanSession(perSession, profile);

        return (pooled, mean, unattributed.Total);
    }
}
