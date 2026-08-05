using System.Reflection;
using CommunityHub.Core.Integrations.DocLibrary;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.3 — every dependency of the logistics chain is registered in BOTH hosts.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Why this exists.</b> Until §6.3 the chain lived only in the jobs host, because only
/// <c>LogisticsFilesJob</c> used it. <c>/Organizer/Logistics</c> now lists the generated files and
/// offers "Generate now", so the WEB host injects <see cref="LogisticsRunService"/> too.</para>
///
/// <para>⚠️ <b>A missing registration here is invisible to the compiler and to every unit test.</b>
/// Razor Pages resolve their model per request, so one unregistered producer is a <b>500 on a real
/// page in production</b> — not a build error, not a red test. The jobs host already carries a
/// comment saying exactly this about <c>ISponsorPurchaseSummary</c>, which it hit once.</para>
///
/// <para>🔑 <b>It reads the constructor, not a hand-written list.</b> A list would have to be
/// remembered every time a producer is added — which is the drift being guarded against. Reflection
/// asks the type itself what it needs, so a new producer is covered the day it is introduced.</para>
/// </remarks>
public class LogisticsChainIsRegisteredInBothHostsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repo root by walking up from '{AppContext.BaseDirectory}'.");
    }

    private static string ProgramText(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot(), "src" }.Concat(parts).ToArray()));

    /// <summary>
    /// Every file a host keeps its SERVICE REGISTRATIONS in, concatenated.
    /// </summary>
    /// <remarks>
    /// ⚠️ §784.15 — the jobs host's registrations no longer live in <c>Program.cs</c>. They were
    /// lifted into <c>JobsServiceRegistration.cs</c> so a test could build that container for real,
    /// and this scan went red the moment they moved. 🔒 It is an EXPLICIT list, not a directory
    /// glob: every job class also mentions the types it depends on, so globbing the project would
    /// match a dependency's NAME and report it as registered — a false pass, in the one test whose
    /// whole job is to catch a missing registration.
    /// </remarks>
    private static string RegistrationText(string project) => project switch
    {
        "CommunityHub.Jobs" => ProgramText(project, "Program.cs")
                               + ProgramText(project, "JobsServiceRegistration.cs"),
        _ => ProgramText(project, "Program.cs"),
    };

    /// <summary>
    /// The concrete CEH types a service asks for — the ones a container must be told about.
    /// Framework and interface dependencies are excluded: those are registered elsewhere and are
    /// not what drifts.
    /// </summary>
    private static IEnumerable<Type> CehDependenciesOf(Type service) =>
        service.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First()
            .GetParameters()
            .Select(p => p.ParameterType)
            .Where(t => t.Namespace?.StartsWith("CommunityHub", StringComparison.Ordinal) == true)
            .Where(t => !t.IsInterface)
            .Where(t => t != typeof(Core.Data.CommunityHubDbContext))
            .Distinct();

    public static TheoryData<string, string> Hosts() => new()
    {
        { "CommunityHub", "web" },
        { "CommunityHub.Jobs", "jobs" },
    };

    [Theory]
    [MemberData(nameof(Hosts))]
    public void Every_logistics_dependency_is_registered(string project, string host)
    {
        var program = RegistrationText(project);

        var needed = CehDependenciesOf(typeof(LogisticsRunService))
            .Concat(CehDependenciesOf(typeof(LogisticsArtifactsService)))
            .Concat([typeof(LogisticsRunService), typeof(LogisticsArtifactsService)])
            .Distinct()
            .ToList();

        // The chain is not trivially empty — if reflection stops finding dependencies, this test
        // would pass while proving nothing.
        Assert.True(needed.Count >= 6, $"Expected the logistics chain to have several dependencies, found {needed.Count}.");

        var missing = needed
            .Where(t => !program.Contains(t.Name, StringComparison.Ordinal))
            .Select(t => t.Name)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"The {host} host does not register: {string.Join(", ", missing)}. "
            + "An unregistered dependency is a 500 when the page or job is activated — the compiler "
            + "cannot see it.");
    }
}
