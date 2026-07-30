using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;

namespace CommunityHub.Telemetry;

/// <summary>
/// §392 — reads Application Insights so the organizer can see what the platform is actually doing
/// (operator 2026-07-26: <i>"add telemetry view graphs + stats on the admin interface so i will be
/// able to follow issues + trends + concurrent users etc"</i>).
///
/// <para><b>Secretless.</b> Queries run as the web app's SYSTEM-ASSIGNED MANAGED IDENTITY, which
/// holds <c>Monitoring Reader</c> on the Application Insights component. No API key is minted,
/// stored or rotated — consistent with the project's "no secrets in source or config" posture, and
/// with SQL already being Entra-only.</para>
///
/// <para><b>Fails soft, always.</b> Every query is wrapped: a telemetry outage, a missing role
/// assignment or a throttled workspace returns an empty result and a message, never an exception on
/// an organizer page. A dashboard that 500s because the monitoring backend hiccuped would be worse
/// than no dashboard.</para>
/// </summary>
public sealed class PlatformTelemetryService
{
    /// <summary>The AI component's resource id — what the Azure Monitor query client addresses.</summary>
    private readonly string? _resourceId;
    private readonly ILogger<PlatformTelemetryService> _log;
    private readonly LogsQueryClient? _client;

    public PlatformTelemetryService(IConfiguration config, ILogger<PlatformTelemetryService> log)
    {
        _log = log;
        _resourceId = config["Telemetry:AppInsightsResourceId"];

        if (!string.IsNullOrWhiteSpace(_resourceId))
        {
            // DefaultAzureCredential resolves to the App Service managed identity in Azure and to
            // the developer's own az/VS login locally, so the page works in both without branching.
            _client = new LogsQueryClient(new DefaultAzureCredential());
        }
    }

    /// <summary>True when telemetry is wired; false disables the page with an explanation.</summary>
    public bool IsConfigured => _client is not null && !string.IsNullOrWhiteSpace(_resourceId);

    // ---- shapes the page renders -------------------------------------------

    /// <param name="Bucket">Start of the time bucket (UTC).</param>
    public sealed record TrafficPoint(DateTimeOffset Bucket, int Requests, int Failed, double P95Ms, int Users);

    /// <param name="Name">Request/operation name.</param>
    public sealed record OperationRow(string Name, int Count, double P50Ms, double P95Ms, int Failed);

    /// <param name="Kind">Dependency type — SQL, HTTP, …</param>
    public sealed record DependencyRow(string Kind, string Target, int Count, double P95Ms, int Failed);

    public sealed record ProblemRow(string Problem, int Count, DateTimeOffset LastSeen);

    /// <param name="Ok">False when the query could not run; <paramref name="Message"/> says why.</param>
    public sealed record Snapshot(
        bool Ok,
        string? Message,
        IReadOnlyList<TrafficPoint> Traffic,
        IReadOnlyList<OperationRow> SlowestOperations,
        IReadOnlyList<DependencyRow> Dependencies,
        IReadOnlyList<ProblemRow> Problems,
        int TotalRequests,
        int TotalFailed,
        int PeakConcurrentUsers,
        double OverallP95Ms);

    private static Snapshot Empty(string message) => new(
        false, message,
        Array.Empty<TrafficPoint>(), Array.Empty<OperationRow>(),
        Array.Empty<DependencyRow>(), Array.Empty<ProblemRow>(), 0, 0, 0, 0);

