using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Issue / list / revoke a participant's <b>secure-token</b> links.
/// Each link lets a delegate sign in scoped to this ONE participant and fill in
/// their onboarding/tasks on their behalf; links are time-bound and revocable.
/// Organizer-only, server-enforced.
/// </summary>
[Authorize]
public class SecureLinkModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SecretaryTokenService _tokens;

    public SecureLinkModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SecretaryTokenService tokens)
    {
        _db = db;
        _participant = participant;
        _tokens = tokens;
    }

    [BindProperty(SupportsGet = true)]
    public int ParticipantId { get; set; }

    [BindProperty]
    public string? Label { get; set; }

    /// <summary>Grant lifetime in days (default 7).</summary>
    [BindProperty]
    public int LifetimeDays { get; set; } = 7;

    public string TargetName { get; private set; } = string.Empty;
    public bool AccessDenied { get; private set; }
    public bool NotFound_ { get; private set; }
    public string? Message { get; private set; }
    public string BaseUrl { get; private set; } = string.Empty;

    public List<ParticipantSecretaryToken> Grants { get; private set; } = new();

    /// <summary>The full /s/{token} URL for a grant.</summary>
    public string LinkFor(ParticipantSecretaryToken g) => $"{BaseUrl}/s/{g.Token}";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        return await LoadAsync(me.EventId, ct);
    }

    public async Task<IActionResult> OnPostIssueAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var days = LifetimeDays is >= 1 and <= 90 ? LifetimeDays : 7;
        var grant = await _tokens.IssueAsync(
            me.EventId, ParticipantId, Label, me.Email, TimeSpan.FromDays(days), ct);
        if (grant is null) { NotFound_ = true; return Page(); }

        Message = $"A secure link was issued (valid {days} day(s)). Copy it below and share it securely.";
        return await LoadAsync(me.EventId, ct);
    }

    public async Task<IActionResult> OnPostRevokeAsync(int tokenId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        await _tokens.RevokeAsync(me.EventId, tokenId, ct);
        Message = "The secure link was revoked and no longer works.";
        return await LoadAsync(me.EventId, ct);
    }

    private async Task<IActionResult> LoadAsync(int eventId, CancellationToken ct)
    {
        var target = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == ParticipantId && p.EventId == eventId, ct);
        if (target is null) { NotFound_ = true; return Page(); }

        TargetName = target.FullName;
        BaseUrl = $"{Request.Scheme}://{Request.Host.Value}";
        Grants = (await _tokens.ListForParticipantAsync(eventId, ParticipantId, ct)).ToList();
        return Page();
    }

    private static bool IsRealOrganizer(CurrentParticipant me) =>
        me.Role == ParticipantRole.Organizer && !me.IsActingAs;
}
