using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// §6.4 / §3.5 — the swag files: award, polo, and one Credly pair per role.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This is an EXTRACTION, not a second implementation.</b> These workbooks already
/// existed as organizer downloads (§326bu, <c>/Organizer/Swag</c>). Writing a separate producer for
/// the scheduled publish would have created two answers to "how many polos does this person get?" —
/// the §767 failure, with a vendor invoice attached. The page and the publisher call THIS.</para>
///
/// <para>🔒 <b>Deterministic by construction</b>, because §6.4's whole mail rule depends on it: no
/// timestamp inside the sheet (the old download put one in the FILE NAME only, which was safe — a
/// stamp in a CELL would make every daily rebuild "change"), fixed row order, fixed columns. Two
/// runs over unchanged data produce byte-identical files, so the weekly mail says nothing changed
/// when nothing changed.</para>
///
/// <para>⚠️ <b>Who counts:</b> <see cref="LogisticsAudience"/> — active, non-test. The old download
/// filtered on <c>IsActive</c> alone; test personas are now excluded too, because these sheets order
/// physical goods and a test persona's shirt is a real shirt. The count it drops is logged.</para>
/// </remarks>
public sealed class SwagLogisticsProducer
{
    /// <summary>§299 OPEN-14 (operator 2026-07-23): every organizer gets 5 polos, flat.</summary>
    public const int OrganizerPoloCount = 5;

    private readonly CommunityHubDbContext _db;

    public SwagLogisticsProducer(CommunityHubDbContext db) => _db = db;

    /// <summary>One person's swag row, already reduced to what the sheets need.</summary>
    private sealed record Row(
        int Id, string FullName, string Email, ParticipantRole Role,
        bool WantsPolo, string? PoloSize, bool WantsGift, bool WantsCredlyBadge, int Polos);

