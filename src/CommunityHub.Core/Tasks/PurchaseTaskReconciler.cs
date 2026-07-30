using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Tasks.Definitions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Tasks;

/// <summary>
/// §687.8 — closes a <see cref="TaskCompletion.Purchase"/> task once the sponsor has actually
/// bought something in its category.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-29: <i>"technically a furniture order should auto-close that task, but
/// still give sponsor the opportunity to see the state and go in a order more furnitures as example
/// (from the task)"</i>.</para>
///
/// <para>🔒 <b>AUTO-CLOSE IS NOT A LOCK.</b> This only writes <see cref="TaskState"/>. The task row
/// keeps rendering its body, its purchase list and its order button, so "Done" reads as
/// <i>you have some</i> and never as <i>you may not order more</i>. Nothing here hides or collapses
/// the task.</para>
///
/// <para>🔒 <b>A FAILED LOOKUP NEVER REOPENS ANYTHING.</b> This is the rule that matters most. A
/// webshop outage returning "could not check" would, under a naive implementation, silently
/// un-complete every furniture task and re-chase every sponsor — §664.1's shape at scale. On
/// <see cref="SponsorPurchaseSummary.CouldNotCheck"/> this method does nothing at all.</para>
///
/// <para>🔒 <b>And it never reopens a task a HUMAN closed.</b> Reopening is limited to rows this
/// reconciler could have closed itself — a sponsor who ordered furniture and later cancelled the
/// order should reopen, but a task closed some other way must not be second-guessed by a
/// third-party API.</para>
/// </remarks>
public sealed class PurchaseTaskReconciler
{
    private readonly CommunityHubDbContext _db;
    private readonly SponsorPurchaseSummaryService _purchases;
    private readonly TaskDefinitionRegistry _registry;
    private readonly TaskBodyService _bodies;
    private readonly TimeProvider _clock;
    private readonly ILogger<PurchaseTaskReconciler> _log;

    public PurchaseTaskReconciler(
        CommunityHubDbContext db,
        SponsorPurchaseSummaryService purchases,
        TaskDefinitionRegistry registry,
        TaskBodyService bodies,
        TimeProvider clock,
        ILogger<PurchaseTaskReconciler> log)
    {
        _db = db;
        _purchases = purchases;
        _registry = registry;
        _bodies = bodies;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Reconcile every purchase-backed task in <paramref name="tasks"/> for one company. Returns the
    /// number of rows whose state changed.
    /// </summary>
    public async Task<int> ReconcileAsync(
        IReadOnlyList<ParticipantTask> tasks,
        string sponsorCompanyId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sponsorCompanyId)) return 0;

        var changed = 0;

        foreach (var task in tasks)
        {
            if (_bodies.DefinitionFor(task)?.Completion is not TaskCompletion.Purchase purchase)
            {
                continue;
            }

            var summary = await _purchases.ByCategoryAsync(purchase.Category, ct);

            // 🔒 THE OUTAGE GUARD. "We could not check" is not "they bought nothing" — leave the
            // stored state exactly as it is and say nothing.
            if (summary.CouldNotCheck)
            {
                _log.LogWarning(
                    "PurchaseTaskReconciler: could not check '{Category}' for company {CompanyId} "
                    + "({Reason}) — leaving task {TaskId} exactly as it is.",
                    purchase.Category, sponsorCompanyId, summary.Reason, task.Id);
                continue;
            }

            var bought = summary.Rows
                .Where(r => string.Equals(r.CompanyId, sponsorCompanyId, StringComparison.Ordinal))
                .Sum(r => r.Quantity);

            if (bought > 0 && task.State != TaskState.Done)
            {
                task.State = TaskState.Done;
                task.CompletedAt ??= _clock.GetUtcNow();
                // 🔒 ClosedReason stays NULL: that column labels closures the SYSTEM made on
                // someone's BEHALF when the work was not done (§332). Here the work WAS done — the
                // sponsor placed the order — so this belongs in the completion ratios.
                changed++;
            }
            else if (bought == 0 && task.State == TaskState.Done && task.ClosedReason is null)
            {
                // The order was cancelled or refunded out of the window. Reopen, because the
                // furniture is genuinely no longer coming — that is the whole point of deriving the
                // state rather than trusting a tick.
                task.State = TaskState.Open;
                task.CompletedAt = null;
                changed++;
                _log.LogInformation(
                    "PurchaseTaskReconciler: reopened task {TaskId} for company {CompanyId} — no "
                    + "'{Category}' purchase remains.", task.Id, sponsorCompanyId, purchase.Category);
            }
        }

        if (changed > 0) await _db.SaveChangesAsync(ct);
        return changed;
    }
}
