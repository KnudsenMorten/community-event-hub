using CommunityHub.Auth;
using CommunityHub.Core.Auth;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer admin for WELCOME auto-login grants — the single-use, short-lived
/// sign-in links minted into welcome emails (<see cref="MagicLinkGrant"/>). Lets
/// an organizer see every issued grant (who, when, expiry, used/revoked) and
/// REVOKE a still-active one so a leaked welcome link can be killed immediately.
/// Edition-scoped + organizer-gated (server-enforced). The security model's
/// "organizer-facing revoke UI" (REQUIREMENTS §4).
/// </summary>
[Authorize]
public class WelcomeLinksModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly WelcomeGrantAdminService _admin;

    public WelcomeLinksModel(
        ICurrentParticipantAccessor participant, WelcomeGrantAdminService admin)
    {
        _participant = participant;
        _admin = admin;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public IReadOnlyList<WelcomeGrantAdminService.GrantRow> Rows { get; private set; }
        = Array.Empty<WelcomeGrantAdminService.GrantRow>();

    public int ActiveCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? msg, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Message = msg;
        Rows = await _admin.ListAsync(me.EventId, DateTimeOffset.UtcNow, activeOnly: false, ct);
        ActiveCount = Rows.Count(r => r.State == WelcomeGrantAdminService.GrantState.Active);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var ok = await _admin.RevokeAsync(me.EventId, id, DateTimeOffset.UtcNow, ct);
        return RedirectToPage(new { msg = ok ? "Link revoked." : "That link could not be revoked (already used, revoked, or unknown)." });
    }
}
