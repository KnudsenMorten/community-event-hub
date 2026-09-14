using System.Reflection;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Organizer;
using CommunityHub.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1114 — dependencies the jobs host declares OPTIONAL but cannot actually work without.
/// </summary>
/// <remarks>
/// <para><b>The gap this exists to close.</b> <see cref="ErpWebshopContactSyncService"/> takes
/// <see cref="ParticipantDeactivationService"/> as an optional constructor parameter, and only the
/// WEB host registered it. In the jobs host it therefore arrived <c>null</c> on every run, and both
/// branches that use it returned at their first line: §502's orphan pruning — <b>while the mail it
/// sends said "Already done automatically: the matching hub participant was deactivated"</b> — and
/// §1112's de-sponsored sweep, which is how it was finally caught: deployed, run for 92 s against
/// real production data, and it changed nothing.</para>
///
/// <para>🔑 <b>Why <c>JobDependenciesResolveTests</c> could not catch it.</b> That test activates
/// every <c>[Function]</c> class against the real registration and passed the whole time — <b>an
/// optional parameter is satisfied by null</b>, so construction was never in doubt. Optionality
/// converts a missing registration from a startup error into silence. This test pays for that
/// optionality: the services below must be PRESENT, not merely permitted to be absent.</para>
///
/// <para>⚠️ Adding a service here is a claim that a job's behaviour changes when it is missing. If a
/// dependency is genuinely optional — a host legitimately runs without it — it does not belong.</para>
/// </remarks>
public sealed class JobsOptionalButLoadBearingDepsTests
{
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "CommunityHub.Jobs.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string EnvironmentName { get; set; } = "Production";
    }

    private static ServiceProvider BuildJobsContainer(bool testMode = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sql:ConnectionStringTemplate"] = "Server=tcp:localhost,1433;Database=jobs-di-test;Encrypt=True;",
            ["TestMode:Enabled"] = testMode ? "true" : "false",
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        JobsServiceRegistration.Register(services, config);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public async Task The_jobs_host_registers_the_participant_deactivation_cascade()
    {
        await using var sp = BuildJobsContainer();
        using var scope = sp.CreateScope();

        // Without this the ERP reconcile deactivates nobody — and says it did.
        Assert.NotNull(scope.ServiceProvider.GetService<ParticipantDeactivationService>());
    }

    [Fact]
    public async Task The_ERP_reconcile_really_HAS_its_db_and_its_deactivation_cascade()
    {
        await using var sp = BuildJobsContainer();
        using var scope = sp.CreateScope();

        var svc = scope.ServiceProvider.GetRequiredService<ErpWebshopContactSyncService>();

        // 🔒 Asserting the FIELDS, not just that the container could supply them. The defect was a
        // constructed service holding null — resolvable and inert — so "the container has one" is
        // the question that already answered yes while the feature did nothing.
        foreach (var name in new[] { "_db", "_deactivate" })
        {
            var field = typeof(ErpWebshopContactSyncService)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(field is not null, $"{name} no longer exists — update this guard.");
            Assert.True(field!.GetValue(svc) is not null,
                $"ErpWebshopContactSyncService.{name} is null in the jobs host, so orphan pruning "
                + "(§502) and the de-sponsored sweep (§1112) both silently do nothing. Register the "
                + "missing service in JobsServiceRegistration.");
        }
    }

    /// <summary>
    /// 🔴 §1119 — the invoicing jobs must actually RECEIVE <c>TestModeOptions</c>, or the "DEV does
    /// not invoice" gate is a comment.
    /// </summary>
    /// <remarks>
    /// <para>Both jobs take it as an OPTIONAL parameter, which is the shape this whole class exists
    /// to distrust: <c>null</c> satisfies the constructor, <c>_testMode?.Enabled == true</c> is then
    /// false on every host, and the sweep runs — and mails — from DEV exactly as before. The failure
    /// is silent in the only way that matters: the code reads as if it were fixed.</para>
    ///
    /// <para>🔑 Asserting the FIELD on an activated job, not the registration: the worker constructs
    /// a job per invocation, so "the container has a TestModeOptions" is not the question — "does the
    /// job hold it" is.</para>
    /// </remarks>
    [Theory]
    [InlineData(typeof(CouponInvoiceJob))]
    [InlineData(typeof(WebshopInvoiceJob))]
    public async Task The_invoicing_jobs_can_see_that_TestMode_is_on(Type jobType)
    {
        await using var sp = BuildJobsContainer(testMode: true);
        using var scope = sp.CreateScope();

        var job = ActivatorUtilities.CreateInstance(scope.ServiceProvider, jobType);

        var field = jobType.GetField("_testMode", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field is not null,
            $"{jobType.Name}._testMode no longer exists — update this guard.");

        var options = Assert.IsType<CommunityHub.Core.Integrations.TestModeOptions>(
            field!.GetValue(job));
        Assert.True(options.Enabled,
            $"{jobType.Name} received a TestModeOptions that does not know TestMode is on, so the "
            + "§1119 gate never fires and DEV keeps creating invoice drafts and mailing about them.");
    }
}
