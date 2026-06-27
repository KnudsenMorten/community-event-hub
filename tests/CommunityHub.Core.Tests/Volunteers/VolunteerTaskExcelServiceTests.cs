using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// Round-trip tests for <see cref="VolunteerTaskExcelService"/> (§151) on an EF Core
/// InMemory DbContext with the real <see cref="HeuristicTaskGuidanceGenerator"/> (no
/// network/secret). Proves: every exported row carries an ExternalKey; re-importing
/// the exported sheet is a no-op upsert (updates by id, creates nothing); a blank-id
/// row creates a fresh-keyed task with a generated Description; an edited cell updates
/// only that one task by id; and the column order is stable.
/// </summary>
public sealed class VolunteerTaskExcelServiceTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"voltask-excel-{Guid.NewGuid():N}")
            .Options);

    private static VolunteerTaskExcelService NewSvc(CommunityHubDbContext db) =>
        new(db, new HeuristicTaskGuidanceGenerator(), TimeProvider.System);

    /// <summary>Seed a Category → Subcategory with a few tasks; return the subcategory id.</summary>
    private static async Task<int> SeedAsync(CommunityHubDbContext db, int eventId = EventId)
    {
        var cat = new VolunteerCategory { EventId = eventId, Name = "Logistics" };
        db.VolunteerCategories.Add(cat);
        await db.SaveChangesAsync();
        var sub = new VolunteerSubcategory { EventId = eventId, CategoryId = cat.Id, Name = "Badges" };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();

        db.VolunteerTasks.AddRange(
            new VolunteerTask
            {
                EventId = eventId, SubcategoryId = sub.Id, Title = "Print badges",
                Description = "Print all attendee badges.", ResourcesNeeded = 2,
                Criticality = VolunteerTaskCriticality.NeedToHave, ResponsibleTeam = "ELDK-Volunteers",
                DueDate = new DateOnly(2026, 9, 1),
            },
            new VolunteerTask
            {
                EventId = eventId, SubcategoryId = sub.Id, Title = "Set up desk",
                Description = "Set up the registration desk.", ResourcesNeeded = 1,
            },
            new VolunteerTask
            {
                EventId = eventId, SubcategoryId = sub.Id, Title = "Pack swag",
                Description = "Pack the swag bags.", ResourcesNeeded = 3,
            });
        await db.SaveChangesAsync();
        return sub.Id;
    }

    private static XLWorkbook Open(byte[] bytes) => new XLWorkbook(new MemoryStream(bytes));

    [Fact]
    public async Task Export_writes_ExternalKey_for_every_row()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = NewSvc(db);

        var bytes = await svc.ExportAllAsync(EventId);

        using var wb = Open(bytes);
        var ws = wb.Worksheet(1);
        Assert.Equal("ExternalKey", ws.Cell(1, 1).GetString());

        var seededKeys = await db.VolunteerTasks.Select(t => t.ExternalKey).ToListAsync();
        var dataRows = ws.RowsUsed().Where(r => r.RowNumber() > 1).ToList();
        Assert.Equal(seededKeys.Count, dataRows.Count);
        foreach (var r in dataRows)
        {
            var keyText = r.Cell(1).GetString().Trim();
            Assert.True(Guid.TryParse(keyText, out var key), $"row {r.RowNumber()} has no GUID in col 1");
            Assert.Contains(key, seededKeys);
        }
    }

    [Fact]
    public async Task Reimport_of_exported_sheet_is_a_noop_upsert_updates_by_id_creates_nothing()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = NewSvc(db);

        var before = await db.VolunteerTasks.CountAsync();
        var bytes = await svc.ExportAllAsync(EventId);

        var result = await svc.ImportAsync(EventId, new MemoryStream(bytes));

        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(before, result.Updated);
        Assert.Equal(before, await db.VolunteerTasks.CountAsync()); // nothing created
    }

    [Fact]
    public async Task Blank_id_row_creates_a_new_task_with_fresh_key_and_generated_description()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = NewSvc(db);
        var before = await db.VolunteerTasks.CountAsync();

        // A minimal sheet: header + one blank-id row.
        var bytes = BuildSheet(new[]
        {
            // ExternalKey, Title, Description, Criticality, ResponsibleTeam, ResourcesNeeded, Prereq, Expect, DueDate, Bucket
            new[] { "", "Coordinate cloakroom", "", "NiceToHave", "ELDK-Volunteers", "2", "", "", "", "Front of house" },
        });

        var result = await svc.ImportAsync(EventId, new MemoryStream(bytes));

        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(before + 1, await db.VolunteerTasks.CountAsync());

        var created = await db.VolunteerTasks.SingleAsync(t => t.Title == "Coordinate cloakroom");
        Assert.NotEqual(Guid.Empty, created.ExternalKey);
        Assert.False(string.IsNullOrWhiteSpace(created.Description)); // generated from the title
        Assert.Equal(VolunteerTaskCriticality.NiceToHave, created.Criticality);
        Assert.Equal(2, created.ResourcesNeeded);

        // Placed under a bucket derived from the Bucket column.
        var sub = await db.VolunteerSubcategories.SingleAsync(s => s.Id == created.SubcategoryId);
        var bucket = await db.VolunteerCategories.SingleAsync(c => c.Id == sub.CategoryId);
        Assert.Equal("Front of house", bucket.Name);
    }

    [Fact]
    public async Task Edited_cell_updates_only_that_task_by_id()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = NewSvc(db);

        var bytes = await svc.ExportAllAsync(EventId);

        // Edit the Title of the "Print badges" row in-place, keep its ExternalKey.
        byte[] edited;
        using (var wb = Open(bytes))
        {
            var ws = wb.Worksheet(1);
            var target = ws.RowsUsed().Single(r => r.RowNumber() > 1 && r.Cell(2).GetString() == "Print badges");
            target.Cell(2).SetValue("Print badges (v2)");
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            edited = ms.ToArray();
        }

        var result = await svc.ImportAsync(EventId, new MemoryStream(edited));

        Assert.Equal(0, result.Created);
        Assert.True(await db.VolunteerTasks.AnyAsync(t => t.Title == "Print badges (v2)"));
        Assert.False(await db.VolunteerTasks.AnyAsync(t => t.Title == "Print badges")); // renamed, not duplicated
        // The other tasks are untouched.
        Assert.True(await db.VolunteerTasks.AnyAsync(t => t.Title == "Set up desk"));
        Assert.True(await db.VolunteerTasks.AnyAsync(t => t.Title == "Pack swag"));
    }

    [Fact]
    public async Task Export_import_column_order_is_stable()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = NewSvc(db);

        var bytes = await svc.ExportAllAsync(EventId);

        using var wb = Open(bytes);
        var ws = wb.Worksheet(1);
        for (var c = 0; c < VolunteerTaskExcelService.Columns.Length; c++)
            Assert.Equal(VolunteerTaskExcelService.Columns[c], ws.Cell(1, c + 1).GetString());

        // The header has exactly the declared columns, no trailing extras.
        Assert.Equal(
            VolunteerTaskExcelService.Columns.Length,
            ws.Row(1).CellsUsed().Count());
    }

    /// <summary>Build a sheet with the service's stable header + the given data rows.</summary>
    private static byte[] BuildSheet(IEnumerable<string[]> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(VolunteerTaskExcelService.SheetName);
        for (var c = 0; c < VolunteerTaskExcelService.Columns.Length; c++)
            ws.Cell(1, c + 1).SetValue(VolunteerTaskExcelService.Columns[c]);

        var row = 2;
        foreach (var cells in rows)
        {
            for (var c = 0; c < cells.Length; c++)
                ws.Cell(row, c + 1).SetValue(cells[c]);
            row++;
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