    /// <summary>Every swag file for the edition: award, polo, and a Credly pair per role.</summary>
    public async Task<IReadOnlyList<GeneratedFile>> BuildAllAsync(
        int eventId, string eventShortName, CancellationToken ct = default)
    {
        var rows = await LoadAsync(eventId, ct);

        var award = Ordered(rows.Where(r => r.WantsGift));
        var polo = Ordered(rows.Where(r => r.WantsPolo && !string.IsNullOrWhiteSpace(r.PoloSize)));

        var files = new List<GeneratedFile>
        {
            new(LogisticsFileNames.Award(eventShortName), BuildAward(rows),
                GeneratedFile.XlsxContentType,
                GeneratedFile.KeyOf(award.Select(r => $"{r.Id}|{r.FullName}|{r.Email}|{r.Role}")),
                LogisticsHeadline.People(award.Count)),

            // ⚠️ The polo headline counts GARMENTS, not people — an organizer with five polos is one
            // person and five shirts, and the shirts are what gets ordered.
            new(LogisticsFileNames.Polo(eventShortName), BuildPolo(rows),
                GeneratedFile.XlsxContentType,
                GeneratedFile.KeyOf(polo.Select(r => $"{r.Id}|{r.FullName}|{r.Email}|{r.Role}|{r.PoloSize}|{r.Polos}")),
                LogisticsHeadline.Of(polo.Sum(r => r.Polos), "polo", "polos")),
        };

        // §3.5: "One pair per CEH role. Columns: name, email." A role with nobody wanting a badge
        // produces NO file — an empty workbook per unused role is noise in a folder somebody reads.
        foreach (var group in rows
                     .Where(r => r.WantsCredlyBadge)
                     .GroupBy(r => r.Role)
                     .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
        {
            var people = group.OrderBy(r => r.FullName, StringComparer.Ordinal).ToList();
            var role = group.Key.ToString();

            var key = GeneratedFile.KeyOf(
                people.Select(r => $"{r.Id}|{r.FullName}|{r.Email}|{role}"));

            files.Add(new GeneratedFile(
                LogisticsFileNames.Credly(eventShortName, role, ".xlsx"),
                BuildCredlyXlsx(people, role),
                GeneratedFile.XlsxContentType,
                key,
                LogisticsHeadline.People(people.Count)));

            files.Add(new GeneratedFile(
                LogisticsFileNames.Credly(eventShortName, role, ".csv"),
                BuildCredlyCsv(people),
                GeneratedFile.CsvContentType,
                key,
                LogisticsHeadline.People(people.Count)));
        }

        return files;
    }

    private async Task<IReadOnlyList<Row>> LoadAsync(int eventId, CancellationToken ct)
    {
        // 🔒 The audience rule, from the one place that owns it.
        var countable = LogisticsAudience.Countable(_db.Participants, eventId);

        var joined = await _db.SwagPreferences
            .Where(s => s.EventId == eventId)
            .Join(countable, s => s.ParticipantId, p => p.Id, (s, p) => new
            {
                p.Id, p.FullName, p.Email, p.Role,
                s.WantsPolo, s.PoloSize, s.WantsGift, s.WantsCredlyBadge,
            })
            .ToListAsync(ct);

        var categoryByPid = await _db.SpeakerProfiles
            .Where(s => s.EventId == eventId)
            .ToDictionaryAsync(s => s.ParticipantId, s => s.Category, ct);
        var daysBySpeaker = await SpeakerDayScope.DaysBySpeakerAsync(_db, eventId, ct);

        return joined
            .Select(x => new Row(
                x.Id, x.FullName, x.Email, x.Role,
                x.WantsPolo, x.PoloSize, x.WantsGift, x.WantsCredlyBadge,
                PoloCount(x.Role, x.Id, categoryByPid, daysBySpeaker)))
            .ToList();
    }

    /// <summary>
    /// §299 6.3 — polos for one person. Organizers are a flat ×5 (OPEN-14). A person with no
    /// speaker hat keeps their single role-hat polo; a speaker gets the funded per-day count, and a
    /// non-Speaker role who also speaks never drops below one.
    /// </summary>
    internal static int PoloCount(
        ParticipantRole role, int participantId,
        IReadOnlyDictionary<int, SpeakerCategory?> categoryByPid,
        IReadOnlyDictionary<int, SpeakerDays> daysBySpeaker)
    {
        if (role == ParticipantRole.Organizer) return OrganizerPoloCount;
        if (!categoryByPid.TryGetValue(participantId, out var category)) return 1;

        var days = daysBySpeaker.TryGetValue(participantId, out var d) ? d : SpeakerDays.None;
        var funded = SpeakerDayScope.FundedPolos(category, days);
        return role == ParticipantRole.Speaker ? funded : Math.Max(1, funded);
    }

    private static byte[] BuildAward(IReadOnlyList<Row> rows)
    {
        var people = Ordered(rows.Where(r => r.WantsGift));

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Award per person");
        Header(ws, "Name", "Email", "Role", "Awards");

        var row = 2;
        foreach (var r in people)
        {
            ws.Cell(row, 1).Value = r.FullName;
            ws.Cell(row, 2).Value = r.Email;
            ws.Cell(row, 3).Value = r.Role.ToString();
            ws.Cell(row, 4).Value = 1;
            row++;
        }
        Total(ws, people.Count, row, 1, 4);
        Finish(ws);
        return Save(wb);
    }

    private static byte[] BuildPolo(IReadOnlyList<Row> rows)
    {
        var people = Ordered(rows.Where(r => r.WantsPolo && !string.IsNullOrWhiteSpace(r.PoloSize)));

        using var wb = new XLWorkbook();

        // Sheet 1 — WHO gets what: the hand-out list (§326bu: "it must also contain the name of the
        // person (critical to handout)").
        var ws = wb.Worksheets.Add("Polo per person");
        Header(ws, "Name", "Email", "Role", "Size", "Polos");
        var row = 2;
        foreach (var r in people)
        {
            ws.Cell(row, 1).Value = r.FullName;
            ws.Cell(row, 2).Value = r.Email;
            ws.Cell(row, 3).Value = r.Role.ToString();
            ws.Cell(row, 4).Value = r.PoloSize;
            ws.Cell(row, 5).Value = r.Polos;
            row++;
        }
        Total(ws, people.Count, row, 1, 5);
        Finish(ws);

        // Sheet 2 — the VENDOR order: the same numbers rolled up. Kept because the per-person list
        // alone cannot be ordered from.
        var agg = wb.Worksheets.Add("Size totals");
        Header(agg, "Role", "Size", "Polo_Total");
        var lines = people
            .GroupBy(r => new { Role = r.Role.ToString(), Size = r.PoloSize! })
            .Select(g => new { g.Key.Role, g.Key.Size, Count = g.Sum(x => x.Polos) })
            .OrderBy(x => x.Role, StringComparer.Ordinal)
            .ThenBy(x => x.Size, StringComparer.Ordinal)
            .ToList();
        var arow = 2;
        foreach (var l in lines)
        {
            agg.Cell(arow, 1).Value = l.Role;
            agg.Cell(arow, 2).Value = l.Size;
            agg.Cell(arow, 3).Value = l.Count;
            arow++;
        }
        Total(agg, lines.Count, arow, 1, 3);
        agg.Columns().AdjustToContents();

        return Save(wb);
    }

    private static byte[] BuildCredlyXlsx(IReadOnlyList<Row> people, string role)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Credly per person");
        Header(ws, "Name", "Email", "Role", "Badges");

        var row = 2;
        foreach (var r in people)
        {
            ws.Cell(row, 1).Value = r.FullName;
            ws.Cell(row, 2).Value = r.Email;
            ws.Cell(row, 3).Value = role;
            ws.Cell(row, 4).Value = 1;
            row++;
        }
        Total(ws, people.Count, row, 1, 4);
        Finish(ws);
        return Save(wb);
    }

