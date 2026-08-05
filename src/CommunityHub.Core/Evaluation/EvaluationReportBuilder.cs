using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §747 C8 — assembles one session's report and derives its VERSION IDENTIFIER.
/// </summary>
/// <remarks>
/// <para>This exists so the organiser download and the outbound pull produce the <b>same document
/// from the same code</b>. The assembly used to live in the Razor page handler; a second copy in the
/// API controller would have been two report definitions that drift, and the brief treats the PDF as
/// the permanent record once raw responses are purged at 12 months.</para>
///
/// <para>🔒 <b>Reports are GENERATED, never stored</b> (§8, §743.14). There is consequently no file
/// version to hand out, which is the design problem C8 actually poses — see
/// <see cref="DeriveVersion"/>.</para>
/// </remarks>
public sealed class EvaluationReportBuilder
{
    private readonly CommunityHubDbContext _db;
    private readonly EvaluationScoreService _scores;
    private readonly TimeProvider _clock;

    public EvaluationReportBuilder(
        CommunityHubDbContext db, EvaluationScoreService scores, TimeProvider? clock = null)
    {
        _db = db;
        _scores = scores;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The report's inputs, plus the version those inputs imply.</summary>
    public sealed record Report(EvaluationReportService.ReportData Data, string Version);

    /// <summary>
    /// The raw facts a version is derived from. Public so a test can pin the derivation without a
    /// database.
    /// </summary>
    public sealed record VersionInputs(
        int SessionResponses,
        DateTimeOffset? SessionLatestReceived,
        int EventResponses,
        DateTimeOffset? EventLatestReceived,
        DateTimeOffset SessionUpdatedAt);

    /// <summary>
    /// Derive the version identifier the consumer compares against.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>The version describes the DATA the report is made of, not the bytes.</b> Two PDFs
    /// rendered a second apart differ byte-for-byte — the generation timestamp is printed on the page
    /// — so a hash of the file would change on every poll and tell a consumer nothing. The brief asks
    /// for the opposite property: <i>"consumers must be able to detect that a newer version
    /// exists"</i>, i.e. a value that changes <b>exactly when a recomputation would change the
    /// figures</b>.</para>
    ///
    /// <para>The inputs are therefore the things that can move a number on the page:</para>
    /// <list type="bullet">
    ///   <item><b>The session's response count and latest <c>ReceivedTimestamp</c></b> — the session
    ///   block. Count alone is not enough: the 12-month purge and any future correction row can
    ///   change the set without changing its size.</item>
    ///   <item><b>The event's response count and latest <c>ReceivedTimestamp</c></b> — because the
    ///   report prints the event POOLED figure beside the session's, so a press in a different room
    ///   genuinely does change this document. This is not over-signalling; the page really did
    ///   change.</item>
    ///   <item><b>The session's <c>UpdatedAt</c></b> — a C3 sync can retitle a session or move it to
    ///   another room with no new response at all, and the report's header would then be stale.</item>
    /// </list>
    ///
    /// <para>🔒 <b><c>ReceivedTimestamp</c>, never <c>CollectionTimestamp</c>.</b> Late data is
    /// normal operation here: a device flushing a day-old cache produces a row whose collection time
    /// is in the past, so a version keyed on collection time could move BACKWARDS and a consumer
    /// would conclude nothing had changed. Received time is monotonic with our acceptance of data,
    /// which is precisely when a recomputation becomes possible.</para>
    ///
    /// <para>⚠️ <b>Do NOT make this easier by storing a computed score.</b> §8 forbids it and the whole
    /// read path (§743.10/§743.12) is built on deriving every figure at query time.</para>
    /// </remarks>
    public static string DeriveVersion(VersionInputs i)
    {
        ArgumentNullException.ThrowIfNull(i);

        // Fixed-format, invariant, UTC — the string is an identity, so it must not vary with the
        // server's locale or time zone. "-" for a null timestamp keeps a zero-response session
        // distinguishable from one whose only response arrived at the epoch.
        static string Ts(DateTimeOffset? t) =>
            t is null ? "-" : t.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        var material = string.Join('|',
            "v1",
            i.SessionResponses.ToString(CultureInfo.InvariantCulture),
            Ts(i.SessionLatestReceived),
            i.EventResponses.ToString(CultureInfo.InvariantCulture),
            Ts(i.EventLatestReceived),
            Ts(i.SessionUpdatedAt));

        // Hashed rather than emitted raw: the plain string leaks the event's total response count to
        // a consumer entitled to one session, and an opaque token also stops anyone parsing it and
        // depending on a shape we may change. 16 bytes is ample for change detection.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
    }

    /// <summary>
    /// The version alone, without rendering anything — the cheap path behind <c>HEAD</c> and
    /// <c>If-None-Match</c>, so a consumer can poll frequently without generating a PDF each time.
    /// Null when the session does not exist in this edition.
    /// </summary>
    public async Task<string?> VersionAsync(int eventId, int sessionId, CancellationToken ct = default)
    {
        var inputs = await VersionInputsAsync(eventId, sessionId, ct);
        return inputs is null ? null : DeriveVersion(inputs);
    }

    /// <summary>
    /// §6.2 — the CURRENT report version for many sessions at once, for the organizer grid's
    /// "stale" badge.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>It derives with <see cref="DeriveVersion"/> — the same function the publisher
    /// uses.</b> Re-deriving the version string on the page would be two implementations of one
    /// identity, and the page would then disagree with the thing that actually publishes: reporting
    /// a "stale" that no publish ever clears, or "current" about a report about to be superseded.
    /// </para>
    ///
    /// <para>⚠️ <b>BULK on purpose.</b> <see cref="VersionAsync"/> costs four round-trips per
    /// session, so calling it per row would be a hundred queries to paint one page of a grid whose
    /// stated discipline is bulk reads. This is four queries for the whole page.</para>
    ///
    /// <para>A session with NO responses is absent from the result: it has no report at all, so it
    /// cannot have a stale one.</para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, string>> VersionsAsync(
        int eventId, IReadOnlyCollection<int> sessionIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();
        if (sessionIds.Count == 0) return result;

        var sessions = await _db.EvaluationSessions
            .AsNoTracking()
            .Where(s => s.EventId == eventId && sessionIds.Contains(s.Id))
            .Select(s => new { s.Id, s.UpdatedAt })
            .ToListAsync(ct);
        if (sessions.Count == 0) return result;

        var perSession = await _db.EvaluationResponses
            .AsNoTracking()
            .Where(r => r.EventId == eventId && r.SessionId != null
                        && sessionIds.Contains(r.SessionId!.Value))
            .GroupBy(r => r.SessionId!.Value)
            .Select(g => new
            {
                SessionId = g.Key,
                Count = g.Count(),
                Latest = g.Max(x => (DateTimeOffset?)x.ReceivedTimestamp),
            })
            .ToListAsync(ct);
        var bySession = perSession.ToDictionary(x => x.SessionId);

        // The EVENT-wide figures are part of the version too (the report carries a pooled
        // comparison), so a response to ANY session moves every session's version. Two queries for
        // the page, not two per row.
        var eventQuery = _db.EvaluationResponses.AsNoTracking().Where(r => r.EventId == eventId);
        var eventCount = await eventQuery.CountAsync(ct);
        var eventLatest = await eventQuery
            .Select(r => (DateTimeOffset?)r.ReceivedTimestamp).MaxAsync(ct);

        foreach (var s in sessions)
        {
            if (!bySession.TryGetValue(s.Id, out var own)) continue;

            result[s.Id] = DeriveVersion(new VersionInputs(
                SessionResponses: own.Count,
                SessionLatestReceived: own.Latest,
                EventResponses: eventCount,
                EventLatestReceived: eventLatest,
                SessionUpdatedAt: s.UpdatedAt));
        }

        return result;
    }

