using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Sponsors;

/// <summary>
/// Builds a sponsor company's <see cref="SponsorDeliverables"/> lifecycle-completion rollup
/// (REQUIREMENTS §135) from EXISTING data only — it is a read-only AGGREGATOR, never a new
/// source of truth, and it never writes. It resolves one
/// <see cref="SponsorDeliverableSignal"/> per lifecycle stage, then hands them to the pure
/// <see cref="SponsorDeliverablesCalculator"/> for the score + the overdue/at-risk split.
///
/// <para>This COMPLEMENTS Zoho exhibitor management (it does not replace it): the board is a
/// crew/organizer completion view computed from the hub's own SQL.</para>
///
/// <para>Stage sourcing (each reads the SAME hub data the auto-close in
/// <see cref="Integrations.SponsorOrderPullService"/> uses, so the board and the task list
/// agree):</para>
/// <list type="bullet">
///   <item><b>onboarding</b> — a saved <see cref="SponsorInfo.CompanyDescription"/> (the
///   "initial onboarding" deliverable; mirrors the <c>overviewOnFile</c> auto-close signal).</item>
///   <item><b>logo</b> — a logo is on file: <see cref="SponsorInfo.LogoRasterPath"/> /
///   <see cref="SponsorInfo.LogoVectorPath"/> set (§61), OR a <see cref="SponsorUploadAudit"/>
///   for a logo kind (some / print / zoho). Undated (collected on Company Details with no
///   deadline-bearing task), so it is shown as done/not-done, never overdue.</item>
///   <item><b>booth-materials</b> — EXHIBITOR-ONLY: a wall design / collateral is on file —
///   any <see cref="SponsorBoothMaterial"/>, OR a watcher-seen file in the SPONSORWALL folder
///   (<see cref="SponsorUploadFile"/>), OR a "wall" <see cref="SponsorUploadAudit"/>.</item>
///   <item><b>booth-members</b> — EXHIBITOR-ONLY: at least one active (non-tombstoned)
///   <see cref="SponsorBoothMember"/>.</item>
///   <item><b>tasks</b> — every OTHER assigned sponsor to-do is complete; i.e. no remaining
///   OPEN <see cref="ParticipantTask"/> other than the three tasks already represented by the
///   onboarding / wall / members stages above.</item>
/// </list>
///
/// <para>Deadlines come from the matching sponsor <see cref="ParticipantTask.DueDate"/> (the
/// onboarding / wall / members tasks carry the dated deadlines); the catch-all "tasks" stage
/// uses the earliest open due date. A stage with no dated task is undated (never overdue).</para>
/// </summary>
public sealed class SponsorDeliverablesService
{
    private readonly CommunityHubDbContext _db;

    public SponsorDeliverablesService(CommunityHubDbContext db) => _db = db;

    // Fix-it deep links (kept here so both views stay consistent).
    public const string CompanyDetailsLink = "/Sponsor/CompanyDetails";
    public const string TasksLink = "/Sponsor/Tasks";

    // Sponsor task SourceKey slugs that BACK a dedicated stage — the Slug() of the JSON task
    // titles, identical to the auto-close in SponsorOrderPullService. The catch-all "tasks"
    // stage excludes these three so they are not double-counted.
    private const string OnboardingSlug = "initial-onboarding-of-sponsor";
    private const string WallSlug = "upload-sponsor-wall-design-in-vector-format";
    private const string MembersSlug = "register-booth-members";

    private const string SponsorTaskPrefix = "sponsor:";

    /// <summary>
    /// Deliverables for ONE company. <paramref name="companyName"/> is the resolved display
    /// name (the page resolves it via Company Manager, the same chain the other sponsor pages
    /// use); when null the company id is used. <paramref name="today"/> is the date overdue is
    /// measured against (UTC date at the call site).
    /// </summary>
    public async Task<SponsorDeliverables> BuildForCompanyAsync(
        int eventId, string companyId, DateOnly today,
        string? companyName = null, CancellationToken ct = default)
    {
        var info = await _db.SponsorInfos.AsNoTracking()
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);

        var members = await _db.SponsorBoothMembers
            .AnyAsync(m => m.EventId == eventId && m.SponsorCompanyId == companyId && m.DeletedAt == null, ct);

        var materials = await _db.SponsorBoothMaterials
            .AnyAsync(m => m.EventId == eventId && m.SponsorCompanyId == companyId, ct);

        var wallFile = await _db.SponsorUploadFiles
            .AnyAsync(f => f.Location.EventId == eventId
                           && f.Location.SponsorCompanyId == companyId
                           && f.Location.FolderKey == "SPONSORWALL", ct);

        var logoAudit = await _db.SponsorUploadAudits
            .AnyAsync(a => a.EventId == eventId && a.SponsorCompanyId == companyId
                           && (a.Kind == "some" || a.Kind == "print" || a.Kind == "zoho"), ct);

