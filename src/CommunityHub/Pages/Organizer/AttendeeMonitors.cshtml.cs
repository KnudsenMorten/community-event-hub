using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1040 — VOLUME PACKAGE MONITORS: create a shareable, revocable list of who has bought a ticket.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10: <i>"we have a poduct called Volume Package, which is valid for any
/// company buying more than 10 tickets … i want to be able to generate a secure link per entry
/// (monitors), which i can send to persons where they can follow who bought a ticket"</i>.</para>
///
/// <para>🔴 <b>Every link created here exposes real people's names and e-mail addresses to somebody
/// outside the hub, with no login.</b> So this page's job is not only to create monitors — it is to
/// make the consequences visible: who a link covers, when it expires, how often it has been opened,
/// and one click to withdraw it.</para>
/// </remarks>
[Authorize]
public class AttendeeMonitorsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly AttendeeMonitorQuery _query;
    private readonly TimeProvider _clock;

    public AttendeeMonitorsModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant,
        AttendeeMonitorQuery query, TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _query = query;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public record Row(AttendeeMonitor Monitor, int MatchCount, string ShareUrl);

    public List<Row> Monitors { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(
        string name, AttendeeMonitorKind kind, string value, DateTimeOffset? expiresAt,
        CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var normalised = AttendeeMonitor.NormaliseValue(kind, value);
        if (string.IsNullOrWhiteSpace(name) || normalised.Length == 0)
        {
            Error = kind == AttendeeMonitorKind.EmailDomain
                ? "Give it a name and an e-mail domain, e.g. \"Arrow volume package\" and \"arrow.com\"."
                : "Give it a name and a coupon code.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        // ⚠️ A duplicate would mean two links showing the same people, only one of which anybody
        // remembers to revoke.
        var exists = await _db.AttendeeMonitors.AnyAsync(
            m => m.EventId == me.EventId && m.Kind == kind && m.Value == normalised
                 && m.RevokedAt == null, ct);
        if (exists)
        {
            Error = $"An active monitor for \"{normalised}\" already exists — revoke it first, or "
                  + "share the link it already has.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        _db.AttendeeMonitors.Add(new AttendeeMonitor
        {
            EventId = me.EventId,
            Name = name.Trim(),
            Kind = kind,
            Value = normalised,
            Token = AttendeeMonitor.NewToken(),
            // His instruction: "link must be active until 15 feb 2027 (after event)".
            ExpiresAt = expiresAt ?? AttendeeMonitor.DefaultExpiry,
            CreatedByEmail = me.Email,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);

        Message = $"Monitor \"{name.Trim()}\" created. Copy its link below and send it to the company "
                + "— it needs no login, and you can withdraw it at any time.";
        return RedirectToPage(new { });
    }

    /// <summary>
    /// 🔒 REVOKE, never delete. Deleting loses the record of who was given access to what, which is
    /// the only audit trail a link-based share has.
    /// </summary>
    public async Task<IActionResult> OnPostRevokeAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var m = await _db.AttendeeMonitors.FirstOrDefaultAsync(
            x => x.Id == id && x.EventId == me.EventId, ct);
        if (m is null) { Error = "That monitor no longer exists."; await LoadAsync(me.EventId, ct); return Page(); }

        m.RevokedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        Message = $"\"{m.Name}\" is withdrawn — the link now shows nothing.";
        return RedirectToPage(new { });
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        var monitors = await _db.AttendeeMonitors
            .Where(m => m.EventId == eventId)
            .OrderByDescending(m => m.RevokedAt == null)
            .ThenByDescending(m => m.CreatedAt)
            .ToListAsync(ct);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        foreach (var m in monitors)
        {
            // 🔑 The organizer sees the SAME query the shared link runs (AttendeeMonitorQuery), so
            // the count beside a link is what the recipient will actually see — not an estimate.
            var rows = await _query.RunAsync(m, ct);
            Monitors.Add(new Row(m, rows.Count, $"{baseUrl}/monitor/{m.Token}"));
        }
    }
}
