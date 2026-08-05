using System.Reflection;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Jobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §784.15 — THE REGRESSION TEST FOR A LIVE PROD FAULT (2026-08-03), and the guard the Functions
/// host never had.
/// </summary>
/// <remarks>
/// <para><b>What happened.</b> §783.8 gave <c>EvaluationReportPublishJob</c> a dependency on
/// <see cref="CommunityHub.Core.Evaluation.EvaluationQrService"/>. That service was registered in the
/// WEB host and never in the jobs host, so the job failed <b>18 times in a row</b> in production with
/// <i>"Unable to resolve service for type 'EvaluationQrService'"</i>. It was the same defect as
/// §783.10 — a service registered in one host and not the other — reintroduced one host over, the
/// same day §783.10 was fixed.</para>
///
/// <para>🔒 <b>Why nothing on the way to production could see it.</b> §783.10's fix was
/// <c>ValidateOnBuild</c> on the WEB host, which resolves every registration when the container is
/// built and so fails the slot warm-up. <b>The Functions worker has no equivalent</b>: it resolves a
/// function's constructor PER INVOCATION, so a missing registration cannot fail at startup there. The
/// build was green, the suites were green, the deploy printed <c>&gt;&gt; prod jobs deployed</c> — and
/// the fault surfaced minutes later on a timer tick, where it is quiet unless somebody is reading the
/// job log. <see cref="JobCatalogCompletenessTests"/> proves every job is CATALOGUED; nothing proved
/// any job could be CONSTRUCTED.</para>
///
/// <para>🔑 <b>What makes this test worth trusting.</b> It builds the container from
/// <see cref="JobsServiceRegistration.Register"/> — the SAME method <c>Program.cs</c> calls in Azure,
/// not a hand-copied mirror of it. A mirrored list would be maintained by whoever remembered to
/// update it, i.e. by exactly the person who would not have forgotten the registration either. It
/// then ACTIVATES every job type through the container, which is what the worker does per tick, so a
/// gap anywhere in a job's dependency graph fails here — on a laptop, in seconds — instead of on his
/// timer.</para>
///
/// <para>⚠️ It is deliberately run against SEVERAL configuration shapes.
/// <see cref="JobsServiceRegistration.Register"/> branches on TestMode, on the graphics SharePoint
/// block, on LinkedIn and on the e-conomic role source, and a service registered in only one arm of
/// one of those <c>if</c>s resolves perfectly on a dev box and not in production. Nothing here dials
/// out: no DbContext is opened (EF creates a connection lazily), and every client is merely
/// constructed.</para>
/// </remarks>
public sealed class JobDependenciesResolveTests
{
    /// <summary>
    /// The generic host's IHostEnvironment stand-in. 🔒 It reports "Production" on purpose: the
    /// Functions apps set no environment variable at all, so the real worker defaults to Production
    /// in BOTH editions (verified 2026-07-29, §702) — a test that said "Development" here would take
    /// a branch Azure never takes.
    /// </summary>
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "CommunityHub.Jobs";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>
    /// The configuration shapes that select DIFFERENT registrations. Names are what a failure
    /// message prints, so they say which arm of which switch was in force.
    /// </summary>
    public static TheoryData<string> ConfigShapes() => new()
    {
        "shipped-defaults",   // nothing configured — every optional integration takes its Null/off arm
        "prod-shaped",        // TestMode OFF + graphics/LinkedIn/e-conomic roles ON — what PROD runs
        "testmode",           // TestMode ON — the TestMode exhibitor + ERP clients
    };

    /// <summary>
    /// 🔒 The SQL template never connects. EF Core creates the <c>SqlConnection</c> on first use and
    /// no test here executes a query, so this is a syntactically valid string and nothing more —
    /// there is no credential in this file and none is needed. Leaving <c>Sql:AdminPassword</c> unset
    /// is deliberate: it takes the managed-identity arm, which is the one PROD takes.
    /// </summary>
    private const string SqlTemplate = "Server=tcp:localhost,1433;Database=jobs-di-test;Encrypt=True;";

