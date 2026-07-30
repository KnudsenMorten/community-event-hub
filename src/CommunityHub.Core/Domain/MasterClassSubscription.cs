namespace CommunityHub.Core.Domain;

/// <summary>
/// §383 — which Master Class notifications a person has OPTED OUT of.
///
/// <para><b>A row means UNSUBSCRIBED.</b> Subscription is the default (operator 2026-07-26: the
/// toggle is <i>"turned ON by default"</i>), and storing the default as the ABSENCE of a row is what
/// makes that true without a backfill: an attendee who signs up tomorrow, or a speaker linked to the
/// class next week, is subscribed the moment they exist. A default-<c>true</c> boolean column would
/// have needed a row written for every existing and future member, and anyone missed would silently
/// be unsubscribed — the failure mode you cannot see.</para>
///
/// <para><b>One row per person per KIND.</b> The operator asked for a toggle next to box 2 and
/// another next to box 3, so they are independent: muting the Q&amp;A must not also mute
/// "the speakers updated their instructions".</para>
///
/// <para><b>Exactly one of <see cref="ParticipantId"/> / <see cref="AttendeeId"/> is set</b> — the
/// same two-identity shape the comment board already uses (<see cref="MasterClassComment"/>), because
/// a Master Class audience is speakers (hub participants) plus attendees (ticket rows), and those are
/// different tables.</para>
/// </summary>
public class MasterClassSubscription
{
    public int Id { get; set; }

    /// <summary>The edition. Every query is scoped by this.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The master-class session the opt-out applies to.</summary>
    public int SessionId { get; set; }
    public Session Session { get; set; } = null!;

    /// <summary>Set when the person is a hub participant (a linked speaker, or an organizer).</summary>
    public int? ParticipantId { get; set; }
    public Participant? Participant { get; set; }

    /// <summary>Set when the person is an attendee with a signup for this class.</summary>
    public int? AttendeeId { get; set; }
    public Attendee? Attendee { get; set; }

    /// <summary>Which notification stream this opt-out silences.</summary>
    public MasterClassNotificationKind Kind { get; set; }

    /// <summary>When they opted out — kept for support questions ("why did I stop getting these?").</summary>
    public DateTimeOffset UnsubscribedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// §383 — the two independent Master Class notification streams, matching the two toggles on the
/// landing page. Values are persisted, so never renumber them.
/// </summary>
public enum MasterClassNotificationKind
{
    /// <summary>Box 2 — a speaker edited the instructions / what-to-bring on the landing page.</summary>
    SpeakerInstructions = 0,

    /// <summary>Box 3 — somebody posted to the Master Class Q&amp;A.</summary>
    QandA = 1,
}
