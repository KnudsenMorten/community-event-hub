namespace CommunityHub.Core.Domain;

/// <summary>
/// §1077 stage 5 — ONE PHOTO TIMESLOT an organizer has defined, for the planner to choose from.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"i will give you specific timeslot to choose from. 25 timeslots"</i>
/// · <i>"they go into the planner"</i> · <i>"with date time"</i>.</para>
///
/// <para>🔑 <b>Slots are DATA, not a formula.</b> An earlier draft generated a tidy grid from a start
/// time and a slot length; that is wrong here, because the real slots sit around the keynote, the
/// breaks and the lunch queue. Those are facts about the running order that only the organizers
/// know, so the planner picks from what they enter rather than inventing times that collide with the
/// programme.</para>
///
/// <para>🔒 <b>The DAY decides who can use it.</b> A slot on the pre-day is unusable by a company
/// whose people all hold 1-day tickets — they are not at the venue. <see cref="IsPreDay"/> is stored
/// rather than derived from the date, because an edition's pre-day is a property of the edition and
/// a slot may be entered before those dates are final.</para>
/// </remarks>
public class GroupPhotoSlot
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>When the slot starts (UTC).</summary>
    public DateTimeOffset StartUtc { get; set; }

    /// <summary>Photo sessions are short; 10 minutes is the working default.</summary>
    public int DurationMinutes { get; set; } = 10;

    /// <summary>True for a slot on the pre-day. See the class remarks — this is load-bearing.</summary>
    public bool IsPreDay { get; set; }

    /// <summary>Where the photo is taken, when it differs from the venue default.</summary>
    public string? Location { get; set; }

    /// <summary>An organizer's own note, e.g. "after the keynote" or "by the sponsor wall".</summary>
    public string? Label { get; set; }

    /// <summary>
    /// 🔒 Set to take a slot out of the plan WITHOUT deleting it — the photographer's break, a slot
    /// held back for a late arrival. Deleting a slot a company has already been told about is the
    /// one destructive move here, so there is a way to say "not available" that is not deletion.
    /// </summary>
    public bool IsBlocked { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Usable by the planner: not blocked.</summary>
    public bool IsAvailable => !IsBlocked;
}
