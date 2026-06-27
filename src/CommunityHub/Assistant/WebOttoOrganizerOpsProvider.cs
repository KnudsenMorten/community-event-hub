using CommunityHub.Core.Assistant;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Sponsors;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Assistant;

/// <summary>
/// Web implementation of <see cref="IOttoOrganizerOpsProvider"/> — Otto's ORGANIZER OPS MODE
/// grounding (REQUIREMENTS §133). Turns operational questions ("which speakers haven't
/// uploaded slides?", "which sponsors are missing booth materials?", "how many master-class
/// non-selections remain?", "how many attendees / orders?") into curated, TYPED, READ-ONLY
/// aggregates by REUSING the existing crew aggregators:
/// <list type="bullet">
///   <item>§134 <see cref="SpeakerReadinessService"/> — speaker readiness roster + missing slides.</item>
///   <item>§135 <see cref="SponsorDeliverablesService"/> — sponsor deliverables board + at-risk.</item>
///   <item>§11 <see cref="OrganizerOverviewService"/> — participation / task / attendee / volunteer counts.</item>
///   <item>§6 <see cref="MasterClassSignupService"/> + a direct non-selection count.</item>
/// </list>
///
/// <para>It NEVER runs free-form / text-to-SQL — only these curated typed queries — and never
/// writes. Every aggregate is wrapped so one failing section degrades gracefully (Otto stays
/// available). Company display names are resolved from the locally-mirrored
/// <see cref="SponsorInfo.EventCoordinatorCompanyName"/> (no external Company Manager call on
/// the assistant path); a missing name falls back to the id.</para>
///
/// <para><b>SECURITY.</b> This provider is organizer-only. The gate lives in
/// <see cref="OttoGroundingBuilder"/>, which calls it ONLY for a SERVER-resolved
/// <see cref="ParticipantRole.Organizer"/>. Nothing here trusts a client-supplied role/id;
/// it is reached for organizers only, by construction.</para>
/// </summary>
public sealed class WebOttoOrganizerOpsProvider : IOttoOrganizerOpsProvider
{
    // Cap the named lists injected into grounding so a large event keeps the prompt bounded.
    private const int MaxNamed = 15;

    private readonly CommunityHubDbContext _db;
    private readonly OrganizerOverviewService _overview;
    private readonly SpeakerReadinessService _readiness;
    private readonly SponsorDeliverablesService _deliverables;
    private readonly MasterClassSignupService _masterClass;
    private readonly TimeProvider _clock;
    private readonly ILogger<WebOttoOrganizerOpsProvider> _log;

    public WebOttoOrganizerOpsProvider(
        CommunityHubDbContext db,
        OrganizerOverviewService overview,
        SpeakerReadinessService readiness,
        SponsorDeliverablesService deliverables,
        MasterClassSignupService masterClass,
        TimeProvider clock,
        ILogger<WebOttoOrganizerOpsProvider> log)
    {
        _db = db;
        _overview = overview;
        _readiness = readiness;
        _deliverables = deliverables;
        _masterClass = masterClass;
        _clock = clock;
        _log = log;
    }

    public async Task<IReadOnlyList<OttoGroundingSection>> GetOpsAggregatesAsync(
        int eventId, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var sections = new List<OttoGroundingSection>();

        await AddAsync(sections, "Event ops overview (organizer)",
            () => BuildOverviewAsync(eventId, ct), ct);
        await AddAsync(sections, "Speaker readiness (organizer)",
            () => BuildSpeakerReadinessAsync(eventId, ct), ct);
        await AddAsync(sections, "Sponsor deliverables (organizer)",
            () => BuildSponsorDeliverablesAsync(eventId, today, ct), ct);
        await AddAsync(sections, "Master Class selections (organizer)",
            () => BuildMasterClassAsync(eventId, ct), ct);

        return sections;
    }

