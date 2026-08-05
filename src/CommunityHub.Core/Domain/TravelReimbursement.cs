namespace CommunityHub.Core.Domain;

/// <summary>
/// A speaker's travel reimbursement request. Only relevant to speakers
/// travelling from outside Denmark.
///
/// Policy (ELDK27):
///  - Economy flights only.
///  - Single-speaker session: up to EUR 400 per speaker.
///  - Co-speaking session: up to EUR 300 per person.
///  - If the speaker cannot find flights within the cap, they tick
///    "CannotStayInLimits" and the organizer team handles it manually.
/// </summary>
public class TravelReimbursement
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>True = the speaker is requesting reimbursement.</summary>
    public bool RequestReimbursement { get; set; }

    /// <summary>Origin city / country (e.g. "Berlin, Germany").</summary>
    public string? OriginCity { get; set; }

    /// <summary>
    /// Amount the speaker is claiming, in EUR. Self-asserted from the
    /// available caps (300 single-speaker-session per person, 400 single
    /// or any) or a custom value with an Explanation when neither fits.
    /// The system does NOT enforce a cap -- the organizer reviews and pays
    /// out per claim.
    /// </summary>
    public decimal? ClaimAmountEur { get; set; }

    /// <summary>Free-text explanation (required when ClaimAmountEur doesn't match a standard cap).</summary>
    public string? Explanation { get; set; }

    // --- §6.10 claim locking (§768.10 D5) -----------------------------------

    /// <summary>
    /// When the speaker SUBMITTED the claim. Non-null ⇒ the claim is COMPLETED and frozen: no
    /// further receipt uploads, no deletions, no re-saving.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Enforced server-side, not by hiding a button</b> (work-order §6.10): a stale
    /// browser tab or a direct POST must fail identically.</para>
    ///
    /// <para>⚠️ <b>A timestamp, not a bool.</b> "When did this speaker submit?" is the question an
    /// organizer asks when a receipt is missing and the deadline has passed; a bool answers it with
    /// a shrug, and the answer cannot be reconstructed afterwards.</para>
    /// </remarks>
    public DateTimeOffset? SubmittedAt { get; set; }

    /// <summary>
    /// When an organizer last REOPENED a completed claim, returning it to the speaker.
    /// </summary>
    /// <remarks>
    /// 🔒 §768.10 D5, and it is the reason the freeze is safe to ship at all: without a reopen, a
    /// speaker who submits after uploading 3 of 5 receipts is locked out <b>with no recovery path</b>
    /// — the consequence the work order asked to be flagged rather than assumed. The reopen is a
    /// deliberate organizer action and is audit-logged; it is not a hole in the lock.
    /// </remarks>
    public DateTimeOffset? ReopenedAt { get; set; }

    /// <summary>The organizer who reopened it (audit; null when never reopened).</summary>
    public string? ReopenedByEmail { get; set; }

    /// <summary>How many times this claim has been reopened — a pattern worth seeing.</summary>
    public int ReopenCount { get; set; }

    /// <summary>The claim is frozen: submitted, and not since reopened.</summary>
    public bool IsCompleted => SubmittedAt is not null;

    // --- Organizer-only fields (not editable by the speaker) ----------------
    /// <summary>Organizer marks paid when the reimbursement has been transferred.</summary>
    public bool IsPaid { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string? PaidNotes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
