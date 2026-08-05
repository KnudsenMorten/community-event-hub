namespace CommunityHub.Core.Domain.Evaluation;

/// <summary>
/// §753 — a device asking to be onboarded, awaiting an organiser's approval.
/// </summary>
/// <remarks>
/// <para>🔴 <b>This REVERSES a decision recorded twice in the brief</b> — C1: <i>"Devices are
/// pre-registered… There is no self-registration and no pending-approval queue"</i>, and the
/// decisions log: <i>"DECIDED — organiser pre-registers each identifier"</i>. Operator 2026-08-01
/// proposed the change knowingly (<i>"i propose a change in the provision of the session evaluation
/// devices"</i>) and it is agreed, so the brief is updated alongside this rather than left to
/// contradict the code.</para>
///
/// <para>🔑 <b>What the original decision protected is kept.</b> Its point was that the ingest API
/// must reject any identifier no human has sanctioned — and that is still exactly true: a request in
/// this queue has no key, no upload URL and no ability to post a single response until somebody
/// approves it. What changes is only WHERE the identifier comes from. Transcribing 25 MAC addresses
/// off enclosures by hand is the weaker story of the two: a typo silently creates a device that can
/// never authenticate, and it presents as a firmware fault.</para>
///
/// <para>🔒 <b>Requests are never auto-expired</b> (operator: <i>"no auto-expire"</i>). A pending row
/// is a physical unit sitting in a box somewhere; deleting the evidence of it because a timer elapsed
/// would lose the one record that a device tried and was never let in. The queue is bounded instead
/// by the bootstrap secret — a unit that cannot present it never reaches this table.</para>
/// </remarks>
public class EvaluationDeviceProvisionRequest
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>
    /// The unit's hardware identifier — MAC or serial, opaque and never parsed. This is what becomes
    /// <see cref="EvaluationDevice.SerialNumber"/> on approval, so it is the value the ingest API will
    /// authenticate against.
    /// </summary>
    public string SerialNumber { get; set; } = string.Empty;

    public string? FirmwareVersion { get; set; }

    /// <summary>Free text from the unit (model, hardware revision) — diagnostic only, never parsed.</summary>
    public string? Note { get; set; }

    /// <summary>First time this identifier asked.</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>
    /// Most recent poll. A device re-asks on a long backoff until it is approved, and each poll
    /// refreshes this rather than creating a second row — the queue is one row per identifier, so an
    /// unapproved unit polling for a week does not become a week of clutter.
    /// </summary>
    public DateTimeOffset LastRequestedAt { get; set; }

    /// <summary>How many times it has asked. Surfaced so a unit stuck polling is visible.</summary>
    public int RequestCount { get; set; }

    public EvaluationProvisionStatus Status { get; set; } = EvaluationProvisionStatus.Pending;

    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedByEmail { get; set; }

    /// <summary>The device row created on approval, once the unit has collected its key.</summary>
    public int? EvaluationDeviceId { get; set; }
    public EvaluationDevice? EvaluationDevice { get; set; }

    /// <summary>
    /// 🔒 When the one-time key was handed back. The provisioning response returns a device key
    /// EXACTLY ONCE; after this is stamped the endpoint reports the unit as already provisioned and
    /// never re-issues, so a replayed provision call cannot mint a second working credential.
    /// </summary>
    public DateTimeOffset? KeyIssuedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>§753 — where an onboarding request stands.</summary>
public enum EvaluationProvisionStatus
{
    /// <summary>Waiting for a human. The unit can do nothing at all in this state.</summary>
    Pending = 0,

    /// <summary>Approved; the unit may collect its key on its next poll (or already has).</summary>
    Approved = 1,

    /// <summary>
    /// Refused. Kept rather than deleted: a rejected identifier that keeps asking is worth seeing,
    /// and re-approving is a decision someone should have to make deliberately.
    /// </summary>
    Rejected = 2,
}
