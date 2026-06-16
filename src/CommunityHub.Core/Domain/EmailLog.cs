namespace CommunityHub.Core.Domain;

/// <summary>
/// One row per outbound email, written by the central <c>IEmailSender</c> path
/// (the <c>LoggingEmailSender</c> decorator over <c>BrevoEmailSender</c>) so
/// EVERY send is logged — welcome, PIN, task reminders, broadcast, onboarding,
/// manual re-send, session-evaluation, etc. The organizer Email Log view reads
/// this table (all sends + per-person, filter by name or email).
///
/// This is an AUDIT log, not the resume-safe dedup ledger: the
/// <see cref="SentReminder"/> ledger still decides whether a given reminder /
/// onboarding email should be sent; <see cref="EmailLog"/> records what actually
/// went out (or failed) regardless of type, after the redirect/allowlist gate.
/// </summary>
public class EmailLog
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    /// <summary>The edition this send belongs to (0 when the send had no edition
    /// context, e.g. a bootstrap mail). Every organizer query is scoped by this.</summary>
    public int EventId { get; set; }

    /// <summary>
    /// A coarse category for the send, e.g. "welcome", "onboarding",
    /// "task-deadline", "broadcast", "manual-resend", "pin", "session-eval".
    /// Set from the ambient <c>EmailContext</c>; "other" when none was set.
    /// </summary>
    public string Category { get; set; } = "other";

    // --- Recipients ---------------------------------------------------------
    /// <summary>The address the caller asked us to send to (the participant's
    /// effective address: speaker override ?? identity). Used for the per-person
    /// view + the email filter.</summary>
    public string ToEmail { get; set; } = string.Empty;

    /// <summary>
    /// The address the mail was ACTUALLY delivered to after the DEV redirect /
    /// PROD allowlist gate. In DEV this is the redirect inbox; in PROD it equals
    /// <see cref="ToEmail"/> (or empty if the allowlist dropped it).
    /// </summary>
    public string ActualToEmail { get; set; } = string.Empty;

    /// <summary>Comma-separated CC list (e.g. the participant's secondary email).
    /// Empty when there was no CC.</summary>
    public string CcEmails { get; set; } = string.Empty;

    /// <summary>The participant this send is about, when known (resolved from the
    /// ambient context). Null for non-participant mail (e.g. a bootstrap mail).</summary>
    public int? ParticipantId { get; set; }

    /// <summary>The recipient's display name when known (denormalised so the log
    /// view shows a name without a join, and survives a participant rename).</summary>
    public string? RecipientName { get; set; }

    // --- Content + outcome --------------------------------------------------
    public string Subject { get; set; } = string.Empty;

    /// <summary>True = the underlying sender completed without throwing (and the
    /// mail was not silently dropped by the allowlist). False = it failed or was
    /// dropped; see <see cref="Error"/>.</summary>
    public bool Success { get; set; }

    /// <summary>The failure / drop reason when <see cref="Success"/> is false.</summary>
    public string? Error { get; set; }

    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}
