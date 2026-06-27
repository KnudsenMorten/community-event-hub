using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>
/// The SOLE assignment-email emitter for the volunteer / organizer task-allocation
/// flow (REQUIREMENTS §150). The allocation QUEUE is SILENT: the step-2 availability
/// engine PROPOSALS and every lead/organizer DRAFT edit (Add / Remove / Discard /
/// drop-out re-plan seeding in <c>VolunteerAllocationService</c>) emit NO mail
/// at all — hundreds of edits happen without a single notification. Only a COMMIT —
/// when a per-organizer draft queue is turned into real
/// <see cref="VolunteerTaskAssignment"/> rows — notifies, and it does so exactly ONCE
/// per affected person, batching their FINAL committed assignment set into a single
/// summary email (never one-per-edit).
///
/// This service is called ONLY from the page commit handlers, AFTER
/// <c>VolunteerAllocationService.CommitAsync</c> has persisted the real rows.
/// Every send goes through the shared, ring-gated <see cref="IEmailSender"/>; the
/// per-person <see cref="EmailContext"/> carries the committing queue's FeatureKey
/// (<c>volunteer-allocation</c> for the volunteer queue, <c>organizer-allocation</c>
/// for the organizer queue) so <c>BrevoEmailSender</c> applies the SAME ring gate +
/// global kill switch as all prod mail — an out-of-ring recipient is dropped
/// automatically, matching the commit's ring scope. The body is built inline (an
/// ad-hoc send; no branded catalog template is needed for the per-person summary).
/// </summary>
public sealed class CommitNotificationService : ICommitNotificationService
{
    /// <summary>EmailLog category recorded for these per-person commit summaries.</summary>
    private const string Category = "task-allocation";

    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _sender;
    private readonly IEmailContextAccessor _context;

    public CommitNotificationService(
        CommunityHubDbContext db, IEmailSender sender, IEmailContextAccessor context)
    {
        _db = db;
        _sender = sender;
        _context = context;
    }

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>
    /// <see cref="ICommitNotificationService"/> entry point used by the page commit
    /// handlers. Derives the edition from the actor and maps the target role to the
    /// committing queue's feature key (Volunteer → <c>volunteer-allocation</c>,
    /// Organizer → <c>organizer-allocation</c>), then defers to
    /// <see cref="NotifyCommittedAsync"/> (the single batched-send implementation).
    /// </summary>
    public Task NotifyCommitAsync(
        VolunteerStructureService.ActorContext actor,
        IReadOnlyList<int> affectedParticipantIds,
        ParticipantRole targetRole,
        CancellationToken ct = default)
    {
        var queueFeatureKey = targetRole == ParticipantRole.Organizer
            ? OrganizerAllocationService.FeatureKey
            : VolunteerAllocationService.FeatureKey;
        return NotifyCommittedAsync(actor, actor.EventId, affectedParticipantIds, queueFeatureKey, ct);
    }

    /// <summary>One committed assignment as it appears in the summary list.</summary>
    private sealed record AssignmentLine(string Title, string? Shift, DateOnly? DueDate);

