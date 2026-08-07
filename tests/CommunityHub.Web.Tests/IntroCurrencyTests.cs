using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §955 — THE CEH INTRODUCTION MUST NOT SILENTLY FALL BEHIND <c>FEATURES.md</c>.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-07: <i>"each feature must be detailed including those that was build during
/// the period since last update, verify against features.md"</i>.</para>
///
/// <para>🔴 <b>It had drifted by EIGHT chapters before anyone noticed</b>, and the only reason anyone
/// did was that somebody sat down and compared the two files by hand. That is the failure this class
/// exists to end: <i>"is the intro current?"</i> was a judgement, made from memory, at the end of a
/// busy week — which is precisely when it does not get made. §955 asked for it to become a test, the
/// same move <see cref="TestsDocCurrencyTests"/> made for TESTS.md.</para>
///
/// <para>🔑 <b>How it works, and why it is a MAP rather than a keyword search.</b> The obvious
/// implementation — grep the intro for words from each chapter title — is what produced the original
/// hand audit, and it is unreliable in both directions: an OR-matched keyword like "editor" hits
/// unrelated prose and reports a missing chapter as present, while a chapter genuinely covered in the
/// reader's language ("your test accounts stop ordering lunch" → "the lunch order") uses none of the
/// title's words. So coverage is DECLARED, once, per chapter. Adding a chapter to FEATURES.md fails
/// this test until somebody states what happened to it.</para>
///
/// <para>🔒 <b>"Not intro-relevant" is a legitimate answer, but it must be a STATED one.</b> The intro
/// is a customer-facing narrative, not a mirror of the changelog — a chapter can reasonably be a
/// refinement of something already described. <see cref="NotInIntro"/> records those WITH A REASON, so
/// the decision is visible and revisitable instead of being indistinguishable from an oversight.</para>
/// </remarks>
public sealed class IntroCurrencyTests
{
    private const string IntroPath = "config/content/eldk27/ceh-introduction.md";

    /// <summary>
    /// Chapters deliberately NOT given their own passage in the intro, each with the reason.
    /// ⚠️ Adding a line here is a decision, not a way to make the build green — if the chapter is
    /// something a customer would want to read about, write the passage instead.
    /// </summary>
    private static readonly Dictionary<int, string> NotInIntro = new()
    {
        [14] = "Bilingual UI — the intro itself is published in English only, so a passage about the "
             + "language switcher would be describing something the reader cannot see on the page.",
    };

    /// <summary>
    /// Chapters below this are GRANDFATHERED — the test enforces coverage from here on.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>Why a baseline rather than mapping all 82 retroactively.</b> The intro was
    /// restructured and re-audited against the catalogue at §953/§954, and §955 then MEASURED where it
    /// had fallen behind: §74 onward. Hand-mapping the earlier 73 chapters to passages written in a
    /// customer's language — where "your test accounts stop ordering lunch" legitimately reads as "the
    /// lunch order" — is exactly the memory-and-judgement exercise §955 asked to be rid of, and it
    /// would produce a map nobody could trust.</para>
    ///
    /// <para>🔒 <b>The purpose is to stop the NEXT drift, not to relitigate the last one.</b> Eight
    /// chapters accumulated because nothing failed when one was forgotten; from here, one does.</para>
    ///
    /// <para>⚠️ Do NOT raise this number to make a failure go away — that silently re-opens the gap.
    /// Lowering it, chapter by chapter, as older passages get marked, is the improvement path.</para>
    /// </remarks>
    private const int FirstEnforcedChapter = 74;

    [Fact]
    public void Every_FEATURES_chapter_is_either_in_the_intro_or_explicitly_excused()
    {
        var repoRoot = FindRepoRoot();
        var features = File.ReadAllText(Path.Combine(repoRoot, "docs", "FEATURES.md"));

        // The doc's own convention throughout: "## 82. Add somebody the sync has not heard of yet".
        var chapters = Regex.Matches(features, @"(?m)^##\s+(\d+)\.\s+(.+?)\s*(?:\*\(|$)")
            .Select(m => (Number: int.Parse(m.Groups[1].Value), Title: m.Groups[2].Value.Trim()))
            .DistinctBy(c => c.Number)
            .OrderBy(c => c.Number)
            .ToList();

        // The regex itself must not silently stop matching — a doc reformat that broke it would
        // otherwise turn this whole test into a no-op that still reports green.
        Assert.True(chapters.Count > 50,
            $"only found {chapters.Count} chapters in FEATURES.md — the heading pattern probably changed.");

        var introFile = Path.Combine(repoRoot, IntroPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(introFile), $"intro not found at {IntroPath}");
        var intro = File.ReadAllText(introFile);

        // Coverage is DECLARED per chapter (see the class remarks on why this is not a keyword
        // search). The marker lives in an HTML COMMENT so it is invisible to the reader — the intro
        // is customer-facing, and a bookkeeping token rendered into the prose would be a worse
        // problem than the one this solves.
        var covered = Regex.Matches(intro, @"covers:\s*([0-9]+(?:\s*,\s*[0-9]+)*)")
            .SelectMany(m => m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries))
            .Select(int.Parse)
            .ToHashSet();

        var unaccounted = chapters
            .Where(c => c.Number >= FirstEnforcedChapter)
            .Where(c => !covered.Contains(c.Number) && !NotInIntro.ContainsKey(c.Number))
            .Select(c => $"§{c.Number} — {c.Title}")
            .ToList();

        Assert.True(unaccounted.Count == 0,
            "FEATURES.md chapters with no corresponding passage in the CEH introduction, and no stated "
            + "reason for leaving them out.\n\n"
            + "Write the passage and mark it with [covers:N], or — if it genuinely does not belong in a "
            + "customer-facing narrative — add it to IntroCurrencyTests.NotInIntro WITH the reason.\n\n"
            + string.Join("\n", unaccounted));
    }

    /// <summary>
    /// 🔒 The excuse list must not outlive the chapters it excuses. A stale entry silently re-opens
    /// the gap it was meant to close, because a renumbered chapter would inherit the exemption.
    /// </summary>
    [Fact]
    public void No_excuse_refers_to_a_chapter_that_no_longer_exists()
    {
        var features = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "FEATURES.md"));
        var numbers = Regex.Matches(features, @"(?m)^##\s+(\d+)\.")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToHashSet();

        var dangling = NotInIntro.Keys.Where(n => !numbers.Contains(n)).ToList();
        Assert.True(dangling.Count == 0,
            $"NotInIntro excuses chapters that are not in FEATURES.md: {string.Join(", ", dangling)}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs"))
                && File.Exists(Path.Combine(dir.FullName, "docs", "FEATURES.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"repo root not found from {AppContext.BaseDirectory}");
    }
}
