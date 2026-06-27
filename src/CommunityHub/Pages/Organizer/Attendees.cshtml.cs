using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Full attendee browser for organizers. The organizer Index card only shows
/// reconciliation MISMATCHES; this page shows the whole reconciled attendee
/// set (synced nightly from Zoho Backstage + Bookings) with filters, search
/// and a CSV export for on-site lists / BI. Read-only by design: attendees
/// are owned by the reconcile job, never edited in the hub (CONTEXT.md 9z).
/// </summary>
[Authorize]
public class AttendeesModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;

    public AttendeesModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant)
    {
        _db = db;
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }
    public List<Core.Domain.Attendee> Attendees { get; private set; } = new();

    // --- Summary tiles --------------------------------------------------
    public int TotalCount { get; private set; }
    public int TwoDayCount { get; private set; }
    public int BookedCount { get; private set; }
    public int MismatchCount { get; private set; }
    public DateTimeOffset? LastSyncedAt { get; private set; }

    // --- Filters (GET-bound so links/bookmarks keep state) ---------------
    [BindProperty(SupportsGet = true)] public string? Ticket { get; set; }
    [BindProperty(SupportsGet = true)] public string? Booking { get; set; }
    [BindProperty(SupportsGet = true)] public bool MismatchOnly { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }

    // --- Sort + paging (GET-bound) ---------------------------------------
    /// <summary>Sort column key: name | email | ticket | booking. Default name.</summary>
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "name";
    /// <summary>true = descending. Default false (A→Z).</summary>
    [BindProperty(SupportsGet = true)] public bool Desc { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNo { get; set; } = 1;

    public GridPage Paging { get; private set; }

    /// <summary>Direction the link for <paramref name="col"/> should request:
    /// flip when this column is already the active ascending sort, else ascending.</summary>
    public bool NextDescFor(string col) => Sort == col && !Desc;

    /// <summary>Little ▲/▼ glyph appended to the active sort header.</summary>
    public string SortIndicator(string col) => Sort != col ? "" : (Desc ? " ▼" : " ▲");

    /// <summary>ARIA <c>aria-sort</c> value for a column header.</summary>
    public string AriaSort(string col) => Sort != col ? "none" : (Desc ? "descending" : "ascending");

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadSummaryAsync(me.EventId, ct);

        var filtered = BuildQuery(me.EventId);
        var matched = await filtered.CountAsync(ct);
        Paging = GridPaging.Resolve(PageNo, GridPaging.DefaultPageSize, matched);

        Attendees = await ApplySort(filtered)
            .Skip(Paging.Skip).Take(Paging.PageSize)
            .ToListAsync(ct);
        return Page();
    }

    /// <summary>Stable, deterministic ordering for the chosen sort column.</summary>
    private IQueryable<Core.Domain.Attendee> ApplySort(IQueryable<Core.Domain.Attendee> q)
    {
        IOrderedQueryable<Core.Domain.Attendee> ordered = Sort switch
        {
            "email"   => Desc ? q.OrderByDescending(a => a.Email)         : q.OrderBy(a => a.Email),
            "ticket"  => Desc ? q.OrderByDescending(a => a.TicketStatus)  : q.OrderBy(a => a.TicketStatus),
            "booking" => Desc ? q.OrderByDescending(a => a.BookingStatus) : q.OrderBy(a => a.BookingStatus),
            _         => Desc ? q.OrderByDescending(a => a.LastName)      : q.OrderBy(a => a.LastName),
        };
        // Id tiebreak so paging is deterministic across calls.
        return ordered.ThenBy(a => a.Id);
    }

    /// <summary>CSV export of the CURRENT filter selection (not always-all,
    /// so an organizer can export e.g. just the mismatches).</summary>
    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var csv = await BuildExportCsvAsync(me.EventId, ';', ct);

        // UTF-8 BOM so Excel detects the encoding (Danish names).
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return File(bytes, "text/csv", "attendees.csv");
    }

    /// <summary>Same export as <see cref="OnGetExportAsync"/> (current filter)
    /// but delivered as a native Excel (.xlsx) workbook. Reuses the EXACT same row
    /// builder (<see cref="BuildExportCsvAsync"/>) so columns and rows are identical;
    /// only the file format differs. NOTE: the on-screen CSV download is
    /// semicolon-delimited (an existing download contract we must not change), but
    /// <see cref="CsvToXlsx"/> parses comma-delimited RFC-4180 — so the workbook is
    /// fed a comma-delimited rendering of the SAME rows/columns/values.</summary>
    public async Task<IActionResult> OnGetExportXlsxAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var csv = await BuildExportCsvAsync(me.EventId, ',', ct);
        return File(CsvToXlsx.Build(csv, "Attendees"), CsvToXlsx.ContentType, "attendees.xlsx");
    }

    /// <summary>Builds the attendee export (header + one line per row) for the
    /// CURRENT filter selection. Single source of truth shared by the CSV and the
    /// XLSX download handlers so both contain identical columns/rows. The delimiter
    /// is a parameter so the CSV handler keeps its semicolon contract while the
    /// xlsx feed uses commas (what <see cref="CsvToXlsx"/> parses into columns);
    /// fields are escaped against whichever delimiter is in use.</summary>
    private async Task<string> BuildExportCsvAsync(int eventId, char delimiter, CancellationToken ct)
    {
        var rows = await BuildQuery(eventId)
            .OrderBy(a => a.LastName).ThenBy(a => a.FirstName)
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(delimiter,
            "FirstName", "LastName", "Email", "TicketStatus", "TicketClass",
            "BookingStatus", "MasterClass", "Mismatch", "LastSyncedUtc"));
        foreach (var a in rows)
        {
            sb.AppendLine(string.Join(delimiter,
                Csv(a.FirstName, delimiter), Csv(a.LastName, delimiter), Csv(a.Email, delimiter),
                a.TicketStatus, Csv(a.TicketClassName, delimiter),
                a.BookingStatus, Csv(a.MasterClassName, delimiter),
                a.HasReconciliationMismatch ? "YES" : "",
                a.LastSyncedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm")));
        }
        return sb.ToString();
    }

    private IQueryable<Core.Domain.Attendee> BuildQuery(int eventId)
    {
        // The ACTIVE mirror set only (§128): soft-cancelled rows are kept for history
        // but excluded from the organizer attendee view + counts (CEH == Zoho active set).
        var q = _db.Attendees.Where(a => a.EventId == eventId && a.MirrorState == MirrorState.Active);

        if (Enum.TryParse<TicketStatus>(Ticket, out var ts))
            q = q.Where(a => a.TicketStatus == ts);
        if (Enum.TryParse<MasterClassBookingStatus>(Booking, out var bs))
            q = q.Where(a => a.BookingStatus == bs);
        if (MismatchOnly)
            q = q.Where(a => a.HasReconciliationMismatch);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(a => a.Email.Contains(s)
                             || a.FirstName.Contains(s)
                             || a.LastName.Contains(s)
                             || (a.MasterClassName != null && a.MasterClassName.Contains(s)));
        }
        return q;
    }

    private async Task LoadSummaryAsync(int eventId, CancellationToken ct)
    {
        var all = _db.Attendees.Where(a => a.EventId == eventId && a.MirrorState == MirrorState.Active);
        TotalCount    = await all.CountAsync(ct);
        TwoDayCount   = await all.CountAsync(a => a.TicketStatus == TicketStatus.TwoDay, ct);
        BookedCount   = await all.CountAsync(a => a.BookingStatus != MasterClassBookingStatus.NotBooked, ct);
        MismatchCount = await all.CountAsync(a => a.HasReconciliationMismatch, ct);
        LastSyncedAt  = TotalCount == 0
            ? null
            : await all.MaxAsync(a => (DateTimeOffset?)a.LastSyncedAt, ct);
    }

    /// <summary>CSV field for the given <paramref name="delimiter"/>: quote when the
    /// value contains the delimiter, a quote or a newline. Defaults to the original
    /// semicolon so the on-screen CSV download stays byte-identical.</summary>
    private static string Csv(string? v, char delimiter = ';')
    {
        if (string.IsNullOrEmpty(v)) return string.Empty;
        return v.Contains(delimiter) || v.Contains('"') || v.Contains('\n')
            ? '"' + v.Replace("\"", "\"\"") + '"'
            : v;
    }
}
