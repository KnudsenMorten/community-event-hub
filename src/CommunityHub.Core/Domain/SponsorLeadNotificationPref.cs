namespace CommunityHub.Core.Domain;

/// <summary>
/// Per-sponsor-company notification preferences for the leads pipeline.
/// One row per (EventId, SponsorCompanyId).
///
/// Notifications are delta-only: the daily sync job remembers the
/// timestamp of the last successful per-sponsor notify, and sends a
/// digest of leads captured after that timestamp. So a sponsor doesn't
/// get the same lead twice across two consecutive deltas.
/// </summary>
public class SponsorLeadNotificationPref
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public string SponsorCompanyId { get; set; } = string.Empty;

    /// <summary>Master switch. When false, the sponsor gets nothing.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Comma-separated email recipient list. Defaults to the sponsor's primary contact email at create time.</summary>
    public string Recipients { get; set; } = string.Empty;

    /// <summary>
    /// When to fire the delta notification. Daily (06:00 UTC) by default;
    /// "real-time" (within ~30 minutes of the lead landing) opt-in for
    /// sponsors that prefer push instead of digest.
    /// </summary>
    public SponsorLeadNotifyCadence Cadence { get; set; } = SponsorLeadNotifyCadence.Daily;

    /// <summary>If true, leads the AI screen marked as Junk are excluded from the delta.</summary>
    public bool SkipJunk { get; set; } = true;

    /// <summary>Last delta timestamp the job sent for this sponsor. Used as the "since" cursor for the next run.</summary>
    public DateTimeOffset? LastDeltaSentAt { get; set; }
}

public enum SponsorLeadNotifyCadence
{
    Daily    = 0,
    RealTime = 1,
}
