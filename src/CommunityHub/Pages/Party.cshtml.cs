using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// Anonymous (no-login) Party sign-up. Public form: name + email + opt-in to
/// attend the Party (16:00–18:00 on the pre-day). Drives the Bella Center food
/// headcount (REQUIREMENTS §6). Spam handling mirrors the no-login survey /
/// ask-a-question forms: a honeypot field + an IP hash (never reversible to PII).
/// Upserts by email so a re-submit updates the same RSVP.
/// </summary>
[AllowAnonymous]
public class PartyModel : PageModel
{
    private readonly PartyRsvpService _svc;
    private readonly ILogger<PartyModel> _log;

    public PartyModel(PartyRsvpService svc, ILogger<PartyModel> log)
    {
        _svc = svc;
        _log = log;
    }

    public PartyRsvpService.PartyInfo? Party { get; private set; }
    public bool SubmittedOk { get; private set; }
    public string? ErrorMessage { get; private set; }

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public bool Attending { get; set; } = true;

    /// <summary>Honeypot — hidden; any value ⇒ silent success, no write.</summary>
    [BindProperty] public string? Website { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Party = await _svc.GetActivePartyAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Party = await _svc.GetActivePartyAsync(ct);
        if (Party is null) { ErrorMessage = "There is no active event right now."; return Page(); }

        if (!string.IsNullOrWhiteSpace(Website))   // honeypot tripped
        {
            _log.LogInformation("Party RSVP honeypot tripped from {Ip}",
                HttpContext.Connection.RemoteIpAddress);
            SubmittedOk = true;
            return Page();
        }

        var ipHash = HashIp(HttpContext.Connection.RemoteIpAddress?.ToString());
        var result = await _svc.SubmitAsync(Name, Email, Attending, ipHash, ct);
        if (!result.Ok) { ErrorMessage = result.Error; return Page(); }

        SubmittedOk = true;
        return Page();
    }

    private static string? HashIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("party:" + ip));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// "Add to my calendar" — a single-VEVENT RFC 5545 .ics for the Party (no email
    /// invite: the allowlist would drop a public address, and mailing arbitrary
    /// typed addresses is an abuse vector). Anonymous; no PII, no per-person token —
    /// the same party for everyone.
    /// </summary>
    public async Task<IActionResult> OnGetIcsAsync(CancellationToken ct)
    {
        var p = await _svc.GetActivePartyAsync(ct);
        if (p is null) return NotFound();

        DateTimeOffset Local(int h) =>
            ScheduleService.EventLocal(p.Date.ToDateTime(new TimeOnly(h, 0)));
        string Z(DateTimeOffset d) => d.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");

        var host = HttpContext.Request.Host.Host;
        var uid = $"party-{p.EventId}@{host}";
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//CommunityHub//Party//EN",
            "METHOD:PUBLISH", "CALSCALE:GREGORIAN",
            "BEGIN:VEVENT",
            $"UID:{uid}",
            $"DTSTAMP:{Z(Local(p.StartHour))}",
            $"DTSTART:{Z(Local(p.StartHour))}",
            $"DTEND:{Z(Local(p.EndHour))}",
            $"SUMMARY:{p.EventName} — Party",
            "END:VEVENT", "END:VCALENDAR") + "\r\n";

        // Inline (no filename) so it opens in the calendar app rather than downloading.
        return File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8");
    }
}
