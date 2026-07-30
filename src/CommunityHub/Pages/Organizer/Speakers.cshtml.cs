using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

[Authorize]
public class SpeakersModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly SpeakerDeletionService _deletion;

    public SpeakersModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant, TimeProvider clock,
        SpeakerDeletionService deletion)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _deletion = deletion;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    // --- Search / sort / paging (GET-bound; also re-posted as hidden fields so
    //     the grid keeps its place after a bulk action returns Page()). ---------
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    /// <summary>Sort column key: name | email | preday | mainday. Default name.</summary>
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "name";
    [BindProperty(SupportsGet = true)] public bool Desc { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNo { get; set; } = 1;

    public GridPage Paging { get; private set; }

    public bool NextDescFor(string col) => Sort == col && !Desc;
    public string SortIndicator(string col) => Sort != col ? "" : (Desc ? " ▼" : " ▲");
    public string AriaSort(string col) => Sort != col ? "none" : (Desc ? "descending" : "ascending");

    [BindProperty] public int[] SelectedIds { get; set; } = Array.Empty<int>();

    public List<Row> Rows { get; private set; } = new();
    // §308 (operator 2026-07-24): the manual pre-/main-day flags are GONE from this
    // grid — "we have dates when speakers are speaking", so SpeakingDays is DERIVED
    // from the linked sessions' dates (read-only).
    public record Row(
        int Id, string Name, string Email, string Role,
        IReadOnlyList<string> SpeakingDays,
        string? Accreditation, string? Country, bool? IsFirstTime, bool IsActive,
        IReadOnlyList<string> Sessions)
    {
        /// <summary>
        /// True when this speaker can be removed from the roster with no agenda
        /// orphaning — i.e. they are not linked to any session. Drives whether the
        /// grid offers a "remove from speakers" affordance or a "still on the
        /// agenda" note. Matches <see cref="CommunityHub.Core.Organizer.SpeakerDeletionService"/>.
        /// </summary>
        public bool CanRemove => Sessions.Count == 0;
    }

    /// <summary>Total sessions imported for the edition (header stat).</summary>
    public int SessionCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    // §308: OnPostBulkSelectedAsync (manual pre-/main-day flag setter) DELETED —
    // speaking days derive from the sessions' dates; nothing to define by hand.

    /// <summary>
    /// Remove a single person from the speaker roster (REQUIREMENTS §22 "Speakers
    /// delete"). Safe semantics live in <see cref="SpeakerDeletionService"/>: a
    /// speaker still linked to a session is refused with a reason (the agenda is
    /// never silently orphaned); a clean speaker has their speaker profile removed
    /// while the participant row (identity / login / logistics) is untouched.
    /// Organizer-only, edition-scoped; the page's confirm modal gates the click.
    /// </summary>
    public async Task<IActionResult> OnPostRemoveSpeakerAsync(int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var result = await _deletion.DeleteAsync(me.EventId, participantId, ct);
        switch (result.Status)
        {
            case SpeakerDeletionService.DeletionStatus.Deleted:
                Message = $"\"{result.Name}\" was removed from the speaker registrations "
                          + "(the person stays as a participant).";
                break;
            case SpeakerDeletionService.DeletionStatus.Blocked:
                Error = $"\"{result.Name}\" was not removed because they are still linked to "
                        + $"{result.SessionCount} session(s). Unlink the session(s) first.";
                break;
            default:
                Error = "That speaker could not be found in this edition.";
                break;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Bulk-remove the ticked people from the speaker roster (§20 universal CRUD +
    /// bulk). Applies the single-row safe semantics row by row in
    /// <see cref="SpeakerDeletionService"/>: speakers still on the agenda are left
    /// untouched and reported; clean speakers have their profile removed; the
    /// honest banner reports removed / kept / not-found. Organizer-only,
    /// edition-scoped; the page's confirm modal (live count) gates the click.
    /// </summary>
    public async Task<IActionResult> OnPostBulkRemoveAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var requested = SelectedIds.Where(id => id > 0).Distinct().Count();
        if (requested == 0)
        {
            Error = "Pick at least one speaker first.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        var result = await _deletion.DeleteManyAsync(me.EventId, SelectedIds, ct);
        var skipped = result.Skipped(requested);

        if (result.Deleted == 0 && result.Blocked > 0)
        {
            Error = $"{result.Blocked} speaker(s) are still linked to a session and were not "
                    + "removed. Unlink those session(s) first.";
        }
        else
        {
            Message = $"{result.Deleted} speaker(s) removed from the registrations"
                + (result.Blocked > 0 ? $", {result.Blocked} kept (still on the agenda)" : string.Empty)
                + (skipped > 0 ? $", {skipped} not found" : string.Empty)
                + ".";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        // Flattened, filterable+sortable speaker query — server-side count/sort/page.
        var baseQuery = _db.Participants
            .Where(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Speaker)
            .Select(p => new
            {
                p.Id, p.FullName, p.Email, p.Role, p.IsActive,
            });

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            baseQuery = baseQuery.Where(r => r.FullName.Contains(s) || r.Email.Contains(s));
        }

        var matched = await baseQuery.CountAsync(ct);
        Paging = GridPaging.Resolve(PageNo, GridPaging.DefaultPageSize, matched);

        var sorted = (Sort, Desc) switch
        {
            ("email", false)   => baseQuery.OrderBy(r => r.Email).ThenBy(r => r.Id),
            ("email", true)    => baseQuery.OrderByDescending(r => r.Email).ThenByDescending(r => r.Id),
            (_, true)          => baseQuery.OrderByDescending(r => r.FullName).ThenByDescending(r => r.Id),
            _                  => baseQuery.OrderBy(r => r.FullName).ThenBy(r => r.Id),
        };

        var pageRows = await sorted
            .Skip(Paging.Skip).Take(Paging.PageSize)
            .ToListAsync(ct);

        // Per-row detail (sessions + extra profile fields) only for the rows on
        // this page — keeps the heavier joins off the full result set.
        var pageIds = pageRows.Select(r => r.Id).ToList();

        var profileById = await _db.SpeakerProfiles
            .Where(sp => sp.EventId == eventId && pageIds.Contains(sp.ParticipantId))
            .ToDictionaryAsync(sp => sp.ParticipantId, sp => new
            {
                sp.Accreditation, sp.Country, sp.IsFirstTimeSpeaker
            }, ct);

        var sessionRows = await _db.SessionSpeakers
            .Where(ss => ss.Session.EventId == eventId && pageIds.Contains(ss.ParticipantId))
            .OrderBy(ss => ss.Session.StartsAt).ThenBy(ss => ss.Session.Title)
            .Select(ss => new { ss.ParticipantId, ss.Session.Title, ss.Session.StartsAt })
            .ToListAsync(ct);
        var sessionsById = sessionRows
            .GroupBy(x => x.ParticipantId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Title).ToList());
        // §308: SPEAKING DAYS are DERIVED from the linked sessions' dates (Danish
        // time) — the old manual pre-/main-day flags are gone from this grid.
        var daysById = sessionRows
            .Where(x => x.StartsAt is not null)
            .GroupBy(x => x.ParticipantId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g
                    .Select(x => TimeZoneInfo.ConvertTime(x.StartsAt!.Value,
                        CommunityHub.Core.Integrations.EventTimezone.Tz).Date)
                    .Distinct()
                    .OrderBy(d => d)
                    .Select(d => d.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture))
                    .ToList());

        SessionCount = await _db.Sessions.CountAsync(s => s.EventId == eventId, ct);

        Rows = pageRows
            .Select(r => new Row(
                r.Id, r.FullName, r.Email,
                CommunityHub.Branding.RoleDisplay.Name(r.Role),
                daysById.TryGetValue(r.Id, out var days) ? days : Array.Empty<string>(),
                profileById.TryGetValue(r.Id, out var pr) ? pr.Accreditation : null,
                profileById.TryGetValue(r.Id, out var pc) ? pc.Country : null,
                profileById.TryGetValue(r.Id, out var pf) ? pf.IsFirstTimeSpeaker : null,
                r.IsActive,
                sessionsById.TryGetValue(r.Id, out var ses) ? ses : new List<string>()))
            .ToList();
    }
}
