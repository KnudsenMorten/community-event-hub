namespace CommunityHub.Core.Domain;

/// <summary>
/// One company's group-photo session at the event (README: Group photos
/// management). The organizer registers the company + lead contact, picks a
/// time slot, and sends a calendar invite to the lead plus any internal
/// participants. Re-sending uses a stable ICS UID, so an updated slot
/// UPDATES the existing calendar entry instead of duplicating it.
/// </summary>
public class GroupPhotoRegistration
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public string CompanyName { get; set; } = string.Empty;
    public string ContactName { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>
    /// §1077 stage 4 — the volume-package entity this photo belongs to, when it came from there.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>The link exists so there is ONE group photo per company, not two.</b> This entity
    /// (§25f, June) and <c>VolumePackageCompany</c> (§1077, August) both describe "a company that
    /// bought a block of tickets gets a photo"; stage 4 joins them rather than adding a second slot,
    /// a second contact and a second invite that would drift apart within a week.</para>
    ///
    /// <para>⚠️ NULL for a registration an organizer typed by hand — that path still works, and a
    /// company need not be a tracked volume-package entity to have its picture taken.</para>
    ///
    /// <para>✅ <b>They now share ONE qualification rule</b> (ten or more) — see
    /// <see cref="Qualifies"/>. Linking them is what exposed that they had two.</para>
    /// </remarks>
    public int? VolumePackageCompanyId { get; set; }
    public VolumePackageCompany? VolumePackageCompany { get; set; }

    /// <summary>
    /// Number of tickets in the company's volume package. The group photo is a
    /// perk for the larger packages: only companies with MORE THAN
    /// <see cref="QualifyingTicketThreshold"/> tickets qualify. Entered manually
    /// by the organizer (there is no automated ticket-volume feed); 0 = unset.
    /// </summary>
    public int TicketCount { get; set; }

    /// <summary>A company qualifies for the group photo at or above this ticket count.</summary>
    public const int QualifyingTicketThreshold = 10;

    /// <summary>
    /// True when the company's volume package qualifies it for the group photo: <b>TEN OR MORE</b>.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>CHANGED 2026-08-11 — this used to be "more than 10", and exactly ten was refused.</b>
    /// Two rules had grown apart: §25f (June) made the threshold EXCLUSIVE here, while §1077
    /// (August) defines the volume package as <b>≥10</b> and names the group photo as one of its
    /// three benefits. A company with exactly ten therefore qualified for the package and was turned
    /// away at the invite — with a message telling them to raise the ticket count.
    ///
    /// <para>✅ The operator settled it when stage 4 surfaced the conflict: <i>"10 or more is
    /// correct"</i>. There is now ONE rule, in one place, and §1077's is it. ⚠️ The old behaviour
    /// was pinned by a test asserting <c>10 ⇒ false</c>; that test now asserts the decision, so the
    /// boundary cannot drift back without somebody choosing to.</para>
    /// </remarks>
    public bool Qualifies => TicketCount >= QualifyingTicketThreshold;

    /// <summary>
    /// Comma/semicolon-separated internal staff emails — kept for the organizer's
    /// reference only. The calendar invite goes to the appointed company lead
    /// (<see cref="ContactEmail"/>) ONLY; these are NOT invited (operator 2026-06-22).
    /// </summary>
    public string InternalParticipants { get; set; } = string.Empty;

    /// <summary>
    /// Photo slot start (UTC) — <b>the PUBLISHED time</b>. Null = registered but not scheduled yet.
    /// </summary>
    /// <remarks>
    /// 🔴 §1077 stage 5 — this is what the COMPANY sees and what the calendar file carries. A
    /// proposal lives in <see cref="PlannedAtUtc"/> until somebody publishes it, because a time on a
    /// company's page that may still move is worse than no time at all: they forward it to eleven
    /// colleagues the moment they read it.
    /// </remarks>
    public DateTimeOffset? ScheduledAtUtc { get; set; }

    /// <summary>
    /// §1077 stage 5 — the PROPOSED slot from the planner, not yet published.
    /// </summary>
    /// <remarks>
    /// 🔑 Separate from <see cref="ScheduledAtUtc"/> so the plan can be regenerated, argued with and
    /// thrown away without a single company being told anything. Publishing copies it across.
    /// </remarks>
    public DateTimeOffset? PlannedAtUtc { get; set; }

    /// <summary>When this company's time was published to them. Null ⇒ they have not been told.</summary>
    public DateTimeOffset? SlotPublishedAt { get; set; }

    /// <summary>When the "your slot is X" mail last went to the coordinator.</summary>
    public DateTimeOffset? SlotNotifiedAt { get; set; }

    /// <summary>
    /// 🔒 A published slot is PINNED: the planner fills around it and never moves it.
    /// </summary>
    /// <remarks>
    /// ⚠️ The whole reason the planner is safe to re-run. Adding one more company in January must not
    /// silently move a time eleven people already have in their calendars — and a planner that
    /// reshuffles everything on every run is a planner nobody dares press twice.
    /// </remarks>
    public bool SlotIsPinned => SlotPublishedAt is not null && ScheduledAtUtc is not null;

    /// <summary>Slot length; photo sessions are short.</summary>
    public int DurationMinutes { get; set; } = 15;

    public string? Location { get; set; }
    public string? Notes { get; set; }

    public DateTimeOffset? InviteLastSentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
