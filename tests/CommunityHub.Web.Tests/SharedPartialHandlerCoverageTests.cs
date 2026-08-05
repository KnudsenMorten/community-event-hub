using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §783.7 — a fields-partial that posts to a NAMED handler makes that handler part of the contract
/// for EVERY page that renders it.
/// </summary>
/// <remarks>
/// <para>🔴 <b>The bug this exists for.</b> <c>_SponsorSessionFields</c> is shared by
/// <c>/Forms/Wizard</c> and <c>/Sponsor/SessionForm</c>. §734 added a <b>Remove</b> button to the
/// partial posting <c>asp-page-handler="RemoveSpeaker"</c>, and implemented the handler on the
/// wizard host only. On the standalone page the POST matched nothing — and Razor Pages does not
/// FAULT an unmatched named handler, it simply runs no handler and re-renders the page.</para>
///
/// <para>So the button returned HTTP 200, the page came back looking normal, and the speaker was
/// still there. Operator 2026-08-03: <i>"When I click REMOVE button speaker is not removed from list
/// below of speaker. I also tried to refresh page, but speaker is still linked to the session."</i>
/// Nothing was logged, nothing errored, and no test failed.</para>
///
/// <para>🔒 <b>This is the sibling of <see cref="SubmitButtonHasAFormTests"/>.</b> That one catches a
/// submit button with no form (silently inert); this one catches a submit button with a form but no
/// handler (also silently inert). Both are invisible to the compiler, the browser and every unit
/// test — the only other detector is a person clicking and noticing nothing happened.</para>
/// </remarks>
public sealed class SharedPartialHandlerCoverageTests
{
    private static readonly Regex NamedHandler =
        new(@"asp-page-handler=""(?<name>[A-Za-z0-9_]+)""", RegexOptions.IgnoreCase);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// Each shared fields-partial, and every page model that renders it. Add a host here when a
    /// partial gains one — that is the whole point: the list is what makes the contract explicit.
    /// </summary>
    public static TheoryData<string, string[]> SharedPartials => new()
    {
        {
            Path.Combine("Pages", "Forms", "_SponsorSessionFields.cshtml"),
            new[]
            {
                Path.Combine("Pages", "Forms", "Wizard.cshtml.cs"),
                Path.Combine("Pages", "Sponsor", "SessionForm.cshtml.cs"),
            }
        },
        {
            // §783.3 — wizard-hosted only today. Listed so that adding a second host without its
            // handler fails here rather than in front of a sponsor.
            Path.Combine("Pages", "Forms", "_SponsorContactsFields.cshtml"),
            new[] { Path.Combine("Pages", "Forms", "Wizard.cshtml.cs") }
        },
    };

    // ⚠️ NOT listed, deliberately: `_SponsorLogosFields` and `_SponsorBoothMaterialsFields`. Their
    // §783.4/§783.5 buttons post `__dir=stay` to the wizard's ORDINARY handler rather than to a
    // NAMED one, so there is no handler name for this test to look for. Adding them would trip the
    // "the sweep found nothing" guard below — which is the guard working, not a gap. If either
    // partial ever gains an `asp-page-handler`, add it here.

    [Theory]
    [MemberData(nameof(SharedPartials))]
    public void Every_handler_a_shared_partial_posts_to_exists_on_EVERY_host_page(
        string partialRelPath, string[] hostRelPaths)
    {
        var web = Path.Combine(FindRepoRoot(), "src", "CommunityHub");
        var partialPath = Path.Combine(web, partialRelPath);
        Assert.True(File.Exists(partialPath), $"Partial not found: {partialPath}");

        var handlers = NamedHandler.Matches(File.ReadAllText(partialPath))
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // If this ever reaches zero the test has stopped testing anything — the §768 lesson that a
        // sweep matching nothing looks exactly like a sweep that passed.
        Assert.NotEmpty(handlers);

        foreach (var hostRel in hostRelPaths)
        {
            var hostPath = Path.Combine(web, hostRel);
            Assert.True(File.Exists(hostPath), $"Host page not found: {hostPath}");
            var host = File.ReadAllText(hostPath);

            foreach (var handler in handlers)
            {
                // Razor Pages binds `asp-page-handler="Foo"` on a POST to OnPostFooAsync/OnPostFoo.
                var expected = $"OnPost{handler}";
                Assert.True(
                    host.Contains(expected, StringComparison.Ordinal),
                    $"{Path.GetFileName(partialRelPath)} posts to handler '{handler}', but "
                    + $"{Path.GetFileName(hostRel)} has no '{expected}Async'. An unmatched named "
                    + "handler does NOT error in Razor Pages — it renders the page and the control "
                    + "silently does nothing (§783.7).");
            }
        }
    }
}
