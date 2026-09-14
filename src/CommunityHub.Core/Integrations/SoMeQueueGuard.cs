using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>What one guard pass removed from the planner.</summary>
/// <param name="Removed">Posts tombstoned on this pass.</param>
/// <param name="Reasons">One line per removed post, naming the subject and why — for the log and the page.</param>
public sealed record SoMeQueueGuardResult(int Removed, IReadOnlyList<string> Reasons)
{
    public static readonly SoMeQueueGuardResult None = new(0, Array.Empty<string>());
}

/// <summary>
/// §1178 — THE GUARD: a test or excluded subject cannot be in the planner at all.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-12: <i>"guard needed. no test sessions or test sponsor or excluded can
/// exist in some planner"</i>.</para>
///
/// <para>🔴 <b>A FILTER IS NOT A GUARD, and the campaign had only filters.</b> The planner stopped
/// PROPOSING an excluded subject and the gate stopped it being APPROVED — but a post planned BEFORE
/// the flag was set was already sitting in the queue, and nothing took it back. §1060(h) noticed half
/// of this and built <c>WithdrawExcludedAsync</c>, which un-approves; un-approving leaves the post
/// exactly where he can still see it, which is what he is objecting to now.</para>
///
/// <para>🔒 <b>TOMBSTONED (§853), not hard-deleted, and never published posts.</b> Three properties
/// that all matter:</para>
/// <list type="bullet">
/// <item>a tombstone is what stops the planner re-proposing the subject on the next run, so the post
///   does not simply come back;</item>
/// <item>it is REVERSIBLE — un-tick the flag, press Restore in the editor, and the post returns with
///   its words intact. §1042's lesson: a capability that merely LOOKS gone costs an afternoon;</item>
/// <item>🛑 a PUBLISHED post is never touched. It is out; the record must keep saying so, and a
///   tombstone over history would make the campaign lie about what went to LinkedIn.</item>
/// </list>
///
/// <para>⚠️ <b>It also switches <c>IsActive</c> off on its way out</b>, matching the editor's own
/// delete: a tombstone that stayed Active would be one restored flag away from publishing.</para>
///
/// <para>🔑 The exclusion rule itself is NOT here — it is <see cref="SoMeSubjectScope"/>, shared with
/// the planner and the graphics sweep. This class is only the enforcement, so the guard can never
/// disagree with the filter about what "excluded" means. <c>[[ceh-count-the-shared-things]]</c></para>
/// </remarks>
public sealed class SoMeQueueGuard
{
    private readonly CommunityHubDbContext _db;
    private readonly ILogger<SoMeQueueGuard>? _log;

