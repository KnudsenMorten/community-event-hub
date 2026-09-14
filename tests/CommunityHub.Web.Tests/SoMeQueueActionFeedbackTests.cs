using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1193 — "WHEN I CLICK ACTIVATE NOTHING HAPPENS".
///
/// <para>Operator 2026-09-12, on post #320454 — six rows into a list of fifty-four. Something DID
/// happen: the approval gate refused it (*"the session description was reviewed and is not ready to
/// announce"*) and said so in the page-level message at the very TOP of the page, which was scrolled
/// off screen. He worked the real reason out from elsewhere, which is the tell — the button looked
/// broken.</para>
///
/// <para>🔑 <b>This is §887.2 in a second place.</b> That fixed the editor's date save for exactly
/// this reason — <i>"saving from down here looked like nothing happened at all"</i> — and the lesson
/// never travelled to the queue, where the distance between the control and its answer is larger
/// still. ⇒ <b>An outcome belongs where the control is, not where the page starts.</b></para>
/// </summary>
public sealed class SoMeQueueActionFeedbackTests
{
    private static string Queue() =>
        File.ReadAllText(Path.Combine(
            FindDir("src", "CommunityHub", "Pages"), "Organizer", "SoMeQueue.cshtml"));

    /// <summary>🔴 The outcome renders on the row that was acted on.</summary>
    [Fact]
    public void The_outcome_is_rendered_on_the_acted_row()
    {
        var page = Queue();

        Assert.Contains("Model.ActionedPostId == p.Id", page, StringComparison.Ordinal);
        // Inside the row loop, after the row itself — not only in the page header.
        var rowLoop = page.IndexOf("foreach (var p in Model.Posts)", StringComparison.Ordinal);
        var onRow = page.IndexOf("Model.ActionedPostId == p.Id", StringComparison.Ordinal);
        Assert.True(rowLoop > 0 && onRow > rowLoop,
            "The per-row outcome must be inside the row loop.");
    }

    /// <summary>
    /// 🔒 A REFUSAL reads as a refusal. The old page-level message was green regardless, so even
    /// when it was on screen a refusal looked like a success.
    /// </summary>
    [Fact]
    public void A_refusal_is_styled_and_announced_as_an_error()
    {
        var page = Queue();

        Assert.Contains("Not activated.", page, StringComparison.Ordinal);
        Assert.Contains("Model.ActionFailed ? \"alert\" : \"status\"", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ The POST returns to the top of the page, so the row — and the answer printed on it — is
    /// off screen without this. The scroll is what makes the fix visible at all.
    /// </summary>
    [Fact]
    public void The_page_returns_to_the_row_that_was_acted_on()
    {
        var page = Queue();

        Assert.Contains("getElementById('acted')", page, StringComparison.Ordinal);
        Assert.Contains("scrollIntoView", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔒 The page-level message stays. It is right for a page you are at the top of; the row copy
    /// is for the fifty-row list. Removing it would trade one blind spot for another.
    /// </summary>
    [Fact]
    public void The_page_level_message_is_kept_as_well()
    {
        var page = Queue();

        var header = page.IndexOf("@if (Model.Message is not null)", StringComparison.Ordinal);
        var rowLoop = page.IndexOf("foreach (var p in Model.Posts)", StringComparison.Ordinal);
        Assert.True(header > 0 && header < rowLoop,
            "The page-level message must still render above the list.");
    }

    private static string FindDir(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(Path.Combine(parts));
    }
}
