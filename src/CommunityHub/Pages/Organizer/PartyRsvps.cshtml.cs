using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer view of the anonymous Party RSVPs — the headline is the ATTENDING
/// count for the Bella Center food order (REQUIREMENTS §6), plus the full list and
/// a CSV export. Organizer-gated.
/// </summary>
[Authorize]
public class PartyRsvpsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly PartyRsvpService _svc;

    public PartyRsvpsModel(ICurrentParticipantAccessor participant, PartyRsvpService svc)
    {
        _participant = participant;
        _svc = svc;
    }

    public bool AccessDenied { get; private set; }
    public int Attending { get; private set; }
    public int Total { get; private set; }
    public PartyRsvpService.PartyInfo? Party { get; private set; }
    public IReadOnlyList<PartyRsvp> Rows { get; private set; } = Array.Empty<PartyRsvp>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Party = await _svc.GetActivePartyAsync(ct);
        (Total, Attending) = await _svc.CountsAsync(me.EventId, ct);
        Rows = await _svc.GetAllAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var csv = await BuildCsvAsync(me.EventId, ct);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", "party-rsvps.csv");
    }

    public async Task<IActionResult> OnGetXlsxAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var csv = await BuildCsvAsync(me.EventId, ct);
        return File(CsvToXlsx.Build(csv, "Party RSVPs"), CsvToXlsx.ContentType, "party-rsvps.xlsx");
    }

    private async Task<string> BuildCsvAsync(int eventId, CancellationToken ct)
    {
        var rows = await _svc.GetAllAsync(eventId, ct);
        var sb = new StringBuilder();
        sb.Append("Name,Email,Attending,SubmittedUtc\r\n");
        foreach (var r in rows)
            sb.Append($"{Csv(r.Name)},{Csv(r.Email)},{(r.Attending ? "yes" : "no")},{r.UpdatedAt:yyyy-MM-dd HH:mm}\r\n");
        return sb.ToString();
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
