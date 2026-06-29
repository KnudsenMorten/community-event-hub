using System.Text;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CommunityHub.Api;

/// <summary>
/// PUBLIC, no-login per-session calendar download (REQUIREMENTS §21 PUBLIC):
///
///   GET /Sessions/{id}.ics
///       Returns a one-event RFC 5545 VCALENDAR (<c>METHOD:PUBLISH</c>) for a single
///       PUBLIC session, so an anonymous visitor can add the talk to their personal
///       calendar straight from the session-detail page. Reuses
///       <see cref="PublicSessionsService.BuildIcsAsync"/> (same builder as the
///       per-user feed), so the file validates identically.
///
/// Anonymous on purpose — the session is already public (it shows on
/// <c>/Sessions/{id}</c>) and calendar clients fetch the file without a cookie. The
/// service applies the SAME hard gate as the detail page (active edition, non-service
/// session, real id), so a service session / unknown id / no-active-event — OR an
/// UNSCHEDULED session (nothing to put on a calendar) — returns 404, never private
/// data. Caching is off so a moved/renamed session re-downloads fresh.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class PublicSessionCalendarController : ControllerBase
{
    private readonly PublicSessionsService _sessions;

    public PublicSessionCalendarController(PublicSessionsService sessions) =>
        _sessions = sessions;

    // Distinct route segment (literal "calendar.ics" under /sessions/{id}/) so it never
    // collides with the /Sessions/{id:int} detail PAGE — a `/Sessions/{id}.ics` request was
    // falling through to the fail-closed auth fallback (anon → login, authed → 500) instead of
    // matching this AllowAnonymous endpoint.
    [HttpGet("/sessions/{id:int}/calendar.ics")]
    public async Task<IActionResult> GetSessionIcsAsync(int id, CancellationToken ct)
    {
        var host = Request.Host.Value ?? "communityhub";
        var ics = await _sessions.BuildIcsAsync(id, host, ct);
        if (ics is null)
        {
            // 404 for an unknown / non-public / unscheduled session — never leak.
            return NotFound();
        }

        Response.Headers["Cache-Control"] = "no-store, max-age=0";
        return File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8");
    }
}