    /// <summary>
    /// Notify each person whose committed assignment set changed by a commit. For
    /// every DISTINCT id in <paramref name="affectedParticipantIds"/> (the committing
    /// <paramref name="actor"/> themselves is never mailed for their own action) this
    /// loads their CURRENT (post-commit) <see cref="VolunteerTaskAssignment"/> rows +
    /// task titles and sends ONE batched summary email — never one per edit.
    ///
    /// <paramref name="queueFeatureKey"/> is the committing queue's feature
    /// (<c>volunteer-allocation</c> or <c>organizer-allocation</c>); it is set on the
    /// ambient <see cref="EmailContext"/> so the sender's ring gate tightens to that
    /// feature's released ring (out-of-ring recipients dropped) and the global kill
    /// switch is honoured — this method itself never decides send-vs-drop, it delegates
    /// that to the one ring-gated <see cref="IEmailSender"/> chokepoint.
    ///
    /// Returns the number of people a send was ISSUED for (a person with no email is
    /// skipped; a ring / kill-switch drop happens inside the sender and is still counted
    /// as issued here, because the drop is the sender's decision, not this service's).
    /// </summary>
    public async Task<int> NotifyCommittedAsync(
        VolunteerStructureService.ActorContext actor,
        int eventId,
        IReadOnlyList<int> affectedParticipantIds,
        string queueFeatureKey,
        CancellationToken ct = default)
    {
        if (affectedParticipantIds is null || affectedParticipantIds.Count == 0) return 0;

        // Distinct, positive ids; never notify the actor about their own commit.
        var ids = affectedParticipantIds
            .Where(id => id > 0 && id != actor.ParticipantId)
            .Distinct()
            .ToList();
        if (ids.Count == 0) return 0;

        // Load the affected people in this edition once (single query).
        var people = await _db.Participants
            .Where(p => p.EventId == eventId && ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Email, p.FullName })
            .ToListAsync(ct);
        if (people.Count == 0) return 0;

        // Their CURRENT committed assignments + task titles, grouped per person (one
        // query for all of them, then bucketed in memory).
        var rows = await _db.VolunteerTaskAssignments
            .Where(a => a.EventId == eventId && ids.Contains(a.ParticipantId))
            .Select(a => new
            {
                a.ParticipantId,
                a.Task.Title,
                a.Task.Shift,
                a.Task.DueDate,
            })
            .ToListAsync(ct);

        var byPerson = rows
            .GroupBy(r => r.ParticipantId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .Select(r => new AssignmentLine(r.Title, r.Shift, r.DueDate))
                    .OrderBy(l => l.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var issued = 0;
        foreach (var p in people)
        {
            if (string.IsNullOrWhiteSpace(p.Email)) continue;

            var lines = byPerson.TryGetValue(p.Id, out var l) ? l : new List<AssignmentLine>();
            var (subject, html) = BuildSummary(p.FullName, lines);

            // Set the per-person email context so the sender's ring gate scopes the
            // send to THIS commit's queue feature (volunteer-/organizer-allocation)
            // and the kill switch applies — then issue the single batched send.
            using (_context.Set(new EmailContext(
                Category, eventId, p.Id, p.FullName, FeatureKey: queueFeatureKey)))
            {
                await _sender.SendAsync(p.Email, subject, html, ct);
            }
            issued++;
        }

        return issued;
    }

    /// <summary>
    /// Build the single batched summary email for one person from their FINAL
    /// committed assignment set. An empty set (e.g. all their assignments were freed
    /// in a drop-out re-plan that this commit applied) still sends one mail that says
    /// they currently have no assigned tasks, so the person is never left guessing.
    /// </summary>
    private static (string subject, string html) BuildSummary(
        string fullName, IReadOnlyList<AssignmentLine> lines)
    {
        var name = string.IsNullOrWhiteSpace(fullName) ? "there" : fullName;
        var sb = new StringBuilder();
        sb.Append($"<p>Hi {Enc(name)},</p>");

        if (lines.Count == 0)
        {
            sb.Append("<p>Your volunteer task assignments have been updated. You "
                + "currently have <strong>no assigned tasks</strong>.</p>");
        }
        else
        {
            sb.Append("<p>Your volunteer task assignments have been updated. Here is "
                + "your current task list:</p>");
            sb.Append("<ul>");
            foreach (var line in lines)
            {
                var detail = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(line.Shift))
                    detail.Append($" — {Enc(line.Shift!)}");
                if (line.DueDate is { } due)
                    detail.Append($" (due {Enc(due.ToString("yyyy-MM-dd"))})");
                sb.Append($"<li><strong>{Enc(line.Title)}</strong>{detail}</li>");
            }
            sb.Append("</ul>");
        }

        sb.Append("<p>Thank you for volunteering!</p>");

        var subject = lines.Count == 0
            ? "Your volunteer task assignments have been updated"
            : $"Your volunteer task assignments ({lines.Count})";
        return (subject, sb.ToString());
    }
}
