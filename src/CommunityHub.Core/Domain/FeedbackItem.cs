namespace CommunityHub.Core.Domain;

/// <summary>
/// What a <see cref="FeedbackItem"/> is — used to route + label it (REQUIREMENTS §137).
/// </summary>
public enum FeedbackKind
{
    /// <summary>A bug / error report (Danish "fejl"). Routed to the dev mailbox.</summary>
    Bug = 0,

    /// <summary>A feature request / suggestion (Danish "forslag"). Routed to the dev mailbox.</summary>
    Feature = 1,

    /// <summary>A direct question/message a user asked us to forward to the organizers.</summary>
    Question = 2,
}

/// <summary>
/// One captured item in the "CEH feed" — a bug/feature report the AiHelper detected in a
/// user's message, or a question a user explicitly asked us to forward to the organizers
/// (REQUIREMENTS §137). Lightweight, append-only; the durable record that backs the
/// outbound intake email so feedback is never lost if mail fails.
///
/// IDENTITY IS SERVER-RESOLVED: <see cref="ParticipantId"/> + <see cref="Role"/> are taken
/// from the signed-in principal at the endpoint, NEVER from the request body — so the feed
/// faithfully records who/role/when.
/// </summary>
public class FeedbackItem
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The signed-in participant who sent the message (server-resolved).</summary>
    public int? ParticipantId { get; set; }
    public Participant? Participant { get; set; }

    /// <summary>The participant's role at capture time (server-resolved).</summary>
    public ParticipantRole Role { get; set; }

    /// <summary>Bug / Feature / Question — drives routing + the subject line.</summary>
    public FeedbackKind Kind { get; set; }

    /// <summary>The user's original message text.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>The page/url the user was on when they sent it (context; optional).</summary>
    public string? PageUrl { get; set; }

    /// <summary>The address the intake email was routed to (e.g. the dev or organizer mailbox).</summary>
    public string? RoutedTo { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set when an organizer marks the item handled on the /Organizer/Feed surface.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
}
