using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1060(b)/(f) — the announcement notice's ledger keys and its follow-state copy.
/// </summary>
/// <remarks>
/// <para>These are the two decisions in the notifier that are pure and therefore pinnable without a
/// mail server: <b>what makes an occasion unique</b>, and <b>what we are willing to CLAIM about
/// someone's LinkedIn</b>. The recipient queries are exercised by the service tests alongside.</para>
/// </remarks>
public sealed class SoMeAnnouncementNotifierTests
{
    /// <summary>
    /// 🔴 The two sends must be different OCCASIONS, or the ledger collapses them and only the first
    /// ever goes out — a bug whose symptom is silence on the day-before mail, which nobody notices
    /// because the first mail arrived correctly.
    /// </summary>
    [Fact]
    public void The_two_stages_of_one_post_are_different_occasions()
    {
        var scheduled = SoMeAnnouncementNotifier.OccasionKeyFor(7, SoMeAnnouncementStage.Scheduled);
        var dayBefore = SoMeAnnouncementNotifier.OccasionKeyFor(7, SoMeAnnouncementStage.DayBefore);

        Assert.NotEqual(scheduled, dayBefore);
    }

    /// <summary>
    /// 🔒 …and two posts are different occasions too. ⚠️ The key is per POST, never per participant:
    /// <c>graphics-ready:{participantId}</c> was §664's defect, meaning "told once, ever", so a
    /// speaker with a second session could never be told about it.
    /// </summary>
    [Fact]
    public void Two_posts_never_share_an_occasion()
    {
        Assert.NotEqual(
            SoMeAnnouncementNotifier.OccasionKeyFor(7, SoMeAnnouncementStage.Scheduled),
            SoMeAnnouncementNotifier.OccasionKeyFor(8, SoMeAnnouncementStage.Scheduled));
    }

    /// <summary>🔒 Stable across runs — a key that varied by run would re-mail on every pass.</summary>
    [Fact]
    public void The_key_is_stable()
    {
        Assert.Equal(
            SoMeAnnouncementNotifier.OccasionKeyFor(7, SoMeAnnouncementStage.DayBefore),
            SoMeAnnouncementNotifier.OccasionKeyFor(7, SoMeAnnouncementStage.DayBefore));
    }

    // ---- §1060(f) — what we are willing to claim ---------------------------

    /// <summary>Only a RESOLVED lookup earns the thank-you, because only it proves they follow.</summary>
    [Fact]
    public void A_confirmed_follower_is_thanked_and_told_they_will_be_tagged()
    {
        var block = SoMeAnnouncementNotifier.FollowBlock("Resolved");

        Assert.Contains("tagged", block, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("thanks for following", block, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 🔴 THE CASE THE FIVE-STATE COLUMN EXISTS FOR. `NotAFollower` is a real negative; the other
    /// three mean the lookup did not answer. None of them may tell someone they do not follow us —
    /// a throttled sweep is not evidence (§858.16h). All four therefore get the same neutral
    /// INVITE, which is true regardless of the truth we could not establish.
    /// </summary>
    [Theory]
    [InlineData("NotAFollower")]
    [InlineData("Ambiguous")]
    [InlineData("KeywordUnusable")]
    [InlineData("LookupFailed")]
    [InlineData(null)]
    public void Anything_short_of_a_confirmed_follower_gets_the_invite_and_no_claim(string? status)
    {
        var block = SoMeAnnouncementNotifier.FollowBlock(status);

        Assert.Contains("optional", block, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("linkedin.com/company", block, StringComparison.OrdinalIgnoreCase);
        // 🔒 Never asserts a negative about them.
        Assert.DoesNotContain("you do not follow", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thanks for following", block, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ⚠️ An UNKNOWN status string — a new outcome added to the resolver later — must fall to the
    /// invite, not to the thank-you. Written because the default arm of a switch is where a future
    /// enum value silently lands, and landing on "thanks for following" would be a claim we never
    /// verified.
    /// </summary>
    [Fact]
    public void An_unrecognised_status_falls_to_the_invite_not_the_thank_you()
    {
        var block = SoMeAnnouncementNotifier.FollowBlock("SomeFutureOutcome");

        Assert.Contains("optional", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thanks for following", block, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 🔴 Operator 2026-10-01: the "Follow" link was broken — the follow card (speaker AND sponsor
    /// announcement pages) and this mail linked <c>/company/experts-live-denmark/</c>, which LinkedIn
    /// answers with 404. The invite must link the one shared constant, and no source file may carry
    /// the dead slug again.
    /// </summary>
    [Fact]
    public void The_follow_invite_links_the_real_company_page_and_the_dead_slug_is_gone()
    {
        Assert.Equal("https://www.linkedin.com/company/expertslivedk", EventLinkedInPage.Url);
        Assert.Contains($"href=\"{EventLinkedInPage.Url}\"", SoMeAnnouncementNotifier.FollowBlock(null),
            StringComparison.Ordinal);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var offenders = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.EndsWith("EventLinkedInPage.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("company/experts-live-denmark", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(offenders.Count == 0, "Dead LinkedIn slug still linked in: " + string.Join(", ", offenders));
    }
}
