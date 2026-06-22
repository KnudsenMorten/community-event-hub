using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// The organizer "Exports &amp; run-sheets" hub (REQUIREMENTS §20 Organizer —
/// "Exports &amp; printable run-sheets"). On-site operations still run on paper /
/// offline artifacts, so this page gives downloadable CSV exports AND
/// print-friendly run-sheet views (browser-print / PDF-via-print-CSS) for the
/// attendee list, lunch headcount, room/session sheets, the volunteer rota and
/// badge data.
///
/// Every projection is a pure, read-only, edition-scoped view built by
/// <see cref="OrganizerExportsService"/> — exports of EXISTING data only. It is
/// NOT an event check-in / live-headcount tool (out of scope §20): the lunch
/// numbers are the preference already collected and the room QR token is merely
/// rendered. Organizer-gated server-side; the page never writes.
/// </summary>
[Authorize]
public class ExportsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly OrganizerExportsService _exports;

    public ExportsModel(
        ICurrentParticipantAccessor participant,
        OrganizerExportsService exports)
    {
        _participant = participant;
        _exports = exports;
    }

    public bool AccessDenied { get; private set; }

    // --- Run-sheet rows for the print-friendly screen view ------------------
    public IReadOnlyList<AttendeeListRow> Attendees { get; private set; } = Array.Empty<AttendeeListRow>();
    public IReadOnlyList<LunchHeadcountRow> LunchHeadcount { get; private set; } = Array.Empty<LunchHeadcountRow>();
    public IReadOnlyList<LunchPersonRow> LunchPeople { get; private set; } = Array.Empty<LunchPersonRow>();
    public IReadOnlyList<RoomSheetRow> RoomSheets { get; private set; } = Array.Empty<RoomSheetRow>();
    public IReadOnlyList<VolunteerRotaRow> VolunteerRota { get; private set; } = Array.Empty<VolunteerRotaRow>();
    public IReadOnlyList<BadgeRow> BadgeData { get; private set; } = Array.Empty<BadgeRow>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Attendees = await _exports.BuildAttendeeListAsync(me.EventId, ct);
        LunchHeadcount = await _exports.BuildLunchHeadcountAsync(me.EventId, ct);
        LunchPeople = await _exports.BuildLunchPeopleAsync(me.EventId, ct);
        RoomSheets = await _exports.BuildRoomSheetsAsync(me.EventId, ct);
        VolunteerRota = await _exports.BuildVolunteerRotaAsync(me.EventId, ct);
        BadgeData = await _exports.BuildBadgeDataAsync(me.EventId, ct);
        return Page();
    }

    // --- CSV download handlers (one per artifact) ---------------------------
    // Each re-checks the organizer gate, builds the same projection the screen
    // shows, and streams it as a UTF-8 (BOM) CSV so Excel detects Danish names.

    public Task<IActionResult> OnGetAttendeesCsvAsync(CancellationToken ct)
        => CsvAsync(id => _exports.BuildAttendeeListCsvAsync(id, ct), "attendee-list.csv");

    public Task<IActionResult> OnGetLunchCsvAsync(CancellationToken ct)
        => CsvAsync(id => _exports.BuildLunchCsvAsync(id, ct), "lunch-headcount.csv");

    public Task<IActionResult> OnGetRoomsCsvAsync(CancellationToken ct)
        => CsvAsync(id => _exports.BuildRoomSheetsCsvAsync(id, ct), "room-sheets.csv");

    public Task<IActionResult> OnGetVolunteerRotaCsvAsync(CancellationToken ct)
        => CsvAsync(id => _exports.BuildVolunteerRotaCsvAsync(id, ct), "volunteer-rota.csv");

    public Task<IActionResult> OnGetBadgesCsvAsync(CancellationToken ct)
        => CsvAsync(id => _exports.BuildBadgeDataCsvAsync(id, ct), "badge-data.csv");

    // --- Excel (.xlsx) download handlers ------------------------------------
    // Each reuses the exact same CSV builder the CSV handler uses (single source
    // of truth for the data), then converts via CsvToXlsx — the data is never
    // re-derived. Sheet name comes from the export's name.

    public Task<IActionResult> OnGetAttendeesXlsxAsync(CancellationToken ct)
        => XlsxAsync(id => _exports.BuildAttendeeListCsvAsync(id, ct), "attendee-list.xlsx", "Attendees");

    public Task<IActionResult> OnGetLunchXlsxAsync(CancellationToken ct)
        => XlsxAsync(id => _exports.BuildLunchCsvAsync(id, ct), "lunch-headcount.xlsx", "Lunch headcount");

    public Task<IActionResult> OnGetRoomsXlsxAsync(CancellationToken ct)
        => XlsxAsync(id => _exports.BuildRoomSheetsCsvAsync(id, ct), "room-sheets.xlsx", "Room sheets");

    public Task<IActionResult> OnGetVolunteerRotaXlsxAsync(CancellationToken ct)
        => XlsxAsync(id => _exports.BuildVolunteerRotaCsvAsync(id, ct), "volunteer-rota.xlsx", "Volunteer rota");

    public Task<IActionResult> OnGetBadgesXlsxAsync(CancellationToken ct)
        => XlsxAsync(id => _exports.BuildBadgeDataCsvAsync(id, ct), "badge-data.xlsx", "Badge data");

    private async Task<IActionResult> CsvAsync(Func<int, Task<string>> build, string fileName)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var csv = await build(me.EventId);
        // UTF-8 BOM so Excel detects the encoding (Danish names).
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return File(bytes, "text/csv", fileName);
    }

    private async Task<IActionResult> XlsxAsync(Func<int, Task<string>> build, string fileName, string sheetName)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        // Same CSV builder as the CSV handler — data is not re-derived.
        var csv = await build(me.EventId);
        return File(CsvToXlsx.Build(csv, sheetName), CsvToXlsx.ContentType, fileName);
    }
}
