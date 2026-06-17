using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// A stable, language-neutral key for one data feed on the freshness panel. The
/// UI maps it to a localized label (en + da-DK); the service never emits display
/// text, only the key + the timestamps.
/// </summary>
public enum FreshnessFeed
{
    /// <summary>Most recent outbound email (the <c>EmailLog</c> audit).</summary>
    Email,
    /// <summary>Most recent attendee reconciliation sync (Zoho Backstage → hub).</summary>
    AttendeeSync,
    /// <summary>Most recent master-class booking sync (Zoho Booking → hub).</summary>
    MasterClassBookingSync,
    /// <summary>Most recent sponsor-lead capture or CRM sync.</summary>
    SponsorLeads,
    /// <summary>Most recent Sessionize speaker import.</summary>
    SpeakerImport,
    /// <summary>Most recent Sessionize session import.</summary>
    SessionImport,
    /// <summary>Most recent attendee question asked for a session.</summary>
    SessionQuestions,
    /// <summary>Most recent session evaluation (rating) submitted.</summary>
    SessionEvaluations,
    /// <summary>Most recent social-media post published to the company page.</summary>
    SoMePublished,
}

/// <summary>
/// One row of the freshness panel: when a given data feed last produced data,
/// how long ago that was, and whether it has crossed the staleness threshold.
/// All timestamps are UTC. <see cref="LastActivityUtc"/> is <c>null</c> when the
/// feed has never produced anything for the edition (the UI shows "no data yet").
/// </summary>
public sealed record FreshnessRow(FreshnessFeed Feed, DateTimeOffset? LastActivityUtc, TimeSpan StaleAfter)
{
    /// <summary>True once the feed has produced data at least once.</summary>
    public bool HasData => LastActivityUtc is not null;

    /// <summary>Age of the most recent activity relative to "now"
    /// (<c>null</c> when the feed has no data). Never negative — a clock skew
    /// that puts the last activity slightly in the future clamps to zero.</summary>
    public TimeSpan? AgeFor(DateTimeOffset now) =>
        LastActivityUtc is null
            ? null
            : (now > LastActivityUtc.Value ? now - LastActivityUtc.Value : TimeSpan.Zero);

    /// <summary>
    /// True when the feed has data AND that data is older than its
    /// <see cref="StaleAfter"/> window — the organizer's "this looks stale,
    /// did a sync stop?" signal. A feed that has never produced data is NOT
    /// flagged stale (it is a distinct "no data yet" state, surfaced via
    /// <see cref="HasData"/>), so a brand-new edition does not light up red.
    /// </summary>
    public bool IsStale(DateTimeOffset now)
    {
        var age = AgeFor(now);
        return age is not null && age.Value > StaleAfter;
    }
}

/// <summary>The freshness snapshot for one edition.</summary>
public sealed class FreshnessSnapshot
{
    /// <summary>"Now" the snapshot was computed at (UTC) — the reference the UI
    /// uses to render each row's age + staleness consistently.</summary>
    public DateTimeOffset GeneratedAtUtc { get; set; }

    /// <summary>One row per <see cref="FreshnessFeed"/>, in display order.</summary>
    public List<FreshnessRow> Feeds { get; set; } = new();

    /// <summary>The feeds that have data and have crossed their staleness window.</summary>
    public IReadOnlyList<FreshnessRow> StaleFeeds =>
        Feeds.Where(f => f.IsStale(GeneratedAtUtc)).ToList();

    /// <summary>True when at least one feed is stale (drives the "needs
    /// attention" badge on the organizer dashboards).</summary>
    public bool HasStaleFeeds => Feeds.Any(f => f.IsStale(GeneratedAtUtc));
}

