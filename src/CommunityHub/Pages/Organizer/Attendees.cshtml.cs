using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
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

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadSummaryAsync(me.EventId, ct);
        Attendees = await BuildQuery(me.EventId)
            .OrderBy(a => a.LastName).ThenBy(a => a.FirstName)
            .ToListAsync(ct);
        return Page();
    }

    /// <summary>CSV export of the CURRENT filter selection (not always-all,
    /// so an organizer can export e.g. just the mismatches).</summary>
    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) return Forbid();

        var rows = await BuildQuery(me.EventId)
            .OrderBy(a => a.LastName).ThenBy(a => a.FirstName)
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("FirstName;LastName;Email;TicketStatus;TicketClass;BookingStatus;MasterClass;Mismatch;LastSyncedUtc");
        foreach (var a in rows)
        {
            sb.AppendLine(string.Join(';',
                Csv(a.FirstName), Csv(a.LastName), Csv(a.Email),
                a.TicketStatus, Csv(a.TicketClassName),
                a.BookingStatus, Csv(a.MasterClassName),
                a.HasReconciliationMismatch ? "YES" : "",
                a.LastSyncedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm")));
        }

        // UTF-8 BOM so Excel detects the encoding (Danish names).
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", "attendees.csv");
    }

    private IQueryable<Core.Domain.Attendee> BuildQuery(int eventId)
    {
        var q = _db.Attendees.Where(a => a.EventId == eventId);

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
        var all = _db.Attendees.Where(a => a.EventId == eventId);
        TotalCount    = await all.CountAsync(ct);
        TwoDayCount   = await all.CountAsync(a => a.TicketStatus == TicketStatus.TwoDay, ct);
        BookedCount   = await all.CountAsync(a => a.BookingStatus != MasterClassBookingStatus.NotBooked, ct);
        MismatchCount = await all.CountAsync(a => a.HasReconciliationMismatch, ct);
        LastSyncedAt  = TotalCount == 0
            ? null
            : await all.MaxAsync(a => (DateTimeOffset?)a.LastSyncedAt, ct);
    }

    /// <summary>Semicolon-separated CSV field: quote when needed.</summary>
    private static string Csv(string? v)
    {
        if (string.IsNullOrEmpty(v)) return string.Empty;
        return v.Contains(';') || v.Contains('"') || v.Contains('\n')
            ? '"' + v.Replace("\"", "\"\"") + '"'
            : v;
    }
}
