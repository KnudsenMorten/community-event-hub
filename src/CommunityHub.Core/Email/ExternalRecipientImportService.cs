using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>What an import did — and what it refused.</summary>
public sealed record ImportResult(int Added, int Updated, int Skipped, IReadOnlyList<string> Problems)
{
    /// <summary>§1381 — rows whose source list said they opted out (suppressed instead of imported).</summary>
    public int OptedOut { get; init; }
    /// <summary>§1381 — cancelled ticket rows skipped.</summary>
    public int Cancelled { get; init; }

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
        ["name", "full name", "fullname", "attendee", "person", "contact name"];
    // §1381 — the two real export formats: Zoho CRM contacts ("First Name"/"Last Name", "Account Name") and the
    // Backstage ticket export ("first_name"/"last_name", "company_name"). Headers are compared lower-cased.
    private static readonly string[] FirstNameHeaders = ["first name", "first_name", "firstname"];
    private static readonly string[] LastNameHeaders = ["last name", "last_name", "lastname"];
    private static readonly string[] CompanyHeaders =
        ["company", "company name", "company_name", "organisation", "organization", "firma", "account name"];
    /// <summary>§1381 — Zoho CRM's own opt-out columns. Someone who opted out there must not be mailed by us.</summary>
    private static readonly string[] OptOutHeaders = ["email opt out", "unsubscribed mode", "unsubscribed time"];
    /// <summary>§1381 — the Backstage ticket status; a cancelled ticket is not an attendee.</summary>
    private static readonly string[] StatusHeaders = ["attendee_status", "attendee status", "status"];

    private static bool Truthy(string v) =>
        v.Length > 0 && !v.Equals("false", StringComparison.OrdinalIgnoreCase) && !v.Equals("no", StringComparison.OrdinalIgnoreCase)
        && !v.Equals("0", StringComparison.Ordinal) && !v.Equals("nej", StringComparison.OrdinalIgnoreCase);

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
        int emailCol = 0, nameCol = 0, companyCol = 0, firstCol = 0, lastCol = 0, statusCol = 0;
        var optOutCols = new List<int>();
        foreach (var cell in header.Cells())
        {
            var text = cell.GetString().Trim().ToLowerInvariant();
            var col = cell.Address.ColumnNumber;
            if (emailCol == 0 && EmailHeaders.Contains(text)) emailCol = col;
            else if (nameCol == 0 && NameHeaders.Contains(text)) nameCol = col;
            else if (firstCol == 0 && FirstNameHeaders.Contains(text)) firstCol = col;
            else if (lastCol == 0 && LastNameHeaders.Contains(text)) lastCol = col;
            else if (companyCol == 0 && CompanyHeaders.Contains(text)) companyCol = col;
            else if (statusCol == 0 && StatusHeaders.Contains(text)) statusCol = col;
            else if (OptOutHeaders.Contains(text)) optOutCols.Add(col);
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
        var suppressed = (await _db.MailSuppressions.Where(s => s.EventId == eventId)
            .Select(s => s.Email).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int optedOut = 0, cancelled = 0;

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
            // §1381 — a cancelled ticket is not an attendee (the Backstage export lists every ticket ever issued).
            if (statusCol > 0 && row.Cell(statusCol).GetString().Contains("cancel", StringComparison.OrdinalIgnoreCase))
            { cancelled++; continue; }

            // §1381 — someone who opted out in the source system is SUPPRESSED, never imported as a recipient.
            // 🔒 Checked before the per-file dedup so an opt-out on any of their rows wins.
            if (optOutCols.Any(c => Truthy(row.Cell(c).GetString().Trim())))
            {
                if (suppressed.Add(email))
                    _db.MailSuppressions.Add(new MailSuppression
                    {
                        EventId = eventId, Email = email, Reason = MailSuppressionReason.Unsubscribed,
                        Detail = $"Opted out in the source list ({sourceEdition ?? batchLabel ?? "import"})", CreatedAt = now,
                    });
                optedOut++;
                seen.Add(email);
                continue;
            }

            if (!seen.Add(email)) { skipped++; continue; }            // the same address twice in one file

            var name = nameCol > 0 ? row.Cell(nameCol).GetString().Trim() : null;
            if (string.IsNullOrWhiteSpace(name) && (firstCol > 0 || lastCol > 0))
                name = $"{(firstCol > 0 ? row.Cell(firstCol).GetString().Trim() : "")} {(lastCol > 0 ? row.Cell(lastCol).GetString().Trim() : "")}".Trim();
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
        if (optedOut > 0) problems.Add($"{optedOut} address(es) had opted out in the source list — suppressed, not imported.");
        if (cancelled > 0) problems.Add($"{cancelled} cancelled ticket row(s) skipped.");
        return new ImportResult(added, updated, skipped, problems) { OptedOut = optedOut, Cancelled = cancelled };
    }
}
