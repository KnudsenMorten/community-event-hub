using CommunityHub.Core.Tasks.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Tasks.Data;

/// <summary>
/// §684.9 — resolves every <c>:::data</c> directive in a set of task bodies BEFORE they render, so
/// the renderers stay synchronous and pure.
/// </summary>
/// <remarks>
/// <para>🔒 <b>A task list may never hang on a third-party API.</b> Every provider call is bounded
/// by <see cref="ProviderTimeout"/> and cached per (edition, company, participant, source), so one
/// slow webshop cannot hold a sponsor's task page open — and ten tasks naming the same source cost
/// one lookup, not ten.</para>
///
/// <para>🔒 <b>Every failure path lands on <see cref="TaskDataResult.CouldNotCheck"/>, never on
/// "nothing found".</b> An unregistered provider, a throw, a timeout and a cancellation are all
/// "we could not check" — which is the truth. §555/§666: the alternative silently tells a sponsor
/// they have not booked a TV, and they buy a second one.</para>
/// </remarks>
public sealed class TaskDataResolver
{
    /// <summary>
    /// How long a single provider may take. Deliberately short: the sponsor's task page is the
    /// caller, and a page that renders late is a page nobody waits for.
    /// </summary>
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How long a resolved answer is reused. Short enough that a sponsor who has just bought a TV
    /// sees it on their next visit, long enough that a task list is one lookup rather than ten.
    /// </summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, ITaskDataProvider> _providers;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TaskDataResolver> _log;

    public TaskDataResolver(
        IEnumerable<ITaskDataProvider> providers,
        IMemoryCache cache,
        ILogger<TaskDataResolver> log)
    {
        _providers = providers.ToDictionary(p => p.Source, StringComparer.OrdinalIgnoreCase);
        _cache = cache;
        _log = log;
    }

    /// <summary>Resolve every data source named by <paramref name="bodies"/>.</summary>
    public async Task<IReadOnlyDictionary<string, TaskDataResult>> ResolveAsync(
        IEnumerable<TaskBody> bodies,
        TaskDataContext context,
        CancellationToken ct = default)
    {
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var body in bodies) CollectSources(body.Blocks, sources);

        var resolved = new Dictionary<string, TaskDataResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            resolved[source] = await ResolveOneAsync(source, context, ct);
        }
        return resolved;
    }

    private async Task<TaskDataResult> ResolveOneAsync(
        string source, TaskDataContext context, CancellationToken ct)
    {
        var cacheKey = $"taskdata:{context.EventId}:{context.SponsorCompanyId}:"
                     + $"{context.ParticipantId}:{source}";
        if (_cache.TryGetValue<TaskDataResult>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var result = await CallProviderAsync(source, context, ct);

        // 🔒 A "could not check" is cached only BRIEFLY. Caching a failure for the full window would
        // keep telling a sponsor we cannot see their orders for two minutes after the webshop came
        // back — and the retry is cheap.
        _cache.Set(
            cacheKey,
            result,
            result is TaskDataResult.CouldNotCheck ? TimeSpan.FromSeconds(20) : CacheFor);

        return result;
    }

    private async Task<TaskDataResult> CallProviderAsync(
        string source, TaskDataContext context, CancellationToken ct)
    {
        if (!_providers.TryGetValue(source, out var provider))
        {
            // An authored body names a provider nobody registered. TaskBodyCatalogTests turns this
            // into a BUILD failure; at runtime it must still be honest rather than silent.
            _log.LogError(
                "TaskDataResolver: no provider registered for ':::data {Source}'. The task renders "
                + "'we could not check' — register the provider or remove the directive.",
                source);
            return TaskDataResult.Unavailable(
                "We could not check this right now.", $"no provider registered for '{source}'");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);

        try
        {
            return await provider.ResolveAsync(context, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(
                "TaskDataResolver: provider '{Source}' timed out after {Seconds}s for company "
                + "{CompanyId}.", source, ProviderTimeout.TotalSeconds, context.SponsorCompanyId);
            return TaskDataResult.Unavailable(
                "We could not check this right now.", $"'{source}' timed out");
        }
        catch (Exception ex)
        {
            // 🔒 §682 — authored content and third-party data must never be able to break a runtime
            // path. A provider that throws degrades this ONE block to "could not check"; the task,
            // the page and the reminder job all carry on.
            _log.LogError(
                ex, "TaskDataResolver: provider '{Source}' threw for company {CompanyId}.",
                source, context.SponsorCompanyId);
            return TaskDataResult.Unavailable(
                "We could not check this right now.", $"'{source}' threw: {ex.GetType().Name}");
        }
    }

    /// <summary>Every <c>:::data</c> source named anywhere in the tree, including inside sections.</summary>
    private static void CollectSources(IReadOnlyList<TaskBlock> blocks, HashSet<string> into)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TaskData data:
                    into.Add(data.Source);
                    break;
                case TaskSection section:
                    CollectSources(section.Children, into);
                    break;
                case TaskCallout callout:
                    CollectSources(callout.Children, into);
                    break;
            }
        }
    }
}
