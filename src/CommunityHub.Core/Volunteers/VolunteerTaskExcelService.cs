using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// The §151 real <b>.xlsx round-trip</b> for volunteer tasks — distinct from the
/// legacy one-way semicolon-CSV plan importer (<see cref="VolunteerPlanImportService"/>).
/// An organizer EXPORTS every task to Excel, makes bulk edits, and IMPORTS it back.
///
/// Upsert is driven by the stable, immutable <see cref="VolunteerTask.ExternalKey"/>
/// (a GUID that survives a re-seed/migration), which the export ALWAYS writes into
/// column 1:
///  - a row carrying a known ExternalKey → UPDATE that task (the match key);
///  - a row with a BLANK ExternalKey → CREATE a new task (it gets a fresh
///    ExternalKey) placed under a bucket/subcategory derived from its Bucket /
///    Responsible Team column, mirroring how <see cref="VolunteerPlanImportService"/>
///    structures imported tasks;
///  - a row whose ExternalKey is non-blank but matches no task in this edition is
///    SKIPPED (never created under a caller-chosen key, so an external id is never
///    injected).
///
/// Blank Description cells are filled from <see cref="ITaskGuidanceGenerator"/>
/// (heuristic by default, AI when configured) so every task ends up with a detailed
/// description even when the operator left it empty — the same generator that fills
/// Pre-req / Expectations.
///
/// The first 304-task import (§150 step 1) runs through this importer with no ids,
/// so every row is CREATED and gets an ExternalKey + generated guidance from the
/// start. That bulk run is performed by the operator, not here.
/// </summary>
public sealed class VolunteerTaskExcelService
{
    private readonly CommunityHubDbContext _db;
    private readonly ITaskGuidanceGenerator _guidance;
    private readonly TimeProvider _clock;

    public VolunteerTaskExcelService(
        CommunityHubDbContext db, ITaskGuidanceGenerator guidance, TimeProvider clock)
    {
        _db = db;
        _guidance = guidance;
        _clock = clock;
    }

    /// <summary>MIME type for an .xlsx (OpenXML spreadsheet).</summary>
    public const string ContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public const string SheetName = "Tasks";

    /// <summary>Fallback bucket name for a created task that names neither a Bucket
    /// nor a Responsible Team — keeps every task under SOME bucket, like the plan
    /// importer's structural holder.</summary>
    public const string UnsortedBucketName = "Unsorted";

    /// <summary>
    /// The export/import column order. ExternalKey is ALWAYS column 1 (the upsert
    /// match key). The order is stable so a re-import of an exported sheet lines up
    /// header-for-header; the importer also tolerates re-ordered columns because it
    /// resolves cells by header name, but the export always writes this exact order.
    /// </summary>
    public static readonly string[] Columns =
    {
        "ExternalKey",      // 1 — stable GUID match key (blank ⇒ create)
        "Title",            // 2
        "Description",      // 3 — blank ⇒ generated via ITaskGuidanceGenerator
        "Criticality",      // 4 — Unspecified | NiceToHave | NeedToHave
        "ResponsibleTeam",  // 5
        "ResourcesNeeded",  // 6 — integer demand
        "Prerequisites",    // 7
        "Expectations",     // 8
        "DueDate",          // 9 — yyyy-MM-dd
        "Bucket",           // 10 — the owning VolunteerCategory name
    };

    // -- Export --------------------------------------------------------------

    /// <summary>Write every <see cref="VolunteerTask"/> for the edition to an .xlsx
    /// byte array, ExternalKey always in column 1, in the stable <see cref="Columns"/>
    /// order.</summary>
    public async Task<byte[]> ExportAllAsync(int eventId, CancellationToken ct = default)
    {
        var tasks = await _db.VolunteerTasks
            .Where(t => t.EventId == eventId)
            .Include(t => t.Subcategory).ThenInclude(s => s.Category)
            .OrderBy(t => t.Subcategory.Category.Name)
            .ThenBy(t => t.Title)
            .ToListAsync(ct);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SheetName);

        for (var c = 0; c < Columns.Length; c++)
            ws.Cell(1, c + 1).SetValue(Columns[c]);
        ws.Row(1).Style.Font.Bold = true;