/// <summary>
/// Builds the organizer <b>data-freshness panel</b> (REQUIREMENTS §21 Organizer
/// [M] — "last synced at" timestamps across the dashboards). It is a
/// <b>read-only aggregation</b>: for each major data feed it finds the single
/// most-recent activity timestamp for the edition and reports its age + whether
/// it has gone stale. It computes nothing into a new table and never writes;
/// calling <see cref="BuildAsync"/> twice yields the same snapshot of the data.
///
/// Distinct from <see cref="OrganizerOverviewService"/> (counts + completion
/// rates) and <see cref="CommsCockpitService"/> (the comms timeline): this
/// answers a different question — "is each data source still being fed, or has a
/// sync silently stopped?" — by surfacing one freshness/recency stamp per feed.
///
/// Each query resolves a single max-timestamp server-side (no client-side
/// nested Select/OrderBy), so the whole snapshot is SQL-translatable.
/// </summary>
public sealed class DataFreshnessService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    // Per-feed staleness windows. These are recency expectations for a feed that
    // is actively being fed during event prep; a feed quieter than its window is
    // worth an organizer's glance ("did the sync stop?"). They are display hints,
    // not hard SLAs — kept as plain constants (no config/secret).
    public static readonly TimeSpan EmailStale            = TimeSpan.FromDays(3);
    public static readonly TimeSpan AttendeeSyncStale     = TimeSpan.FromHours(36);
    public static readonly TimeSpan BookingSyncStale      = TimeSpan.FromHours(36);
    public static readonly TimeSpan SponsorLeadsStale     = TimeSpan.FromDays(7);
    public static readonly TimeSpan SpeakerImportStale    = TimeSpan.FromDays(7);
    public static readonly TimeSpan SessionImportStale    = TimeSpan.FromDays(7);
    public static readonly TimeSpan SessionQuestionsStale = TimeSpan.FromDays(14);
    public static readonly TimeSpan SessionEvalsStale     = TimeSpan.FromDays(14);
    public static readonly TimeSpan SoMePublishedStale    = TimeSpan.FromDays(7);

    public DataFreshnessService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<FreshnessSnapshot> BuildAsync(int eventId, CancellationToken ct = default)
    {
        var snapshot = new FreshnessSnapshot
        {
            GeneratedAtUtc = _clock.GetUtcNow(),
        };

        // --- one max-timestamp per feed (each resolved server-side) ----------

        var email = await _db.EmailLogs
            .Where(l => l.EventId == eventId)
            .MaxAsync(l => (DateTimeOffset?)l.SentAt, ct);

        var attendeeSync = await _db.Attendees
            .Where(a => a.EventId == eventId)
            .MaxAsync(a => (DateTimeOffset?)a.LastSyncedAt, ct);

        var bookingSync = await _db.MasterClassParticipants
            .Where(m => m.EventId == eventId)
            .MaxAsync(m => m.LastSyncedAt, ct);

        // A lead's freshness = the later of its capture and its last CRM sync.
        var leadCaptured = await _db.SponsorLeads
            .Where(l => l.EventId == eventId)
            .MaxAsync(l => (DateTimeOffset?)l.CapturedAt, ct);
        var leadSynced = await _db.SponsorLeads
            .Where(l => l.EventId == eventId)
            .MaxAsync(l => (DateTimeOffset?)l.LastSyncedAt, ct);
        var sponsorLeads = Later(leadCaptured, leadSynced);

        var speakerImport = await _db.SpeakerProfiles
            .Where(s => s.EventId == eventId)
            .MaxAsync(s => s.LastSessionizeImportAt, ct);

        var sessionImport = await _db.Sessions
            .Where(s => s.EventId == eventId)
            .MaxAsync(s => s.LastSessionizeImportAt, ct);

        var sessionQuestions = await _db.SessionQuestions
            .Where(q => q.EventId == eventId)
            .MaxAsync(q => (DateTimeOffset?)q.CreatedAt, ct);

        var sessionEvals = await _db.SessionEvaluations
            .Where(e => e.EventId == eventId)
            .MaxAsync(e => (DateTimeOffset?)e.CreatedAt, ct);

        var soMePublished = await _db.SoMePosts
            .Where(p => p.EventId == eventId)
            .MaxAsync(p => p.PublishedAtUtc, ct);

        snapshot.Feeds = new List<FreshnessRow>
        {
            new(FreshnessFeed.Email,                  email,            EmailStale),
            new(FreshnessFeed.AttendeeSync,           attendeeSync,     AttendeeSyncStale),
            new(FreshnessFeed.MasterClassBookingSync, bookingSync,      BookingSyncStale),
            new(FreshnessFeed.SponsorLeads,           sponsorLeads,     SponsorLeadsStale),
            new(FreshnessFeed.SpeakerImport,          speakerImport,    SpeakerImportStale),
            new(FreshnessFeed.SessionImport,          sessionImport,    SessionImportStale),
            new(FreshnessFeed.SessionQuestions,       sessionQuestions, SessionQuestionsStale),
            new(FreshnessFeed.SessionEvaluations,     sessionEvals,     SessionEvalsStale),
            new(FreshnessFeed.SoMePublished,          soMePublished,    SoMePublishedStale),
        };

        return snapshot;
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Value >= b.Value ? a : b;
    }
}
