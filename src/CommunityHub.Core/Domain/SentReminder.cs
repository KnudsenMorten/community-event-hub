namespace CommunityHub.Core.Domain;

/// <summary>
/// The idempotency ledger for the reminder scheduler (CONTEXT.md 11c / 11e).
/// The daily reminder job is stateless: on each run it re-evaluates what is
/// due, then checks this table to see what has already been sent. One row is
/// written per reminder actually delivered. A missed run self-heals on the
/// next run because nothing here marks it as sent.
///
/// The dedup key is (recipient + reminder type + occasion):
///   - RecipientEmail : who received it
///   - ReminderType   : which kind of reminder (e.g. "speaker-deadline")
///   - OccasionKey    : the specific thing it was about (e.g. a task id, or
///                      a deadline+offset), so the same recipient can get the
///                      same TYPE of reminder for different occasions, but
///                      never the same one twice.
/// </summary>
public class SentReminder
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- Dedup key (recipient + type + occasion) ----------------------------
    public string RecipientEmail { get; set; } = string.Empty;

    /// <summary>
    /// The reminder type, e.g. "speaker-deadline", "sponsor-overdue",
    /// "task-deadline", "incomplete-form". Matches a content template name.
    /// </summary>
    public string ReminderType { get; set; } = string.Empty;

    /// <summary>
    /// Identifies the specific occasion this reminder was about, so a repeat
    /// run does not re-send it. For a weekly digest this includes the week,
    /// so next week's digest is a distinct occasion.
    /// </summary>
    public string OccasionKey { get; set; } = string.Empty;

    /// <summary>When the reminder was actually sent.</summary>
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}