        var row = 2;
        foreach (var t in tasks)
        {
            ws.Cell(row, 1).SetValue(t.ExternalKey.ToString());
            ws.Cell(row, 2).SetValue(t.Title ?? string.Empty);
            ws.Cell(row, 3).SetValue(t.Description ?? string.Empty);
            ws.Cell(row, 4).SetValue(t.Criticality.ToString());
            ws.Cell(row, 5).SetValue(t.ResponsibleTeam ?? string.Empty);
            ws.Cell(row, 6).SetValue(t.ResourcesNeeded);
            ws.Cell(row, 7).SetValue(t.Prerequisites ?? string.Empty);
            ws.Cell(row, 8).SetValue(t.Expectations ?? string.Empty);
            ws.Cell(row, 9).SetValue(t.DueDate?.ToString("yyyy-MM-dd") ?? string.Empty);
            ws.Cell(row, 10).SetValue(t.Subcategory?.Category?.Name ?? string.Empty);
            row++;
        }

        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // -- Import --------------------------------------------------------------

    /// <summary>UPSERT tasks from an exported (or operator-edited) .xlsx. Returns a
    /// summary of how many tasks were created / updated / skipped.</summary>
    public async Task<TaskExcelImportResult> ImportAsync(
        int eventId, Stream stream, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        int created = 0, updated = 0, skipped = 0;

        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.FirstOrDefault();
        if (ws is null) return new TaskExcelImportResult(0, 0, 0);

        // Resolve columns by header (case-insensitive) so a re-ordered sheet still works.
        var headerRow = ws.FirstRowUsed();
        if (headerRow is null) return new TaskExcelImportResult(0, 0, 0);
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in headerRow.CellsUsed())
        {
            var k = c.GetString().Trim();
            if (!string.IsNullOrEmpty(k) && !col.ContainsKey(k)) col[k] = c.Address.ColumnNumber;
        }

        // Pre-load the edition's tasks for ExternalKey matching.
        var byKey = await _db.VolunteerTasks
            .Where(t => t.EventId == eventId)
            .ToDictionaryAsync(t => t.ExternalKey, t => t, ct);

        // Bucket/subcategory caches for CREATE placement (mirrors the plan importer).
        var categories = await _db.VolunteerCategories
            .Where(c => c.EventId == eventId)
            .ToListAsync(ct);
        var categoryByName = categories.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
        var subByCategory = new Dictionary<int, VolunteerSubcategory>();

        var headerRowNum = headerRow.RowNumber();
        foreach (var r in ws.RowsUsed().Where(r => r.RowNumber() > headerRowNum))
        {
            var title = Cell(r, col, "Title");
            var keyText = Cell(r, col, "ExternalKey");

            // Wholly blank row — ignore (not a skip).
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(keyText))
                continue;

            VolunteerTask task;
            bool isNew;
            if (string.IsNullOrWhiteSpace(keyText))
            {
                // CREATE — blank id. Place under a bucket derived from Bucket / Responsible Team.
                isNew = true;
                var bucketName = FirstNonBlank(
                    Cell(r, col, "Bucket"), Cell(r, col, "ResponsibleTeam"), UnsortedBucketName);
                var sub = await ResolveSubcategoryAsync(
                    eventId, bucketName, now, categoryByName, subByCategory, ct);

                task = new VolunteerTask
                {
                    // ExternalKey defaults to a fresh GUID on the model — leave it.
                    EventId = eventId,
                    SubcategoryId = sub.Id,
                    CreatedAt = now,
                };
                _db.VolunteerTasks.Add(task);
            }
            else if (Guid.TryParse(keyText, out var key) && byKey.TryGetValue(key, out var existing))
            {
                // UPDATE — known id is the match key.
                isNew = false;
                task = existing;
            }
            else
            {
                // A non-blank id that matches no task in this edition: do NOT create
                // under a caller-supplied key (no external-id injection) — skip it.
                skipped++;
                continue;
            }

            // -- Apply content from the row -------------------------------------
            if (col.ContainsKey("Title")) task.Title = title;
            if (col.ContainsKey("Criticality")) task.Criticality = ParseCriticality(Cell(r, col, "Criticality"));
            if (col.ContainsKey("ResponsibleTeam")) task.ResponsibleTeam = NullIfBlank(Cell(r, col, "ResponsibleTeam"));
            if (col.ContainsKey("ResourcesNeeded")) task.ResourcesNeeded = ParseInt(Cell(r, col, "ResourcesNeeded"));
            if (col.ContainsKey("Prerequisites")) task.Prerequisites = NullIfBlank(Cell(r, col, "Prerequisites"));
            if (col.ContainsKey("Expectations")) task.Expectations = NullIfBlank(Cell(r, col, "Expectations"));
            if (col.ContainsKey("DueDate")) task.DueDate = ParseDate(Cell(r, col, "DueDate"));

            var description = col.ContainsKey("Description") ? NullIfBlank(Cell(r, col, "Description")) : task.Description;

            // Blank Description ⇒ generate one from the title via the guidance generator.
            if (string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(task.Title))
                description = await GenerateDescriptionAsync(
                    task.Title, FirstNonBlank(Cell(r, col, "Bucket"), null), task.ResponsibleTeam, ct);
            task.Description = description;