    /// <summary>
    /// §3.5 asks for a <c>.csv</c> beside each Credly workbook — Credly's own bulk-issue import
    /// takes CSV, so this is the file that actually gets uploaded. Columns: name, email.
    /// </summary>
    private static byte[] BuildCredlyCsv(IReadOnlyList<Row> people)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Name,Email\n");
        foreach (var r in people)
        {
            sb.Append(CsvField(r.FullName)).Append(',').Append(CsvField(r.Email)).Append('\n');
        }
        // ⚠️ No BOM and \n line endings, both deliberate: the bytes must be identical run to run,
        // and a bulk importer is happier without a BOM than a human is with one.
        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string CsvField(string? v)
    {
        var s = v ?? string.Empty;
        return s.IndexOfAny([',', '"', '\n', '\r']) < 0 ? s : $"\"{s.Replace("\"", "\"\"")}\"";
    }

    /// <summary>
    /// 🔒 ORDINAL ordering, not culture-aware. A culture-sensitive sort puts Danish Æ/Ø/Å in a
    /// different place depending on the HOST's locale — so the same data would produce different
    /// bytes on the web host and the jobs host, and every file would "change" on alternate days.
    /// </summary>
    private static List<Row> Ordered(IEnumerable<Row> rows) =>
        rows.OrderBy(r => r.Role.ToString(), StringComparer.Ordinal)
            .ThenBy(r => r.FullName, StringComparer.Ordinal)
            .ThenBy(r => r.Id)
            .ToList();

    private static void Header(IXLWorksheet ws, params string[] headers)
    {
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Range(1, 1, 1, headers.Length).Style.Font.Bold = true;
    }

    private static void Total(IXLWorksheet ws, int dataCount, int row, int labelCol, int sumCol)
    {
        if (dataCount == 0) return;
        ws.Cell(row, labelCol).Value = "Grand Total";
        ws.Cell(row, sumCol).FormulaA1 =
            $"SUM({ws.Cell(2, sumCol).Address.ColumnLetter}2:{ws.Cell(row - 1, sumCol).Address.ColumnLetter}{row - 1})";
        ws.Range(row, labelCol, row, sumCol).Style.Font.Bold = true;
    }

    private static void Finish(IXLWorksheet ws)
    {
        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
    }

    private static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
