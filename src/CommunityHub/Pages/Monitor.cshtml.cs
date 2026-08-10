using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

/// <summary>
/// §1040 — THE SHARED PAGE: who has bought a ticket, for the company that bought the block.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10: <i>"i can send to persons where they can follow who bought a ticket"</i>.
/// No login — the URL is the credential.</para>
///
/// <para>🔴 <b>This is the one page in the hub that shows real people's names and e-mail addresses to
/// an anonymous caller.</b> Four things make that defensible, and all four are load-bearing:</para>
/// <list type="number">
///   <item>the token is 256 bits of cryptographic randomness — the URL cannot be guessed;</item>
///   <item>a monitor scopes to ONE domain or ONE coupon, so a forwarded link cannot widen;</item>
///   <item>it is revocable, and it EXPIRES by itself (15 Feb 2027 by his instruction);</item>
///   <item>it is <b>read-only</b> — there is no action on this page at all.</item>
/// </list>
///
/// <para>🔒 <c>noindex</c> is set in the view: a link pasted into a public thread must not become a
/// search result. That is defence against the accident, not against an attacker.</para>
/// </remarks>
[AllowAnonymous]
public class MonitorModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly AttendeeMonitorQuery _query;
    private readonly TimeProvider _clock;

    public MonitorModel(CommunityHubDbContext db, AttendeeMonitorQuery query, TimeProvider clock)
    {
        _db = db;
        _query = query;
        _clock = clock;
    }

    public AttendeeMonitor? Monitor { get; private set; }
    public IReadOnlyList<MonitoredAttendee> Rows { get; private set; } = Array.Empty<MonitoredAttendee>();
    public string? EventName { get; private set; }

    public async Task<IActionResult> OnGetAsync(string token, CancellationToken ct)
    {
        var monitor = await ResolveAsync(token, ct);
        if (monitor is null) return NotFound();

        // §1040 — record the read so an organizer can see whether a link is in use before revoking
        // it, and notice one being opened that should not be.
        monitor.ViewCount++;
        monitor.LastViewedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        Monitor = monitor;
        Rows = await _query.RunAsync(monitor, ct);
        EventName = await _db.Events.Where(e => e.Id == monitor.EventId)
            .Select(e => e.DisplayName).FirstOrDefaultAsync(ct);
        return Page();
    }

    /// <summary>
    /// §1040 — the Excel export he asked for: <i>"i also need them to have an export to excel
    /// button"</i>.
    /// </summary>
    /// <remarks>
    /// 🔑 Runs the SAME query as the page, so the file and the screen can never disagree — a export
    /// built from its own query is a second definition of "who counts", and the one that drifts is
    /// the one somebody forwards to their finance team.
    /// </remarks>
    public async Task<IActionResult> OnGetExportAsync(string token, CancellationToken ct)
    {
        var monitor = await ResolveAsync(token, ct);
        if (monitor is null) return NotFound();

        var rows = await _query.RunAsync(monitor, ct);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Tickets");
        var headers = new[]
        {
            "Bought", "First name", "Last name", "E-mail", "Company", "Order id",
            "Ordered by", "Ordered by e-mail",
        };
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
        }

        var r = 2;
        foreach (var a in rows)
        {
            // ⚠️ The DATE as a real date cell, not a string: a spreadsheet people sort by ticket
            // date is the whole point, and text sorts 1 May before 2 April.
            if (a.BoughtAt is { } b)
            {
                ws.Cell(r, 1).Value = b.UtcDateTime;
                ws.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            }
            ws.Cell(r, 2).Value = a.FirstName;
            ws.Cell(r, 3).Value = a.LastName;
            ws.Cell(r, 4).Value = a.Email;
            ws.Cell(r, 5).Value = a.Company ?? string.Empty;
            ws.Cell(r, 6).Value = a.OrderId;
            ws.Cell(r, 7).Value = a.BuyerName ?? string.Empty;
            ws.Cell(r, 8).Value = a.BuyerEmail ?? string.Empty;
            r++;
        }
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var safe = string.Join("-", monitor.Name.Split(Path.GetInvalidFileNameChars()));
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"tickets-{safe}-{_clock.GetUtcNow():yyyyMMdd}.xlsx");
    }

    /// <summary>
    /// The token → monitor lookup, and the ONLY gate on this page.
    /// </summary>
    /// <remarks>
    /// 🔒 Returns null for unknown, revoked AND expired alike, and the caller answers <b>404</b> for
    /// all three. ⚠️ Deliberately not "this link has expired": distinguishing them would confirm to
    /// a stranger that a token is real, which is the one fact the URL's secrecy is protecting.
    /// </remarks>
    private async Task<AttendeeMonitor?> ResolveAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var monitor = await _db.AttendeeMonitors
            .FirstOrDefaultAsync(m => m.Token == token, ct);

        if (monitor is null) return null;
        if (monitor.RevokedAt is not null) return null;
        if (monitor.IsExpired(_clock.GetUtcNow())) return null;
        return monitor;
    }
}