            if (isNew)
            {
                byKey[task.ExternalKey] = task; // guard against a duplicate blank-then-key in the same sheet
                created++;
            }
            else
            {
                task.UpdatedAt = now;
                updated++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return new TaskExcelImportResult(created, updated, skipped);
    }

    /// <summary>Compose a detailed description from the guidance generator's output.
    /// Consumes <see cref="ITaskGuidanceGenerator"/> so the text upgrades automatically
    /// when an LLM is configured; never throws (the generator falls back internally).</summary>
    private async Task<string> GenerateDescriptionAsync(
        string title, string? bucketName, string? responsibleTeam, CancellationToken ct)
    {
        var g = await _guidance.GenerateAsync(title, bucketName, responsibleTeam, ct);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(g.Prerequisites)) parts.Add("Before you start: " + g.Prerequisites.Trim());
        if (!string.IsNullOrWhiteSpace(g.Expectations)) parts.Add("Done means: " + g.Expectations.Trim());
        return parts.Count > 0 ? string.Join(" ", parts) : title.Trim() + ".";
    }

    /// <summary>Upsert the bucket (<see cref="VolunteerCategory"/>) by name and ensure
    /// its single "Imported plan" subcategory, mirroring <see cref="VolunteerPlanImportService"/>.</summary>
    private async Task<VolunteerSubcategory> ResolveSubcategoryAsync(
        int eventId, string bucketName, DateTimeOffset now,
        Dictionary<string, VolunteerCategory> categoryByName,
        Dictionary<int, VolunteerSubcategory> subByCategory,
        CancellationToken ct)
    {
        if (!categoryByName.TryGetValue(bucketName, out var cat))
        {
            cat = new VolunteerCategory { EventId = eventId, Name = bucketName, CreatedAt = now };
            _db.VolunteerCategories.Add(cat);
            await _db.SaveChangesAsync(ct); // need cat.Id for the subcategory FK
            categoryByName[bucketName] = cat;
        }

        if (subByCategory.TryGetValue(cat.Id, out var cached)) return cached;

        var sub = await _db.VolunteerSubcategories.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.CategoryId == cat.Id
                 && s.Name == VolunteerPlanImportService.ImportedSubcategoryName, ct);
        if (sub is null)
        {
            sub = new VolunteerSubcategory
            {
                EventId = eventId,
                CategoryId = cat.Id,
                Name = VolunteerPlanImportService.ImportedSubcategoryName,
                CreatedAt = now,
            };
            _db.VolunteerSubcategories.Add(sub);
            await _db.SaveChangesAsync(ct); // need sub.Id for the task FK
        }
        subByCategory[cat.Id] = sub;
        return sub;
    }

    // -- Cell helpers --------------------------------------------------------

    private static string Cell(IXLRow row, IReadOnlyDictionary<string, int> col, string name)
        => col.TryGetValue(name, out var c) ? row.Cell(c).GetString().Trim() : string.Empty;

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string FirstNonBlank(string? a, string? b, string fallback = "")
    {
        if (!string.IsNullOrWhiteSpace(a)) return a.Trim();
        if (!string.IsNullOrWhiteSpace(b)) return b.Trim();
        return fallback;
    }

    private static int ParseInt(string s)
        => int.TryParse(s?.Trim(), out var n) && n > 0 ? n : 0;

    private static DateOnly? ParseDate(string s)
    {
        s = s?.Trim() ?? string.Empty;
        if (s.Length == 0) return null;
        if (DateOnly.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt)) return DateOnly.FromDateTime(dt);
        return null;
    }

    private static VolunteerTaskCriticality ParseCriticality(string s)
    {
        s = s?.Trim() ?? string.Empty;
        if (s.Length == 0) return VolunteerTaskCriticality.Unspecified;
        if (Enum.TryParse<VolunteerTaskCriticality>(s, ignoreCase: true, out var c)) return c;
        // Tolerate the plan's human bands.
        if (s.Replace("-", "").Replace(" ", "").Equals("needtohave", StringComparison.OrdinalIgnoreCase))
            return VolunteerTaskCriticality.NeedToHave;
        if (s.Replace("-", "").Replace(" ", "").Equals("nicetohave", StringComparison.OrdinalIgnoreCase))
            return VolunteerTaskCriticality.NiceToHave;
        return VolunteerTaskCriticality.Unspecified;
    }
}

/// <summary>The outcome of an Excel task import, for the organizer's confirmation screen.</summary>
public sealed record TaskExcelImportResult(int Created, int Updated, int Skipped);
