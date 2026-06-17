using ClosedXML.Excel;
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
    [BindProperty] public string? EmailList { get; set; }
    /// <summary>"preday" | "mainday".</summary>
    [BindProperty] public string FieldToSet { get; set; } = "preday";
    /// <summary>true = tick the flag; false = clear it.</summary>
    [BindProperty] public bool TargetValue { get; set; } = true;

    public List<Row> Rows { get; private set; } = new();
    public record Row(
        int Id, string Name, string Email, string Role,
        bool SpeakingPreDay, bool SpeakingMainDay,
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

    public async Task<IActionResult> OnPostBulkSelectedAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        if (SelectedIds.Length == 0)
        {
            Error = "No speakers selected.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        var affected = await ApplyToParticipantsAsync(me.EventId, SelectedIds, ct);
        Message = $"{FieldLabel()} = {(TargetValue ? "Yes" : "No")} applied to {affected} speaker(s).";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostBulkPasteAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        if (string.IsNullOrWhiteSpace(EmailList))
        {
            Error = "Paste at least one email.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        var emails = EmailList
            .Split(new[] { '\n', '\r', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().ToLowerInvariant())
            .Where(e => e.Contains('@'))
            .Distinct()
            .ToList();

        var ids = await _db.Participants
            .Where(p => p.EventId == me.EventId && emails.Contains(p.Email)
                        && (p.Role == ParticipantRole.Speaker
                            || p.Role == ParticipantRole.MasterclassSpeaker))
            .Select(p => p.Id)
            .ToArrayAsync(ct);

        var notFound = emails.Count - ids.Length;
        var affected = await ApplyToParticipantsAsync(me.EventId, ids, ct);
        Message = $"{FieldLabel()} = {(TargetValue ? "Yes" : "No")} applied to {affected} speaker(s)."
                  + (notFound > 0 ? $" {notFound} email(s) did not match an active speaker and were skipped." : "");
        await LoadAsync(me.EventId, ct);
        return Page();
    }

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
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var result = await _deletion.DeleteAsync(me.EventId, participantId, ct);
        switch (result.Status)
        {
            case SpeakerDeletionService.DeletionStatus.Deleted:
                Message = $"\"{result.Name}\" was removed from the speaker roster "
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
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

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
            Message = $"{result.Deleted} speaker(s) removed from the roster"
                + (result.Blocked > 0 ? $", {result.Blocked} kept (still on the agenda)" : string.Empty)
                + (skipped > 0 ? $", {skipped} not found" : string.Empty)
                + ".";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task<int> ApplyToParticipantsAsync(
        int eventId, int[] participantIds, CancellationToken ct)
    {
        if (participantIds.Length == 0) return 0;
        var now = _clock.GetUtcNow();
        var existing = await _db.SpeakerProfiles
            .Where(sp => sp.EventId == eventId && participantIds.Contains(sp.ParticipantId))
            .ToDictionaryAsync(sp => sp.ParticipantId, sp => sp, ct);

        int n = 0;
        foreach (var pid in participantIds)
        {
            if (!existing.TryGetValue(pid, out var prof))
            {
                prof = new SpeakerProfile
                {
                    EventId = eventId,
                    ParticipantId = pid,
                    CreatedAt = now,
                };
                _db.SpeakerProfiles.Add(prof);
            }
            if (FieldToSet == "mainday") prof.SpeakingMainDay = TargetValue;
            else                         prof.SpeakingPreDay  = TargetValue;
            prof.UpdatedAt = now;
            n++;
        }
        await _db.SaveChangesAsync(ct);
        return n;
    }

    private string FieldLabel() => FieldToSet == "mainday" ? "SpeakingMainDay" : "SpeakingPreDay";

    /// <summary>
    /// Download an xlsx template the organizer can fill out + upload.
    /// Columns: Email | SpeakingPreDay | SpeakingMainDay
    /// Empty cell = no change. Yes/Y/1/true/x = tick. No/N/0/false = untick.
    /// </summary>
    public IActionResult OnGetTemplate()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Speakers");
        ws.Cell(1, 1).Value = "Email";
        ws.Cell(1, 2).Value = "SpeakingPreDay";
        ws.Cell(1, 3).Value = "SpeakingMainDay";
        ws.Range(1, 1, 1, 3).Style.Font.Bold = true;

        ws.Cell(2, 1).Value = "alice@example.com";
        ws.Cell(2, 2).Value = "Yes";
        ws.Cell(2, 3).Value = "No";

        ws.Cell(3, 1).Value = "bob@example.com";
        ws.Cell(3, 2).Value = "No";
        ws.Cell(3, 3).Value = "Yes";

        ws.Cell(4, 1).Value = "carol@example.com";
        ws.Cell(4, 2).Value = "Yes";
        ws.Cell(4, 3).Value = "Yes";

        var notes = wb.Worksheets.Add("Notes");
        notes.Cell(1, 1).Value = "Header row is REQUIRED. Email is the match key (lower-cased, trimmed).";
        notes.Cell(2, 1).Value = "Yes / Y / 1 / true / x  =  tick the flag.";
        notes.Cell(3, 1).Value = "No / N / 0 / false       =  untick the flag.";
        notes.Cell(4, 1).Value = "Empty cell                =  leave that flag unchanged.";
        notes.Cell(5, 1).Value = "Only matching active Speakers / Master Class Speakers are updated.";
        notes.Columns().AdjustToContents();
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "speakers-template.xlsx");
    }

    [BindProperty] public IFormFile? UploadFile { get; set; }

    public async Task<IActionResult> OnPostImportXlsxAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        if (UploadFile is null || UploadFile.Length == 0)
        {
            Error = "Pick a file to upload.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }
        var ext = Path.GetExtension(UploadFile.FileName).ToLowerInvariant();
        if (ext != ".xlsx" && ext != ".xlsm")
        {
            Error = "Upload an Excel .xlsx file (the .xls binary format is not supported).";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        var emailIdx = await _db.Participants
            .Where(p => p.EventId == me.EventId
                        && (p.Role == ParticipantRole.Speaker
                            || p.Role == ParticipantRole.MasterclassSpeaker))
            .ToDictionaryAsync(p => p.Email, p => p.Id, StringComparer.OrdinalIgnoreCase, ct);

        var profileIdx = await _db.SpeakerProfiles
            .Where(sp => sp.EventId == me.EventId)
            .ToDictionaryAsync(sp => sp.ParticipantId, sp => sp, ct);

        await using var stream = UploadFile.OpenReadStream();
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.FirstOrDefault();
        if (ws is null)
        {
            Error = "Workbook has no sheets.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        // Locate columns by header (case-insensitive).
        var headerRow = ws.FirstRowUsed();
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (headerRow is not null)
        {
            foreach (var c in headerRow.CellsUsed())
            {
                var k = c.GetString().Trim();
                if (!string.IsNullOrEmpty(k)) headers[k] = c.Address.ColumnNumber;
            }
        }
        if (!headers.TryGetValue("Email", out var emailCol))
        {
            Error = "No 'Email' column found in the header row.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }
        headers.TryGetValue("SpeakingPreDay",  out var preCol);
        headers.TryGetValue("SpeakingMainDay", out var mainCol);

        int updated = 0, notMatched = 0;
        var firstData = headerRow!.RowNumber() + 1;
        var lastRow   = ws.LastRowUsed()?.RowNumber() ?? 0;
        var now = _clock.GetUtcNow();
        for (var rowNum = firstData; rowNum <= lastRow; rowNum++)
        {
            var row = ws.Row(rowNum);
            var email = row.Cell(emailCol).GetString().Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(email)) continue;

            if (!emailIdx.TryGetValue(email, out var pid))
            {
                notMatched++;
                continue;
            }

            if (!profileIdx.TryGetValue(pid, out var prof))
            {
                prof = new SpeakerProfile
                {
                    EventId = me.EventId,
                    ParticipantId = pid,
                    CreatedAt = now,
                };
                _db.SpeakerProfiles.Add(prof);
                profileIdx[pid] = prof;
            }

            bool changed = false;
            if (preCol > 0)
            {
                var v = ParseYesNoNull(row.Cell(preCol).GetString());
                if (v is not null) { prof.SpeakingPreDay = v.Value; changed = true; }
            }
            if (mainCol > 0)
            {
                var v = ParseYesNoNull(row.Cell(mainCol).GetString());
                if (v is not null) { prof.SpeakingMainDay = v.Value; changed = true; }
            }
            if (changed) { prof.UpdatedAt = now; updated++; }
        }
        await _db.SaveChangesAsync(ct);

        Message = $"Updated {updated} speaker(s)."
                  + (notMatched > 0 ? $" {notMatched} row(s) did not match any speaker email in this edition." : "");
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private static bool? ParseYesNoNull(string raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return null;
        return s switch
        {
            "yes" or "y" or "1" or "true"  or "x" or "tick"    => true,
            "no"  or "n" or "0" or "false" or "-"              => false,
            _ => null,  // ambiguous -> leave unchanged
        };
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        // Flattened, filterable+sortable speaker query. Profile flags are pulled
        // via correlated subqueries so the whole thing stays server-side (we can
        // count + sort + page in SQL, never loading every speaker into memory).
        var baseQuery = _db.Participants
            .Where(p => p.EventId == eventId
                        && (p.Role == ParticipantRole.Speaker
                            || p.Role == ParticipantRole.MasterclassSpeaker))
            .Select(p => new
            {
                p.Id, p.FullName, p.Email, p.Role, p.IsActive,
                PreDay = _db.SpeakerProfiles
                    .Where(sp => sp.EventId == eventId && sp.ParticipantId == p.Id)
                    .Select(sp => (bool?)sp.SpeakingPreDay).FirstOrDefault() ?? false,
                MainDay = _db.SpeakerProfiles
                    .Where(sp => sp.EventId == eventId && sp.ParticipantId == p.Id)
                    .Select(sp => (bool?)sp.SpeakingMainDay).FirstOrDefault() ?? false,
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
            ("preday", false)  => baseQuery.OrderBy(r => r.PreDay).ThenBy(r => r.FullName).ThenBy(r => r.Id),
            ("preday", true)   => baseQuery.OrderByDescending(r => r.PreDay).ThenBy(r => r.FullName).ThenBy(r => r.Id),
            ("mainday", false) => baseQuery.OrderBy(r => r.MainDay).ThenBy(r => r.FullName).ThenBy(r => r.Id),
            ("mainday", true)  => baseQuery.OrderByDescending(r => r.MainDay).ThenBy(r => r.FullName).ThenBy(r => r.Id),
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

        var sessionsById = (await _db.SessionSpeakers
            .Where(ss => ss.Session.EventId == eventId && pageIds.Contains(ss.ParticipantId))
            .OrderBy(ss => ss.Session.StartsAt).ThenBy(ss => ss.Session.Title)
            .Select(ss => new { ss.ParticipantId, ss.Session.Title })
            .ToListAsync(ct))
            .GroupBy(x => x.ParticipantId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Title).ToList());

        SessionCount = await _db.Sessions.CountAsync(s => s.EventId == eventId, ct);

        Rows = pageRows
            .Select(r => new Row(
                r.Id, r.FullName, r.Email,
                CommunityHub.Branding.RoleDisplay.Name(r.Role),
                r.PreDay, r.MainDay,
                profileById.TryGetValue(r.Id, out var pr) ? pr.Accreditation : null,
                profileById.TryGetValue(r.Id, out var pc) ? pc.Country : null,
                profileById.TryGetValue(r.Id, out var pf) ? pf.IsFirstTimeSpeaker : null,
                r.IsActive,
                sessionsById.TryGetValue(r.Id, out var ses) ? ses : new List<string>()))
            .ToList();
    }
}
