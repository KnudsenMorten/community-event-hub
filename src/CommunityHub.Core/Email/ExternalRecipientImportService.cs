using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>What an import did — and what it refused.</summary>
public sealed record ImportResult(int Added, int Updated, int Skipped, IReadOnlyList<string> Problems)
{
    public override string ToString() =>
        $"{Added} added, {Updated} updated, {Skipped} skipped";
}

/// <summary>
/// §1080 stage 5 — importing the operator's Excel list of previous editions' attendees.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-12: <i>"i will provide list of email addresses in excel format which you
/// can import and store"</i>.</para>
///
/// <para>🔑 <b>The columns are found by NAME, not by position.</b> A spreadsheet that has been
/// through three people has its columns in a different order than the one it started in, and an
/// importer keyed on "column A is the address" silently imports names as addresses the first time
/// somebody inserts a column.</para>
///
/// <para>🔒 <b>Re-importing UPDATES rather than duplicating</b> (the unique index on edition+address
/// enforces it). Sending the same person two copies of a mailing is the fastest way to look like a
/// machine.</para>
///
/// <para>⚠️ <b>Rows that cannot be read are REPORTED, never skipped silently.</b> A list of eight
/// thousand where forty vanished is a list nobody can reconcile.</para>
/// </remarks>
public sealed class ExternalRecipientImportService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public ExternalRecipientImportService(CommunityHubDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    private static readonly string[] EmailHeaders =
        ["email", "e-mail", "mail", "email address", "e-mail address", "emailaddress"];
    private static readonly string[] NameHeaders =
        ["name", "full name", "fullname", "attendee", "person"];
    private static readonly string[] CompanyHeaders =
        ["company", "company name", "organisation", "organization", "firma"];

    /// <summary>
    /// Read an .xlsx into <see cref="ExternalRecipient"/> rows.
    /// </summary>
    public async Task<ImportResult> ImportAsync(
        int eventId, Stream xlsx, string? batchLabel, string? byEmail,
        string? sourceEdition = null, CancellationToken ct = default)
    {
        var problems = new List<string>();
        int added = 0, updated = 0, skipped = 0;

        using var wb = new XLWorkbook(xlsx);
        var ws = wb.Worksheets.FirstOrDefault();
        if (ws is null) return new ImportResult(0, 0, 0, ["The workbook has no sheets."]);

        var used = ws.RangeUsed();
        if (used is null) return new ImportResult(0, 0, 0, ["The sheet is empty."]);

        // Header row: find the columns by name.
        var header = used.FirstRow();
        int emailCol = 0, nameCol = 0, companyCol = 0;
        foreach (var cell in header.Cells())
        {
            var text = cell.GetString().Trim().ToLowerInvariant();
            if (emailCol == 0 && EmailHeaders.Contains(text)) emailCol = cell.Address.ColumnNumber;
            else if (nameCol == 0 && NameHeaders.Contains(text)) nameCol = cell.Address.ColumnNumber;
            else if (companyCol == 0 && CompanyHeaders.Contains(text)) companyCol = cell.Address.ColumnNumber;
        }

        if (emailCol == 0)
        {
            // ⚠️ Refuse the whole file rather than guess. Guessing the address column is how a list
            // of names gets mailed.
            return new ImportResult(0, 0, 0,
                ["No e-mail column found. Name one of the columns \"Email\" and try again."]);
        }

        var existing = await _db.ExternalRecipients
            .Where(r => r.EventId == eventId)
            .ToDictionaryAsync(r => r.Email, StringComparer.OrdinalIgnoreCase, ct);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = _clock.GetUtcNow();

        foreach (var row in used.Rows().Skip(1))
        {
            var raw = row.Cell(emailCol).GetString();
            var email = MailSuppression.Normalise(raw);

            if (email.Length == 0) { skipped++; continue; }          // a blank line is not an error
            if (!email.Contains('@') || email.Contains(' '))
            {
                problems.Add($"Row {row.RowNumber()}: “{raw.Trim()}” is not an e-mail address.");
                continue;
            }
            if (!seen.Add(email)) { skipped++; continue; }            // the same address twice in one file

            var name = nameCol > 0 ? row.Cell(nameCol).GetString().Trim() : null;
            var company = companyCol > 0 ? row.Cell(companyCol).GetString().Trim() : null;

            if (existing.TryGetValue(email, out var current))
            {
                // 🔑 Fill gaps, never overwrite something with nothing: a second file that lacks the
                // name column must not blank the names the first one supplied.
                if (!string.IsNullOrWhiteSpace(name)) current.FullName = name;
                if (!string.IsNullOrWhiteSpace(company)) current.CompanyName = company;
                updated++;
            }
            else
            {
                _db.ExternalRecipients.Add(new ExternalRecipient
                {
                    EventId = eventId, Email = email,
                    FullName = string.IsNullOrWhiteSpace(name) ? null : name,
                    CompanyName = string.IsNullOrWhiteSpace(company) ? null : company,
                    SourceEdition = sourceEdition, ImportBatch = batchLabel,
                    ImportedAt = now, ImportedByEmail = byEmail,
                });
                added++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return new ImportResult(added, updated, skipped, problems);
    }
}
