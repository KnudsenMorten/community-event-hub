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
/// §828 — THE EVENT POST REPO: the Type 5 posts imported from his markdown deck, and the per-post
/// overwrite tick that governs whether a later deck may replace one.
///
/// <para>The deck in the document library is a <b>DROP-BOX</b>: he lands a file, imports it here, and
/// the database owns the words from that moment on. Importing again is safe by default —
/// <b>an existing slug is left alone and SAID SO</b> (§828.1).</para>
///
/// <para>⚠️ The import runs only when he clicks it. A timer would defeat the entire overwrite model:
/// the tick is a per-post decision he makes on this page, and an unattended import could consume it
/// at 3am against a deck he had not finished editing.</para>
/// </summary>
[Authorize]
public class EventPostsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;
    private readonly EventSoMePostImportService _import;

    public EventPostsModel(
        ICurrentParticipantAccessor participant,
        CommunityHubDbContext db,
        EventSoMePostImportService import)
    {
        _participant = participant;
        _db = db;
        _import = import;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }

    public IReadOnlyList<EventSoMePost> Posts { get; private set; } = Array.Empty<EventSoMePost>();

    /// <summary>The last run's report, shown line by line. Null until he imports in this session.</summary>
    public EventSoMePostImportReport? Report { get; private set; }

    /// <summary>False when the library is not wired here — the button explains itself instead of failing.</summary>
    public bool CanImport => _import.CanRead;

    public int TotalRuns => Posts.Sum(p => p.Occurrences.Count);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        Report = await _import.ImportAsync(me.EventId, me.Email, ct);
        Message = Report.Summary;
        MessageIsError = !Report.Succeeded;

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Sets or clears ONE post's overwrite tick. §828.1 — deliberately per post, never a global
    /// "allow overwrites" mode, so authorising a replacement is always an act on a specific post.
    /// </summary>
    public async Task<IActionResult> OnPostToggleOverwriteAsync(int id, bool allow, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var post = await _db.EventSoMePosts
            .FirstOrDefaultAsync(p => p.Id == id && p.EventId == me.EventId, ct);

        if (post is null)
        {
            Message = "That post no longer exists.";
            MessageIsError = true;
        }
        else
        {
            post.AllowImportOverwrite = allow;
            post.UpdatedAt = DateTimeOffset.UtcNow;
            post.LastUpdatedByEmail = me.Email;
            await _db.SaveChangesAsync(ct);

            Message = allow
                ? $"'{post.Slug}' may now be replaced by the next import. The tick clears itself once "
                  + "that import uses it."
                : $"'{post.Slug}' is protected again — the next import will leave it alone.";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Posts = await _db.EventSoMePosts
            .Include(p => p.Occurrences)
            .Where(p => p.EventId == eventId)
            // The earliest run first: the deck is a calendar, and he reads it in posting order.
            .OrderBy(p => p.Occurrences.Min(o => (DateOnly?)o.PostDate) ?? DateOnly.MaxValue)
            .ThenBy(p => p.Slug)
            .AsNoTracking()
            .ToListAsync(ct);
    }
}
