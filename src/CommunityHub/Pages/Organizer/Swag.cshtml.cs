using ClosedXML.Excel;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

[Authorize]
public class SwagModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;

    public SwagModel(CommunityHubDbContext db, ICurrentParticipantAccessor participant)
    {
        _db = db;
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }

    public int TotalSubmissions { get; private set; }
    public int PoloYesCount { get; private set; }
    public int JacketYesCount { get; private set; }
    public int GiftYesCount { get; private set; }
    public int CredlyYesCount { get; private set; }

    /// <summary>(Role, Size) → count, polo only, "yes" rows only. Sorted by role then size.</summary>
    public IReadOnlyList<PoloLine> PoloLines { get; private set; } = Array.Empty<PoloLine>();
    /// <summary>Size → count, jacket only, "yes" rows only.</summary>
    public IReadOnlyList<JacketLine> JacketLines { get; private set; } = Array.Empty<JacketLine>();

    public record PoloLine(string Role, string Size, int Count);
    public record JacketLine(string Size, int Count);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadAggregatesAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnGetDownloadAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var rows = await _db.SwagPreferences
            .Where(s => s.EventId == me.EventId)
            .Join(_db.Participants, s => s.ParticipantId, p => p.Id, (s, p) => new
            {
                p.FullName, p.Email, p.Role,
                s.WantsPolo, s.PoloSize,
                s.WantsJacket, s.JacketSize,
                s.WantsGift, s.WantsCredlyBadge, s.Notes,
                s.UpdatedAt, s.CreatedAt
            })
            .ToListAsync(ct);

        using var wb = new XLWorkbook();

        // --- Sheet 1: Polo Order (Role → Size → Count) -----------------------
        var poloSheet = wb.Worksheets.Add("Polo Order");
        poloSheet.Cell(1, 1).Value = "Role";
        poloSheet.Cell(1, 2).Value = "Size";
        poloSheet.Cell(1, 3).Value = "Polo_Total";
        poloSheet.Range(1, 1, 1, 3).Style.Font.Bold = true;

        var poloAgg = rows
            .Where(r => r.WantsPolo && !string.IsNullOrWhiteSpace(r.PoloSize))
            .GroupBy(r => new { Role = r.Role.ToString(), Size = r.PoloSize! })
            .Select(g => new { g.Key.Role, g.Key.Size, Count = g.Count() })
            .OrderBy(x => x.Role).ThenBy(x => x.Size)
            .ToList();

        int prow = 2;
        foreach (var p in poloAgg)
        {
            poloSheet.Cell(prow, 1).Value = p.Role;
            poloSheet.Cell(prow, 2).Value = p.Size;
            poloSheet.Cell(prow, 3).Value = p.Count;
            prow++;
        }
        if (poloAgg.Count > 0)
        {
            poloSheet.Cell(prow, 1).Value = "Grand Total";
            poloSheet.Cell(prow, 3).FormulaA1 = $"SUM(C2:C{prow - 1})";
            poloSheet.Range(prow, 1, prow, 3).Style.Font.Bold = true;
        }
        poloSheet.Columns().AdjustToContents();

        // --- Sheet 2: Jacket Order (Size → Count) ---------------------------
        var jacketSheet = wb.Worksheets.Add("Jacket Order");
        jacketSheet.Cell(1, 1).Value = "Size";
        jacketSheet.Cell(1, 2).Value = "Jacket_Total";
        jacketSheet.Range(1, 1, 1, 2).Style.Font.Bold = true;

        var jacketAgg = rows
            .Where(r => r.WantsJacket && !string.IsNullOrWhiteSpace(r.JacketSize))
            .GroupBy(r => r.JacketSize!)
            .Select(g => new { Size = g.Key, Count = g.Count() })
            .OrderBy(x => x.Size)
            .ToList();

        int jrow = 2;
        foreach (var j in jacketAgg)
        {
            jacketSheet.Cell(jrow, 1).Value = j.Size;
            jacketSheet.Cell(jrow, 2).Value = j.Count;
            jrow++;
        }
        if (jacketAgg.Count > 0)
        {
            jacketSheet.Cell(jrow, 1).Value = "Grand Total";
            jacketSheet.Cell(jrow, 2).FormulaA1 = $"SUM(B2:B{jrow - 1})";
            jacketSheet.Range(jrow, 1, jrow, 2).Style.Font.Bold = true;
        }
        jacketSheet.Columns().AdjustToContents();

        // --- Sheet 3: Award Order (one row per gift recipient, name + role) -
        //  Engraver needs name + role per award. Grouped by Role for vendor
        //  pivot-style readability; per-row count is 1 so SUM = headcount.
        var awardSheet = wb.Worksheets.Add("Award Order");
        awardSheet.Cell(1, 1).Value = "Role";
        awardSheet.Cell(1, 2).Value = "Name";
        awardSheet.Cell(1, 3).Value = "Count";
        awardSheet.Range(1, 1, 1, 3).Style.Font.Bold = true;

        var awardRows = rows
            .Where(r => r.WantsGift)
            .OrderBy(r => r.Role.ToString())
            .ThenBy(r => r.FullName)
            .ToList();

        int arow = 2;
        foreach (var r in awardRows)
        {
            awardSheet.Cell(arow, 1).Value = r.Role.ToString();
            awardSheet.Cell(arow, 2).Value = r.FullName;
            awardSheet.Cell(arow, 3).Value = 1;
            arow++;
        }
        if (awardRows.Count > 0)
        {
            awardSheet.Cell(arow, 1).Value = "Grand Total";
            awardSheet.Cell(arow, 3).FormulaA1 = $"SUM(C2:C{arow - 1})";
            awardSheet.Range(arow, 1, arow, 3).Style.Font.Bold = true;
        }
        awardSheet.Columns().AdjustToContents();

        // --- Sheet 4: Credly Badge List -------------------------------------
        var credlySheet = wb.Worksheets.Add("Credly Badge List");
        credlySheet.Cell(1, 1).Value = "Role";
        credlySheet.Cell(1, 2).Value = "Name";
        credlySheet.Cell(1, 3).Value = "Email";
        credlySheet.Cell(1, 4).Value = "Count";
        credlySheet.Range(1, 1, 1, 4).Style.Font.Bold = true;
        var credlyRows = rows
            .Where(r => r.WantsCredlyBadge)
            .OrderBy(r => r.Role.ToString()).ThenBy(r => r.FullName)
            .ToList();
        int crow = 2;
        foreach (var r in credlyRows)
        {
            credlySheet.Cell(crow, 1).Value = r.Role.ToString();
            credlySheet.Cell(crow, 2).Value = r.FullName;
            credlySheet.Cell(crow, 3).Value = r.Email;
            credlySheet.Cell(crow, 4).Value = 1;
            crow++;
        }
        if (credlyRows.Count > 0)
        {
            credlySheet.Cell(crow, 1).Value = "Grand Total";
            credlySheet.Cell(crow, 4).FormulaA1 = $"SUM(D2:D{crow - 1})";
            credlySheet.Range(crow, 1, crow, 4).Style.Font.Bold = true;
        }
        credlySheet.Columns().AdjustToContents();

        // --- Sheet 5: Raw (all rows, all detail) ----------------------------
        var raw = wb.Worksheets.Add("Raw");
        var headers = new[] { "Name", "Email", "Role",
            "Wants polo", "Polo size",
            "Wants jacket", "Jacket size",
            "Wants award", "Wants Credly", "Notes", "Last updated (UTC)" };
        for (int i = 0; i < headers.Length; i++) raw.Cell(1, i + 1).Value = headers[i];
        raw.Range(1, 1, 1, headers.Length).Style.Font.Bold = true;

        int rrow = 2;
        foreach (var r in rows.OrderBy(x => x.Role.ToString()).ThenBy(x => x.FullName))
        {
            raw.Cell(rrow, 1).Value = r.FullName;
            raw.Cell(rrow, 2).Value = r.Email;
            raw.Cell(rrow, 3).Value = r.Role.ToString();
            raw.Cell(rrow, 4).Value = r.WantsPolo ? "Yes" : "No";
            raw.Cell(rrow, 5).Value = r.PoloSize ?? "";
            raw.Cell(rrow, 6).Value = r.WantsJacket ? "Yes" : "No";
            raw.Cell(rrow, 7).Value = r.JacketSize ?? "";
            raw.Cell(rrow, 8).Value = r.WantsGift ? "Yes" : "No";
            raw.Cell(rrow, 9).Value = r.WantsCredlyBadge ? "Yes" : "No";
            raw.Cell(rrow, 10).Value = r.Notes ?? "";
            raw.Cell(rrow, 11).Value = (r.UpdatedAt ?? r.CreatedAt).UtcDateTime;
            rrow++;
        }
        raw.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var fileName = $"swag-order-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx";
        return File(
            ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private async Task LoadAggregatesAsync(int eventId, CancellationToken ct)
    {
        var rows = await _db.SwagPreferences
            .Where(s => s.EventId == eventId)
            .Join(_db.Participants, s => s.ParticipantId, p => p.Id, (s, p) => new
            {
                p.Role,
                s.WantsPolo, s.PoloSize,
                s.WantsJacket, s.JacketSize,
                s.WantsGift, s.WantsCredlyBadge,
            })
            .ToListAsync(ct);

        TotalSubmissions = rows.Count;
        PoloYesCount   = rows.Count(r => r.WantsPolo);
        JacketYesCount = rows.Count(r => r.WantsJacket);
        GiftYesCount   = rows.Count(r => r.WantsGift);
        CredlyYesCount = rows.Count(r => r.WantsCredlyBadge);

        PoloLines = rows
            .Where(r => r.WantsPolo && !string.IsNullOrWhiteSpace(r.PoloSize))
            .GroupBy(r => new { Role = r.Role.ToString(), Size = r.PoloSize! })
            .Select(g => new PoloLine(g.Key.Role, g.Key.Size, g.Count()))
            .OrderBy(x => x.Role).ThenBy(x => x.Size)
            .ToList();

        JacketLines = rows
            .Where(r => r.WantsJacket && !string.IsNullOrWhiteSpace(r.JacketSize))
            .GroupBy(r => r.JacketSize!)
            .Select(g => new JacketLine(g.Key, g.Count()))
            .OrderBy(x => x.Size)
            .ToList();
    }
}
