using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1381 — importing the previous-attendee lists in the two real shapes: a Zoho CRM contact export (with its own
/// opt-out columns) and a Backstage ticket export (with cancelled tickets). FAKE data only.
/// </summary>
public sealed class ExternalRecipientImportTests
{
    private const int EventId = 3;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>().UseInMemoryDatabase($"ext-import-{Guid.NewGuid():N}").Options);

    private static MemoryStream Sheet(string[] headers, params string[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++) ws.Cell(r + 2, c + 1).Value = rows[r][c];
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task A_Zoho_export_imports_names_and_companies_and_suppresses_its_opt_outs()
    {
        using var db = NewDb();
        var file = Sheet(
            new[] { "Record Id", "First Name", "Last Name", "Account Name", "Email", "Email Opt Out", "Unsubscribed Time" },
            new[] { "1", "Ada", "Lovelace", "Analytical Ltd", "Ada@Example.test", "false", "" },
            new[] { "2", "Opted", "Out", "Quiet A/S", "optout@example.test", "true", "" },
            new[] { "3", "Gone", "Away", "Elsewhere", "unsub@example.test", "false", "2026-03-01 10:00" });

        var r = await new ExternalRecipientImportService(db).ImportAsync(EventId, file, "zoho.xlsx", "org@x.test", "ELDK25");

        var person = Assert.Single(db.ExternalRecipients);
        Assert.Equal("ada@example.test", person.Email);
        Assert.Equal("Ada Lovelace", person.FullName);
        Assert.Equal("Analytical Ltd", person.CompanyName);
        Assert.Equal("ELDK25", person.SourceEdition);

        Assert.Equal(2, r.OptedOut);
        Assert.Equal(new[] { "optout@example.test", "unsub@example.test" },
            db.MailSuppressions.Select(s => s.Email).OrderBy(e => e).ToArray());
        Assert.All(db.MailSuppressions, s => Assert.Equal(MailSuppressionReason.Unsubscribed, s.Reason));
    }

    [Fact]
    public async Task A_Backstage_export_skips_cancelled_tickets_and_counts_a_buyer_of_two_tickets_once()
    {
        using var db = NewDb();
        var file = Sheet(
            new[] { "ORDER_ID", "FIRST_NAME", "LAST_NAME", "EMAIL", "COMPANY_NAME", "ATTENDEE_STATUS" },
            new[] { "10", "Grace", "Hopper", "grace@example.test", "Navy", "Checked In" },
            new[] { "11", "Grace", "Hopper", "grace@example.test", "Navy", "Registered" },
            new[] { "12", "Never", "Came", "cancel@example.test", "Nope", "Cancelled" });

        var r = await new ExternalRecipientImportService(db).ImportAsync(EventId, file, "bs.xlsx", "org@x.test", "ELDK26");

        var person = Assert.Single(db.ExternalRecipients);
        Assert.Equal("Grace Hopper", person.FullName);
        Assert.Equal("Navy", person.CompanyName);
        Assert.Equal(1, r.Cancelled);
        Assert.Equal(1, r.Skipped);   // the second ticket row of the same address
    }

    [Fact]
    public async Task An_opt_out_already_on_file_is_not_duplicated_on_a_second_import()
    {
        using var db = NewDb();
        db.MailSuppressions.Add(new MailSuppression { EventId = EventId, Email = "optout@example.test", Reason = MailSuppressionReason.Unsubscribed });
        await db.SaveChangesAsync();
        var file = Sheet(new[] { "Email", "Email Opt Out" }, new[] { "optout@example.test", "true" });

        await new ExternalRecipientImportService(db).ImportAsync(EventId, file, "again.xlsx", null, "ELDK24");

        Assert.Single(db.MailSuppressions);
        Assert.Empty(db.ExternalRecipients);
    }
}
