using ClosedXML.Excel;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
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
    public int GiftYesCount { get; private set; }
    public int CredlyYesCount { get; private set; }

    /// <summary>(Role, Size) → count, polo only, "yes" rows only. Sorted by role then size.</summary>
    public IReadOnlyList<PoloLine> PoloLines { get; private set; } = Array.Empty<PoloLine>();

    public record PoloLine(string Role, string Size, int Count);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadAggregatesAsync(me.EventId, ct);
        return Page();
    }

    // §326bu (operator 2026-07-25: "download excel must be 1 file per polo, award,
    // credly. it must also contain the name of the person (critical to handout, send
    // to people)"). Three separate downloads, each PER PERSON and named.
    //
    // The old single workbook aggregated polos to (Role × Size) counts with no names at
    // all — fine for placing a vendor order, useless on the day, when someone has to
    // hand a specific shirt to a specific person. Each file now leads with the person
    // list; the polo file keeps the size totals as a SECOND sheet so the vendor order
    // is not lost. Jackets are gone entirely (§326bt).

    public Task<IActionResult> OnGetPoloXlsxAsync(CancellationToken ct) =>
        BuildAsync("polo", ct);

    public Task<IActionResult> OnGetAwardXlsxAsync(CancellationToken ct) =>
        BuildAsync("award", ct);

    public Task<IActionResult> OnGetCredlyXlsxAsync(CancellationToken ct) =>
        BuildAsync("credly", ct);

    private async Task<IActionResult> BuildAsync(string kind, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // ACTIVE people only (§253 G5): these sheets drive what is ORDERED and what is
        // physically handed over — a drop-out's row would be paid for and printed.
        var rows = await _db.SwagPreferences
            .Where(s => s.EventId == me.EventId)
            .Join(_db.Participants, s => s.ParticipantId, p => p.Id, (s, p) => new
            {
                p.Id, p.FullName, p.Email, p.Role, p.IsActive,
                s.WantsPolo, s.PoloSize, s.WantsGift, s.WantsCredlyBadge, s.Notes,
            })
            .Where(x => x.IsActive)
            .ToListAsync(ct);

        var (categoryByPid, daysBySpeaker) = await LoadSpeakerPoloInputsAsync(me.EventId, ct);

        using var wb = new XLWorkbook();
        string fileStem;

        if (kind == "polo")
        {
            fileStem = "polo-order";
            var people = rows
                .Where(r => r.WantsPolo && !string.IsNullOrWhiteSpace(r.PoloSize))
                .OrderBy(r => r.Role.ToString()).ThenBy(r => r.FullName)
                .ToList();

            // Sheet 1: WHO gets what — the hand-out list.
            var ws = wb.Worksheets.Add("Polo per person");
            WriteHeader(ws, "Name", "Email", "Role", "Size", "Polos");
            var row = 2;
            foreach (var r in people)
            {
                ws.Cell(row, 1).Value = r.FullName;
                ws.Cell(row, 2).Value = r.Email;
                ws.Cell(row, 3).Value = r.Role.ToString();
                ws.Cell(row, 4).Value = r.PoloSize;
                // §299 6.3: a master-class speaker is funded for 2 (pre-day + main day).
                ws.Cell(row, 5).Value = PoloCount(r.Role, r.Id, categoryByPid, daysBySpeaker);
                row++;
            }
            Total(ws, people.Count, row, labelCol: 1, sumCol: 5);
            ws.Columns().AdjustToContents();
            ws.SheetView.FreezeRows(1);

            // Sheet 2: the vendor order — same numbers, rolled up by role + size.
            var agg = wb.Worksheets.Add("Size totals");
            WriteHeader(agg, "Role", "Size", "Polo_Total");
            var lines = people
                .GroupBy(r => new { Role = r.Role.ToString(), Size = r.PoloSize! })
                .Select(g => new { g.Key.Role, g.Key.Size, Count = g.Sum(r => PoloCount(r.Role, r.Id, categoryByPid, daysBySpeaker)) })
                .OrderBy(x => x.Role).ThenBy(x => x.Size)
                .ToList();
            var arow = 2;
            foreach (var l in lines)
            {
                agg.Cell(arow, 1).Value = l.Role;
                agg.Cell(arow, 2).Value = l.Size;
                agg.Cell(arow, 3).Value = l.Count;
                arow++;
            }
            Total(agg, lines.Count, arow, labelCol: 1, sumCol: 3);
            agg.Columns().AdjustToContents();
        }
        else if (kind == "award")
        {
            fileStem = "award-order";
            var people = rows
                .Where(r => r.WantsGift)
                .OrderBy(r => r.Role.ToString()).ThenBy(r => r.FullName)
                .ToList();

            var ws = wb.Worksheets.Add("Award per person");
            WriteHeader(ws, "Name", "Email", "Role", "Awards");
            var row = 2;
            foreach (var r in people)
            {
                ws.Cell(row, 1).Value = r.FullName;
                ws.Cell(row, 2).Value = r.Email;
                ws.Cell(row, 3).Value = r.Role.ToString();
                ws.Cell(row, 4).Value = 1;
                row++;
            }
            Total(ws, people.Count, row, labelCol: 1, sumCol: 4);
            ws.Columns().AdjustToContents();
            ws.SheetView.FreezeRows(1);
        }
        else
        {
            fileStem = "credly-badges";
            var people = rows
                .Where(r => r.WantsCredlyBadge)
                .OrderBy(r => r.Role.ToString()).ThenBy(r => r.FullName)
                .ToList();

            // Credly badges are ISSUED to an address, so email is the operative column.
            var ws = wb.Worksheets.Add("Credly per person");
            WriteHeader(ws, "Name", "Email", "Role", "Badges");
            var row = 2;
            foreach (var r in people)
            {
                ws.Cell(row, 1).Value = r.FullName;
                ws.Cell(row, 2).Value = r.Email;
                ws.Cell(row, 3).Value = r.Role.ToString();
                ws.Cell(row, 4).Value = 1;
                row++;
            }
            Total(ws, people.Count, row, labelCol: 1, sumCol: 4);
            ws.Columns().AdjustToContents();
            ws.SheetView.FreezeRows(1);
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(
            ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"{fileStem}-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx");
    }

    private static void WriteHeader(IXLWorksheet ws, params string[] headers)
    {
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Range(1, 1, 1, headers.Length).Style.Font.Bold = true;
    }

    /// <summary>Bold grand-total row, written only when there is something to total.</summary>
    private static void Total(IXLWorksheet ws, int dataCount, int row, int labelCol, int sumCol)
    {
        if (dataCount == 0) return;
        ws.Cell(row, labelCol).Value = "Grand Total";
        ws.Cell(row, sumCol).FormulaA1 =
            $"SUM({ws.Cell(2, sumCol).Address.ColumnLetter}2:{ws.Cell(row - 1, sumCol).Address.ColumnLetter}{row - 1})";
        ws.Range(row, labelCol, row, sumCol).Style.Font.Bold = true;
    }

    private async Task LoadAggregatesAsync(int eventId, CancellationToken ct)
    {
        // ACTIVE people only (§253 G5) — the on-screen aggregates must equal the
        // vendor sheets built in OnGetDownloadAsync.
        var rows = await _db.SwagPreferences
            .Where(s => s.EventId == eventId)
            .Join(_db.Participants, s => s.ParticipantId, p => p.Id, (s, p) => new
            {
                p.Id, p.Role, p.IsActive,
                s.WantsPolo, s.PoloSize,

                s.WantsGift, s.WantsCredlyBadge,
            })
            .Where(x => x.IsActive)
            .ToListAsync(ct);

        // §299 6.3 — same category + derived-days polo rule the vendor sheet uses, so
        // the on-screen preview totals equal the downloaded Polo_Total.
        var (categoryByPid, daysBySpeaker) = await LoadSpeakerPoloInputsAsync(eventId, ct);

        TotalSubmissions = rows.Count;
        PoloYesCount   = rows.Count(r => r.WantsPolo);
        GiftYesCount   = rows.Count(r => r.WantsGift);
        CredlyYesCount = rows.Count(r => r.WantsCredlyBadge);

        PoloLines = rows
            .Where(r => r.WantsPolo && !string.IsNullOrWhiteSpace(r.PoloSize))
            .GroupBy(r => new { Role = r.Role.ToString(), Size = r.PoloSize! })
            .Select(g => new PoloLine(g.Key.Role, g.Key.Size, g.Sum(r => PoloCount(r.Role, r.Id, categoryByPid, daysBySpeaker))))
            .OrderBy(x => x.Role).ThenBy(x => x.Size)
            .ToList();

        // §326bt: jackets are no longer offered in the portal, so there is no jacket
        // preview. The stored columns are untouched — whatever was collected before the
        // change is still in the database.
    }

    /// <summary>
    /// §299 6.3 polo inputs: each speaker profile's organizer-set category
    /// (participant id → category, null = uncategorized) and the DERIVED
    /// presenting days from linked sessions (<see cref="SpeakerDayScope"/>).
    /// </summary>
    private async Task<(IReadOnlyDictionary<int, SpeakerCategory?> CategoryByPid,
        IReadOnlyDictionary<int, SpeakerDays> DaysBySpeaker)> LoadSpeakerPoloInputsAsync(
        int eventId, CancellationToken ct)
    {
        var categoryByPid = await _db.SpeakerProfiles
            .Where(s => s.EventId == eventId)
            .ToDictionaryAsync(s => s.ParticipantId, s => s.Category, ct);
        var daysBySpeaker = await SpeakerDayScope.DaysBySpeakerAsync(_db, eventId, ct);
        return (categoryByPid, daysBySpeaker);
    }

    /// <summary>
    /// §299 6.3 — polo count for one "wants polo" row. A person WITHOUT a speaker
    /// hat keeps their single role-hat polo (organizer / volunteer / booth member /
    /// media / partner). A person WITH a speaker hat gets the funded speaker count:
    /// Community/Guest = one per DISTINCT presenting day (0 with no linked
    /// sessions); Sponsor-category or uncategorized = 0 from the speaker hat — a
    /// pure Speaker row is therefore EXCLUDED from the tally, while a non-Speaker
    /// role who also speaks never drops below their single role-hat polo.
    /// </summary>
    /// <summary>§299 OPEN-14 (operator 2026-07-23): every ORGANIZER gets 5 polos —
    /// a flat per-organizer quantity that supersedes any speaker-hat day count.
    /// Candidate for the per-edition config block arriving with batch 3.</summary>
    internal const int OrganizerPoloCount = 5;

    private static int PoloCount(
        ParticipantRole role, int participantId,
        IReadOnlyDictionary<int, SpeakerCategory?> categoryByPid,
        IReadOnlyDictionary<int, SpeakerDays> daysBySpeaker)
    {
        // OPEN-14: organizers are a flat ×5, whether or not they also speak.
        if (role == ParticipantRole.Organizer) return OrganizerPoloCount;

        if (!categoryByPid.TryGetValue(participantId, out var category))
            return 1; // no speaker hat — one role-hat polo

        var days = daysBySpeaker.TryGetValue(participantId, out var d) ? d : SpeakerDays.None;
        var funded = SpeakerDayScope.FundedPolos(category, days);
        return role == ParticipantRole.Speaker ? funded : Math.Max(1, funded);
    }
}
