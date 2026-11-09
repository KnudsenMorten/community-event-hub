using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Visa;

/// <summary>What a page may show about a stored letter, without its bytes.</summary>
public sealed record VisaLetterInfo(int ParticipantId, string FileName, long SizeBytes, DateTimeOffset UploadedAt);

/// <summary>
/// §1383 — the PDF an organizer hands back to each visa applicant once the application has gone to the
/// Danish authorities (typically the invitation letter). One per participant; a new upload replaces it.
/// </summary>
/// <remarks>
/// <para>🔒 Stored as bytes in the database (<see cref="VisaLetter"/>), the mechanism CEH already uses for
/// personal documents (<see cref="TravelReceipt"/>). Callers pass the participant id from the SIGNED-IN
/// principal for the applicant side, so an applicant can only ever read their own row.</para>
/// <para>Validation reads the CONTENT, not the name: the file must carry a <c>%PDF-</c> header (within the
/// first 1024 bytes, as PDF readers allow) and be at most <see cref="MaxBytes"/>.</para>
/// </remarks>
public sealed class VisaLetterStore
{
    /// <summary>10 MB — an invitation letter is a few hundred kB.</summary>
    public const long MaxBytes = 10L * 1024 * 1024;

    private static readonly byte[] PdfMagic = Encoding.ASCII.GetBytes("%PDF-");

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public VisaLetterStore(CommunityHubDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>True when <paramref name="content"/> carries a PDF header within its first 1024 bytes.</summary>
    public static bool LooksLikePdf(ReadOnlySpan<byte> content)
    {
        var head = content[..Math.Min(content.Length, 1024)];
        return head.IndexOf(PdfMagic.AsSpan()) >= 0;
    }

    /// <summary>The problem with a proposed upload, or null when it is acceptable.</summary>
    public static string? Validate(long length, ReadOnlySpan<byte> content)
    {
        if (length <= 0 || content.Length == 0) return "Choose a PDF file to upload.";
        if (length > MaxBytes || content.Length > MaxBytes) return $"The file is too large (max {MaxBytes / (1024 * 1024)} MB).";
        if (!LooksLikePdf(content)) return "That file is not a PDF. Upload the letter as a PDF.";
        return null;
    }

    /// <summary>
    /// Store (or replace) the letter for one applicant. Returns the problem, or null when saved.
    /// Only for someone on the visa list: flagged <see cref="Participant.NeedsVisa"/>, or who already answered.
    /// </summary>
    public async Task<string?> UploadAsync(int eventId, int participantId, string? fileName, byte[] content, CancellationToken ct = default)
    {
        var problem = Validate(content.LongLength, content);
        if (problem is not null) return problem;

        var onList = await _db.Participants.AnyAsync(p => p.Id == participantId && p.EventId == eventId && p.NeedsVisa, ct)
                     || await _db.VisaApplications.AnyAsync(v => v.ParticipantId == participantId && v.EventId == eventId, ct);
        if (!onList) return "That person is not on the visa list.";

        var row = await _db.VisaLetters.FirstOrDefaultAsync(l => l.ParticipantId == participantId && l.EventId == eventId, ct);
        if (row is null)
        {
            row = new VisaLetter { EventId = eventId, ParticipantId = participantId };
            _db.VisaLetters.Add(row);
        }
        row.FileName = CleanName(fileName);
        row.Content = content;
        row.SizeBytes = content.LongLength;
        row.UploadedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Remove one applicant's letter. True when there was one.</summary>
    public async Task<bool> RemoveAsync(int eventId, int participantId, CancellationToken ct = default)
    {
        var row = await _db.VisaLetters.FirstOrDefaultAsync(l => l.ParticipantId == participantId && l.EventId == eventId, ct);
        if (row is null) return false;
        _db.VisaLetters.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The letter's details (no bytes), or null.</summary>
    public Task<VisaLetterInfo?> InfoAsync(int eventId, int participantId, CancellationToken ct = default) =>
        _db.VisaLetters.AsNoTracking()
            .Where(l => l.ParticipantId == participantId && l.EventId == eventId)
            .Select(l => new VisaLetterInfo(l.ParticipantId, l.FileName, l.SizeBytes, l.UploadedAt))
            .FirstOrDefaultAsync(ct);

    /// <summary>Every letter of the edition (no bytes), by participant — for the organizer list.</summary>
    public async Task<IReadOnlyDictionary<int, VisaLetterInfo>> InfosAsync(int eventId, CancellationToken ct = default) =>
        (await _db.VisaLetters.AsNoTracking()
            .Where(l => l.EventId == eventId)
            .Select(l => new VisaLetterInfo(l.ParticipantId, l.FileName, l.SizeBytes, l.UploadedAt))
            .ToListAsync(ct))
        .ToDictionary(l => l.ParticipantId);

    /// <summary>
    /// The letter with its bytes, for exactly this participant in this edition. 🔒 The applicant side
    /// passes the SIGNED-IN participant's id — never one taken from the request.
    /// </summary>
    public Task<VisaLetter?> GetAsync(int eventId, int participantId, CancellationToken ct = default) =>
        _db.VisaLetters.AsNoTracking()
            .FirstOrDefaultAsync(l => l.ParticipantId == participantId && l.EventId == eventId, ct);

    /// <summary>A safe download name: the uploaded name without any path, always ending in .pdf.</summary>
    public static string CleanName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/').Split('/').Last()).Trim();
        name = new string(name.Where(c => !char.IsControl(c) && c != '"').ToArray());
        if (name.Length == 0) name = "visa-letter.pdf";
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
        return name.Length > 300 ? name[^300..] : name;
    }
}
