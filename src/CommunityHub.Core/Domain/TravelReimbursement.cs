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

    // --- §1387 document review ----------------------------------------------

    /// <summary>
    /// §1387 — the speaker's answer to "do you invoice through a registered company?". Decides which
    /// second document is required: a company invoice (yes) or a personal declaration (no). Null =
    /// not answered (claims submitted before the question existed).
    /// </summary>
    public bool? HasCompany { get; set; }

    /// <summary>§1387 — where the organizer review stands.</summary>
    public TravelReviewState ReviewState { get; set; } = TravelReviewState.None;

    /// <summary>§1387 — the AI's judgement of the submitted documents against the form's requirements.</summary>
    public TravelAiVerdict AiVerdict { get; set; } = TravelAiVerdict.NotChecked;

    /// <summary>§1387 — one-line summary of what is missing (or "all requirements met").</summary>
    public string? AiSummary { get; set; }

    /// <summary>§1387 — the per-requirement checklist as JSON (<see cref="TravelCheckItem"/>[]). Never holds extracted ID numbers.</summary>
    public string? AiFindingsJson { get; set; }

    public DateTimeOffset? AiCheckedAt { get; set; }

    /// <summary>§1387 — hash of the files + claim the verdict was formed on; a change re-opens the question.</summary>
    public string? AiInputHash { get; set; }

    /// <summary>§1387 — when the organizers were mailed about a not-approved verdict (once per <see cref="AiInputHash"/>).</summary>
    public string? AiNotifiedHash { get; set; }

    /// <summary>§1387 — the organizer's note on Accept / Send back (sent to the speaker on Send back).</summary>
    public string? ReviewNote { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewedByEmail { get; set; }
    public int SentBackCount { get; set; }

    /// <summary>
    /// §1387 — when the claim + files went to the ERP inbox. The mail now waits for Accept, and is
    /// sent once: claims submitted before §1387 were already sent on submit (stamped by the migration).
    /// </summary>
    public DateTimeOffset? ErpSentAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>§1387 — the organizer review of a submitted travel claim.</summary>
public enum TravelReviewState
{
    /// <summary>Not submitted (or opted out).</summary>
    None = 0,
    /// <summary>Submitted and waiting for an organizer: Accept or Send back.</summary>
    UnderReview = 1,
    /// <summary>An organizer accepted the documents; the claim went to the ERP inbox.</summary>
    Accepted = 2,
    /// <summary>An organizer sent it back; the claim is open again for new files.</summary>
    SentBack = 3,
}

/// <summary>§1387 — "Document meets requirements" as judged by the AI.</summary>
public enum TravelAiVerdict
{
    NotChecked = 0,
    Approved = 1,
    NotApproved = 2,
    /// <summary>The AI could not form a verdict (not configured, call failed, unreadable scan) — never read as "no".</summary>
    CouldNotCheck = 3,
}

/// <summary>§1387 — what one uploaded file is, as the speaker labels it.</summary>
public enum TravelDocumentKind
{
    Unknown = 0,
    TicketReceipt = 1,
    Invoice = 2,
    PersonalDeclaration = 3,
    Other = 4,
}

/// <summary>
/// §1387 — one line of the AI checklist. <c>Ok</c> null = could not tell (in question).
/// An organizer can ACCEPT a line that is missing or in question (operator 2026-11-08: <i>"i need a way to
/// accept/override each topic in question, if we fx accept biz class claim"</i>) — who, when and why are kept on the
/// line, survive a re-validation of the same requirement, and go to finance with the claim (<i>"eldk finance can see
/// my overrides"</i>).
/// </summary>
public sealed record TravelCheckItem(string Requirement, bool? Ok, string? Note,
    string? AcceptedBy = null, DateTimeOffset? AcceptedAt = null, string? AcceptNote = null,
    string? SpeakerNote = null)
{
    /// <summary>
    /// What the SPEAKER reads for this line (operator 2026-11-08: <i>"user friendly and humble to the speaker, be gentle in
    /// language"</i> · <i>"we got your ticket but nowhere to send the money"</i>): the AI's kind sentence, else its finding.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ForSpeaker => string.IsNullOrWhiteSpace(SpeakerNote) ? Note : SpeakerNote;

    /// <summary>An organizer accepted this line.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAccepted => AcceptedAt is not null;

    /// <summary>Missing and NOT accepted — what blocks the claim and what the speaker is told to fix.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOpenFailure => Ok == false && !IsAccepted;

    /// <summary>Not met (missing or in question) and not accepted — what an organizer can still accept.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanAccept => Ok != true && !IsAccepted;

    /// <summary>One word for the line, as finance and the organizer page show it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusText => IsAccepted ? "Accepted by organizer" : Ok switch { true => "Met", false => "Missing", _ => "In question" };
}
