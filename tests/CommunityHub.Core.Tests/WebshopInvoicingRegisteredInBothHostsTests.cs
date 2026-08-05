using System.Reflection;
using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §786.6 — THE REGRESSION TEST FOR A FAILED PROD DEPLOY (2026-08-04).
/// </summary>
/// <remarks>
/// <para><b>What happened.</b> §786 added <see cref="WebshopDraftInvoiceService"/> to the WEB host.
/// It needs <see cref="IFxRateProvider"/>, which the JOBS host has always registered and the web host
/// never had to — nothing there had needed it. The web host runs <c>ValidateOnBuild</c> (§783.10), so
/// the container failed to build, the app never started, the staging slot never answered 200, and
/// <c>deploy-app.ps1</c> refused to swap. <b>Production was never touched</b> — the guard worked
/// exactly as designed.</para>
///
/// <para>🔒 <b>But it cost a full deploy cycle to learn</b>, because <c>ValidateOnBuild</c> only runs
/// when the app STARTS. This test moves that discovery to <c>dotnet test</c>, where it costs seconds.
/// It is the web-host counterpart of <see cref="JobDependenciesResolveTests"/> — which covers the
/// Functions host properly, by building its real container, and could not see this because the fault
/// was one host over. Again.</para>
///
/// <para>🔑 <b>It reads the CONSTRUCTOR, not a hand-written list</b> — the same technique as
/// <see cref="LogisticsChainIsRegisteredInBothHostsTests"/>, for the same reason: a list has to be
/// remembered every time a dependency is added, which is precisely the drift being guarded against.
/// Add a parameter to the invoicing service and this test starts demanding it in both hosts on its
/// own.</para>
///
/// <para>⚠️ This is a SOURCE-TEXT check, so it proves a registration LINE exists — not that the
/// container builds. That stronger guarantee exists for the jobs host only. Widening it to the web
/// host means extracting <c>CommunityHub/Program.cs</c>'s registrations the way §784.15 extracted the
/// jobs host's; worth doing, and deliberately not attempted in the same change as a live cutover.</para>
/// </remarks>
public class WebshopInvoicingRegisteredInBothHostsTests
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
        throw new DirectoryNotFoundException("Could not locate the repo root.");
    }

    /// <summary>
    /// Where each host keeps its registrations. ⚠️ The jobs host's live in JobsServiceRegistration.cs
    /// since §784.15, NOT in Program.cs. An explicit map, not a glob — globbing the project would
    /// match a dependency's NAME inside a job class and report it as registered (a false pass in the
    /// one test whose job is to catch a missing registration).
    /// </summary>
    private static string RegistrationText(string project) => StripComments(project switch
    {
        "CommunityHub.Jobs" =>
            File.ReadAllText(Path.Combine(RepoRoot(), "src", project, "Program.cs"))
            + File.ReadAllText(Path.Combine(RepoRoot(), "src", project, "JobsServiceRegistration.cs")),
        _ => File.ReadAllText(Path.Combine(RepoRoot(), "src", project, "Program.cs")),
    });

    /// <summary>
    /// 🔒 Removes comments before searching, and this is NOT a nicety — it is the difference between
    /// a guard and a decoration.
    /// </summary>
    /// <remarks>
    /// The first version of this test PASSED with the registration deleted, because the explanatory
    /// comment written directly above it in <c>Program.cs</c> contains the words
    /// <c>IFxRateProvider</c>. A source-text scan cannot tell an explanation of a registration from
    /// the registration. Every well-commented codebase makes this failure MORE likely, not less —
    /// the better the comment, the more certainly it names the type the test is hunting for.
    ///
    /// <para>⚠️ The same weakness exists in the other source-scanning tests in this suite
    /// (<see cref="LogisticsChainIsRegisteredInBothHostsTests"/>, the credential-alert scan). They
    /// have not been bitten yet; that is luck, not design.</para>
    /// </remarks>
    private static string StripComments(string source)
    {
        // Block comments first (they can contain //), then line comments.
        var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);

        var lines = withoutBlocks.Split('\n')
            .Select(line =>
            {
                var idx = line.IndexOf("//", StringComparison.Ordinal);
                return idx >= 0 ? line[..idx] : line;
            });

        return string.Join("\n", lines);
    }

    /// <summary>The concrete CEH types a service asks for. Interfaces are INCLUDED here (unlike the
    /// logistics test): the fault this exists for was a missing INTERFACE mapping.</summary>
    private static IEnumerable<Type> CehDependenciesOf(Type service) =>
        service.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First()
            .GetParameters()
            .Select(p => p.ParameterType)
            .Where(t => t.Namespace?.StartsWith("CommunityHub", StringComparison.Ordinal) == true)
            .Where(t => t != typeof(Core.Data.CommunityHubDbContext))
            .Distinct();

    public static TheoryData<string, string> Hosts() => new()
    {
        { "CommunityHub", "web" },
        { "CommunityHub.Jobs", "jobs" },
    };

    [Theory]
    [MemberData(nameof(Hosts))]
    public void Every_invoicing_dependency_is_registered_in_this_host(string project, string host)
    {
        var registrations = RegistrationText(project);

        var needed = CehDependenciesOf(typeof(WebshopDraftInvoiceService))
            .Append(typeof(WebshopDraftInvoiceService))
            .Distinct()
            .ToList();

        // Not trivially empty — if reflection stops finding dependencies this would pass proving
        // nothing, which is how a guard becomes decoration.
        Assert.True(needed.Count >= 5,
            $"Expected the invoicing chain to have several dependencies, found {needed.Count}.");

        var missing = needed
            .Where(t => !registrations.Contains(t.Name, StringComparison.Ordinal))
            .Select(t => t.Name)
            .ToList();

        Assert.True(missing.Count == 0,
            $"The {host} host does not register: {string.Join(", ", missing)}. "
            + "§786.6: this exact gap (IFxRateProvider, missing in the web host) failed a PROD deploy "
            + "at the staging warm-up — ValidateOnBuild refused to build the container, so the slot "
            + "never answered 200 and the swap was correctly abandoned.");
    }

    [Fact]
    public void The_fx_provider_specifically_is_registered_in_both_hosts()
    {
        // Named on its own because it is the one that actually broke, and a reader hitting this
        // failure should not have to work out which of nine dependencies the message means.
        foreach (var (project, host) in new[] { ("CommunityHub", "web"), ("CommunityHub.Jobs", "jobs") })
        {
            var text = RegistrationText(project);
            Assert.True(
                text.Contains("IFxRateProvider", StringComparison.Ordinal),
                $"The {host} host does not register IFxRateProvider, which "
                + "WebshopDraftInvoiceService requires (§786.6).");
        }
    }
}