    /// <summary>Run one aggregate, append it when it produced text; never throw to the builder.</summary>
    private async Task AddAsync(
        List<OttoGroundingSection> into, string heading, Func<Task<string?>> build, CancellationToken ct)
    {
        try
        {
            var body = await build();
            if (!string.IsNullOrWhiteSpace(body))
            {
                into.Add(new OttoGroundingSection(heading, body!.Trim()));
            }
        }
        catch (Exception ex)
        {
            // Degrade gracefully — a single failing aggregate must not break Otto.
            _log.LogWarning(ex, "Otto organizer ops: '{Heading}' aggregate failed.", heading);
        }
    }

    // --- §11 participation / tasks / attendees / volunteers --------------------
    private async Task<string?> BuildOverviewAsync(int eventId, CancellationToken ct)
    {
        var o = await _overview.BuildAsync(eventId, ct);
        var lines = new List<string>
        {
            $"People: {o.TotalPeople} total ({o.ActivePeople} active).",
        };
        foreach (var rc in o.RolesBreakdown)
        {
            lines.Add($"- {rc.Role}: {rc.Total} ({rc.Active} active).");
        }
        lines.Add($"Attendees (from Zoho orders): {o.AttendeeTotal}.");
        lines.Add($"Tasks: {o.TaskOverall.Done}/{o.TaskOverall.Total} done " +
                  $"({o.TaskOverall.Percent}%); {o.OverdueTasks} overdue.");
        lines.Add($"Sponsor tasks: {o.SponsorTaskDone}/{o.SponsorTaskTotal} done; " +
                  $"sponsor leads: {o.SponsorLeadOpen} open of {o.SponsorLeadTotal}.");
        lines.Add($"Volunteer work items: {o.VolunteerTasksAssigned} assigned, " +
                  $"{o.VolunteerTasksOpen} open (of {o.VolunteerTasksTotal}).");
        lines.Add($"Needs attention: {o.OverdueTasks} overdue task(s), " +
                  $"{o.UnassignedVolunteerTasks} unassigned volunteer item(s), " +
                  $"{o.OpenHelpRequests} open help request(s), " +
                  $"{o.PendingVolunteerApplications} pending volunteer application(s).");
        return string.Join("\n", lines);
    }

    // --- §134 speaker readiness + missing slides ------------------------------
    private async Task<string?> BuildSpeakerReadinessAsync(int eventId, CancellationToken ct)
    {
        var roster = await _readiness.BuildRosterAsync(eventId, ct);
        if (roster.Count == 0) return "No speakers in this edition yet.";

        var ready = roster.Count(r => r.IsReady);
        var notReady = roster.Where(r => !r.IsReady).ToList();

        // "Missing slides" = the §120 preview/final upload signals not yet done.
        bool MissingSlides(SpeakerReadiness r) => r.MissingItems.Any(
            i => i.Key is "upload-preview" or "upload-final");
        var missingSlides = roster.Where(MissingSlides).ToList();

        var lines = new List<string>
        {
            $"{ready} of {roster.Count} speaker(s) fully ready; {notReady.Count} still have open items.",
            $"{missingSlides.Count} speaker(s) have not uploaded slides (preview and/or final):",
        };
        foreach (var r in missingSlides.Take(MaxNamed))
        {
            var which = r.MissingItems
                .Where(i => i.Key is "upload-preview" or "upload-final")
                .Select(i => i.Label);
            lines.Add($"- {r.FullName} ({r.Email}) — missing: {string.Join(", ", which)}");
        }
        if (missingSlides.Count > MaxNamed)
            lines.Add($"- …and {missingSlides.Count - MaxNamed} more.");

        if (notReady.Count > 0)
        {
            lines.Add("Lowest readiness (need chasing):");
            foreach (var r in notReady.Take(MaxNamed))
            {
                lines.Add($"- {r.FullName} — {r.Percent}% ({r.Summary}); " +
                          $"missing: {string.Join(", ", r.MissingItems.Select(i => i.Label))}");
            }
            if (notReady.Count > MaxNamed)
                lines.Add($"- …and {notReady.Count - MaxNamed} more.");
        }
        return string.Join("\n", lines);
    }

