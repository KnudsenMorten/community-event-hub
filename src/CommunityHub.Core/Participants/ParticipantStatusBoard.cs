using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Sponsors;
using CommunityHub.Core.Tasks;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Participants;

/// <summary>Which rows the board is asked for — the completion filter (§1085).</summary>
public enum ParticipantStatusFilter
{
    /// <summary>Everyone in scope, at-risk first.</summary>
    All = 0,

    /// <summary>Wizard finished — every evaluable step done.</summary>
    Complete = 1,

    /// <summary>Started but not finished.</summary>
    InProgress = 2,

    /// <summary>Nothing done yet — the people a welcome never landed with.</summary>
    NotStarted = 3,

    /// <summary>At least one open task past its due date, whatever the wizard says.</summary>
    Overdue = 4,
}

/// <summary>
/// §1085 — one row of the participant-status board: a PERSON, or (for sponsors) a COMPANY.
/// </summary>
/// <param name="ParticipantId">
/// The person the wizard was built for. On a sponsor row that is the company's representative
/// contact — the checklist is company state, so any coordinator gives the same answer.
/// </param>
/// <param name="ContactCount">
/// How many active contacts this row covers — 1 for a person, N for a sponsor company. Shown so a
/// company row never reads as a single person's backlog.
/// </param>
/// <param name="OpenStepKeys">
/// Step keys, with <paramref name="StepTitlePrefix"/> to resolve them. 🔑 The VIEW localises; Core
/// composes no English (the hub ships en + da-DK).
/// </param>
/// <param name="MissingFieldKeys">
/// §854/§1081 — for a sponsor, the CEH-owned content fields that are still blank, under
/// <see cref="SponsorCompanyContent.ResourcePrefix"/>. The same fields the sponsor's own reminder
/// names, so organizer and sponsor read one fact. Empty for every other role (§1083 was closed: the
/// operator does not want per-field naming for the personal roles).
/// </param>
public sealed record ParticipantStatusRow(
    string Key,
    int ParticipantId,
    string? SponsorCompanyId,
    string Name,
    string? Email,
    ParticipantRole Role,
    string? CompanyName,
    int ContactCount,
    bool HasWizard,
    int Percent,
    int DoneCount,
    int EvaluableCount,
    IReadOnlyList<string> OpenStepKeys,
    string StepTitlePrefix,
    IReadOnlyList<string> MissingFieldKeys,
    int OpenTaskCount,
    int OverdueTaskCount,
    DateOnly? EarliestOverdueDue)
{
    /// <summary>Finished — every evaluable step done. A row with no wizard is never complete.</summary>
    public bool IsComplete => HasWizard && EvaluableCount > 0 && DoneCount >= EvaluableCount;

    /// <summary>Nothing done yet.</summary>
    public bool NotStarted => HasWizard && DoneCount == 0;

    /// <summary>Started, not finished.</summary>
    public bool InProgress => HasWizard && DoneCount > 0 && !IsComplete;

    /// <summary>The at-risk test the default sort leads with: an open task is past its due date.</summary>
    public bool Overdue => OverdueTaskCount > 0;

    /// <summary>Does this row belong in the given filter?</summary>
    public bool Matches(ParticipantStatusFilter filter) => filter switch
    {
        ParticipantStatusFilter.Complete => IsComplete,
        ParticipantStatusFilter.InProgress => InProgress,
        ParticipantStatusFilter.NotStarted => NotStarted,
        ParticipantStatusFilter.Overdue => Overdue,
        _ => true,
    };
}

