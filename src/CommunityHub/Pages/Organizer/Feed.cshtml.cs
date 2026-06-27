using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// The "CEH feed" surface (REQUIREMENTS §137). Organizer-only. Lists the bug/feature
/// reports the AiHelper captured + the questions users forwarded to the organizers — the
/// durable record behind the intake emails — newest-first, with an open/resolved split and
/// a "mark handled" action. Read-mostly: the items themselves are written by the AiHelper
/// intake flow, never edited here.
/// </summary>
[Authorize]
public class FeedModel : PageModel
{
    private const int PageSize = 200;

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;

    public FeedModel(CommunityHubDbContext db, ICurrentParticipantAccessor participant)
    {
        _db = db;
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    /// <summary>Optional kind filter (Bug/Feature/Question); null = all.</summary>
    [BindProperty(SupportsGet = true)] public FeedbackKind? Kind { get; set; }

    public IReadOnlyList<Row> OpenItems { get; private set; } = Array.Empty<Row>();
    public IReadOnlyList<Row> ResolvedItems { get; private set; } = Array.Empty<Row>();

    public sealed record Row(
        int Id, FeedbackKind Kind, ParticipantRole Role, string Who, string? Email,
        string Message, string? PageUrl, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostResolveAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var item = await _db.FeedbackItems
            .FirstOrDefaultAsync(x => x.Id == id && x.EventId == me.EventId, ct);
        if (item is null)
        {
            Message = "That item was not found.";
        }
        else if (item.ResolvedAt is null)
        {
            item.ResolvedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            Message = "Marked handled.";
        }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostReopenAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var item = await _db.FeedbackItems
            .FirstOrDefaultAsync(x => x.Id == id && x.EventId == me.EventId, ct);
        if (item is { ResolvedAt: not null })
        {
            item.ResolvedAt = null;
            await _db.SaveChangesAsync(ct);
            Message = "Re-opened.";
        }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        var q = _db.FeedbackItems
            .AsNoTracking()
            .Include(x => x.Participant)
            .Where(x => x.EventId == eventId);
        if (Kind is not null) q = q.Where(x => x.Kind == Kind);

        var rows = await q
            .OrderByDescending(x => x.CreatedAt)
            .Take(PageSize)
            .ToListAsync(ct);

        OpenItems = rows.Where(x => x.ResolvedAt is null).Select(ToRow).ToList();
        ResolvedItems = rows.Where(x => x.ResolvedAt is not null).Select(ToRow).ToList();
    }

    private static Row ToRow(FeedbackItem x) => new(
        x.Id, x.Kind, x.Role,
        x.Participant?.FullName ?? "(unknown participant)",
        x.Participant?.Email,
        x.Message, x.PageUrl, x.CreatedAt, x.ResolvedAt);
}
