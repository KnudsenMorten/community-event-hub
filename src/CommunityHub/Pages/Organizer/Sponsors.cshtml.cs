using ClosedXML.Excel;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
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

    public SponsorsModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant, TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }

    public int CompanyCount { get; private set; }
    public int ContactCount { get; private set; }
    public int TotalTasks { get; private set; }
    public int DoneTasks { get; private set; }
    public int OverdueTasks { get; private set; }

    public List<CompanyRow> Companies { get; private set; } = new();
    public List<Contact> ContactsWithoutCompany { get; private set; } = new();

    public record CompanyRow(
        string CompanyId,
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

    public async Task<IActionResult> OnGetDownloadAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

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
                            || (t.SourceKey != null && t.SourceKey.StartsWith("woo:"))))
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
                            || (t.SourceKey != null && t.SourceKey.StartsWith("woo:"))))
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

        Companies = allCompanyIds.Select(cid =>
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
            return new CompanyRow(cid, co, open, ip, done, ovr, t.Count, nxt);
        }).ToList();

        ContactsWithoutCompany = contacts
            .Where(c => string.IsNullOrWhiteSpace(c.SponsorCompanyId))
            .OrderBy(c => c.FullName)
            .Select(c => new Contact(c.Id, c.FullName, c.Email, c.IsActive))
            .ToList();

        CompanyCount = Companies.Count;
        ContactCount = contacts.Count;
    }
}
