using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer page to send the <b>welcome email for all roles with one-click
/// auto-login</b> (<see cref="WelcomeWithLoginEmailService"/>). DEV-ONLY: the
/// Core service refuses to send unless the host is Development, and this page
/// additionally hides the send button outside DEV so the intent is obvious. The
/// send is re-sendable for testing — it never gates on a prior send — and each
/// send stamps <c>Participant.WelcomeWithLoginSentAt</c> so the table shows who
/// was sent and when.
/// </summary>
[Authorize]
public class SendWelcomeLoginModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly WelcomeWithLoginEmailService _welcome;
    private readonly IEnvironmentInfo _env;

    public SendWelcomeLoginModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        WelcomeWithLoginEmailService welcome,
        IEnvironmentInfo env)
    {
        _db = db;
        _participant = participant;
        _welcome = welcome;
        _env = env;
    }

    public bool AccessDenied { get; private set; }
    public bool IsDev => _env.IsDevelopment;
    public string EnvironmentName => _env.EnvironmentName;
    public int ActiveCount { get; private set; }
    public int EverSentCount { get; private set; }
    public string? Message { get; private set; }
    public bool IsError { get; private set; }
    public List<RoleRow> ByRole { get; private set; } = new();

    public record RoleRow(string Role, int Active, int EverSent);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadCountsAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSendAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        // Belt-and-braces: the Core service hard-guards too, but refuse early
        // and clearly here so a non-DEV operator gets a useful message.
        if (!_env.IsDevelopment)
        {
            await LoadCountsAsync(me.EventId, ct);
            IsError = true;
            Message = $"This email is DEV-only. The current environment is '{_env.EnvironmentName}', so nothing was sent.";
            return Page();
        }

        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        var ids = await _db.Participants
            .Where(p => p.EventId == me.EventId && p.IsActive)
            .Select(p => p.Id)
            .ToListAsync(ct);

        int sent = 0, refused = 0, failed = 0;
        foreach (var id in ids)
        {
            try
            {
                var result = await _welcome.SendAsync(id, baseUrl, ct);
                if (result.Sent) sent++;
                else refused++;
            }
            catch
            {
                failed++;
            }
        }

        await LoadCountsAsync(me.EventId, ct);
        Message = $"Sent {sent} welcome email(s) with auto-login (re-sendable)."
                  + (refused > 0 ? $" {refused} refused." : string.Empty)
                  + (failed > 0 ? $" {failed} failed." : string.Empty);
        return Page();
    }

    private async Task LoadCountsAsync(int eventId, CancellationToken ct)
    {
        var active = await _db.Participants
            .Where(p => p.EventId == eventId && p.IsActive)
            .Select(p => new { p.Role, p.WelcomeWithLoginSentAt })
            .ToListAsync(ct);

        ActiveCount = active.Count;
        EverSentCount = active.Count(p => p.WelcomeWithLoginSentAt != null);

        ByRole = active
            .GroupBy(p => p.Role.ToString())
            .Select(g => new RoleRow(
                g.Key,
                g.Count(),
                g.Count(p => p.WelcomeWithLoginSentAt != null)))
            .OrderBy(r => r.Role)
            .ToList();
    }
}
