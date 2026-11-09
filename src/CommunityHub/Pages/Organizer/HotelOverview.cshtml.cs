using System.Globalization;
using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1386 — THE hotel page: readiness, summary tiles (each a link), the per-night table (every number a link),
/// the guest grid (everyone × every night, filterable, exportable), problems, arrivals, per hotel the
/// spreadsheet / import / sent-to-hotel marker, each hotel's nights + deadlines, and the change log.
/// </summary>
/// <remarks>🔒 Nothing on this page sends mail. Organizer-gated on every handler.</remarks>
[Authorize]
public class HotelOverviewModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly HotelOverviewService _overview;
    private readonly HotelAgreementSheet _sheet;
    private readonly HotelSheetImportService _import;
    private readonly HotelPlacementService _placement;
    private readonly HotelChangeLogService _log;
    private readonly HotelManagementService _hotels;
    private readonly TimeProvider _clock;
    private readonly HotelAllotmentService _allotments;

    public HotelOverviewModel(
        ICurrentParticipantAccessor participant, HotelOverviewService overview, HotelAgreementSheet sheet,
        HotelSheetImportService import, HotelPlacementService placement, HotelChangeLogService log,
        HotelManagementService hotels, TimeProvider clock, HotelAllotmentService allotments)
    {
        _participant = participant;
        _overview = overview;
        _sheet = sheet;
        _import = import;
        _placement = placement;
        _log = log;
        _hotels = hotels;
        _clock = clock;
        _allotments = allotments;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public HotelOverview Overview { get; private set; } = new();
    public IReadOnlyList<HotelChangeLogEntry> Log { get; private set; } = Array.Empty<HotelChangeLogEntry>();
    public IReadOnlyList<Hotel> Hotels { get; private set; } = Array.Empty<Hotel>();
    public string? PrimaryHotelName { get; private set; }
    public AllotmentBoard Board { get; private set; } = new();
    public HotelGuestGrid Grid { get; private set; } = new(Array.Empty<DateOnly>(), Array.Empty<HotelGuestGridRow>(), 0);
    public IReadOnlyList<HotelProblem> Problems { get; private set; } = Array.Empty<HotelProblem>();
    public IReadOnlyList<HotelDayMovement> Arrivals { get; private set; } = Array.Empty<HotelDayMovement>();
    public HotelReadiness Readiness { get; private set; } = new(0, 0, 0, new Dictionary<ParticipantRole, int>());
    public Dictionary<int, List<HotelChangeLogEntry>> SinceSent { get; private set; } = new();

    /// <summary>Set after an import upload: what the file would do. Null otherwise.</summary>
    public IReadOnlyList<HotelImportLine>? ImportPreview { get; private set; }
    public int? ImportHotelId { get; private set; }

    // ---- the guest-grid filter (query string) ----------------------------------------------------
    [BindProperty(SupportsGet = true)] public HotelAnswer? Answer { get; set; }
    [BindProperty(SupportsGet = true)] public ParticipantRole? Role { get; set; }
    [BindProperty(SupportsGet = true, Name = "Hotel")] public int? HotelFilter { get; set; }
    [BindProperty(SupportsGet = true)] public HotelConfirmationState? State { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Night { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }

    public HotelGridFilter Filter => new(Answer, Role, HotelFilter, State, Night, Q, Sort);
    public bool Filtered => Answer is not null || Role is not null || HotelFilter is not null || State is not null || Night is not null || !string.IsNullOrWhiteSpace(Q);

    public async Task<IActionResult> OnGetAsync(string? msg, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) { AccessDenied = true; return Page(); }
        Message = msg;
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Download one hotel's Excel 97-2003 spreadsheet.</summary>
    public async Task<IActionResult> OnGetDownloadAsync(int hotelId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) return Forbid();
        var file = await _sheet.BuildAsync(me.EventId, hotelId, ct);
        if (file is null) return NotFound();
        return File(file.Content, HotelSheetFile.ContentType, file.FileName);
    }

    /// <summary>The guest grid, exactly as filtered, as .xlsx (one column per night).</summary>
    public async Task<IActionResult> OnGetGridExportAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) return Forbid();
        var grid = await _overview.GuestGridAsync(me.EventId, Filter, ct);

        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.Worksheets.Add("Hotel guests");
        var headers = new List<string> { "Name", "Email", "Role", "Hotel answer", "Hotel", "State", "Arrival", "Departure",
            "Roommate", "Guests in room", "Confirmation number" };
        headers.AddRange(grid.NightColumns.Select(n => n.ToString("ddd d MMM", CultureInfo.InvariantCulture)));
        for (var i = 0; i < headers.Count; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Range(1, 1, 1, headers.Count).Style.Font.Bold = true;
        var r = 2;
        foreach (var g in grid.Rows)
        {
            ws.Cell(r, 1).Value = g.FullName;
            ws.Cell(r, 2).Value = g.Email;
            ws.Cell(r, 3).Value = g.Role.ToString();
            ws.Cell(r, 4).Value = HotelAnswerText.Of(g.Answer);
            ws.Cell(r, 5).Value = g.HotelName ?? "";
            ws.Cell(r, 6).Value = g.Answer == HotelAnswer.Room && g.State is HotelConfirmationState s ? HotelReservationStateText.Of(s) : "";
            ws.Cell(r, 7).Value = g.CheckIn?.ToString("yyyy-MM-dd") ?? "";
            ws.Cell(r, 8).Value = g.CheckOut?.ToString("yyyy-MM-dd") ?? "";
            ws.Cell(r, 9).Value = g.RoommateName ?? "";
            if (g.GuestsInRoom is int gir) ws.Cell(r, 10).Value = gir;
            ws.Cell(r, 11).Value = g.ConfirmationNumber ?? "";
            for (var i = 0; i < grid.NightColumns.Count; i++)
                if (g.Nights.Contains(grid.NightColumns[i])) ws.Cell(r, 12 + i).Value = 1;
            r++;
        }
        // Totals under the night columns, so the sheet reads like the per-night table.
        ws.Cell(r, 1).Value = "People per night";
        ws.Cell(r, 1).Style.Font.Bold = true;
        for (var i = 0; i < grid.NightColumns.Count; i++)
            ws.Cell(r, 12 + i).Value = grid.Rows.Count(g => g.Nights.Contains(grid.NightColumns[i]));
        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
        ws.SheetView.FreezeColumns(1);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "hotel-guests.xlsx");
    }

    public async Task<IActionResult> OnPostPlaceUnassignedAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var n = await _placement.PlaceUnassignedInPrimaryAsync(me.EventId, me.Email, ct);
        return RedirectToPage(new { msg = n == 0
            ? "Nobody was waiting for a hotel."
            : $"Placed {n} reserved guest(s) in the priority-1 hotel. No mail was sent to them." });
    }

    /// <summary>"Mark as sent to the hotel" — the organizer forwarded the current spreadsheet.</summary>
    public async Task<IActionResult> OnPostMarkSentAsync(int hotelId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var ok = await _hotels.MarkSentToHotelAsync(me.EventId, hotelId, me.Email, ct);
        return RedirectToPage(new { msg = ok ? "Marked as sent to the hotel. New changes will be counted from now." : "Hotel not found." });
    }

    /// <summary>Save the forecast extras: nights[i] / extras[i] / notes[i].</summary>
    public async Task<IActionResult> OnPostSaveForecastAsync(
        string[] nights, string?[] extras, string?[] notes, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var changed = 0;
        for (var i = 0; i < nights.Length; i++)
        {
            if (!DateOnly.TryParse(nights[i], CultureInfo.InvariantCulture, out var night)) continue;
            int? extra = i < extras.Length && int.TryParse(extras[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : null;
            var note = i < notes.Length ? notes[i] : null;
            if (await _overview.SetForecastAsync(me.EventId, night, extra, note, me.Email, _log, _clock, ct)) changed++;
        }
        return RedirectToPage(new { msg = changed == 0 ? "No forecast changes." : $"Forecast saved for {changed} night(s)." });
    }

    /// <summary>Add a forecast for a night that is not in the table yet.</summary>
    public async Task<IActionResult> OnPostAddForecastAsync(DateOnly night, int extra, string? note, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        await _overview.SetForecastAsync(me.EventId, night, extra, note, me.Email, _log, _clock, ct);
        return RedirectToPage(new { msg = $"Forecast for {night.ToString("d MMM", CultureInfo.InvariantCulture)} saved." });
    }

    /// <summary>Step 1 of the import: read the hotel's file and show what it would change. Writes nothing.</summary>
    public async Task<IActionResult> OnPostImportPreviewAsync(int hotelId, IFormFile? file, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (file is null || file.Length == 0) Error = "Choose the spreadsheet the hotel sent back (.xls or .xlsx).";
        else if (file.Length > 10 * 1024 * 1024) Error = "That file is larger than 10 MB — is it the right one?";
        else
        {
            try
            {
                await using var s = file.OpenReadStream();
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms, ct);
                ms.Position = 0;
                var rows = HotelAgreementSheet.Parse(ms);
                ImportPreview = await _import.PreviewAsync(me.EventId, hotelId, rows, ct);
                ImportHotelId = hotelId;
                if (ImportPreview.Count == 0) Error = "The file has a header row but no guest rows.";
            }
            catch (InvalidDataException ex)
            {
                Error = ex.Message;
            }
        }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Step 2: apply the rows the organizer kept ticked. Sends no mail.</summary>
    public async Task<IActionResult> OnPostImportApplyAsync(
        int hotelId, int[] pids, string?[] confs, string?[] cancels, int[] selected, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var pick = selected.ToHashSet();
        var approvals = new List<HotelImportApproval>();
        for (var i = 0; i < pids.Length; i++)
        {
            if (!pick.Contains(pids[i])) continue;
            approvals.Add(new HotelImportApproval(pids[i],
                i < confs.Length ? confs[i] : null, i < cancels.Length ? cancels[i] : null));
        }
        var n = await _import.ApplyAsync(me.EventId, hotelId, approvals, me.Email, ct);
        return RedirectToPage(new { msg = $"Imported {n} reservation(s) from the hotel's spreadsheet. Guests see their number on their hotel page; no mail was sent." });
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Overview = await _overview.BuildAsync(eventId, includeNotAnswered: true, ct);
        Log = await _log.ListAsync(eventId, take: 300, ct: ct);
        Hotels = await _hotels.ListHotelsAsync(eventId, ct);
        PrimaryHotelName = Hotels.OrderBy(h => h.Priority).ThenBy(h => h.Id).FirstOrDefault()?.Name;
        Board = await _allotments.BuildAsync(eventId, ct);
        Grid = await _overview.GuestGridAsync(eventId, Filter, ct);
        Problems = await _overview.ProblemsAsync(eventId, ct);
        Arrivals = await _overview.ArrivalsAsync(eventId, ct);
        Readiness = new HotelReadiness(Overview.NotAnswered.Count, Overview.UnplacedPeople, Problems.Count,
            Overview.NotAnswered.GroupBy(p => p.Role).ToDictionary(g => g.Key, g => g.Count()));
        foreach (var h in Hotels) SinceSent[h.Id] = await _hotels.ChangesSinceSentAsync(eventId, h.Id, ct);
    }

    public string HotelName(int? id) =>
        id is int i ? Hotels.FirstOrDefault(h => h.Id == i)?.Name ?? $"#{i}" : "—";

    /// <summary>A link to this page with the grid filtered; null arguments are left out.</summary>
    public string GridUrl(HotelAnswer? answer = null, ParticipantRole? role = null, int? hotel = null,
        HotelConfirmationState? state = null, DateOnly? night = null)
    {
        var q = new List<string>();
        if (answer is not null) q.Add($"Answer={answer}");
        if (role is not null) q.Add($"Role={role}");
        if (hotel is not null) q.Add($"Hotel={hotel}");
        if (state is not null) q.Add($"State={state}");
        if (night is DateOnly n) q.Add($"Night={n:yyyy-MM-dd}");
        return Url.Page("/Organizer/HotelOverview") + (q.Count == 0 ? "" : "?" + string.Join("&", q)) + "#guests";
    }

    /// <summary>
    /// §1386 — the operator's colour rule (2026-11-08): <i>"if we have too many that is GREEN, but we still need to
    /// reduce allocation number with x (yellow, separate number). but if we have too little, then it is RED"</i>.
    /// Covered (allocated ≥ needed) ⇒ the cell is GREEN; a surplus adds a SEPARATE yellow "release N" badge;
    /// a gap ⇒ RED "N short".
    /// </summary>
    public static string ResultStyle(int result) =>
        result < 0 ? "background:#fee2e2;color:#991b1b;font-weight:700;"
        : "background:#dcfce7;color:#166534;font-weight:700;";

    public static IHtmlContent ResultHtml(int result) =>
        new HtmlString(result < 0
            ? $"{-result} short"
            : result == 0
                ? "OK"
                : "OK <span style=\"display:inline-block;margin-left:6px;background:#fef3c7;color:#92400e;border:1px solid #fcd34d;"
                  + $"border-radius:10px;padding:1px 8px;font-size:12px;font-weight:700;white-space:nowrap;\">release {result}</span>");
}
