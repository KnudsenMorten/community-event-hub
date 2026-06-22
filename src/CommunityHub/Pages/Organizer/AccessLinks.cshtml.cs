using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer tool: generate a PERMANENT one-tap sign-in URL per participant
/// (organizers / speakers / volunteers / …). Each URL is a magic-link
/// (<c>/Login/Magic?token=…</c>) that signs the person straight into the hub MAIN
/// MENU and creates a session that lasts until they explicitly sign out (the magic
/// link sets a 365-day sliding persistent cookie). The link token itself is valid
/// for a year, so it works as a bookmark; share it with the person privately.
///
/// Organizer-only (server-enforced). The links are bearer URLs — anyone who has the
/// URL can sign in as that person until the token expires, so treat them like a
/// password.
/// </summary>
[Authorize]
public class AccessLinksModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;
    private readonly MagicLinkService _magic;

    public AccessLinksModel(
        ICurrentParticipantAccessor participant,
        CommunityHubDbContext db,
        MagicLinkService magic)
    {
        _participant = participant;
        _db = db;
        _magic = magic;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>The role currently filtered on (null/empty = all roles).</summary>
    public string? RoleFilter { get; private set; }

    /// <summary>The ring currently filtered on (null/empty = all). "01" = ring 0 + 1 (testers).</summary>
    public string? RingFilter { get; private set; }

    public sealed record Row(string FullName, string Email, ParticipantRole Role,
        CommunityHub.Core.Settings.Ring Ring, string Url);
    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();

    /// <summary>The role chips offered as filters, in display order.</summary>
    public static readonly ParticipantRole[] RoleChips =
    {
        ParticipantRole.Organizer,
        ParticipantRole.Speaker,
        ParticipantRole.Volunteer,
    };

    // The personal link is valid for a year (a reliable bookmark); the session it
    // creates then persists until sign-out (Magic.cshtml.cs sets a 365-day cookie).
    private static readonly TimeSpan LinkTtl = TimeSpan.FromDays(365);

    public async Task<IActionResult> OnGetAsync(string? role, string? ring, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        RoleFilter = role;
        RingFilter = ring;
        var q = _db.Participants.Where(p => p.EventId == me.EventId && p.IsActive);
        if (Enum.TryParse<ParticipantRole>(role, ignoreCase: true, out var r))
            q = q.Where(p => p.Role == r);

        // Ring filter: "01" = ring 0 + 1 (the test roster); else an exact ring.
        if (ring == "01")
            q = q.Where(p => p.Ring == CommunityHub.Core.Settings.Ring.Ring0
                          || p.Ring == CommunityHub.Core.Settings.Ring.Ring1);
        else if (Enum.TryParse<CommunityHub.Core.Settings.Ring>(
                     ring is "0" or "1" or "2" or "3" ? $"Ring{ring}" : ring, ignoreCase: true, out var rg))
            q = q.Where(p => p.Ring == rg);

        var people = await q
            .OrderBy(p => p.Ring).ThenBy(p => p.Role).ThenBy(p => p.FullName)
            .Select(p => new { p.Id, p.FullName, p.Email, p.Role, p.Ring })
            .ToListAsync(ct);

        var origin = $"{Request.Scheme}://{Request.Host}";
        Rows = people.Select(p => new Row(
            string.IsNullOrWhiteSpace(p.FullName) ? p.Email : p.FullName,
            p.Email,
            p.Role,
            p.Ring,
            $"{origin}/Login/Magic?token={Uri.EscapeDataString(_magic.CreateToken(p.Id, LinkTtl))}"))
            .ToList();

        return Page();
    }
}
