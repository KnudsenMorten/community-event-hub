using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Tasks;

/// <summary>
/// §1312 — a MANDATORY task past its due date and not done, for the red "!" on the task menu items (operator 2026-09-27:
/// "can we do that as well for mandatory tasks that is overdue for all roles … so they show up with similar marking").
/// Overdue is the task rows' own rule: due date before today (UTC), not Done. Optional tasks never mark anything.
/// </summary>
public static class OverdueTasks
{
    public static IQueryable<ParticipantTask> MandatoryOverdue(IQueryable<ParticipantTask> tasks, DateOnly today) =>
        tasks.Where(t => t.IsMandatory && t.State != TaskState.Done && t.DueDate != null && t.DueDate < today);

    /// <summary>The person's own tasks ("My tasks").</summary>
    public static Task<bool> AnyForPersonAsync(CommunityHubDbContext db, int eventId, int participantId, DateOnly today, CancellationToken ct = default) =>
        MandatoryOverdue(db.Tasks.AsNoTracking(), today)
            .AnyAsync(t => t.EventId == eventId && t.AssignedParticipantId == participantId, ct);

    /// <summary>A sponsor company's shared tasks ("Sponsor tasks") — the same rows that page lists.</summary>
    public static Task<bool> AnyForSponsorCompanyAsync(CommunityHubDbContext db, int eventId, string companyId, DateOnly today, CancellationToken ct = default) =>
        MandatoryOverdue(db.Tasks.AsNoTracking(), today)
            .Where(TaskClosure.NotSystemClosed)
            .AnyAsync(t => t.EventId == eventId && t.SourceKey != null && t.SourceKey.StartsWith("sponsor:")
                           && t.SponsorCompanyId == companyId, ct);
}
