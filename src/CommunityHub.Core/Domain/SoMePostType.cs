namespace CommunityHub.Core.Domain;

/// <summary>
/// What a queued LinkedIn company-page post is about (REQUIREMENTS §19). Drives
/// the compliance-aware tag set (sponsor posts tag the signer + event coordinator
/// + sponsor company; speaker posts tag organizers only — the LinkedIn API cannot
/// tag external speakers) and the optional auto-generated text/branding.
/// </summary>
public enum SoMePostType
{
    /// <summary>A post promoting a sponsor company.</summary>
    Sponsor = 0,

    /// <summary>A post promoting a speaker / their session.</summary>
    Speaker = 1,

    /// <summary>A one-off organizer-composed post (no linked sponsor/speaker).</summary>
    AdHoc = 2,
}

/// <summary>The lifecycle status of one queued post.</summary>
public enum SoMePostStatus
{
    /// <summary>Scheduled, not yet published. The dispatcher publishes due, Active, Queued posts.</summary>
    Queued = 0,

    /// <summary>Successfully published to the LinkedIn company page.</summary>
    Published = 1,

    /// <summary>A publish attempt failed; the error is recorded (never silently dropped).</summary>
    Failed = 2,
}
