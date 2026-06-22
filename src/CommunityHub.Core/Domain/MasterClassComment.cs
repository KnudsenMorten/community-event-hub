namespace CommunityHub.Core.Domain;

/// <summary>
/// One Q&amp;A comment on a master class's attendee landing page (FEATURE 2).
/// Visible WITHIN the master class only: anyone with a CONFIRMED
/// <see cref="MasterClassSignup"/> for the session, plus the master class's
/// speakers (and organizers), can post and read. Edition-scoped via
/// <see cref="EventId"/>; threaded via the optional <see cref="ParentCommentId"/>.
///
/// An author is EITHER a hub <see cref="Participant"/> (a speaker / organizer, via
/// <see cref="AuthorParticipantId"/>) OR an <see cref="Domain.Attendee"/> (a confirmed
/// signup, via <see cref="AuthorAttendeeId"/>); exactly one is set. The display name
/// is denormalised at post time so the thread renders without extra joins and reads
/// honestly even if the author row later changes.
/// </summary>
public class MasterClassComment
{
    public int Id { get; set; }

    /// <summary>The edition this comment belongs to. Every query is scoped by this.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The master-class session the comment is posted on.</summary>
    public int SessionId { get; set; }
    public Session Session { get; set; } = null!;

    /// <summary>The hub participant (speaker / organizer) who posted, when the author is a hub user.</summary>
    public int? AuthorParticipantId { get; set; }
    public Participant? AuthorParticipant { get; set; }

    /// <summary>The attendee (confirmed signup) who posted, when the author is an attendee.</summary>
    public int? AuthorAttendeeId { get; set; }
    public Attendee? AuthorAttendee { get; set; }

    /// <summary>Denormalised author display name, captured at post time.</summary>
    public string AuthorDisplayName { get; set; } = string.Empty;

    /// <summary>The comment text. Required; capped (see the DbContext mapping).</summary>
    public string Body { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optional parent for a threaded reply; null = a top-level comment.</summary>
    public int? ParentCommentId { get; set; }
    public MasterClassComment? ParentComment { get; set; }
}