/// <summary>
/// §1085 — <b>ONE participant-status board for ALL roles, with a filter.</b>
/// </summary>
/// <remarks>
/// <para>🔑 <b>Why one page.</b> Operator 2026-08-13, after distrusting two sponsor boards:
/// <i>"in general we should consider one page for all roles with filter"</i> ·
/// <i>"instead of having 7 individual page"</i> · <i>"it makes no sense"</i>. Seven per-role boards
/// would be seven chances to disagree with each other, which is the failure they were reported for.</para>
///
/// <para>🔒 <b>Completion comes from <see cref="WizardProgressReader"/>, i.e. from the wizard
/// SERVICES.</b> No arithmetic of its own — see that type for why a second opinion is a bug even when
/// it is right.</para>
///
/// <para>🔴 <b>SPONSORS ARE GROUPED BY COMPANY.</b> Their wizard and their tasks are company-scoped
/// (one task row per company, <c>AssignedParticipantId = NULL</c>), so a per-contact row would repeat
/// one company's state N times and invite exactly the incident §1081 was reported for: chasing a
/// coordinator for work a colleague had already done.</para>
///
/// <para>⚠️ <b>System-closed tasks are excluded wherever tasks are counted</b>
/// (<see cref="TaskClosure.NotSystemClosed"/>). A retired row is <c>State = Done</c>, so counting it
/// would credit somebody with work nobody did — §1082's rule.</para>
///
/// <para>🔴 <b>THE COST CONTROL — read this before widening the scope.</b> A wizard build is several
/// queries, and the operator's objection to the completion sweep (<i>"but dont impact performance
/// necessary with this against sql"</i>) is about exactly this shape. Three things keep it bounded,
/// and all three are deliberate:</para>
/// <list type="number">
///   <item>Everything that is NOT the wizard — tasks, sponsor content, company names — is read in
///     BATCH before the loop: a fixed handful of queries whatever the population.</item>
///   <item>The ROLE FILTER is applied in SQL, before any wizard is built. Filtering one role is the
///     normal way this page is used, and it is what makes it cheap.</item>
///   <item>The caller caches the result briefly (see the page model), so paging and re-sorting a
///     board do not rebuild it.</item>
/// </list>
/// <para>⇒ The completion filter and the sort are applied by the CALLER, over the built rows, because
/// both need a percent that only exists after the build. That is why this returns the whole
/// role-scoped set rather than a page of it.</para>
/// </remarks>
public sealed class ParticipantStatusBoardBuilder
{
    private readonly CommunityHubDbContext _db;
    private readonly WizardProgressReader _wizards;
    private readonly TimeProvider _clock;

    public ParticipantStatusBoardBuilder(
        CommunityHubDbContext db, WizardProgressReader wizards, TimeProvider clock)
    {
        _db = db;
        _wizards = wizards;
        _clock = clock;
    }

    /// <summary>
    /// Every row in scope, at-risk first then least complete. Filter by role in SQL; filter by
    /// completion state afterwards with <see cref="ParticipantStatusRow.Matches"/>.
    /// </summary>
    /// <param name="role">One role, or null for every role.</param>
    /// <param name="includeTest">
    /// 🔴 §1212 — include TEST people and TEST sponsor companies. Default FALSE.
    /// </summary>
    /// <remarks>
    /// Operator 2026-09-12: <i>"it shows test users also - i need to filter that out with option to
    /// include"</i>. The board is a chase list, and a test account is not somebody to chase — but it
    /// is somebody to check on while testing, so the option stays rather than the rows disappearing
    /// for good. ⚠️ Both halves: a test PERSON (<c>Participant.IsTestUser</c>) and a test COMPANY
    /// (<c>SponsorInfo.IsTestData</c>) — sponsors are listed per company, so filtering only people
    /// would leave the test company on the board with its contacts removed.
    /// </remarks>
    public async Task<IReadOnlyList<ParticipantStatusRow>> BuildAsync(
        int eventId, ParticipantRole? role = null, CancellationToken ct = default,
        bool includeTest = false)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        // 🔒 The SAME population the reminders use (§499): someone who cannot sign in cannot be
        // chased and does not belong on a board about who still owes work. Deactivated contacts and
        // §242-suspended 1-day attendees are out — §1071 was the operator reporting exactly that
        // ("it still shows old sponsors that was deactivated") on the board this one replaces.
        var query = _db.Participants.AsNoTracking()
            .Remindable()
            .Where(p => p.EventId == eventId);
        if (role is not null) query = query.Where(p => p.Role == role.Value);

        // 🔴 §1212 — test PEOPLE out unless he asks for them.
        if (!includeTest) query = query.Where(p => !p.IsTestUser);

        var people = await query
            .Select(p => new
            {
                p.Id, p.Role, p.FullName, p.Email, p.SponsorCompanyId, p.IsEventCoordinator,
            })
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
        if (people.Count == 0) return Array.Empty<ParticipantStatusRow>();

        // ---- BATCH READS (a fixed number of queries, whatever the population) ------------------

        // Open tasks only — a done row is not outstanding — and never a system-closed one (§1082).
        var openTasks = await _db.Tasks.AsNoTracking()
            .Where(t => t.EventId == eventId && t.State != TaskState.Done)
            .Where(TaskClosure.NotSystemClosed)
            .Select(t => new OpenTask(t.AssignedParticipantId, t.SponsorCompanyId, t.DueDate))
            .ToListAsync(ct);

        var companyIds = people
            .Where(p => p.Role == ParticipantRole.Sponsor && !string.IsNullOrWhiteSpace(p.SponsorCompanyId))
            .Select(p => p.SponsorCompanyId!)
            .Distinct()
            .ToList();

