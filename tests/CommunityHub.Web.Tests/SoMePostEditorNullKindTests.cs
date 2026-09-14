using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1180 — ONE POST WITH NO TEMPLATE KIND TOOK THE WHOLE EDITOR DOWN.
///
/// <para>Operator 2026-09-12: <i>"duplicating existing publish post givs error"</i> — HTTP 500 on
/// <c>/Organizer/SoMePostEditor?id=319752</c>. App Insights:
/// <c>System.ArgumentNullException: Value cannot be null. (Parameter 'key')</c> in
/// <c>SoMePostEditorModel.LoadAsync</c>, on both the GET and the save.</para>
///
/// <para>🔑 <b>A nullable key TYPE does not make a null key legal.</b>
/// <c>Dictionary&lt;SoMeTemplateKind?, int&gt;</c> compiles and is accepted as a declared type, then
/// throws the instant a null arrives — so <c>ToDictionary</c> over a <c>GroupBy</c> that produced a
/// null group fails at runtime with nothing the compiler could have warned about.</para>
///
/// <para>⚠️ <b>The blast radius is the defect.</b> The count is taken over EVERY post in the edition,
/// so one kindless post 500s the editor for every post, not only its own — and two ordinary actions
/// create one: Duplicate (§1050 gives the copy no kind and no subject, deliberately) and the
/// New-post button. The editor was one ad-hoc post away from total failure from the day both
/// shipped.</para>
/// </summary>
public sealed class SoMePostEditorNullKindTests
{
    /// <summary>
    /// 🔒 The invariant the fix rests on, pinned so nobody "simplifies" the guard away believing a
    /// nullable key type permits null.
    /// </summary>
    [Fact]
    public void A_dictionary_with_a_nullable_enum_key_still_refuses_null()
    {
        var dict = new Dictionary<SoMeTemplateKind?, int>();

        var ex = Assert.Throws<ArgumentNullException>(() => dict.Add(null, 1));
        Assert.Equal("key", ex.ParamName);
    }

    /// <summary>
    /// 🔴 The reported case, as the page builds it: posts of several kinds plus a kindless duplicate.
    /// Grouping them straight into a dictionary is what threw.
    /// </summary>
    [Fact]
    public void Counting_posts_by_kind_survives_a_post_with_no_kind()
    {
        SoMeTemplateKind?[] kinds =
        [
            SoMeTemplateKind.Sponsor,
            SoMeTemplateKind.Session,
            SoMeTemplateKind.Sponsor,
            null,   // the duplicate — §1050 gives it no kind on purpose
        ];

        // The shape the editor used, reproduced so the regression is visible rather than described.
        Assert.Throws<ArgumentNullException>(() =>
            kinds.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count()));

        // The shape it uses now.
        var counts = kinds
            .Where(k => k is not null)
            .GroupBy(k => k)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(2, counts[SoMeTemplateKind.Sponsor]);
        Assert.Equal(1, counts[SoMeTemplateKind.Session]);
        // 🔒 The kindless post is COUNTED NOWHERE, which is correct: the type picker offers the five
        // real kinds and never asks for null, so a null bucket had no reader in the first place.
        Assert.Equal(2, counts.Count);
    }

    /// <summary>The guard is present in the page model, where the 500 came from.</summary>
    [Fact]
    public void The_editor_drops_kindless_posts_before_counting()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "CommunityHub", "Pages", "Organizer", "SoMePostEditor.cshtml.cs"));

        var counting = source.IndexOf("CountByKind = all", StringComparison.Ordinal);
        Assert.True(counting > 0, "The kind count must still be built in LoadAsync.");

        var window = source[counting..(counting + 260)];
        Assert.Contains("TemplateKind is not null", window, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir ?? throw new InvalidOperationException("Repo root not found from the test binary.");
    }
}
