namespace CommunityHub.Core.Domain;

/// <summary>
/// §1235 — ONE PERSON, SEVERAL SPONSOR COMPANIES. The link between a sponsor contact
/// (<see cref="Participant"/>) and a sponsor company, carrying the per-company role flags.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why this exists.</b> <see cref="Participant.SponsorCompanyId"/> is a single scalar, and
/// <c>(EventId, Email)</c> is UNIQUE, so one human could belong to exactly one company. A sponsor group
/// with several national divisions — separate companies in the webshop and separate accounts in the ERP —
/// whose GLOBAL CMO signs for all of them, and whose GLOBAL event coordinator coordinates all of them, had
/// nowhere to go. Operator 2026-09-16: <i>"in my erp they will exist with 2 contacts under each account.
/// contacts are the same"</i> · <i>"we dont have any overlap now … but we CAN have it"</i>.</para>
///
/// <para>⚠️ <b>What happened WITHOUT it, and it was silent.</b> The contact sync looked a person up by
/// e-mail with no company filter and then REASSIGNED them to the syncing company. With the roster pulled
/// every ~15 minutes per company, a shared contact ping-ponged between divisions — last-synced-company
/// wins — and the divisions that lost the race silently had no coordinator for their tasks and mail.
/// Recorded as a known gap since 2026-07-07 (<i>"sponsor contact company-flap"</i>); this is the fix.</para>
///
/// <para>🔑 <b>The flags belong on the LINK, not on the person.</b> Being a signer is something you are
/// FOR A COMPANY: the Danish division's signer is not automatically the Swedish one's. Operator's own
/// framing of the second real case: <i>"arrow can have same issue, as they share event coordinator, but
/// different signers"</i>. <see cref="Participant.IsSigner"/> and friends stay as the flags of the
/// PRIMARY company so every existing reader keeps working unchanged (§1235 stage 1).</para>
///
/// <para>🔒 <b><see cref="Participant.SponsorCompanyId"/> remains the PRIMARY company</b> — the one the
/// sponsor portal scopes to until the company switcher ships (stage 2). It is never stolen by a later
/// sync: a second company adds a LINK, it does not move the person.</para>
/// </remarks>
public class SponsorCompanyContact
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The person. Cascade-deleted with the participant.</summary>
    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>
    /// The external sponsor-company id (the same value space as
    /// <see cref="Participant.SponsorCompanyId"/> and <see cref="SponsorInfo.SponsorCompanyId"/> —
    /// there is no company ENTITY to point at).
    /// </summary>
    public string SponsorCompanyId { get; set; } = string.Empty;

    /// <summary>Signs this company's agreements. Per company: a group signer holds it on each link.</summary>
    public bool IsSigner { get; set; }

    /// <summary>Coordinates this company's event tasks — the audience for its sponsor mail.</summary>
    public bool IsEventCoordinator { get; set; }

    /// <summary>Staffs this company's booth.</summary>
    public bool IsBoothMember { get; set; }

    /// <summary>
    /// The ERP (e-conomic) contact number **within this company's account**.
    /// 🔴 Per COMPANY, never event-wide: e-conomic numbers are per-customer sequences, so two accounts
    /// both have a contact 6. Matching on the number alone (as the sync did) matches the wrong person
    /// the moment two accounts share a contact.
    /// </summary>
    public int? ErpContactNumber { get; set; }

    /// <summary>True when this is the person's primary company (mirrors <see cref="Participant.SponsorCompanyId"/>).</summary>
    public bool IsPrimary { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
