using CommunityHub.Jobs;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1317 — the jobs host's on-demand logging switch (<c>Diagnostics__VerboseLogging</c>). Quiet by
/// default: EF Core command lines and HttpClient request lines held at Warning, fast successful SQL
/// dependencies dropped. Verbose: everything flows as before. These ~2.8 GB/day per environment were
/// 70% of the Log Analytics bill.
/// </summary>
public sealed class DiagnosticsLoggingTests
{
    private static IConfiguration Config(string? verbose) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sql:ConnectionStringTemplate"] = "Server=tcp:localhost,1433;Database=diag-test;Encrypt=True;",
            [DiagnosticsLogging.VerboseSetting] = verbose,
        }).Build();

    private static ILoggerFactory Factory(IConfiguration config) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            // A logger with no provider is never enabled, so give it one to filter.
            builder.AddProvider(new NullProvider());
            builder.Services.Configure<LoggerFilterOptions>(o => DiagnosticsLogging.Apply(o, config));
        });

    /// <summary>A provider whose logger accepts every level, so only the filter rules decide.</summary>
    private sealed class NullProvider : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }
        public void Dispose() { }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("nonsense")]
    public void Quiet_by_default_holds_EF_commands_and_HttpClient_at_Warning(string? setting)
    {
        using var factory = Factory(Config(setting));

        Assert.False(factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").IsEnabled(LogLevel.Information));
        Assert.False(factory.CreateLogger("System.Net.Http.HttpClient.ZohoClient.LogicalHandler").IsEnabled(LogLevel.Information));
        Assert.True(factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").IsEnabled(LogLevel.Warning));
        // A job's own lines are never touched.
        Assert.True(factory.CreateLogger("CommunityHub.Core.Integrations.SponsorOrderPullService").IsEnabled(LogLevel.Information));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void Verbose_lets_EF_commands_and_HttpClient_through(string setting)
    {
        using var factory = Factory(Config(setting));

        Assert.True(factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").IsEnabled(LogLevel.Information));
        Assert.True(factory.CreateLogger("System.Net.Http.HttpClient.SharePointUploadClient.ClientHandler").IsEnabled(LogLevel.Information));
    }

    private sealed class Sink : ITelemetryProcessor
    {
        public List<ITelemetry> Items { get; } = [];
        public void Process(ITelemetry item) => Items.Add(item);
    }

    private static DependencyTelemetry Sql(TimeSpan duration, bool success) =>
        new() { Type = "SQL", Duration = duration, Success = success };

    [Fact]
    public void Quiet_drops_fast_successful_SQL_but_keeps_slow_failed_and_other_telemetry()
    {
        var sink = new Sink();
        var filter = new QuietSqlDependencyFilter(sink, Config(null));

        var fast = Sql(TimeSpan.FromMilliseconds(3), true);
        var slow = Sql(TimeSpan.FromSeconds(2), true);
        var failed = Sql(TimeSpan.FromMilliseconds(3), false);
        var http = new DependencyTelemetry { Type = "Http", Duration = TimeSpan.FromMilliseconds(3), Success = true };
        var exception = new ExceptionTelemetry(new InvalidOperationException("x"));

        foreach (var item in new ITelemetry[] { fast, slow, failed, http, exception }) filter.Process(item);

        Assert.Equal(new ITelemetry[] { slow, failed, http, exception }, sink.Items);
    }

    [Fact]
    public void Quiet_drops_the_health_check_metric_but_keeps_other_metrics()
    {
        var sink = new Sink();
        var filter = new QuietSqlDependencyFilter(sink, Config(null));

        var health = new MetricTelemetry("azure.functions.health_check.reports", 1);
        var other = new MetricTelemetry("SoMePostsPublished", 1);
        filter.Process(health);
        filter.Process(other);

        Assert.Equal(new ITelemetry[] { other }, sink.Items);
    }

    [Fact]
    public void Verbose_keeps_the_health_check_metric()
    {
        var sink = new Sink();
        new QuietSqlDependencyFilter(sink, Config("true"))
            .Process(new MetricTelemetry("azure.functions.health_check.reports", 1));

        Assert.Single(sink.Items);
    }

    [Fact]
    public void Verbose_keeps_every_SQL_dependency()
    {
        var sink = new Sink();
        var filter = new QuietSqlDependencyFilter(sink, Config("true"));

        filter.Process(Sql(TimeSpan.FromMilliseconds(3), true));

        Assert.Single(sink.Items);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "CommunityHub.Jobs";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public async Task The_real_jobs_registration_puts_the_filter_in_the_telemetry_pipeline()
    {
        // Built from JobsServiceRegistration.Register — the method Program.cs calls in Azure — so a
        // registration removed there fails here.
        var config = Config(null);
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        JobsServiceRegistration.Register(services, config);

        await using var provider = services.BuildServiceProvider();
        var telemetry = provider.GetRequiredService<TelemetryConfiguration>();

        Assert.Contains(telemetry.DefaultTelemetrySink.TelemetryProcessors, p => p is QuietSqlDependencyFilter);
    }
}
