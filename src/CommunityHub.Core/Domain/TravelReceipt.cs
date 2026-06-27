namespace CommunityHub.Core.Domain;

/// <summary>
/// A single receipt / invoice file uploaded by a speaker for their travel
/// reimbursement (REQUIREMENTS §48). The bytes are stored IN THE DATABASE (not
/// only on SharePoint) so that:
///   • "Step 1 done" can be answered by a simple DB query (≥1 receipt exists),
///     gating the Step-2 reimbursement request, and
///   • the Step-2 submit can attach the actual file(s) to the ERP-inbox email
///     without a fragile SharePoint round-trip (SharePoint is config-gated and
///     absent in DEV / tests).
///
/// One row per uploaded file; a speaker may upload several (multiple flight
/// legs, an invoice + a receipt, …). Scoped by (EventId, ParticipantId) like
/// the parent <see cref="TravelReimbursement"/>.
/// </summary>
public class TravelReceipt
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>Original upload file name (e.g. "flight-receipt.pdf").</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>MIME type as reported on upload (e.g. "application/pdf").</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>The raw file bytes.</summary>
    public byte[] Content { get; set; } = System.Array.Empty<byte>();

    /// <summary>Convenience size in bytes (denormalised for listing without loading Content).</summary>
    public long SizeBytes { get; set; }

    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}
