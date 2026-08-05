using System.Text.RegularExpressions;
using CommunityHub.Forms;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §708.10a — every step handler's partial must RESOLVE TO A FILE, from any host.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Written after a live break, and it is the test that was missing.</b> Handlers mostly
/// return a bare partial name (<c>_HotelFields</c>) and those partials live in <c>/Pages/Forms/</c>.
/// Razor resolves a bare name against the current page's folder, then <c>/Pages/</c>, then
/// <c>/Pages/Shared/</c> — never <c>/Pages/Forms/</c>. It worked for years only because the ONLY
/// host was the wizard, which sits in that folder. §708.10 added a second host (<c>/Tasks</c>) and
/// every bare name began throwing <i>"The partial view was not found"</i>, taking the task list down
/// for four roles.</para>
///
/// <para>Both suites were green through all of that: neither renders a page. So this test asserts
/// the one thing a compiler cannot — that the NAME a handler declares corresponds to a FILE.</para>
/// </remarks>
public class WizardStepPartialResolutionTests
{
    /// <summary>Walk up to the repo root (where <c>src/</c> lives).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// Every <c>PartialName =&gt; "…"</c> declared by a step handler, read from the source.
    /// </summary>
    /// <remarks>
    /// Read from SOURCE rather than by reflecting over instances on purpose: constructing a handler
    /// needs its whole service graph, and the thing under test is a string constant, not behaviour.
    /// </remarks>
    public static TheoryData<string, string> DeclaredPartialNames()
    {
        var data = new TheoryData<string, string>();
        var stepsDir = Path.Combine(RepoRoot(), "src", "CommunityHub", "Forms", "Steps");

        foreach (var file in Directory.GetFiles(stepsDir, "*StepHandler.cs"))
        {
            var match = Regex.Match(
                File.ReadAllText(file), @"PartialName\s*=>\s*""([^""]+)""");
            if (match.Success) data.Add(Path.GetFileName(file), match.Groups[1].Value);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DeclaredPartialNames))]
    public void Every_declared_step_partial_resolves_to_a_file_on_disk(
        string handlerFile, string partialName)
    {
        var path = WizardStepPartials.PathFor(partialName);

        // PathFor returns an app-relative Razor path ("/Pages/Forms/_HotelFields.cshtml"); map it
        // onto the project on disk.
        var onDisk = Path.Combine(
            RepoRoot(), "src", "CommunityHub", path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        Assert.True(
            File.Exists(onDisk),
            $"{handlerFile} declares PartialName '{partialName}', which resolves to '{path}' — "
            + $"no file at {onDisk}. A host outside /Pages/Forms/ will throw at render time.");
    }

    [Fact]
    public void A_bare_name_is_qualified_and_a_full_path_is_left_alone()
    {
        // The bare form — the one that broke.
        Assert.Equal("/Pages/Forms/_HotelFields.cshtml", WizardStepPartials.PathFor("_HotelFields"));

        // The already-qualified form, which two handlers use because their partials live elsewhere.
        // Re-qualifying these would break them the opposite way.
        Assert.Equal(
            "/Pages/Speaker/_DetailsFields.cshtml",
            WizardStepPartials.PathFor("/Pages/Speaker/_DetailsFields.cshtml"));
    }
}
