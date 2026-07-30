namespace CommunityHub.Core.Tasks.Data;

/// <summary>
/// Who is looking at the task, for a <see cref="ITaskDataProvider"/> to answer about.
/// </summary>
/// <param name="EventId">The edition. Evergreen rule — a provider never assumes an edition.</param>
/// <param name="SponsorCompanyId">The sponsor company, or null for a non-sponsor task.</param>
/// <param name="ParticipantId">The signed-in participant, or null (a reminder e-mail render).</param>
public sealed record TaskDataContext(
    int EventId,
    string? SponsorCompanyId,
    int? ParticipantId);

/// <summary>
/// §684.9 — a named source of LIVE data for a <c>:::data</c> directive, resolved when the task
/// RENDERS rather than when it was seeded.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Providers must be cheap and failure-tolerant.</b> A task page must render when the
/// webshop is down: short timeout, cached per (company, source), and
/// <see cref="TaskDataResult.CouldNotCheck"/> on timeout. <b>A task list may never hang on a
/// third-party API</b> — <see cref="TaskDataResolver"/> enforces the timeout, but a provider that
/// ignores its cancellation token defeats it.</para>
///
/// <para>🔒 A provider MUST distinguish "nothing found" from "could not check" — see
/// <see cref="TaskDataResult"/>. Returning the wrong one is the §555 violation §666 exists to
/// prevent.</para>
/// </remarks>
public interface ITaskDataProvider
{
    /// <summary>The name used in the directive — <c>:::data tvPurchase</c> ⇒ <c>"tvPurchase"</c>.</summary>
    string Source { get; }

    /// <summary>Resolve for this participant/company. Should not throw; the resolver catches anyway.</summary>
    Task<TaskDataResult> ResolveAsync(TaskDataContext context, CancellationToken ct);
}