    private async Task<VersionInputs?> VersionInputsAsync(
        int eventId, int sessionId, CancellationToken ct)
    {
        var session = await _db.EvaluationSessions
            .Where(s => s.Id == sessionId && s.EventId == eventId)
            .Select(s => new { s.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        if (session is null) return null;

        // Aggregates rather than pulling rows: this runs on every poll.
        //
        // 🔒 Plain Count/Max, NOT a GroupBy(_ => 1) projection. The grouped form is tidier and reads
        // as one round-trip, but its SQL translation is a provider detail — and EF InMemory, which
        // every test here runs on, has no translation step at all: it evaluates in memory and passes
        // regardless. A green suite therefore cannot tell a translatable query from one that throws
        // on SQL Server at runtime. Two extra cheap round-trips buy certainty on a path that must
        // not fail in front of a machine consumer. (Same lesson as §744.1's foreign keys: InMemory
        // proves less about SQL Server than it appears to.)
        //
        // Casting to a nullable inside Max is what makes an EMPTY set return null rather than throw.
        var sessionQuery = _db.EvaluationResponses
            .Where(r => r.EventId == eventId && r.SessionId == sessionId);
        var sessionCount = await sessionQuery.CountAsync(ct);
        var sessionLatest = await sessionQuery
            .Select(r => (DateTimeOffset?)r.ReceivedTimestamp).MaxAsync(ct);

        var eventQuery = _db.EvaluationResponses.Where(r => r.EventId == eventId);
        var eventCount = await eventQuery.CountAsync(ct);
        var eventLatest = await eventQuery
            .Select(r => (DateTimeOffset?)r.ReceivedTimestamp).MaxAsync(ct);

        return new VersionInputs(
            SessionResponses: sessionCount,
            SessionLatestReceived: sessionLatest,
            EventResponses: eventCount,
            EventLatestReceived: eventLatest,
            SessionUpdatedAt: session.UpdatedAt);
    }

    /// <summary>
    /// Assemble one session's report. Null when the session is not part of this edition — the same
    /// answer for "no such session" and "another event's session", so a caller holding a credential
    /// for one event cannot probe another's schedule.
    /// </summary>
    public async Task<Report?> BuildAsync(int eventId, int sessionId, CancellationToken ct = default)
    {
        var scheduled = await _db.EvaluationSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.EventId == eventId, ct);
        if (scheduled is null) return null;

        var session = await _scores.ForSessionAsync(eventId, sessionId, ct: ct);
        if (session is null) return null;

        var (pooled, _, _) = await _scores.ForEventAsync(eventId, ct: ct);

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct);

        // Free text arrives with the QR channel (C5/C6); until then there is genuinely none, and the
        // report says so rather than implying feedback was withheld.
        var comments = await _db.EvaluationResponses
            .Where(r => r.SessionId == sessionId && r.FreeText != null && r.FreeText != "")
            .OrderBy(r => r.CollectionTimestamp)
            .Select(r => r.FreeText!)
            .ToListAsync(ct);

        var inputs = await VersionInputsAsync(eventId, sessionId, ct);

        var timeline = await BuildTimelineAsync(sessionId, scheduled, ct);

        // §750.8 — which CHANNEL each press came from. Source is diagnostics-only for scoring (both
        // weigh identically, §748), but it is real information for the reader: it says whether the
        // room's device was working, and whether anyone actually scanned the printed code.
        var sourceCounts = await _db.EvaluationResponses
            .Where(r => r.SessionId == sessionId)
            .GroupBy(r => 1)
            .Select(g => new
            {
                Qr = g.Count(r => r.Source == Domain.Evaluation.EvaluationResponseSources.Qr),
                Device = g.Count(r => r.Source != Domain.Evaluation.EvaluationResponseSources.Qr),
            })
            .FirstOrDefaultAsync(ct) ?? new { Qr = 0, Device = 0 };

        var data = new EvaluationReportService.ReportData(
            EventName: ev?.DisplayName ?? ev?.Code ?? "Event",
            SessionTitle: session.Title,
            Room: session.Room,
            TrackName: session.TrackName,
            ScheduledStart: scheduled.ScheduledStart,
            ScheduledEnd: scheduled.ScheduledEnd,
            Score: session.Score,
            EventPooled: pooled,
            Comments: comments,
            GeneratedAt: _clock.GetUtcNow(),
            Timeline: timeline,
            WindowOpensAt: scheduled.CollectionWindowOpensAt,
            WindowClosesAt: scheduled.CollectionWindowClosesAt,
            QrResponses: sourceCounts.Qr,
            DeviceResponses: sourceCounts.Device);

        return new Report(data, DeriveVersion(inputs!));
    }

