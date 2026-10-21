using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1317 — the jobs host's on-demand logging switch. QUIET by default, VERBOSE when the operator sets
/// <c>Diagnostics__VerboseLogging = true</c> on the Functions app (portal → Environment variables; the
/// host restarts and picks it up — no deploy).
/// </summary>
/// <remarks>
/// <para><b>Why.</b> Measured 2026-10-05: ~3.5 GB/day of telemetry per environment, ~4,000 DKK/month
/// for PROD + DEV, and 99% of it came from this host — EF Core's <c>Executed DbCommand</c> line for every
/// SQL statement (~1.7 GB/day), one SQL dependency per statement (~0.7 GB/day) and HttpClient's
/// per-request lines (~0.4 GB/day). Of 782,486 SQL dependencies in 24h, zero were slow and zero failed.</para>
///
/// <para><b>Quiet</b> keeps everything at Warning and above for those categories, and keeps a SQL
/// dependency only when it failed or took ≥ <see cref="SlowDependency"/> — the two cases that explain a
/// hang. Every job's own log lines, every warning, error and exception are untouched.</para>
///
/// <para>⚠️ The worker is built on a bare <c>new HostBuilder()</c>, which does NOT bind the
/// <c>Logging</c> configuration section to logger filters — so a <c>Logging__LogLevel__*</c> app setting
/// would do nothing here. That is why the switch is one explicit setting read in code.</para>
/// </remarks>
public static class DiagnosticsLogging
{
    /// <summary>App setting <c>Diagnostics__VerboseLogging</c>. Anything but <c>true</c> = quiet.</summary>
    public const string VerboseSetting = "Diagnostics:VerboseLogging";

    /// <summary>Categories held at Warning while quiet (prefix match, so every named HttpClient is covered).</summary>
    public static readonly IReadOnlyList<string> QuietCategories =
    [
        "Microsoft.EntityFrameworkCore.Database.Command",
        "System.Net.Http.HttpClient",
    ];

    /// <summary>A SQL call at least this slow is always kept, quiet or not.</summary>
    public static readonly TimeSpan SlowDependency = TimeSpan.FromSeconds(1);

    public static bool IsVerbose(IConfiguration config) =>
        bool.TryParse(config[VerboseSetting], out var verbose) && verbose;

    /// <summary>Adds the Warning floor for <see cref="QuietCategories"/> unless verbose is on.</summary>
    public static void Apply(LoggerFilterOptions options, IConfiguration config)
    {
        if (IsVerbose(config)) return;
        foreach (var category in QuietCategories)
            options.Rules.Add(new LoggerFilterRule(null, category, LogLevel.Warning, null));
    }

    /// <summary>
    /// The Functions worker's health-check metric — one series per check every few seconds, ~120 MB/day
    /// on PROD and read by nothing. A failing check still surfaces as a warning/error log.
    /// </summary>
    public const string HealthCheckMetricPrefix = "azure.functions.health_check.";

    /// <summary>True when a telemetry item should be dropped while quiet: a fast, successful SQL call.</summary>
    public static bool IsRoutineSql(ITelemetry item) =>
        item is DependencyTelemetry { Type: "SQL" } dependency
        && dependency.Success != false
        && dependency.Duration < SlowDependency;

    /// <summary>True for the routine health-check metric series dropped while quiet.</summary>
    public static bool IsHealthCheckMetric(ITelemetry item) =>
        item is MetricTelemetry metric
        && metric.Name?.StartsWith(HealthCheckMetricPrefix, StringComparison.Ordinal) == true;
}

/// <summary>§1317 — drops fast, successful SQL dependencies and the health-check metric unless verbose logging is on.</summary>
public sealed class QuietSqlDependencyFilter(ITelemetryProcessor next, IConfiguration config) : ITelemetryProcessor
{
    private readonly bool _verbose = DiagnosticsLogging.IsVerbose(config);

    public void Process(ITelemetry item)
    {
        if (!_verbose && (DiagnosticsLogging.IsRoutineSql(item) || DiagnosticsLogging.IsHealthCheckMetric(item))) return;
        next.Process(item);
    }
}