    private static IConfiguration BuildConfig(string shape)
    {
        var values = new Dictionary<string, string?>
        {
            ["Sql:ConnectionStringTemplate"] = SqlTemplate,
        };

        switch (shape)
        {
            case "prod-shaped":
                values["TestMode:Enabled"] = "false";
                // Flips ISharePointFileStore from the Null store to the live Graph store
                // (GraphicsSharePointOptions.IsConfigured = Enabled && SiteUrl present).
                values["Graphics:SharePoint:Enabled"] = "true";
                values["Graphics:SharePoint:SiteUrl"] = "https://example.invalid/sites/test";
                values["Graphics:SharePoint:DriveName"] = "Documents";
                // §324 — the live LinkedIn publisher arm (DryRun still holds every post).
                values["LinkedIn:Enabled"] = "true";
                // §7c — the read-only e-conomic ROLE source arm.
                values["EconomicRoles:Enabled"] = "true";
                break;

            case "testmode":
                values["TestMode:Enabled"] = "true";
                break;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ServiceProvider BuildRealJobsContainer(string shape)
    {
        var services = new ServiceCollection();
        var config = BuildConfig(shape);

        // HOST PLUMBING ONLY — the things HostBuilder/ConfigureFunctionsWorkerDefaults put in the
        // container before our code runs, and which Register() therefore assumes are there:
        // IConfiguration (HubEnvironment and the AI telemetry setup both resolve it), logging (every
        // job takes an ILogger<T>) and IHostEnvironment (JobsEnvironmentInfo). Deliberately nothing
        // else: anything a job needs beyond these must come from Register(), or the test would be
        // papering over the exact gap it exists to find.
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());

        JobsServiceRegistration.Register(services, config);

        // ValidateScopes so a scoped service captured by a singleton fails HERE — that fault also
        // survives a deploy and only misbehaves under concurrency.
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>Every type in the jobs assembly that carries at least one <c>[Function]</c> method —
    /// i.e. every class the worker will construct. Timer AND HTTP triggers.</summary>
    private static IReadOnlyList<Type> FunctionTypes() =>
        typeof(ReminderJob).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .Any(m => m.GetCustomAttribute<FunctionAttribute>() is not null))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    [Theory]
    [MemberData(nameof(ConfigShapes))]
    public async Task Every_job_can_be_CONSTRUCTED_from_the_real_jobs_container(string shape)
    {
        // `await using`, not `using`: the Functions AI telemetry module is IAsyncDisposable-only, so
        // a synchronous Dispose throws and would mask the result this test is actually reporting.
        await using var provider = BuildRealJobsContainer(shape);
        using var scope = provider.CreateScope();

        var failures = new List<string>();

        foreach (var type in FunctionTypes())
        {
            try
            {
                // Exactly what the worker does on a tick: activate the function class, resolving
                // every constructor parameter (and everything THEY need) from the container.
                ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);
            }
            catch (Exception ex)
            {
                var root = ex;
                while (root.InnerException is not null) root = root.InnerException;
                failures.Add($"  • {type.Name} — {root.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"These jobs cannot be constructed from the jobs host container ('{shape}' configuration). "
            + "In Azure this does NOT fail the deploy — it fails on the first timer tick, minutes "
            + "after the deploy reports success, and only the job log shows it (§784.15):\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public async Task The_container_itself_builds_for_every_configuration_shape()
    {
        // Separated from the activation test on purpose: if Register() itself throws, the failure
        // above would read as "every job is broken" and send the next reader hunting a job.
        foreach (var shape in new[] { "shipped-defaults", "prod-shaped", "testmode" })
        {
            var ex = await Record.ExceptionAsync(async () =>
            {
                await using var provider = BuildRealJobsContainer(shape);
            });
            Assert.True(ex is null,
                $"The jobs host container does not build with the '{shape}' configuration: "
                + $"{ex?.Message}");
        }
    }

    [Fact]
    public void The_activation_sweep_actually_covers_the_catalogued_jobs()
    {
        // Without this, deleting the [Function] discovery would leave the sweep passing over an
        // empty list — a green test that checks nothing, which is worse than no test at all.
        var found = FunctionTypes();
        Assert.True(found.Count >= 20,
            $"Only {found.Count} function classes were discovered in the jobs assembly. The sweep "
            + "above is only as good as this list.");

        // Every job the organizer's jobs page advertises must be one of the types we activate.
        var activated = found.SelectMany(t => t.GetMethods())
            .Select(m => m.GetCustomAttribute<FunctionAttribute>()?.Name)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.Ordinal);

        var uncovered = CommunityHub.Core.Settings.JobCatalog.All
            .Select(j => j.FunctionName)
            .Where(n => !activated.Contains(n))
            .ToList();

        Assert.True(uncovered.Count == 0,
            "These catalogued jobs are not covered by the activation sweep: "
            + string.Join(", ", uncovered));
    }

    [Fact]
    public async Task The_prod_shaped_configuration_really_selects_the_live_graphics_store()
    {
        // Proves the 'prod-shaped' row exercises the arm it claims to. If IsConfigured's rule ever
        // changes, that row would silently fall back to the null store and quietly stop covering
        // the live path — the failure mode this whole test exists to prevent, one level up.
        await using var provider = BuildRealJobsContainer("prod-shaped");
        using var scope = provider.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<ISharePointFileStore>();

        Assert.True(store is GraphSharePointFileStore,
            $"The 'prod-shaped' shape resolved {store.GetType().Name}, so it is NOT covering the "
            + "live-graphics registrations it is supposed to cover.");
    }
}
