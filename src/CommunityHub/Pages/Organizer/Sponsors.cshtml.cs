using ClosedXML.Excel;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

[Authorize]
public class SponsorsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly SponsorInfoDeletionService _infoDeletion;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly ILogger<SponsorsModel> _logger;

    public SponsorsModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant, TimeProvider clock,
        SponsorInfoDeletionService infoDeletion,
        CompanyManagerClient cm, CompanyManagerOptions cmOptions, ILogger<SponsorsModel> logger)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _infoDeletion = infoDeletion;
        _cm = cm;
        _cmOptions = cmOptions;
        _logger = logger;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public int CompanyCount { get; private set; }
    public int ContactCount { get; private set; }
    public int TotalTasks { get; private set; }
    public int DoneTasks { get; private set; }
    public int OverdueTasks { get; private set; }

    public List<CompanyRow> Companies { get; private set; } = new();
    public List<Contact> ContactsWithoutCompany { get; private set; } = new();

    /// <summary>
    /// Orphaned sponsor company-facts rows (REQUIREMENTS §22): a
    /// <see cref="SponsorInfo"/> (logos / description / website / tier — what drives
    /// the PUBLIC sponsors page) whose company has NO active contact in this
    /// edition. These are the stale / duplicate records (e.g. a booth order
    /// processed under a wrong or later-changed company id) the organizer can now
    /// safely delete. A facts row for a LIVE company is never listed here (and the
    /// service refuses it).
    /// </summary>
    public List<OrphanedFacts> OrphanedSponsorFacts { get; private set; } = new();
    public record OrphanedFacts(int Id, string CompanyId, string? ShortDescription);

    // --- Search / sort / paging (GET-bound so links/bookmarks keep state).
    //     Mirrors the Participants / Speakers / Attendees grids (REQUIREMENTS §20/§21).
    //     The roster is grouped per company; we filter + sort + page that derived
    //     set (one edition's sponsors is a small, bounded set). ------------------
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    /// <summary>Sort column key: company | contacts | open | done | overdue | total | nextdue. Default company.</summary>
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "company";
    [BindProperty(SupportsGet = true)] public bool Desc { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNo { get; set; } = 1;

    public GridPage Paging { get; private set; }

    public bool NextDescFor(string col) => Sort == col && !Desc;
    public string SortIndicator(string col) => Sort != col ? "" : (Desc ? " ▼" : " ▲");
    public string AriaSort(string col) => Sort != col ? "none" : (Desc ? "descending" : "ascending");

    public record CompanyRow(
        string CompanyId,
        string CompanyName,
        List<Contact> Contacts,
        int Open, int InProgress, int Done, int Overdue, int Total,
        DateOnly? NextDue);

    public record Contact(int ParticipantId, string Name, string Email, bool IsActive);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Delete a stale / orphaned sponsor company-facts row (REQUIREMENTS §22).
    /// Safe semantics live in <see cref="SponsorInfoDeletionService"/>: a facts row
    /// whose company still has an active sponsor contact is REFUSED (its public
    /// card is live); only an orphaned row is removed. Organizer-only,
    /// edition-scoped; the page's confirm modal gates the click.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteFactsAsync(int sponsorInfoId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var result = await _infoDeletion.DeleteAsync(me.EventId, sponsorInfoId, ct);
        switch (result.Status)
        {
            case SponsorInfoDeletionService.DeletionStatus.Deleted:
                Message = $"Stale company-facts row for \"{result.SponsorCompanyId}\" was deleted.";
                break;
            case SponsorInfoDeletionService.DeletionStatus.Blocked:
                Error = $"The facts for \"{result.SponsorCompanyId}\" were not deleted: the company "
                        + $"still has {result.ActiveContactCount} active contact(s). Handle the "
                        + "contacts first.";
                break;
            default:
                Error = "That company-facts row could not be found in this edition.";
                break;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnGetDownloadAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var contacts = await _db.Participants
            .Where(p => p.EventId == me.EventId && p.Role == ParticipantRole.Sponsor)
            .Select(p => new
            {
                p.Id, p.SponsorCompanyId, p.FullName, p.Email, p.IsActive, p.Phone
            })
            .ToListAsync(ct);

        var tasks = await _db.Tasks
            .Where(t => t.EventId == me.EventId
                        && (t.SponsorCompanyId != null
                            || (t.SourceKey != null && t.SourceKey.StartsWith("sponsor:"))))
            .Select(t => new
            {
                t.Id, t.SponsorCompanyId, t.Title, t.DueDate, t.State,
                t.AssignedParticipantId, t.SourceKey, t.CreatedAt
            })
            .ToListAsync(ct);

        using var wb = new XLWorkbook();

        // --- Sheet 1: Company roster ---------------------------------------
        var companies = wb.Worksheets.Add("Companies");
        var hdr = new[] { "Company id", "Contacts", "Open", "In progress", "Done", "Overdue", "Total tasks", "Next due" };
        for (int i = 0; i < hdr.Length; i++) companies.Cell(1, i + 1).Value = hdr[i];
        companies.Range(1, 1, 1, hdr.Length).Style.Font.Bold = true;

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        int row = 2;
        foreach (var grp in tasks.GroupBy(t => t.SponsorCompanyId ?? "(no company id)"))
        {
            var coList = string.Join("; ", contacts
                .Where(c => string.Equals(c.SponsorCompanyId, grp.Key, StringComparison.OrdinalIgnoreCase))
                .Select(c => $"{c.FullName} <{c.Email}>"));
            var open = grp.Count(t => t.State == TaskState.Open);
            var ip   = grp.Count(t => t.State == TaskState.InProgress);
            var done = grp.Count(t => t.State == TaskState.Done);
            var ovr  = grp.Count(t => t.State != TaskState.Done && t.DueDate is not null && t.DueDate < today);
            var nxt  = grp.Where(t => t.State != TaskState.Done && t.DueDate is not null)
                          .Min(t => t.DueDate);
            companies.Cell(row, 1).Value = grp.Key;
            companies.Cell(row, 2).Value = coList;
            companies.Cell(row, 3).Value = open;
            companies.Cell(row, 4).Value = ip;
            companies.Cell(row, 5).Value = done;
            companies.Cell(row, 6).Value = ovr;
            companies.Cell(row, 7).Value = grp.Count();
            companies.Cell(row, 8).Value = nxt?.ToString("dd/MM/yyyy") ?? "";
            row++;
        }
        companies.Columns().AdjustToContents();

        // --- Sheet 2: Contacts (all sponsor-role participants) -------------
        var contactsSheet = wb.Worksheets.Add("Contacts");
        var ch = new[] { "Company id", "Name", "Email", "Phone", "Active" };
        for (int i = 0; i < ch.Length; i++) contactsSheet.Cell(1, i + 1).Value = ch[i];
        contactsSheet.Range(1, 1, 1, ch.Length).Style.Font.Bold = true;
        int crow = 2;
        foreach (var c in contacts.OrderBy(c => c.SponsorCompanyId ?? "(none)").ThenBy(c => c.FullName))
        {
            contactsSheet.Cell(crow, 1).Value = c.SponsorCompanyId ?? "";
            contactsSheet.Cell(crow, 2).Value = c.FullName;
            contactsSheet.Cell(crow, 3).Value = c.Email;
            contactsSheet.Cell(crow, 4).Value = c.Phone ?? "";
            contactsSheet.Cell(crow, 5).Value = c.IsActive ? "Yes" : "No";
            crow++;
        }
        contactsSheet.Columns().AdjustToContents();

        // --- Sheet 3: Tasks (every sponsor task, every state) --------------
        var tasksSheet = wb.Worksheets.Add("Tasks");
        var th = new[] { "Company id", "Task", "State", "Due", "Days overdue", "Assignee id", "SourceKey", "Created" };
        for (int i = 0; i < th.Length; i++) tasksSheet.Cell(1, i + 1).Value = th[i];
        tasksSheet.Range(1, 1, 1, th.Length).Style.Font.Bold = true;
        int trow = 2;
        foreach (var t in tasks.OrderBy(t => t.SponsorCompanyId).ThenBy(t => t.DueDate))
        {
            tasksSheet.Cell(trow, 1).Value = t.SponsorCompanyId ?? "";
            tasksSheet.Cell(trow, 2).Value = t.Title;
            tasksSheet.Cell(trow, 3).Value = t.State.ToString();
            tasksSheet.Cell(trow, 4).Value = t.DueDate?.ToString("dd/MM/yyyy") ?? "";
            int overdueDays = (t.State != TaskState.Done && t.DueDate is not null && t.DueDate < today)
                ? today.DayNumber - t.DueDate.Value.DayNumber : 0;
            tasksSheet.Cell(trow, 5).Value = overdueDays;
            tasksSheet.Cell(trow, 6).Value = t.AssignedParticipantId?.ToString() ?? "";
            tasksSheet.Cell(trow, 7).Value = t.SourceKey ?? "";
            tasksSheet.Cell(trow, 8).Value = t.CreatedAt.UtcDateTime;
            trow++;
        }
        tasksSheet.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"sponsors-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx");
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        var contacts = await _db.Participants
            .Where(p => p.EventId == eventId && p.Role == ParticipantRole.Sponsor)
            .Select(p => new
            {
                p.Id, p.SponsorCompanyId, p.FullName, p.Email, p.IsActive
            })
            .ToListAsync(ct);

        var tasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && (t.SponsorCompanyId != null
                            || (t.SourceKey != null && t.SourceKey.StartsWith("sponsor:"))))
            .Select(t => new { t.SponsorCompanyId, t.DueDate, t.State })
            .ToListAsync(ct);

        TotalTasks   = tasks.Count;
        DoneTasks    = tasks.Count(t => t.State == TaskState.Done);
        OverdueTasks = tasks.Count(t => t.State != TaskState.Done
                                         && t.DueDate is not null
                                         && t.DueDate < today);

        // Group everything by SponsorCompanyId.
        var allCompanyIds = contacts.Select(c => c.SponsorCompanyId)
            .Concat(tasks.Select(t => t.SponsorCompanyId))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Resolve each company's display NAME (don't show the raw id). Same
        // Company Manager chain (public -> legal name) the sponsor-facing pages
        // and the SponsorAdmin dashboard use; falls back to "Company {id}" only
        // when the lookup is unavailable. Resolved once per company per request.
        var names = await ResolveCompanyNamesAsync(allCompanyIds, ct);

        var allCompanies = allCompanyIds.Select(cid =>
        {
            var co = contacts
                .Where(c => string.Equals(c.SponsorCompanyId, cid, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.FullName)
                .Select(c => new Contact(c.Id, c.FullName, c.Email, c.IsActive))
                .ToList();
            var t = tasks.Where(t => string.Equals(t.SponsorCompanyId, cid, StringComparison.OrdinalIgnoreCase)).ToList();
            var open  = t.Count(x => x.State == TaskState.Open);
            var ip    = t.Count(x => x.State == TaskState.InProgress);
            var done  = t.Count(x => x.State == TaskState.Done);
            var ovr   = t.Count(x => x.State != TaskState.Done && x.DueDate is not null && x.DueDate < today);
            var nxt   = t.Where(x => x.State != TaskState.Done && x.DueDate is not null).Min(x => (DateOnly?)x.DueDate);
            var name  = names.TryGetValue(cid, out var nm) ? nm : $"Company {cid}";
            return new CompanyRow(cid, name, co, open, ip, done, ovr, t.Count, nxt);
        }).ToList();

        // Free-text search over the company name + id + any contact name/email.
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            allCompanies = allCompanies
                .Where(r => r.CompanyName.Contains(s, StringComparison.OrdinalIgnoreCase)
                            || r.CompanyId.Contains(s, StringComparison.OrdinalIgnoreCase)
                            || r.Contacts.Any(c => c.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                                                   || c.Email.Contains(s, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        // Deterministic ordering (company id tiebreak) for the chosen column.
        Func<CompanyRow, object> key = Sort switch
        {
            "contacts" => r => r.Contacts.Count,
            "open"     => r => r.Open,
            "done"     => r => r.Done,
            "overdue"  => r => r.Overdue,
            "total"    => r => r.Total,
            "nextdue"  => r => r.NextDue ?? DateOnly.MaxValue,
            _          => r => r.CompanyName,
        };
        var ordered = (Desc
            ? allCompanies.OrderByDescending(key)
            : allCompanies.OrderBy(key))
            .ThenBy(r => r.CompanyName, StringComparer.OrdinalIgnoreCase);

        var sortedCompanies = ordered.ToList();

        Paging = GridPaging.Resolve(PageNo, GridPaging.DefaultPageSize, sortedCompanies.Count);
        Companies = sortedCompanies
            .Skip(Paging.Skip).Take(Paging.PageSize)
            .ToList();

        ContactsWithoutCompany = contacts
            .Where(c => string.IsNullOrWhiteSpace(c.SponsorCompanyId))
            .OrderBy(c => c.FullName)
            .Select(c => new Contact(c.Id, c.FullName, c.Email, c.IsActive))
            .ToList();

        CompanyCount = allCompanyIds.Count;
        ContactCount = contacts.Count;

        // Orphaned company-facts (§22): a SponsorInfo whose company has NO active
        // contact in this edition. The set of company ids WITH an active contact is
        // the "live" set; any facts row outside it is a stale / duplicate record.
        var liveCompanyIds = new HashSet<string>(
            (await _db.Participants
                .Where(p => p.EventId == eventId
                            && p.Role == ParticipantRole.Sponsor
                            && p.IsActive
                            && p.SponsorCompanyId != null)
                .Select(p => p.SponsorCompanyId!)
                .Distinct()
                .ToListAsync(ct)),
            StringComparer.OrdinalIgnoreCase);

        OrphanedSponsorFacts = (await _db.SponsorInfos
                .Where(s => s.EventId == eventId)
                .Select(s => new { s.Id, s.SponsorCompanyId, s.CompanyDescriptionShort })
                .ToListAsync(ct))
            .Where(s => !liveCompanyIds.Contains(s.SponsorCompanyId))
            .OrderBy(s => s.SponsorCompanyId, StringComparer.OrdinalIgnoreCase)
            .Select(s => new OrphanedFacts(s.Id, s.SponsorCompanyId, s.CompanyDescriptionShort))
            .ToList();
    }

    /// <summary>
    /// Map each sponsor company id to its display name via Company Manager
    /// (public name -&gt; legal name, the canonical <see cref="SponsorCompanyName"/>
    /// chain). Resilient: a failed/disabled lookup leaves the id out of the map so
    /// the caller falls back to "Company {id}" rather than 500-ing the page.
    /// Mirrors SponsorAdmin/Dashboard.ResolveCompanyNamesAsync.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveCompanyNamesAsync(
        IEnumerable<string> companyIds, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!_cmOptions.Enabled) return map;

        foreach (var cid in companyIds)
        {
            if (!int.TryParse(cid, out var idInt)) continue;
            try
            {
                var c = await _cm.GetCompanyAsync(idInt, ct);
                if (c is null) continue;
                map[cid] = SponsorCompanyName.Resolve(c.PublicName, c.Name, billingName: null, companyId: cid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sponsors page: company-name lookup failed for {CompanyId}.", cid);
            }
        }
        return map;
    }
}
