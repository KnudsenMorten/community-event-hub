using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace CommunityHub.Core.Domain;

/// <summary>
/// One pending (or decided) item in the DELTA-APPROVAL QUEUE (REQUIREMENTS §59).
///
/// <para><b>Why this exists.</b> Sync engines (today the §38e Zoho→CEH session
/// change-detection engine; later volunteer + CEH→Zoho deltas) MUST NOT auto-apply a
/// detected change. Instead each detected change is ENQUEUED here as a
/// <see cref="SyncDeltaStatus.Pending"/> row, the operator is NOTIFIED, and they
/// APPROVE or REJECT it in <c>/Organizer/SyncQueue</c>. Approving an Update APPLIES the
/// change (and emails the affected party); approving a Disappeared item only
/// acknowledges it (CEH NEVER auto-deletes — REQUIREMENTS §58/§56). Rejecting keeps
/// the current CEH value untouched.</para>
///
/// <para><b>Dedupe.</b> The queue holds at most one PENDING row per
/// (<see cref="EventId"/>, <see cref="EntityType"/>, <see cref="EntityId"/>,
/// <see cref="ChangeKind"/>); a later detection of the same change updates the existing
/// row's <see cref="ChangesJson"/> rather than stacking duplicates (see
/// <c>SyncDeltaQueueService.EnqueueAsync</c>).</para>
///
/// <para><b>Status is the lifecycle marker.</b> Pending → Approved → Applied (an Update
/// that applied), or Pending → Approved (a Disappeared acknowledgement, no Applied), or
/// Pending → Rejected. A decided row is kept for the "recently decided" audit section.</para>
/// </summary>
public class SyncDelta
{
    public int Id { get; set; }

    /// <summary>The edition this delta belongs to. Every query is scoped by this.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>What kind of CEH entity this change is about (Session today; Speaker /
    /// Volunteer later reuse the same framework).</summary>
    public SyncDeltaEntityType EntityType { get; set; }

    /// <summary>
    /// The CEH id of the affected entity, as a string so the framework can carry an
    /// int id (e.g. <c>Session.Id</c>) OR a non-numeric external key later. For a
    /// Session delta this is the CEH <c>Session.Id</c> rendered as a string.
    /// </summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>A human-readable label for the entity (e.g. the session title) — for the
    /// queue UI + the notification email, so the operator recognizes the item.</summary>
    public string EntityLabel { get; set; } = string.Empty;

    /// <summary>
    /// Which sync direction produced this delta. Reuses the shared
    /// <see cref="SessionSyncDirection"/> stage enum (ZohoToCeh / CehToZoho /
    /// SessionizeToCeh) so the source is named in the same vocabulary as §57/§58.
    /// </summary>
    public SessionSyncDirection Source { get; set; }

    /// <summary>Update (a field changed), Disappeared (gone upstream — never auto-deleted),
    /// or New (appeared upstream).</summary>
    public SyncDeltaChangeKind ChangeKind { get; set; }

    /// <summary>
    /// The serialized list of <see cref="SyncFieldChange"/> ({Field, OldValue, NewValue}).
    /// Stored as JSON in a single <c>nvarchar(max)</c> column. Use
    /// <see cref="Changes"/> to read/write the typed list.
    /// </summary>
    public string ChangesJson { get; set; } = "[]";

    /// <summary>Pending / Approved / Rejected / Applied. The status drives the lifecycle.</summary>
    public SyncDeltaStatus Status { get; set; } = SyncDeltaStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When an operator approved/rejected this delta (null while Pending).</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>The operator who approved/rejected this delta (null while Pending).</summary>
    public string? DecidedByEmail { get; set; }

    /// <summary>When an approved Update was actually applied (null for Disappeared/rejected/pending).</summary>
    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>Optional operator note — e.g. the rejection reason, or an apply error.</summary>
    public string? Notes { get; set; }

    // --- Typed access to ChangesJson -----------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The typed field diffs (round-trips <see cref="ChangesJson"/>). Reading a
    /// malformed/blank column yields an empty list rather than throwing; writing
    /// re-serializes to <see cref="ChangesJson"/>.
    /// </summary>
    [NotMapped]
    public IReadOnlyList<SyncFieldChange> Changes
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ChangesJson)) return Array.Empty<SyncFieldChange>();
            try
            {
                return JsonSerializer.Deserialize<List<SyncFieldChange>>(ChangesJson, JsonOpts)
                       ?? new List<SyncFieldChange>();
            }
            catch
            {
                return Array.Empty<SyncFieldChange>();
            }
        }
        set => ChangesJson = JsonSerializer.Serialize(value ?? new List<SyncFieldChange>(), JsonOpts);
    }
}

/// <summary>One field-level diff inside a <see cref="SyncDelta"/> (display + apply).</summary>
public sealed class SyncFieldChange
{
    public SyncFieldChange() { }

    public SyncFieldChange(string field, string? oldValue, string? newValue)
    {
        Field = field;
        OldValue = oldValue;
        NewValue = newValue;
    }

    /// <summary>The logical field name (e.g. <c>StartsAt</c>, <c>EndsAt</c>, <c>Room</c>).</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>The value CEH currently holds (display old→new). Null = not set.</summary>
    public string? OldValue { get; set; }

    /// <summary>The value detected upstream (what an Approve would apply). Null = not set.</summary>
    public string? NewValue { get; set; }
}

/// <summary>The kind of CEH entity a <see cref="SyncDelta"/> is about.</summary>
public enum SyncDeltaEntityType
{
    Session = 0,
    Speaker = 1,
    Volunteer = 2,
    Other = 3,
}

/// <summary>What sort of change a <see cref="SyncDelta"/> represents.</summary>
public enum SyncDeltaChangeKind
{
    /// <summary>A field (time/location/etc) changed upstream — Approve APPLIES it.</summary>
    Update = 0,
    /// <summary>The entity vanished upstream — Approve only ACKNOWLEDGES (never deletes).</summary>
    Disappeared = 1,
    /// <summary>The entity appeared upstream — for a future create-on-approve flow.</summary>
    New = 2,
}

/// <summary>The lifecycle status of a <see cref="SyncDelta"/>.</summary>
public enum SyncDeltaStatus
{
    /// <summary>Awaiting an operator decision (the only state the engine enqueues).</summary>
    Pending = 0,
    /// <summary>Operator approved; for a Disappeared item this is the terminal state.</summary>
    Approved = 1,
    /// <summary>Operator rejected — the current CEH value is kept untouched.</summary>
    Rejected = 2,
    /// <summary>An approved Update whose change was successfully applied to CEH.</summary>
    Applied = 3,
}
