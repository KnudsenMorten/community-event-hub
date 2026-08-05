using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §863.4 (second half) — the post editor must TELL the operator when the post text has unsaved
/// changes, and must not let a navigation throw them away in silence.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-05: <i>"save button to edit text must be next to the post"</i>, paired in
/// §863.4 with an unsaved-changes indicator. The first half shipped; this half did not, and it is
/// the half that matches the actual incident.</para>
///
/// <para>🔴 <b>How an edit is really lost here.</b> <c>Next →</c> is an ordinary link and he walks 84
/// posts with it. A GET discards whatever is in the textarea without a word, so an edit he believed
/// he had made simply is not there. §859.6 lost exactly one that way — a post published carrying a
/// paragraph he had already deleted — and §859.7 proved the save had never been SUBMITTED rather
/// than having failed. No server-side test can see this: the page returns 200 and the model is
/// perfectly happy. So it is pinned in the markup.</para>
///
/// <para>🔒 The Save form is the ONE navigation that must not warn — it is the fix, not the risk.</para>
/// </remarks>
public sealed class SoMeEditorUnsavedGuardTests
{
    private static string Editor() => System.IO.File.ReadAllText(
        System.IO.Path.Combine(
            RepoPath("src", "CommunityHub", "Pages"), "Organizer", "SoMePostEditor.cshtml"));

    [Fact]
    public void The_editor_warns_before_a_navigation_discards_unsaved_post_text()
    {
        var page = Editor();

        // The indicator itself — he must be able to SEE that something is unsaved.
        Assert.Contains("unsaved-flag", page, StringComparison.Ordinal);
        Assert.Contains("Unsaved changes", page, StringComparison.Ordinal);

        // …and the guard that catches the walk buttons, which are plain links.
        Assert.Contains("beforeunload", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_is_exempt_from_the_warning()
    {
        var page = Editor();

        // The Save form must be identifiable and must clear the dirty flag on submit, or the fix
        // becomes a confirm dialog on the very action it exists to encourage.
        Assert.Contains("id=\"save-post-form\"", page, StringComparison.Ordinal);
        Assert.Contains("save-post-form", page, StringComparison.Ordinal);
        Assert.Contains("dirty = false", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔒 §863.4's first half, pinned so it cannot regress: Save sits WITH the textarea, not in a
    /// separate action block. An edit whose Save is off-screen is an edit that never gets submitted.
    /// </summary>
    [Fact]
    public void Save_sits_immediately_after_the_post_text_box()
    {
        var page = Editor();

        var textarea = page.IndexOf("id=\"text\"", StringComparison.Ordinal);
        var save = page.IndexOf("Save post text", StringComparison.Ordinal);

        Assert.True(textarea > 0, "the post-text textarea is gone");
        Assert.True(save > textarea, "Save must come after the textarea it belongs to");

        // "Immediately" means within the same block — not further down the page past the preview,
        // the graphic picker and the variable list.
        var between = page[textarea..save];
        Assert.False(between.Contains("<h2>", StringComparison.Ordinal),
            "another section heading sits between the post text and its Save button");
    }

    private static string RepoPath(params string[] parts)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, System.IO.Path.Combine(parts));
            if (System.IO.Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new System.IO.DirectoryNotFoundException(
            $"Could not locate {System.IO.Path.Combine(parts)} from {AppContext.BaseDirectory}");
    }
}
