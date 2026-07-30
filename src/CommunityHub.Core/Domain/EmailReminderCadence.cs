namespace CommunityHub.Core.Domain;

/// <summary>
/// §707.11 — how often ONE recurring mail repeats, per edition (operator 2026-07-30:
/// <i>"preferable the ability to individually control this cadence frequency per mail which has a
/// recurring goal over it until completed, like task reminders &amp; get started reminders"</i>).
/// </summary>
/// <remarks>
/// <para><b>Only RECURRING mail has one.</b> A mail is recurring when it chases a GOAL until that goal
/// is complete (finish Get Started, do the task, pick a Master Class). A confirmation or a receipt has
/// no cadence, and offering it one would be a control that governs nothing — the §326bx defect. The
/// recurring set and each mail's completion condition live in
/// <see cref="Email.EmailTemplateCatalog.RecurringMails"/>.</para>
///
/// <para>🔑 <b>The rule is <c>lastSentAt + IntervalDays</c>, not a calendar window.</b> §707.10: the
/// builders used to derive <c>windowIndex = daysSince(welcome) / 14</c>, which is NOT the same thing —
/// a send delayed by a ring drop delivered late in its window and the next window could open the very
/// next day, so one person could get two mails a day apart. Measuring from the LAST SEND makes the
/// gap real and self-correcting after any delay, which is the model the operator described.</para>
///
/// <para><b><see cref="IntervalDays"/> null = SEND ONCE, EVER</b> — the existing behaviour of
/// <c>task-deadline-reminder</c> and <c>pending-master-class-selection</c>. That is deliberately the
/// shipped default for those two, so this feature moves nobody on the day it deploys; turning either
/// into a repeating chase is then one number the operator sets and can watch (the §707.1 pattern).</para>
/// </remarks>
public class EmailReminderCadence
{
    public int Id { get; set; }

    /// <summary>The edition this cadence belongs to.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The mail this cadence governs — an <c>EmailTemplateCatalog.Map</c> key.</summary>
    public string TemplateKey { get; set; } = string.Empty;

    /// <summary>
    /// Days between repeats, measured from the LAST SEND to that person.
    /// <c>null</c> ⇒ send once and never repeat.
    /// </summary>
    public int? IntervalDays { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who last changed it — this decides how much mail real people get, so it is audited.</summary>
    public string? UpdatedByEmail { get; set; }
}
