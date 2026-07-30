using CommunityHub.Core.Settings;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §566 step 5 — <b>DECLARED category must match OBSERVED use.</b> The test the ring rebuild asked
/// for, and the one that would have found the phantom rings automatically instead of by hand.
///
/// <para><b>The defect it exists to prevent.</b> §566 recorded the risk plainly: *"Classification is
/// currently HAND-DECLARED and unverified … A wrong `Surface` gives a backend sync a ring badge it
/// does not honour."* That is exactly what happened — `magic-link` and `sponsor-welcome` were
/// DECLARED e-mail feature keys, so the Settings page showed them with a ring, while NO send site
/// passed either key. The ring governed nothing. It read as an audience control and was not one,
/// which is the §326bx incident and a direct cause of the operator losing confidence in the page
/// (§563/§564).</para>
///
/// <para>Both were removed by hand on 2026-07-28 after I searched the source. This test makes that
/// search a build step, so the next one cannot survive review.</para>
/// </summary>
public class FeatureKeyDeclaredVsObservedTests
{
    /// <summary>The repo's src directory, found by walking up from the test binaries.</summary>
    private static string SrcDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("src not found from " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SrcDir(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// Every e-mail FeatureKey the catalog declares by hand must actually be PASSED at a send site.
    /// A key nobody passes gates nothing, so showing it with a ring is a lie.
    /// </summary>
    [Fact]
    public void Every_hand_declared_email_feature_key_is_passed_at_a_send_site()
    {
        // The keys derived from the template catalog are verified by construction — a template
        // declares its own key. Only the HAND-ADDED ones can drift, and they are the ones that did.
        var templateDerived = Core.Email.EmailTemplateCatalog.Map.Values
            .Select(v => v.FeatureKey)
            .ToHashSet(StringComparer.Ordinal);

        var handDeclared = FeatureCatalog.EmailFeatureKeys
            .Where(k => !templateDerived.Contains(k))
            .ToList();

        var allSource = string.Join("\n", SourceFiles().Select(File.ReadAllText));

        var unused = handDeclared
            .Where(k => !allSource.Contains($"FeatureKey: \"{k}\"", StringComparison.Ordinal)
                        && !allSource.Contains($"FeatureKey = \"{k}\"", StringComparison.Ordinal)
                        && !allSource.Contains($"FeatureKey(\"{k}\")", StringComparison.Ordinal))
            .ToList();

        Assert.True(unused.Count == 0,
            "These keys are declared as e-mail FeatureKeys but NO send site passes them, so their "
            + "ring governs nothing while the Settings page shows one — the §326bx defect. Either "
            + "pass the key where the mail is sent, or remove it from EmailFeatureKeys: "
            + string.Join(", ", unused));
    }

    /// <summary>
    /// A ring may only be shown where a ring does something. Nothing that is NOT ring-scoped may be
    /// classified as an e-mail or a feature — those two classes are what the page renders a ring for.
    /// </summary>
    [Fact]
    public void Only_ring_scoped_features_are_classified_as_email_or_feature()
    {
        var wrong = FeatureCatalog.All
            .Where(d => !d.IsRingScoped && d.Governs != FeatureGoverns.Backend)
            .Select(d => $"{d.Key} → {d.Governs}")
            .ToList();

        Assert.True(wrong.Count == 0,
            "A switch that is not ring-scoped must be classified Backend (on/off, no ring shown). "
            + "Anything else puts a ring badge on a control whose ring does nothing: "
            + string.Join(" | ", wrong));
    }

    /// <summary>
    /// §566 — within one role group, no two rows may render an IDENTICAL label. Subjects collide
    /// (three welcomes all read "Welcome to …"), which is precisely why the row carries the internal
    /// key as well: <c>Subject: "…"</c> then <c>key · Ring N</c>. Two identical rows would leave the
    /// operator asking "which one do you mean?" — the round-trip §566 set out to remove.
    /// </summary>
    [Fact]
    public void No_two_templates_in_one_audience_group_share_a_key()
    {
        var dupes = Core.Email.EmailTemplateCatalog.Map
            .GroupBy(kv => Core.Email.EmailTemplateCatalog.AudienceFor(kv.Key))
            .SelectMany(g => g.GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                              .Where(x => x.Count() > 1)
                              .Select(x => $"{g.Key}/{x.Key}"))
            .ToList();

        Assert.True(dupes.Count == 0,
            "Two rows in one role group would render identically and be indistinguishable: "
            + string.Join(", ", dupes));
    }
}