        var wallAudit = await _db.SponsorUploadAudits
            .AnyAsync(a => a.EventId == eventId && a.SponsorCompanyId == companyId && a.Kind == "wall", ct);

        var tasks = (await _db.Tasks
                .Where(t => t.EventId == eventId
                            && t.SponsorCompanyId == companyId
                            && t.SourceKey != null
                            && t.SourceKey.StartsWith(SponsorTaskPrefix))
                .Select(t => new { t.SourceKey, t.State, t.DueDate })
                .ToListAsync(ct))
            .Select(t => new TaskRow(Slug(t.SourceKey), t.State, t.DueDate))
            .ToList();

        var signals = BuildSignals(info, members, materials, wallFile, logoAudit, wallAudit, tasks);

        return SponsorDeliverablesCalculator.Compute(
            companyId, companyName ?? companyId, IsExhibitor(info, members, materials, wallFile, wallAudit, tasks),
            today, signals);
    }

    /// <summary>
    /// Deliverables for EVERY sponsor company in the edition (organizer board), sorted
    /// AT-RISK first (overdue), then lowest completion, then name — so the companies that need
    /// chasing are at the top. Batch-loaded; no per-company round-trips.
    /// <paramref name="companyNames"/> maps company id → display name (the page resolves these
    /// via Company Manager); a missing entry falls back to the id.
    /// </summary>
    public async Task<IReadOnlyList<SponsorDeliverables>> BuildBoardAsync(
        int eventId, DateOnly today,
        IReadOnlyDictionary<string, string>? companyNames = null, CancellationToken ct = default)
    {
        var infos = (await _db.SponsorInfos.AsNoTracking()
                .Where(s => s.EventId == eventId)
                .ToListAsync(ct))
            .ToDictionary(s => s.SponsorCompanyId, StringComparer.Ordinal);

        var memberCompanies = (await _db.SponsorBoothMembers
                .Where(m => m.EventId == eventId && m.DeletedAt == null)
                .Select(m => m.SponsorCompanyId).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var materialCompanies = (await _db.SponsorBoothMaterials
                .Where(m => m.EventId == eventId)
                .Select(m => m.SponsorCompanyId).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var wallFileCompanies = (await _db.SponsorUploadFiles
                .Where(f => f.Location.EventId == eventId && f.Location.FolderKey == "SPONSORWALL")
                .Select(f => f.Location.SponsorCompanyId).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var audits = await _db.SponsorUploadAudits
            .Where(a => a.EventId == eventId)
            .Select(a => new { a.SponsorCompanyId, a.Kind })
            .ToListAsync(ct);
        var logoCompanies = audits
            .Where(a => a.Kind is "some" or "print" or "zoho")
            .Select(a => a.SponsorCompanyId).ToHashSet(StringComparer.Ordinal);
        var wallAuditCompanies = audits
            .Where(a => a.Kind == "wall")
            .Select(a => a.SponsorCompanyId).ToHashSet(StringComparer.Ordinal);

        var tasksByCompany = (await _db.Tasks
                .Where(t => t.EventId == eventId
                            && t.SponsorCompanyId != null
                            && t.SourceKey != null
                            && t.SourceKey.StartsWith(SponsorTaskPrefix))
                .Select(t => new { Cid = t.SponsorCompanyId!, t.SourceKey, t.State, t.DueDate })
                .ToListAsync(ct))
            .GroupBy(t => t.Cid, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TaskRow>)g.Select(t => new TaskRow(Slug(t.SourceKey), t.State, t.DueDate)).ToList(),
                StringComparer.Ordinal);

        var participantCompanies = (await _db.Participants
                .Where(p => p.EventId == eventId
                            && p.Role == ParticipantRole.Sponsor
                            && p.SponsorCompanyId != null)
                .Select(p => p.SponsorCompanyId!).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        // The full company set: anyone with a SponsorInfo row, any deliverable data on file,
        // a sponsor task, or a linked sponsor contact.
        var companyIds = new HashSet<string>(StringComparer.Ordinal);
        companyIds.UnionWith(infos.Keys);
        companyIds.UnionWith(memberCompanies);
        companyIds.UnionWith(materialCompanies);
        companyIds.UnionWith(wallFileCompanies);
        companyIds.UnionWith(logoCompanies);
        companyIds.UnionWith(wallAuditCompanies);
        companyIds.UnionWith(tasksByCompany.Keys);
        companyIds.UnionWith(participantCompanies);

        var result = new List<SponsorDeliverables>(companyIds.Count);
        foreach (var cid in companyIds)
        {
            infos.TryGetValue(cid, out var info);
            var tasks = tasksByCompany.TryGetValue(cid, out var tl) ? tl : Array.Empty<TaskRow>();
            var members = memberCompanies.Contains(cid);
            var materials = materialCompanies.Contains(cid);
            var wallFile = wallFileCompanies.Contains(cid);
            var logoAudit = logoCompanies.Contains(cid);
            var wallAudit = wallAuditCompanies.Contains(cid);

            var signals = BuildSignals(info, members, materials, wallFile, logoAudit, wallAudit, tasks);
            var name = companyNames is not null && companyNames.TryGetValue(cid, out var nm) ? nm : cid;

            result.Add(SponsorDeliverablesCalculator.Compute(
                cid, name, IsExhibitor(info, members, materials, wallFile, wallAudit, tasks), today, signals));
        }

        return result
            .OrderByDescending(r => r.AtRisk)         // at-risk (overdue) first
            .ThenByDescending(r => r.OverdueCount)    // most overdue stages first
            .ThenBy(r => r.Percent)                   // then least complete
            .ThenBy(r => r.CompanyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private readonly record struct TaskRow(string Slug, TaskState State, DateOnly? DueDate);

    /// <summary>
    /// A company is treated as an EXHIBITOR (booth stages apply) when its package includes a
    /// booth, OR when any booth-specific data/task is already on file — so the booth stages
    /// surface even if the package flag hasn't been set yet by the order pull.
    /// </summary>
    private static bool IsExhibitor(
        SponsorInfo? info, bool members, bool materials, bool wallFile, bool wallAudit,
        IReadOnlyList<TaskRow> tasks)
        => (info?.HasBooth ?? false)
           || members || materials || wallFile || wallAudit
           || tasks.Any(t => t.Slug is WallSlug or MembersSlug);

    /// <summary>
    /// Assemble the ordered (lifecycle) signal list for one company from already-resolved
    /// inputs. Pure (no DB) so the single + board paths share identical logic.
    /// </summary>
    private static List<SponsorDeliverableSignal> BuildSignals(
        SponsorInfo? info, bool members, bool materials, bool wallFile, bool logoAudit, bool wallAudit,
        IReadOnlyList<TaskRow> tasks)
    {
        var isExhibitor = IsExhibitor(info, members, materials, wallFile, wallAudit, tasks);

        var onboardingDone = !string.IsNullOrWhiteSpace(info?.CompanyDescription);
        var logoDone =
            !string.IsNullOrWhiteSpace(info?.LogoRasterPath)
            || !string.IsNullOrWhiteSpace(info?.LogoVectorPath)
            || logoAudit;
        var materialsDone = materials || wallFile || wallAudit;
        var membersDone = members;

        // Catch-all: no OPEN sponsor task remains OTHER than the three already represented by
        // their own stages above (mirrors the §134 "other to-dos" exclusion).
        var tasksDone = !tasks.Any(t =>
            t.State != TaskState.Done
            && t.Slug is not (OnboardingSlug or WallSlug or MembersSlug));

        // Deadlines from the matching dated tasks; the catch-all uses the earliest open due.
        var onboardingDeadline = EarliestDue(tasks, OnboardingSlug);
        var materialsDeadline = EarliestDue(tasks, WallSlug);
        var membersDeadline = EarliestDue(tasks, MembersSlug);
        var tasksDeadline = tasks
            .Where(t => t.State != TaskState.Done
                        && t.Slug is not (OnboardingSlug or WallSlug or MembersSlug)
                        && t.DueDate is not null)
            .Select(t => t.DueDate)
            .DefaultIfEmpty(null)
            .Min();

        return new List<SponsorDeliverableSignal>
        {
            new("onboarding",      "Contract & onboarding",  true,        onboardingDone, onboardingDeadline, CompanyDetailsLink),
            new("logo",            "Logo uploaded",          true,        logoDone,       null,               CompanyDetailsLink),
            new("booth-materials", "Booth materials",        isExhibitor, materialsDone,  materialsDeadline,  CompanyDetailsLink),
            new("booth-members",   "Booth members present",  isExhibitor, membersDone,    membersDeadline,    CompanyDetailsLink),
            new("tasks",           "Assigned tasks done",    true,        tasksDone,      tasksDeadline,      TasksLink),
        };
    }

    /// <summary>Earliest due date among tasks whose slug equals <paramref name="slug"/>, or null.</summary>
    private static DateOnly? EarliestDue(IReadOnlyList<TaskRow> tasks, string slug) =>
        tasks.Where(t => t.Slug == slug && t.DueDate is not null)
             .Select(t => t.DueDate)
             .DefaultIfEmpty(null)
             .Min();

    /// <summary>The trailing slug of a sponsor task SourceKey ("sponsor:{companyId}:{slug}").</summary>
    private static string Slug(string? sourceKey)
    {
        if (string.IsNullOrEmpty(sourceKey)) return string.Empty;
        var i = sourceKey.LastIndexOf(':');
        return i >= 0 && i < sourceKey.Length - 1 ? sourceKey[(i + 1)..] : sourceKey;
    }
}
