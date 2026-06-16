namespace CommunityHub.Core.Integrations;

/// <summary>
/// Pure auto-text composer for queued LinkedIn company-page posts
/// (REQUIREMENTS §19 "auto-schedule branding"). Builds a sensible default post
/// body from the linked sponsor/speaker + the edition display name. Static / pure
/// so it is unit-testable and so the queue service can re-populate
/// <c>SoMePost.AutoText</c> without a DB round-trip per token.
///
/// Auto mode is preferred but can be turned OFF per post; when off the
/// organizer's static/manual text wins and these methods are not called.
/// </summary>
public static class SoMeTextComposer
{
    /// <summary>Compose the default body for a SPEAKER post.</summary>
    /// <param name="speakerName">The speaker's display name.</param>
    /// <param name="sessionTitle">The session title (optional).</param>
    /// <param name="eventDisplayName">The edition display name (e.g. "Experts Live Denmark 2027").</param>
    public static string ForSpeaker(
        string speakerName, string? sessionTitle, string eventDisplayName)
    {
        var name = string.IsNullOrWhiteSpace(speakerName) ? "Our speaker" : speakerName.Trim();
        var evt = string.IsNullOrWhiteSpace(eventDisplayName) ? "our event" : eventDisplayName.Trim();

        return string.IsNullOrWhiteSpace(sessionTitle)
            ? $"Meet {name}, joining us at {evt}! We can't wait to have them on stage. #community #event"
            : $"Meet {name}, presenting \"{sessionTitle!.Trim()}\" at {evt}! "
              + "Don't miss this session. #community #event";
    }

    /// <summary>Compose the default body for a SPONSOR post.</summary>
    /// <param name="companyName">The resolved sponsor company name (public→legal→billing chain).</param>
    /// <param name="eventDisplayName">The edition display name.</param>
    public static string ForSponsor(string companyName, string eventDisplayName)
    {
        var co = string.IsNullOrWhiteSpace(companyName) ? "Our sponsor" : companyName.Trim();
        var evt = string.IsNullOrWhiteSpace(eventDisplayName) ? "our event" : eventDisplayName.Trim();

        return $"A huge thank-you to {co} for sponsoring {evt}! "
               + "We're proud to have their support. #sponsor #community #event";
    }
}
