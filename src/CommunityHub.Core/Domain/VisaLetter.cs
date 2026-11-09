namespace CommunityHub.Core.Domain;

/// <summary>
/// §1383 — the PDF an organizer hands back to ONE visa applicant once the application has gone to the
/// Danish authorities (typically the signed invitation letter). One row per participant; a new upload
/// replaces it.
/// </summary>
/// <remarks>
/// <para>Operator 2026-11-07: <i>"organizers must be able to provide a PDF file to each applicant after
/// the application has been submitted to the Danish authorities"</i>.</para>
/// <para>🔒 Bytes in the database, the same mechanism as <see cref="TravelReceipt"/>: personal documents
/// stay inside the app's own data store (already covered by the SQL backup), need no SharePoint config
/// (absent in DEV and tests), and are served only through an auth-checked page handler. A separate table
/// from <see cref="VisaApplication"/> so the organizer list never loads the file bytes.</para>
/// </remarks>
public class VisaLetter
{
    public int Id { get; set; }

    /// <summary>The edition. Plain column — the participant row already carries the event cascade.</summary>
    public int EventId { get; set; }

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>The original file name, shown to the applicant and used for the download.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>The PDF bytes (checked to start with a <c>%PDF-</c> header on upload).</summary>
    public byte[] Content { get; set; } = System.Array.Empty<byte>();

    /// <summary>Denormalised size, so a listing never loads <see cref="Content"/>.</summary>
    public long SizeBytes { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}
