using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer.SponsorAdmin;

/// <summary>
/// Per-sponsor-company status dashboard. One row per sponsor company
/// (Participant.SponsorCompanyId), counting:
///   - tasks done / total / overdue (from <see cref="ParticipantTask"/>)
///   - leads total / last 7 days (zero until the Zoho pipeline lands)
///   - last Zoho sync timestamp (null until the pipeline lands)
///
/// Sorted by overdue tasks first so the operator sees the at-risk
/// sponsors immediately on open. The "overdue" arithmetic uses today's
/// date in UTC to match the existing Sponsors.cshtml computation.
/// </summary>
[Authorize]
public class DashboardModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly ZohoOptions _zoho;

    public DashboardModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        ZohoOptions zoho)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _zoho = zoho;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>
    /// True only when the leads pipeline is genuinely unconfigured: the Zoho
    /// CRM pull is off AND no SponsorLead rows exist for the event. Once the
    /// CRM integration is switched on, or any lead has landed, the banner
    /// stands down. Computed in OnGetAsync -- not a static default.
    /// </summary>
    public bool ZohoPipelinePending { get; private set; }

    public record SponsorRow(
        string CompanyId,
        int Contacts,
        int TasksTotal,
        int TasksDone,
        int TasksOverdue,
        int LeadsTotal,
        int LeadsLast7d,
        DateTimeOffset? LastZohoSync);

    public List<SponsorRow> Rows { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        // Pull sponsor participants once and group locally so we get a
        // per-company contact count cheaply.
        var sponsorParticipants = await _db.Participants
            .Where(p => p.EventId == me.EventId && p.Role == ParticipantRole.Sponsor)
            .Select(p => new { p.Id, p.SponsorCompanyId })
            .ToListAsync(ct);

        // Pull sponsor tasks for the event. Same predicate as
        // Sponsors.cshtml.cs Download handler so the dashboard counts
        // and the existing exports always agree.
        var sponsorTasks = await _db.Tasks
            .Where(t => t.EventId == me.EventId
                        && (t.SponsorCompanyId != null
                            || (t.SourceKey != null && t.SourceKey.StartsWith("sponsor:"))))
            .Select(t => new { t.SponsorCompanyId, t.State, t.DueDate })
            .ToListAsync(ct);

        // Group sponsor tasks by company, tally the four states.
        var taskAgg = sponsorTasks
            .GroupBy(t => t.SponsorCompanyId ?? "(no company id)")
            .Select(g => new
            {
                CompanyId    = g.Key,
                TasksTotal   = g.Count(),
                TasksDone    = g.Count(t => t.State == TaskState.Done),
                TasksOverdue = g.Count(t => t.State != TaskState.Done && t.DueDate is not null && t.DueDate.Value < today),
            })
            .ToDictionary(x => x.CompanyId, x => x);

        // Include companies that have contacts but zero tasks too --
        // they still show on the dashboard with empty counters so we
        // can spot a company that simply has no work assigned yet.
        var contactsByCompany = sponsorParticipants
            .Where(p => !string.IsNullOrEmpty(p.SponsorCompanyId))
            .GroupBy(p => p.SponsorCompanyId!)
            .ToDictionary(g => g.Key, g => g.Count());

        // Leads pipeline columns (live since v1.2.8; were zero-stubs while
        // DbSet<SponsorLead> didn't exist).
        var weekAgo = _clock.GetUtcNow().AddDays(-7);
        var leadAgg = (await _db.SponsorLeads
            .Where(l => l.EventId == me.EventId)
            .Select(l => new { l.SponsorCompanyId, l.CapturedAt, l.LastSyncedAt })
            .ToListAsync(ct))
            .GroupBy(l => l.SponsorCompanyId)
            .ToDictionary(g => g.Key, g => new
            {
                Total  = g.Count(),
                Last7d = g.Count(l => l.CapturedAt >= weekAgo),
                LastSync = (DateTimeOffset?)g.Max(l => l.LastSyncedAt),
            });

        // Banner state: "pending" only when the CRM pull is off AND no leads
        // have ever landed for this event. Either condition clears it.
        ZohoPipelinePending = !(_zoho.CrmEnabled || leadAgg.Count > 0);

        var allCompanyIds = new HashSet<string>(taskAgg.Keys);
        foreach (var cid in contactsByCompany.Keys) allCompanyIds.Add(cid);
        foreach (var cid in leadAgg.Keys) allCompanyIds.Add(cid);

        Rows = allCompanyIds
            .Select(cid =>
            {
                taskAgg.TryGetValue(cid, out var ta);
                contactsByCompany.TryGetValue(cid, out var contactCount);
                leadAgg.TryGetValue(cid, out var la);
                return new SponsorRow(
                    CompanyId:    cid,
                    Contacts:     contactCount,
                    TasksTotal:   ta?.TasksTotal   ?? 0,
                    TasksDone:    ta?.TasksDone    ?? 0,
                    TasksOverdue: ta?.TasksOverdue ?? 0,
                    LeadsTotal:   la?.Total  ?? 0,
                    LeadsLast7d:  la?.Last7d ?? 0,
                    LastZohoSync: la?.LastSync);
            })
            .OrderByDescending(r => r.TasksOverdue)
            .ThenBy(r => r.CompanyId)
            .ToList();

        return Page();
    }
}
