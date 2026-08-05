namespace CommunityHub.Core.Domain.Evaluation;

/// <summary>
/// §743 C3 — a session as Session Evaluation sees it: a room, a time window, and the collection
/// window that decides which presses belong to it.
/// </summary>
/// <remarks>
/// <para>🔒 <b>CEH is the MASTER.</b> These rows are created and updated by the sync from CEH
/// sessions and are keyed by <see cref="CehSessionId"/>. A local edit that disagrees is overwritten
/// — but never silently: the sync records what it replaced, because a room reassignment made during
/// the event is exactly the change that would otherwise vanish and take device attribution with it.
/// A wrong figure must be traceable back to the moment the session moved.</para>
///
/// <para>🔑 <b>The collection window is stored, not computed on the fly.</b> It opens at the
/// scheduled start and closes 30 minutes after the scheduled end. Storing it means a later change to
/// the grace period cannot silently re-attribute presses that were already scored under the old
/// one — and it makes the attribution query a plain range comparison the database can index.</para>
/// </remarks>
public class EvaluationSession
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The CEH session this mirrors. The sync's match key; null only for a hand-made row.</summary>
    public int? CehSessionId { get; set; }

    /// <summary>
    /// The room whose device collects for this session. NULL when CEH has no room on the session
    /// yet — such a session simply cannot be attributed to, which is honest rather than a guess.
    /// </summary>
    public int? RoomId { get; set; }
    public EvaluationRoom? Room { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Denormalised from CEH's free-text track. Kept as a STRING deliberately — see REQUIREMENTS
    /// §743.8: CEH's own track is a string, so a Track entity here would be derived from it and buys
    /// nothing until the §6 <c>scope=track</c> aggregates exist.
    /// </summary>
    public string? TrackName { get; set; }

    /// <summary>Scheduled start/end, from CEH. Both required for a session to be attributable.</summary>
    public DateTimeOffset ScheduledStart { get; set; }
    public DateTimeOffset ScheduledEnd { get; set; }

    /// <summary>Opens at the scheduled start.</summary>
    public DateTimeOffset CollectionWindowOpensAt { get; set; }

    /// <summary>
    /// Closes <see cref="Evaluation.EvaluationAttribution.GraceMinutes"/> after the scheduled end —
    /// the attendee pressing on their way out.
    /// </summary>
    public DateTimeOffset CollectionWindowClosesAt { get; set; }

    /// <summary>Minutes, from CEH's schedule. A reporting dimension: a 20-minute session and a
    /// 60-minute master class are not comparable on response volume.</summary>
    public int? ScheduledLengthMinutes { get; set; }

    /// <summary>
    /// §750 C7 — the report VERSION last published to SharePoint and notified about. NULL means no
    /// report has gone out yet, which is also what distinguishes a FIRST notification from a
    /// SUPERSEDING one.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>This is the whole debounce state, and it is deliberately one field.</b> "Has
    /// anything changed?" is answered by comparing it to the version derived from the CURRENT data
    /// (<see cref="Evaluation.EvaluationReportBuilder.DeriveVersion"/>), so there is no second
    /// bookkeeping table to fall out of step with the responses themselves.</para>
    ///
    /// <para>🔒 <b>Stamped only after the publish AND the notification have both succeeded.</b> If
    /// either fails, the field keeps its old value and the next pass retries — republishing identical
    /// bytes is harmless, whereas stamping first would lose a notification permanently and silently.</para>
    ///
    /// <para>⚠️ <b>Ours, not CEH's.</b> The C3 sync overwrites the mirrored columns above; it must
    /// never touch these two, or every schedule edit would re-notify every speaker.</para>
    /// </remarks>
    public string? PublishedReportVersion { get; set; }

    /// <summary>When <see cref="PublishedReportVersion"/> was published. Display + diagnostics.</summary>
    public DateTimeOffset? PublishedReportAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
