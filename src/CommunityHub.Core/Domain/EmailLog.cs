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

    /// <summary>
    /// The branded template this send rendered, when it came through a template
    /// path (the per-participant <see cref="ParticipantEmailService"/>). Lets the
    /// organizer Email Log <b>re-send a failed row</b> faithfully — same template,
    /// same recipient, same category. Null for raw/ad-hoc sends (broadcast, PIN)
    /// that did not originate from a named template; such rows are not resendable
    /// from the log.
    /// </summary>
    public string? TemplateName { get; set; }

    /// <summary>
    /// §655 — how many times the retry job has re-attempted this failed send. Capped at 3.
    /// </summary>
    /// <remarks>
    /// Lives on the log row rather than in a side table because the row IS the unit of work: one
    /// failed send, one retry budget. A side table would have to be kept in step with a row it
    /// cannot outlive.
    /// </remarks>
    public int RetryCount { get; set; }

    /// <summary>§655 — when the last retry ran, so attempts can be spaced out over the hour.</summary>
    public DateTimeOffset? LastRetryAt { get; set; }

    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// §784.1 — when an organizer DISMISSED this row from the "Resend undelivered mail" queue.
    /// Null (the default) = never dismissed.
    /// </summary>
    /// <remarks>
    /// <para>The resend queue is DERIVED from this table rather than stored, so "I have dealt with
    /// this one" had nowhere to live and the row reappeared on every page load. This is that memory,
    /// and nothing more.</para>
    ///
    /// <para>🔒 <b>SCOPED TO THE QUEUE VIEW, DELIBERATELY.</b> It must never become a second answer
    /// to "was this mail delivered". Delivery is <see cref="Error"/> / <see cref="Success"/>
    /// reconciled against Brevo — these rows are ATTEMPTS, not sends. Dismissing hides a row from
    /// ONE organizer list; it marks nothing delivered, resent or resolved, and no other query may
    /// read it.</para>
    ///
    /// <para>⚠️ Additive + nullable on purpose: CEH auto-applies migrations on startup against a
    /// populated PROD database, so a non-nullable column without a default would fail the boot.</para>
    /// </remarks>
    public DateTimeOffset? ResendDismissedAt { get; set; }

    /// <summary>§784.1 — who dismissed it. Null when never dismissed.</summary>
    public string? ResendDismissedByEmail { get; set; }
}
