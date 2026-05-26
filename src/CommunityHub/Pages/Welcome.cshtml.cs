using CommunityHub.Auth;
using CommunityHub.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

[Authorize]
public class WelcomeModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;

    public WelcomeModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    public string FirstName { get; private set; } = string.Empty;
    public string RoleName { get; private set; } = string.Empty;
    public string EventDisplayName { get; private set; } = string.Empty;
    public string EventCode { get; private set; } = string.Empty;
    public DateOnly? MainDay { get; private set; }
    public string? VenueName { get; private set; }

    public IActionResult OnGet()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        FirstName = string.IsNullOrWhiteSpace(me.FullName) ? "there" : me.FullName.Split(' ')[0];
        RoleName = me.Role.ToString();

        var ev = _db.Events
            .Where(e => e.Id == me.EventId)
            .Select(e => new { e.DisplayName, e.Code, e.StartDate, e.VenueName })
            .FirstOrDefault();
        if (ev is not null)
        {
            EventDisplayName = ev.DisplayName;
            EventCode = ev.Code;
            MainDay = ev.StartDate;
            VenueName = ev.VenueName;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostContinueAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var p = await _db.Participants.FirstOrDefaultAsync(x => x.Id == me.ParticipantId, ct);
        if (p is not null && p.WelcomeShownAt is null)
        {
            p.WelcomeShownAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToPage("/Index");
    }
}