    // --- §135 sponsor deliverables board --------------------------------------
    private async Task<string?> BuildSponsorDeliverablesAsync(int eventId, DateOnly today, CancellationToken ct)
    {
        // Local (no-network) display names from the mirrored coordinator company name.
        var names = await _db.SponsorInfos.AsNoTracking()
            .Where(s => s.EventId == eventId
                        && s.EventCoordinatorCompanyName != null
                        && s.EventCoordinatorCompanyName != "")
            .Select(s => new { s.SponsorCompanyId, s.EventCoordinatorCompanyName })
            .ToDictionaryAsync(x => x.SponsorCompanyId, x => x.EventCoordinatorCompanyName!, ct);

        var board = await _deliverables.BuildBoardAsync(eventId, today, names, ct);
        if (board.Count == 0) return "No sponsor companies in this edition yet.";

        var complete = board.Count(c => c.IsComplete);
        var atRisk = board.Where(c => c.AtRisk).ToList();
        var missingMaterials = board
            .Where(c => c.MissingStages.Any(s => s.Key == "booth-materials"))
            .ToList();
        var missingLogo = board.Count(c => c.MissingStages.Any(s => s.Key == "logo"));

        var lines = new List<string>
        {
            $"{complete} of {board.Count} sponsor company(ies) fully complete; " +
            $"{atRisk.Count} at risk (overdue stage).",
            $"{missingMaterials.Count} exhibitor(s) missing booth materials; {missingLogo} missing a logo.",
        };
        if (missingMaterials.Count > 0)
        {
            lines.Add("Missing booth materials:");
            foreach (var c in missingMaterials.Take(MaxNamed))
                lines.Add($"- {c.CompanyName} — {c.Summary}" + (c.AtRisk ? " [AT RISK]" : ""));
            if (missingMaterials.Count > MaxNamed)
                lines.Add($"- …and {missingMaterials.Count - MaxNamed} more.");
        }
        if (atRisk.Count > 0)
        {
            lines.Add("At-risk companies (overdue stages):");
            foreach (var c in atRisk.Take(MaxNamed))
            {
                var overdue = c.OverdueStages.Select(s => s.Label);
                lines.Add($"- {c.CompanyName} — overdue: {string.Join(", ", overdue)}");
            }
            if (atRisk.Count > MaxNamed)
                lines.Add($"- …and {atRisk.Count - MaxNamed} more.");
        }
        return string.Join("\n", lines);
    }

    // --- §6 master-class non-selections ---------------------------------------
    private async Task<string?> BuildMasterClassAsync(int eventId, CancellationToken ct)
    {
        // Eligible = active 2-day-ticket attendees (the same gate as the selection invite).
        var eligibleIds = await _db.Attendees.AsNoTracking()
            .Where(a => a.EventId == eventId
                        && a.TicketStatus == TicketStatus.TwoDay
                        && a.MirrorState == MirrorState.Active)
            .Select(a => a.Id)
            .ToListAsync(ct);
        if (eligibleIds.Count == 0)
            return "No Master-Class-eligible (2-day) attendees in this edition yet.";

        // A "selection" = any Master Class signup (confirmed / offered / waitlisted).
        var selectedIds = (await _db.MasterClassSignups.AsNoTracking()
                .Where(s => s.EventId == eventId)
                .Select(s => s.AttendeeId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        var nonSelections = eligibleIds.Count(id => !selectedIds.Contains(id));
        var selected = eligibleIds.Count - nonSelections;

        var (_, invited, notInvited) = await _masterClass.InviteStatsAsync(eventId, ct);

        return string.Join("\n", new[]
        {
            $"Master-Class-eligible (2-day) attendees: {eligibleIds.Count}.",
            $"Made a selection: {selected}. Non-selections remaining: {nonSelections}.",
            $"Selection invite: {invited} sent, {notInvited} not yet invited.",
        });
    }
}
