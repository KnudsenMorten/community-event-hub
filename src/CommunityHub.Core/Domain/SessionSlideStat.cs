namespace CommunityHub.Core.Domain;

/// <summary>
/// §322h: per-session slides interest counter for the public session-slides feature
/// (/Sessions/Slides). ONE number per (event, session) — operator 2026-07-24: "tracking
/// should count number of views or downloads (=1 number)". A hit is an embedded-viewer
/// open, a direct download, or a batch-ZIP inclusion. Surfaced to the session's speakers
/// (My Sessions) and to organizers (the Sessions admin grid). A counter, not analytics —
/// no per-user tracking, anonymous page ⇒ anonymous numbers.
/// </summary>
public class SessionSlideStat
{
    public int Id { get; set; }

    /// <summary>The edition this counter belongs to. Every query is scoped by this.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The session the counter is for (unique per event).</summary>
    public int SessionId { get; set; }
    public Session Session { get; set; } = null!;

    /// <summary>Views + downloads combined (one number by design).</summary>
    public int Count { get; set; }
}
