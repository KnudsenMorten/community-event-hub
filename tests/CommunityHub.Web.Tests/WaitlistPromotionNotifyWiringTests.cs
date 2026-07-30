using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §387 — freeing a Master Class seat PROMOTES the next person on the waitlist, and every surface
/// that can free one must TELL them.
///
/// <para><b>The bug this exists to prevent, which shipped:</b>
/// <c>MasterClassSignupService.RemoveAsync</c> returns the promotion it caused, and the wizard's
/// give-up handler threw that return value away. The seat moved in the database — so the promotion
/// looked like it worked — but the person who received it was never e-mailed. Operator, testing it
/// live: <i>"i still did NOT get any email when i was moved up and got a seat from the
/// waitlist"</i>.</para>
///
/// <para>It was silent for the worst possible reason: every OTHER give-up surface sent the mail, so
/// the behaviour looked correct everywhere it was checked — and §370 had just made the wizard the
/// surface attendees actually cancel from, so the one place missing it became the only place used.
/// A discarded return value cannot be caught by a compiler warning, so it is pinned here.</para>
/// </summary>
public sealed class WaitlistPromotionNotifyWiringTests
{
    [Fact]
    public void Every_page_that_frees_a_seat_also_sends_the_promotion_mail()
    {
        var pages = FindPagesDir();
        Assert.True(pages is not null, "src/CommunityHub/Pages was not found — has it moved?");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(pages!, "*.cshtml.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            // Only the Master Class signup service's RemoveAsync frees a seat. Other RemoveAsync
            // methods (AttendeePlanService, speaker bulk-remove) are unrelated, so match the
            // MasterClass call shape: it always passes an event id, an attendee id and a session.
            if (!text.Contains("RemoveAsync(", StringComparison.Ordinal)) continue;
            if (!text.Contains("MasterClass", StringComparison.Ordinal)) continue;

            // A page that frees a seat must also send the promotion. The seat has already moved by
            // then — not sending is a silent loss, not a deferred one.
            if (!text.Contains("SendPromotionAsync", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These pages free a Master Class seat but never notify the promoted attendee — the seat "
            + "moves and nobody is told:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_wizard_give_up_passes_the_RELEASED_title_so_it_stays_ONE_email()
    {
        // Operator: "I dont need to have 2 emails (seat confirmed + cancelled old)". §386 puts the
        // released seat INTO the promotion mail, so the promoted attendee gets one message covering
        // both facts. That only works if the call site forwards ReleasedTitle.
        var wizard = File.ReadAllText(
            Path.Combine(FindPagesDir()!, "Forms", "Wizard.cshtml.cs"));

        Assert.Contains("SendPromotionAsync", wizard, StringComparison.Ordinal);
        Assert.Contains("promotion.ReleasedTitle", wizard, StringComparison.Ordinal);
    }

    private static string? FindPagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