        // 🔴 §1212 — and test COMPANIES. Sponsors are listed PER COMPANY, so dropping only the test
        // people would leave the test company on the board with an empty contact count — the row he
        // is objecting to, minus the explanation.
        if (!includeTest && companyIds.Count > 0)
        {
            var testCompanies = (await _db.SponsorInfos.AsNoTracking()
                    .Where(s => s.EventId == eventId && s.SponsorCompanyId != null && s.IsTestData)
                    .Select(s => s.SponsorCompanyId!)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (testCompanies.Count > 0)
            {
                people = people
                    .Where(p => p.SponsorCompanyId is null || !testCompanies.Contains(p.SponsorCompanyId))
                    .ToList();
                companyIds = companyIds.Where(c => !testCompanies.Contains(c)).ToList();
                if (people.Count == 0) return Array.Empty<ParticipantStatusRow>();
            }
        }

        // §1081 — which CEH-owned content fields are still blank, per company. One query for all of
        // them; the same status object the sponsor's own reminder and wizard read.
        var missingByCompany = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (companyIds.Count > 0)
        {
            var infos = await _db.SponsorInfos.AsNoTracking()
                .Where(s => s.EventId == eventId && s.SponsorCompanyId != null
                            && companyIds.Contains(s.SponsorCompanyId))
                .ToListAsync(ct);
            foreach (var cid in companyIds)
            {
                var info = infos.FirstOrDefault(i => i.SponsorCompanyId == cid);
                missingByCompany[cid] = SponsorCompanyContent.StatusOf(info).MissingFieldKeys;
            }
        }

        // §443 — company display names from the LOCAL copy the sync captured. Never Company Manager
        // on a request path: that is what made the old board ~8 s warm on PROD.
        var companyNames = companyIds.Count == 0
            ? new Dictionary<string, string>()
            : await SponsorCompanyNameService.ResolveFromLocalAsync(_db, eventId, companyIds, ct);

        // ---- ROWS ------------------------------------------------------------------------------

        var rows = new List<ParticipantStatusRow>();

        foreach (var p in people.Where(p => p.Role != ParticipantRole.Sponsor))
        {
            var progress = await _wizards.ReadAsync(eventId, p.Id, p.Role, ct);
            var (open, overdue, earliest) = TallyTasks(openTasks, p.Id, sponsorCompanyId: null, today);

            rows.Add(new ParticipantStatusRow(
                Key: $"p:{p.Id}",
                ParticipantId: p.Id,
                SponsorCompanyId: null,
                Name: string.IsNullOrWhiteSpace(p.FullName) ? (p.Email ?? $"#{p.Id}") : p.FullName,
                Email: p.Email,
                Role: p.Role,
                CompanyName: null,
                ContactCount: 1,
                HasWizard: progress.HasWizard,
                Percent: progress.Percent,
                DoneCount: progress.DoneCount,
                EvaluableCount: progress.EvaluableCount,
                OpenStepKeys: progress.OpenKeys,
                StepTitlePrefix: progress.TitlePrefix,
                MissingFieldKeys: Array.Empty<string>(),
                OpenTaskCount: open,
                OverdueTaskCount: overdue,
                EarliestOverdueDue: earliest));
        }

        // Sponsors: ONE row per company. A contact with no company link keeps a personal row — they
        // exist in the hub and hiding them would hide the problem (a contact the sync never linked).
        foreach (var group in people
                     .Where(p => p.Role == ParticipantRole.Sponsor)
                     .GroupBy(p => p.SponsorCompanyId ?? string.Empty, StringComparer.Ordinal))
        {
            var companyId = group.Key;
            var contacts = group.ToList();

            if (companyId.Length == 0)
            {
                foreach (var p in contacts)
                {
                    var (o, od, e) = TallyTasks(openTasks, p.Id, sponsorCompanyId: null, today);
                    rows.Add(new ParticipantStatusRow(
                        Key: $"p:{p.Id}",
                        ParticipantId: p.Id,
                        SponsorCompanyId: null,
                        Name: string.IsNullOrWhiteSpace(p.FullName) ? (p.Email ?? $"#{p.Id}") : p.FullName,
                        Email: p.Email,
                        Role: ParticipantRole.Sponsor,
                        CompanyName: null,
                        ContactCount: 1,
                        // No company ⇒ no checklist. Reported as "no wizard", never as 0%: they have
                        // not failed to do anything, there is nothing for them to do.
                        HasWizard: false,
                        Percent: 0, DoneCount: 0, EvaluableCount: 0,
                        OpenStepKeys: Array.Empty<string>(),
                        StepTitlePrefix: string.Empty,
                        MissingFieldKeys: Array.Empty<string>(),
                        OpenTaskCount: o, OverdueTaskCount: od, EarliestOverdueDue: e));
                }
                continue;
            }

            // 🔑 The wizard is COMPANY state, so it is built ONCE — for the coordinator where there
            // is one (the contact the sponsor mail addresses, §7c), else the oldest contact. Every
            // coordinator would return the same answer; building N of them would only cost N times.
            var representative = contacts.FirstOrDefault(c => c.IsEventCoordinator) ?? contacts[0];
            var companyProgress = await _wizards.ReadAsync(
                eventId, representative.Id, ParticipantRole.Sponsor, ct);

            // Company tasks + everything assigned to any of its contacts, counted once.
            var contactIds = contacts.Select(c => c.Id).ToHashSet();
            var mine = openTasks.Where(t =>
                (t.SponsorCompanyId is not null && t.SponsorCompanyId == companyId)
                || (t.AssignedParticipantId is int a && contactIds.Contains(a))).ToList();
            var overdueTasks = mine.Where(t => t.DueDate is not null && t.DueDate.Value < today).ToList();

            rows.Add(new ParticipantStatusRow(
                Key: $"c:{companyId}",
                ParticipantId: representative.Id,
                SponsorCompanyId: companyId,
                Name: companyNames.TryGetValue(companyId, out var nm) ? nm : $"Company {companyId}",
                Email: representative.Email,
                Role: ParticipantRole.Sponsor,
                CompanyName: companyNames.TryGetValue(companyId, out var cn) ? cn : null,
                ContactCount: contacts.Count,
                HasWizard: companyProgress.HasWizard,
                Percent: companyProgress.Percent,
                DoneCount: companyProgress.DoneCount,
                EvaluableCount: companyProgress.EvaluableCount,
                OpenStepKeys: companyProgress.OpenKeys,
                StepTitlePrefix: companyProgress.TitlePrefix,
                MissingFieldKeys: missingByCompany.TryGetValue(companyId, out var mf)
                    ? mf : Array.Empty<string>(),
                OpenTaskCount: mine.Count,
                OverdueTaskCount: overdueTasks.Count,
                EarliestOverdueDue: overdueTasks.Count == 0
                    ? null
                    : overdueTasks.Min(t => t.DueDate!.Value)));
        }

        return Sort(rows);
    }

