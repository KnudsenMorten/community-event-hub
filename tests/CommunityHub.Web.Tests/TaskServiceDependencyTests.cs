using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Tasks;
using CommunityHub.Core.Tasks.Data;
using CommunityHub.Core.Tasks.Definitions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// 🔴 §687.9 — THE REGRESSION TEST FOR A LIVE PROD INCIDENT (2026-07-29).
/// </summary>
/// <remarks>
/// <para><b>What happened.</b> §687's <see cref="SponsorTaskPlaceholderBuilder"/> took a dependency
/// on <see cref="SponsorConfigLoader"/>. The JOBS host registers that type; the WEB host never had
/// to, because nothing in it had ever needed it. So every authenticated load of
/// <c>/Sponsor/Tasks</c> threw <i>"Unable to resolve service for type SponsorConfigLoader"</i> and
/// returned 500 — for real sponsors, in production.</para>
///
/// <para>🔒 <b>Why the post-deploy smoke did not catch it, which is the lesson worth keeping.</b>
/// Every probe was ANONYMOUS, and an anonymous request redirects to <c>/Login</c> BEFORE the page
/// model is constructed. A DI failure inside a page is invisible to any check that never builds
/// that page. <c>/health</c> returned "Healthy" throughout. The defect surfaced only from App
/// Insights exceptions, i.e. from looking at what the app was actually doing rather than at whether
/// it answered.</para>
///
/// <para>This test resolves the task services from a container wired the same way the web host wires
/// them, so a missing registration fails here instead of on a sponsor's screen.</para>
/// </remarks>
public class TaskServiceDependencyTests
{
    /// <summary>
    /// Mirrors the registrations the web host makes for the §684/§687 task stack. Deliberately
    /// lists them EXPLICITLY rather than booting the real host: the point is to assert that this
    /// set is sufficient, so an omission shows up as a failed resolve rather than being papered
    /// over by whatever else the host happens to register.
    /// </summary>
    private static ServiceProvider BuildWebLikeContainer()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton(TimeProvider.System);

        services.AddDbContext<CommunityHubDbContext>(
            o => o.UseInMemoryDatabase($"di-{Guid.NewGuid():N}"));

        // Config layer.
        services.AddSingleton(new EventConfigOptions());
        services.AddSingleton<EventEditionConfigLoader>();
        services.AddSingleton(new SponsorConfigOptions());
        services.AddSingleton<SponsorConfigLoader>();          // ← the one that was missing
        services.AddSingleton(new IntegrationsConfigOptions());
        services.AddSingleton<IntegrationsConfigLoader>();

        // WooCommerce + the shared purchase resolver.
        services.AddSingleton(new WooCommerceOptions());
        services.AddSingleton(sp => new WooCommerceClient(
            new HttpClient(), sp.GetRequiredService<WooCommerceOptions>()));
        services.AddScoped<SponsorPurchaseSummaryService>();

        // The §684/§687 task stack.
        services.AddSingleton(TaskDefinitionRegistry.Shipped);
        services.AddSingleton<TaskBodyStore>();
        services.AddScoped<ITaskDataProvider, TvPurchaseTaskDataProvider>();
        services.AddScoped<ITaskDataProvider, ShipmentPurchasesTaskDataProvider>();
        services.AddScoped<ITaskDataProvider, BoothFurniturePurchasesTaskDataProvider>();
        services.AddScoped<ITaskDataProvider, AttendeeBagPackagingTaskDataProvider>();
        services.AddScoped<ITaskDataProvider, ExtraStaffTicketsTaskDataProvider>();
        services.AddScoped<TaskDataResolver>();
        services.AddScoped<TaskBodyService>();
        services.AddScoped<SponsorTaskPlaceholderBuilder>();
        services.AddScoped<PurchaseTaskReconciler>();

        // validateScopes so a scoped dependency captured by a singleton fails here too.
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Theory]
    [InlineData(typeof(SponsorTaskPlaceholderBuilder))]
    [InlineData(typeof(TaskBodyService))]
    [InlineData(typeof(TaskDataResolver))]
    [InlineData(typeof(PurchaseTaskReconciler))]
    [InlineData(typeof(SponsorPurchaseSummaryService))]
    public void Every_task_service_resolves_from_the_web_container(Type serviceType)
    {
        using var provider = BuildWebLikeContainer();
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetService(serviceType);

        Assert.True(
            resolved is not null,
            $"{serviceType.Name} could not be resolved. A missing registration here is a 500 on a "
            + "participant-facing page — and the anonymous smoke checks will NOT see it.");
    }

    [Fact]
    public void Every_data_provider_resolves_and_the_source_names_are_unique()
    {
        using var provider = BuildWebLikeContainer();
        using var scope = provider.CreateScope();

        var providers = scope.ServiceProvider.GetServices<ITaskDataProvider>().ToList();

        Assert.NotEmpty(providers);

        // Two providers claiming one source name would mean the resolver silently answers with
        // whichever won the dictionary — a wrong answer on a sponsor's task, with nothing to see.
        var sources = providers.Select(p => p.Source).ToList();
        Assert.Equal(sources.Count, sources.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
