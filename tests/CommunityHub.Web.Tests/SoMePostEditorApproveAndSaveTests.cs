using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §887 (operator 2026-08-06, two screenshots) — two defects reported on the same post, #495.
///
/// <para><b>1. "i changed date/time but no button to save schedule change".</b> The date input sits
/// ~170 lines below the only submit button in its form, and that button reads "Save post text" — so
/// a changed date looked unsaveable. This is the §863.4 lesson again: a control whose Save is out of
/// sight is a change that never gets submitted, and §859.6 already lost a real edit that way.</para>
///
/// <para><b>2. "i have no approve buton anymore"</b> and <b>"this is wrong as it is now scheduled
/// (approved)"</b>. Approval is <c>IsActive</c>, whose only control lived on a DIFFERENT page, while
/// the editor's button changed <c>PlanState</c> instead. So accepting a slot left the post reading
/// "PLANNED (not approved)" with nothing on the page able to change it. §872 separated the two axes
/// deliberately and the distinction is real — but he makes ONE decision, so accepting now approves.
/// </para>
/// </summary>
public sealed class SoMePostEditorApproveAndSaveTests
{
    private static string Editor() => ReadPage(Path.Combine("Organizer", "SoMePostEditor.cshtml"));

    [Fact]
    public void The_date_field_has_its_own_save_button()
    {
        var page = Editor();

        Assert.Contains("Save date &amp; time", page, StringComparison.Ordinal);

        // It must be a SUBMIT in the same form as the input — a link or a second form would not
        // carry the changed value.
        var dateLabel = page.IndexOf("Posting date &amp; time", StringComparison.Ordinal);
        var save = page.IndexOf("Save date &amp; time", StringComparison.Ordinal);
        Assert.True(dateLabel > 0 && save > dateLabel,
            "The save button must sit AFTER the date field, where the change is made.");
        Assert.Contains("type=\"submit\"", page[dateLabel..save], StringComparison.Ordinal);
    }

    /// <summary>
    /// The label must say it approves, because the whole defect was a button whose effect did not
    /// match its name — it locked the slot and left the post unapproved.
    /// </summary>
    [Fact]
    public void Accepting_a_slot_says_that_it_also_approves()
    {
        var page = Editor();

        Assert.Contains("Accept &amp; approve", page, StringComparison.Ordinal);
        Assert.Contains("approves it to publish", page, StringComparison.Ordinal);

        // The old wording promised only a slot lock.
        Assert.DoesNotContain("Accept this slot", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// §887.1 — the state he was actually stuck in: slot LOCKED, post NOT approved. Two branches
    /// only ever offered "Accept" or "Withdraw", so a post accepted under the old code showed
    /// withdrawal and no way to approve — *"hard for me to approve it when you removed the button"*.
    /// </summary>
    [Fact]
    public void A_locked_but_unapproved_post_is_offered_an_approve_button()
    {
        var page = Editor();

        Assert.Contains("Approve to publish", page, StringComparison.Ordinal);

        // It must be guarded on BOTH axes — locked AND not approved — or it would replace the
        // withdraw button on a post that is already approved.
        Assert.Contains("SoMePostPlanState.Scheduled\n                 && !post.IsActive",
            page.Replace("\r\n", "\n"), StringComparison.Ordinal);

        // …and a published post must never be offered approval again.
        Assert.Contains("post.Status != CommunityHub.Core.Domain.SoMePostStatus.Published",
            page, StringComparison.Ordinal);
    }

    /// <summary>Withdrawing must be equally plain that it stops the post publishing.</summary>
    [Fact]
    public void Withdrawing_says_it_stops_the_post_publishing()
    {
        var page = Editor();

        Assert.Contains("Withdraw approval", page, StringComparison.Ordinal);
        Assert.Contains("stops it publishing", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔒 Accepting sets the publish gate, and the §850 readiness guard still refuses an unready
    /// post — locking a date is harmless, publishing a post whose sponsor owes their text is not.
    /// </summary>
    [Fact]
    public void The_handler_approves_on_accept_but_still_honours_the_readiness_guard()
    {
        var handler = ReadPage(Path.Combine("Organizer", "SoMePostEditor.cshtml.cs"));

        Assert.Contains("post.IsActive = true;", handler, StringComparison.Ordinal);
        Assert.Contains("BlockedReasonAsync", handler, StringComparison.Ordinal);
        // Handing back withdraws approval too, or a "proposal" could still publish.
        Assert.Contains("post.IsActive = false;", handler, StringComparison.Ordinal);
    }

    private static string ReadPage(string relative) =>
        File.ReadAllText(Path.Combine(FindDir("src", "CommunityHub", "Pages"), relative));

    private static string FindDir(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate {Path.Combine(parts)}.");
    }
}