    /// <summary>
    /// The default order — <b>at-risk first</b>, so the rows that need chasing are the ones on
    /// screen: overdue before on-time, then least complete, then a row with no wizard last (it needs
    /// nothing), then by name so the order is stable between loads.
    /// </summary>
    public static IReadOnlyList<ParticipantStatusRow> Sort(IEnumerable<ParticipantStatusRow> rows) =>
        rows
            .OrderByDescending(r => r.Overdue)
            .ThenByDescending(r => r.OverdueTaskCount)
            .ThenBy(r => r.HasWizard ? 0 : 1)
            .ThenBy(r => r.HasWizard ? r.Percent : int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The one shape the batch task read is projected into, so the tally is typed.</summary>
    private sealed record OpenTask(int? AssignedParticipantId, string? SponsorCompanyId, DateOnly? DueDate);

    /// <summary>
    /// This row's outstanding tasks: open, overdue, and the oldest missed date.
    /// <para>🔒 Asked through the SHARED responsibility rule (§1081,
    /// <see cref="ParticipantTaskQueries.VisibleTo"/>): assigned to them, OR their sponsor company's
    /// shared row. Hand-writing "assigned to me" is how <c>TaskReminderBuilder</c> left 131 open
    /// company tasks chased by nothing — this is the same predicate, over an already-materialised
    /// list.</para>
    /// </summary>
    private static (int Open, int Overdue, DateOnly? Earliest) TallyTasks(
        IReadOnlyList<OpenTask> openTasks, int participantId, string? sponsorCompanyId, DateOnly today)
    {
        var mine = openTasks.Where(t =>
            t.AssignedParticipantId == participantId
            || (sponsorCompanyId != null && t.SponsorCompanyId == sponsorCompanyId)).ToList();
        var overdue = mine.Where(t => t.DueDate is not null && t.DueDate.Value < today).ToList();
        return (mine.Count, overdue.Count,
            overdue.Count == 0 ? null : overdue.Min(t => t.DueDate!.Value));
    }
}
