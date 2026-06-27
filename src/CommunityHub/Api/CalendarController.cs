using System.Text;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CommunityHub.Api;

/// <summary>
/// Public, token-secured calendar feed.
///
///   GET /cal/{token}.ics       (canonical, short)
///   GET /calendar/{token}.ics  (alias, kept for already-shared subscriptions)
///       Returns a valid RFC 5545 VCALENDAR of the items belonging to the ONE
///       participant the token resolves to (their deadlines, shifts, tasks).
///       No login session is required — calendar clients (Outlook / Google /
///       Apple) subscribe with the URL and re-fetch on their own schedule, so
///       the feed must be reachable without a cookie. The unguessable per-user
///       token IS the credential; it scopes the response to that participant's
///       own data and nothing else. An unknown / revoked token — or one whose
///       edition has calendar sync DISABLED — returns 404, indistinguishable
///       from a never-issued one.
///
/// The body is built live from the DB on every request, so new reminders,
/// moved deadlines and completed tasks sync automatically on the client's next
/// poll. Caching is disabled to keep the feed fresh.
/// </summary>
// Anonymous to the cookie scheme: external calendar clients (Outlook/Google/Apple)
// fetch this feed with no session — the unguessable per-user token IS the credential
// (an unknown/revoked token returns 404). Must opt out of the fail-closed FallbackPolicy.
[ApiController]
[AllowAnonymous]
public sealed class CalendarController : ControllerBase
{
    private readonly CalendarFeedTokenService _tokens;
    private readonly ParticipantCalendarBuilder _builder;

    public CalendarController(
        CalendarFeedTokenService tokens,
        ParticipantCalendarBuilder builder)
    {
        _tokens = tokens;
        _builder = builder;
    }

    [HttpGet("/cal/{token}.ics")]
    [HttpGet("/calendar/{token}.ics")]
    public async Task<IActionResult> GetFeedAsync(string token, CancellationToken ct)
    {
        var participantId = await _tokens.ResolveParticipantIdAsync(token, ct);
        if (participantId is null)
        {
            // 404 (not 401) — do not reveal whether a token ever existed.
            return NotFound();
        }

        var host = Request.Host.Value ?? "communityhub";
        var ics = await _builder.BuildFeedAsync(participantId.Value, host, ct);

        // No-store so a client poll always reflects live DB state.
        Response.Headers["Cache-Control"] = "no-store, max-age=0";
        return File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8");
    }
}
