using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// PUBLIC, no-login day-by-day agenda / timetable (REQUIREMENTS §21 Public site
/// "agenda/grid view"). Where the flat <c>/Sessions</c> page is "find + filter the
/// sessions", this page is the <b>running order</b>: the active edition's scheduled
/// talks grouped by day, in start-time order, with room/time/speakers — the
/// shareable programme a visitor scans to plan their day.
///
/// Read-only — there is no write path to abuse. Mobile-first (~360px) + a11y
/// (semantic per-day sections, time-labelled rows, <c>role="status"</c> summary).
/// Empty state when no event is active or no talk is scheduled yet.
/// </summary>
[AllowAnonymous]
public class AgendaModel : PageModel
{
    private readonly PublicAgendaService _svc;

    public AgendaModel(PublicAgendaService svc) => _svc = svc;

    public PublicAgendaView? View { get; private set; }

    /// <summary>True when there is no active event (distinct from "nothing scheduled").</summary>
    public bool NoActiveEvent { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        View = await _svc.BuildAsync(ct);
        NoActiveEvent = View is null;
    }

    /// <summary>Type label (shared semantics with the flat sessions page).</summary>
    public static string Display(SessionType t) => Sessions.IndexModel.Display(t);

    /// <summary>Length label (shared semantics with the flat sessions page).</summary>
    public static string Display(SessionLength l) => Sessions.IndexModel.Display(l);
}