    public SoMeQueueGuard(CommunityHubDbContext db, ILogger<SoMeQueueGuard>? log = null)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// 🔴 §1203 — ONE POST PER SUBJECT PER ROUND. Removes true duplicates.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"and cleaned up, so we have only 1 per post"</i>, after §1201
    /// found Type 5 posts being deleted and re-created every ten minutes.</para>
    ///
    /// <para>🔑 <b>A duplicate is the SAME subject AND the SAME round</b> — two posts both saying
    /// "Session levels 300/400/500, run 2". That is always wrong: the planner's own model is one post
    /// per (subject, occurrence), and <c>SoMeSchedulePlanner</c> skips a pair it already sees.</para>
    ///
    /// <para>⚠️ <b>The same subject on several DATES is not a duplicate</b> — those are different
    /// rounds, and for Type 5 they are the dated runs he wrote in the deck. Deleting those would
    /// delete his own campaign, so the round number is part of the key and not an afterthought.</para>
    ///
    /// <para>🔒 <b>Which copy survives is decided, not arbitrary:</b> a PUBLISHED post always wins
    /// (it is history), then one he has accepted or approved (a decision), then the oldest id. The
    /// losers are tombstoned (§853) so nothing is destroyed and a mistake is one Restore away.</para>
    ///
    /// <para>🛑 A published duplicate is never removed even when it loses — two posts genuinely went
    /// out, and the record has to keep saying so.</para>
    /// </remarks>
    public async Task<SoMeQueueGuardResult> RemoveDuplicatesAsync(
        int eventId, CancellationToken ct = default)
    {
        var rows = await _db.SoMePosts
            .Where(p => p.EventId == eventId && !p.IsDeleted
                        && p.SubjectKey != null && p.Occurrence != null)
            .ToListAsync(ct);

        var reasons = new List<string>();
        var now = DateTimeOffset.UtcNow;
        var removed = 0;

        foreach (var group in rows.GroupBy(p => (p.TemplateKind, p.SubjectKey, p.Occurrence)))
        {
            if (group.Count() < 2) continue;

            // 🔒 The keeper, in priority order. Published first: it is the only copy that is a fact.
            var keeper = group
                .OrderByDescending(p => p.Status == SoMePostStatus.Published)
                .ThenByDescending(p => p.PlanState == Domain.SoMePostPlanState.Scheduled)
                .ThenByDescending(p => p.IsActive)
                .ThenBy(p => p.Id)
                .First();

            foreach (var loser in group.Where(p => p.Id != keeper.Id))
            {
                // 🛑 Two posts that both PUBLISHED are two things that happened.
                if (loser.Status == SoMePostStatus.Published || loser.PublishedAtUtc is not null)
                {
                    reasons.Add($"#{loser.Id} duplicate of #{keeper.Id} but already PUBLISHED — left alone");
                    continue;
                }

                loser.IsDeleted = true;
                loser.DeletedAt = now;
                loser.DeletedByEmail = "system (§1203 duplicate cleanup)";
                loser.IsActive = false;
                removed++;

                reasons.Add(
                    $"#{loser.Id} {loser.TemplateKind?.ToString() ?? "ad-hoc"} round {loser.Occurrence} "
                    + $"— duplicate of #{keeper.Id}");
            }
        }

        if (removed == 0) return new SoMeQueueGuardResult(0, reasons);

        await _db.SaveChangesAsync(ct);

        _log?.LogWarning(
            "§1203 duplicate cleanup: removed {Count} duplicate post(s) — {Reasons}.",
            removed, string.Join(" | ", reasons.Take(30)));

        return new SoMeQueueGuardResult(removed, reasons);
    }

    /// <summary>
    /// Remove every queued post whose subject is test data or excluded from announcements.
    /// Idempotent: a second pass over the same edition removes nothing.
    /// </summary>
    public async Task<SoMeQueueGuardResult> EnforceAsync(int eventId, CancellationToken ct = default)
    {
        var excluded = await new SoMeSubjectScope(_db).ExcludedSubjectKeysAsync(eventId, ct);
        if (excluded.Count == 0) return SoMeQueueGuardResult.None;

        // EF translates ICollection<T>.Contains into IN (…); the key set for one edition is at most a
        // few hundred strings.
        var keys = excluded.Keys.ToArray();

        var doomed = await _db.SoMePosts
            .Where(p => p.EventId == eventId
                        && !p.IsDeleted
                        // 🛑 Published posts are history, not queue content.
                        && p.Status != SoMePostStatus.Published
                        && p.PublishedAtUtc == null
                        && p.SubjectKey != null
                        && keys.Contains(p.SubjectKey))
            .ToListAsync(ct);

        if (doomed.Count == 0) return SoMeQueueGuardResult.None;

        var now = DateTimeOffset.UtcNow;
        var reasons = new List<string>(doomed.Count);

        foreach (var p in doomed)
        {
            // ⚠️ The key may differ in case from the one the scope produced, so read the reason back
            // out of the dictionary (built case-insensitive) rather than assuming an exact match.
            var why = excluded.TryGetValue(p.SubjectKey!, out var r) ? r : "excluded from the campaign";

            p.IsDeleted = true;
            p.DeletedAt = now;
            // Not a person, and it must not pretend to be one. §853 says a deletion should never be
            // anonymous; this says plainly that the system did it, and why.
            p.DeletedByEmail = "system (§1178 queue guard)";
            // A removed post must never publish, whatever it was before — the editor's own rule.
            p.IsActive = false;

            reasons.Add($"#{p.Id} {p.TemplateKind?.ToString() ?? "ad-hoc"} — {why}");
        }

        await _db.SaveChangesAsync(ct);

        _log?.LogWarning(
            "§1178 queue guard: removed {Count} planned post(s) whose subject must not be announced: "
            + "{Reasons}.", doomed.Count, string.Join(" | ", reasons.Take(30)));

        return new SoMeQueueGuardResult(doomed.Count, reasons);
    }
}
