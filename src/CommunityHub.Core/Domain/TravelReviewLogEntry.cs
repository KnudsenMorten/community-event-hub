namespace CommunityHub.Core.Domain;

/// <summary>
/// §1387 — one step in the dialog about a travel claim (operator 2026-11-08: <i>"can we also get log of dialog"</i>):
/// the speaker submitting, the AI check, every mail, and every organizer decision — who, when, and what was said.
/// </summary>
/// <remarks>
/// Its own table rather than the audit trail: the audit trail is purged on a schedule, and this log must last until
/// the claim is paid, months later. No FK to the claim or the person on purpose — the log outlives a deleted row.
/// </remarks>
public class TravelReviewLogEntry
{
    public long Id { get; set; }
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The claim (<see cref="TravelReimbursement.Id"/>).</summary>
    public int TravelReimbursementId { get; set; }

    public int ParticipantId { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Who did it: an e-mail, "AI check", or "hub" for a job.</summary>
    public string? Actor { get; set; }

    /// <summary>A <see cref="TravelReviewLogKinds"/> value.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>What happened, in a sentence or two (notes included, never ID or bank numbers).</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>The kinds of dialog step, as stored.</summary>
public static class TravelReviewLogKinds
{
    public const string Submitted = "submitted";
    public const string AiCheck = "ai-check";
    public const string MailedOrganizers = "mail-organizers";
    public const string MailedSpeaker = "mail-speaker";
    public const string MailedFinance = "mail-finance";
    public const string FindingAccepted = "finding-accepted";
    public const string FindingUndone = "finding-undone";
    public const string Accepted = "accepted";
    public const string SentBack = "sent-back";
    public const string Reopened = "reopened";
    public const string Paid = "paid";
    public const string Unpaid = "unpaid";
}
