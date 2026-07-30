using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// CLAUDE.md rule 5 — <b>"`docs/TESTS.md` and the test scripts in `tests/` must contain the same set of
/// tests. Never document a test that isn't implemented in code."</b> That was a promise, and a promise is
/// not a mechanism.
///
/// <para>🔴 It had already drifted when this was written (2026-07-29): TESTS.md documented
/// <c>PublicLandingPageTests</c> and <c>PublicSessionCalendarControllerTests</c>, **neither of which
/// exists**. Both were deleted along with the features they covered — the landing page was removed
/// (operator 2026-06-21: *"the marketing landing page is removed entirely"*) and the public session
/// calendar controller is gone from <c>src</c> too — but their TESTS.md rows survived.</para>
///
/// <para>Why that matters more than tidiness: the stale rows described behaviour that is now the
/// OPPOSITE of what ships. TESTS.md claimed an anonymous visitor gets the landing page "NOT a redirect
/// to Login", while production redirects to <c>/Login</c> by design. Anyone checking the docs before
/// the code — which is the point of having docs — would have called a correct production behaviour a
/// regression. That is the same shape as [ceh-decision-vs-hold]: a document reading as settled while
/// the code says otherwise.</para>
///
/// <para>Deliberately ONE-DIRECTIONAL: every test file NAMED in the doc must exist. The reverse (every
/// file must be documented) is not asserted — a new test should not fail the build for want of a doc
/// row in the same commit, and the operator's rule is about not documenting fiction.</para>
/// </summary>
public sealed class TestsDocCurrencyTests
{
    [Fact]
    public void Every_test_file_named_in_TESTS_md_actually_exists()
    {
        var repoRoot = FindRepoRoot();
        var doc = File.ReadAllText(Path.Combine(repoRoot, "docs", "TESTS.md"));

        // Matches the convention the doc uses throughout, e.g. `CommunityHub.Web.Tests/FooTests.cs`.
        var named = Regex.Matches(doc, @"CommunityHub\.(?:Core|Web)\.Tests/[A-Za-z0-9_]+\.cs")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(named); // the regex itself must not silently stop matching

        var missing = named
            .Where(rel => !File.Exists(Path.Combine(repoRoot, "tests", rel.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

        Assert.True(missing.Count == 0,
            "docs/TESTS.md documents test files that do not exist. Either restore the test or remove "
            + "the row — a documented test that isn't implemented is worse than an undocumented one, "
            + "because it reads as coverage:\n  " + string.Join("\n  ", missing));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repo root (a directory containing both docs/ and tests/) from "
            + AppContext.BaseDirectory);
    }
}