    /// <summary>
    /// One round-trip per panel, over <paramref name="hours"/> of history. The bucket size scales
    /// with the window so a 24-hour view is not 1,440 points: a chart nobody can read is not a
    /// chart.
    /// </summary>
    public async Task<Snapshot> GetAsync(int hours, CancellationToken ct = default)
    {
        if (_client is null || string.IsNullOrWhiteSpace(_resourceId))
        {
            return Empty("Telemetry is not configured for this environment "
                         + "(set Telemetry:AppInsightsResourceId and grant the app Monitoring Reader).");
        }

        hours = Math.Clamp(hours, 1, 168);
        var bucketMinutes = hours <= 3 ? 5 : hours <= 12 ? 15 : hours <= 48 ? 60 : 240;
        var span = TimeSpan.FromHours(hours);

        try
        {
            // WEB ROLE ONLY on the traffic panel: the Functions jobs run on their own schedule and
            // would swamp the "are users having a good time?" question this panel exists to answer.
            var traffic = await QueryAsync(
                $@"requests
                   | where cloud_RoleName !contains 'fn-'
                   | summarize Requests=count(),
                               Failed=countif(success == false),
                               P95=percentile(duration, 95),
                               Users=dcount(user_Id)
                     by Bucket=bin(timestamp, {bucketMinutes}m)
                   | order by Bucket asc",
                span, ct,
                r => new TrafficPoint(
                    r.GetDateTimeOffset("Bucket") ?? DateTimeOffset.MinValue,
                    (int)(r.GetInt64("Requests") ?? 0),
                    (int)(r.GetInt64("Failed") ?? 0),
                    Math.Round(r.GetDouble("P95") ?? 0, 0),
                    (int)(r.GetInt64("Users") ?? 0)));

            var slowest = await QueryAsync(
                $@"requests
                   | where cloud_RoleName !contains 'fn-'
                   | summarize Count=count(),
                               P50=percentile(duration, 50),
                               P95=percentile(duration, 95),
                               Failed=countif(success == false)
                     by Name=name
                   | order by P95 desc
                   | take 12",
                span, ct,
                r => new OperationRow(
                    r.GetString("Name") ?? "(unnamed)",
                    (int)(r.GetInt64("Count") ?? 0),
                    Math.Round(r.GetDouble("P50") ?? 0, 0),
                    Math.Round(r.GetDouble("P95") ?? 0, 0),
                    (int)(r.GetInt64("Failed") ?? 0)));

            // Dependencies are where a "hang" is usually explained: a slow SQL call means lock
            // contention or a missing index; a slow HTTP call means Zoho/Brevo, not us.
            var deps = await QueryAsync(
                $@"dependencies
                   | summarize Count=count(),
                               P95=percentile(duration, 95),
                               Failed=countif(success == false)
                     by Kind=type, Target=target
                   | order by P95 desc
                   | take 12",
                span, ct,
                r => new DependencyRow(
                    r.GetString("Kind") ?? "?",
                    r.GetString("Target") ?? "?",
                    (int)(r.GetInt64("Count") ?? 0),
                    Math.Round(r.GetDouble("P95") ?? 0, 0),
                    (int)(r.GetInt64("Failed") ?? 0)));

            var problems = await QueryAsync(
                $@"union
                     (exceptions | extend Problem = strcat('Exception: ', tostring(type))),
                     (requests | where success == false
                               | extend Problem = strcat('Failed request: ', name)),
                     (dependencies | where success == false
                                   | extend Problem = strcat('Failed call: ', type, ' ', target))
                   | summarize Count=count(), LastSeen=max(timestamp) by Problem
                   | order by Count desc
                   | take 12",
                span, ct,
                r => new ProblemRow(
                    r.GetString("Problem") ?? "?",
                    (int)(r.GetInt64("Count") ?? 0),
                    r.GetDateTimeOffset("LastSeen") ?? DateTimeOffset.MinValue));

            var totals = traffic.Count == 0
                ? (0, 0, 0, 0d)
                : (traffic.Sum(t => t.Requests),
                   traffic.Sum(t => t.Failed),
                   traffic.Max(t => t.Users),
                   Math.Round(traffic.Max(t => t.P95Ms), 0));

            return new Snapshot(
                true, null, traffic, slowest, deps, problems,
                totals.Item1, totals.Item2, totals.Item3, totals.Item4);
        }
        catch (Exception ex)
        {
            // Most likely causes, in order: the role assignment has not propagated yet, the resource
            // id is wrong, or Azure Monitor is throttling. Say so instead of throwing.
            _log.LogWarning(ex, "Telemetry query failed.");
            return Empty($"Could not read telemetry: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string kql, TimeSpan span, CancellationToken ct, Func<LogsTableRow, T> map)
    {
        var result = await _client!.QueryResourceAsync(
            new Azure.Core.ResourceIdentifier(_resourceId!),
            kql,
            new QueryTimeRange(span),
            cancellationToken: ct);

        var table = result.Value.Table;
        return table.Rows.Select(map).ToList();
    }
}