    /// <summary>How many slices the collection window is cut into for the timeline chart.</summary>
    /// <remarks>
    /// 🔑 12 across a typical 90-minute window is roughly one bar per 7 minutes — fine enough to show
    /// the shape (a rush at the end is the normal one) without turning a 45-minute session into a row
    /// of ones and zeros that reads as noise.
    /// </remarks>
    public const int TimelineBuckets = 12;

    /// <summary>
    /// §750.2 — the brief's <i>"response timeline across the collection window"</i>.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>Bucketed by COLLECTION time, not received time.</b> The chart answers "when did the
    /// room respond?", which is about the talk. Received time answers "when did the device manage to
    /// upload?", which is about connectivity — a real question, but a different one, and plotting it
    /// here would draw a cache flush as a spike of applause.</para>
    ///
    /// <para>🔒 Presses OUTSIDE the window are excluded rather than clamped into the end bar. The
    /// window is what the session owns; a clamped outlier would invent a spike that never happened.</para>
    /// </remarks>
    private async Task<IReadOnlyList<EvaluationReportService.TimelineBucket>> BuildTimelineAsync(
        int sessionId, Domain.Evaluation.EvaluationSession scheduled, CancellationToken ct)
    {
        var from = scheduled.CollectionWindowOpensAt;
        var to = scheduled.CollectionWindowClosesAt;
        var span = to - from;
        if (span <= TimeSpan.Zero)
        {
            return Array.Empty<EvaluationReportService.TimelineBucket>();
        }

        var times = await _db.EvaluationResponses
            .Where(r => r.SessionId == sessionId
                        && r.CollectionTimestamp >= from && r.CollectionTimestamp <= to)
            .Select(r => r.CollectionTimestamp)
            .ToListAsync(ct);

        var slice = span / TimelineBuckets;
        var counts = new int[TimelineBuckets];
        foreach (var t in times)
        {
            var i = (int)((t - from).Ticks / slice.Ticks);
            // The final instant belongs to the last bucket rather than to a 13th that does not exist.
            if (i >= TimelineBuckets) i = TimelineBuckets - 1;
            if (i < 0) i = 0;
            counts[i]++;
        }

        return Enumerable.Range(0, TimelineBuckets)
            .Select(i => new EvaluationReportService.TimelineBucket(from + (slice * i), counts[i]))
            .ToList();
    }
}


