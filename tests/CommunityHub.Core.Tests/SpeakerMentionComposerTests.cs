using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §858.16c — composing the people half of a post. The point of these tests is the FALLBACK:
/// 6 of 22 real speakers cannot be mentioned, and the operator asked explicitly for their full
/// names so he can tag them by hand (2026-08-06). A silent fallback is the §854 defect.
/// </summary>
public sealed class SpeakerMentionComposerTests
{
    private static MentionOutcome Resolved(string first, string last, string urn) =>
        new(new MentionablePerson(first, last, $"https://www.linkedin.com/in/{first.ToLower()}/"),
            MentionResolution.Ok(urn, $"{first} {last}"));

    private static MentionOutcome Fallback(
        string first, string last, string? url,
        MentionResolutionStatus status = MentionResolutionStatus.NotAFollower) =>
        new(new MentionablePerson(first, last, url),
            new MentionResolution(null, status, "nobody with that name follows the page"));

    [Fact]
    public void A_mentioned_speaker_renders_as_a_mention_and_a_fallback_renders_as_a_full_name()
    {
        var mentioned = Resolved("Morten", "Knudsen", "urn:li:person:c3hh-hQmZ3");
        var fallback = Fallback("Nikki", "Chapple", "https://www.linkedin.com/in/nikkichapple/");

        Assert.Equal("@[Morten Knudsen](urn:li:person:c3hh-hQmZ3)", mentioned.Rendered);
        // 🔒 The FULL NAME, not a truncation and not an empty gap — a reader must still see who spoke.
        Assert.Equal("Nikki Chapple", fallback.Rendered);
    }

    [Fact]
    public void Names_join_readably_with_the_mention_markup_carried_through()
    {
        var text = SpeakerMentionComposer.JoinNames(new[]
        {
            "@[Morten Knudsen](urn:li:person:AAA)", "Nikki Chapple", "Merill Fernando",
        });

        Assert.Equal("@[Morten Knudsen](urn:li:person:AAA), Nikki Chapple and Merill Fernando", text);
    }

    /// <summary>
    /// The operator's actual requirement: he needs the full name AND where to find them, because a
    /// name alone will not identify the right person among LinkedIn's many duplicates.
    /// </summary>
    [Fact]
    public void The_fallback_report_names_every_unmentioned_person_and_where_to_tag_them()
    {
        var composition = new MentionComposition("…", new[]
        {
            Resolved("Morten", "Knudsen", "urn:li:person:AAA"),
            Fallback("Nikki", "Chapple", "https://www.linkedin.com/in/nikkichapple/"),
            Fallback("Merill", "Fernando", "https://www.linkedin.com/in/merill/"),
        });

        var report = composition.FallbackReport();

        Assert.NotNull(report);
        Assert.Contains("2 of 3", report);
        Assert.Contains("Nikki Chapple", report);
        Assert.Contains("https://www.linkedin.com/in/nikkichapple/", report);
        Assert.Contains("Merill Fernando", report);
        // The mentioned one is not on the worklist.
        Assert.DoesNotContain("Morten Knudsen", report);
    }

    [Fact]
    public void A_missing_profile_url_is_stated_rather_than_left_blank()
    {
        var composition = new MentionComposition("…", new[] { Fallback("Pim", "Jacobs", null) });

        Assert.Contains("no LinkedIn URL on file", composition.FallbackReport());
    }

    /// <summary>
    /// §858.16h, encoded: a lookup FAILURE must not read as a confirmed non-follower. Conflating
    /// the two is precisely how two followers were wrongly written off.
    /// </summary>
    [Fact]
    public void A_lookup_failure_is_flagged_separately_from_a_confirmed_non_follower()
    {
        var failed = new MentionComposition("…", new[]
        {
            Fallback("Jan Vidar", "Elven", "https://linkedin.com/in/janvidarelven",
                     MentionResolutionStatus.LookupFailed),
        });
        var genuine = new MentionComposition("…", new[]
        {
            Fallback("Nikki", "Chapple", "https://www.linkedin.com/in/nikkichapple/"),
        });

        Assert.True(failed.HadLookupFailures);
        Assert.Contains("FAILED TO LOOK UP", failed.FallbackReport());

        Assert.False(genuine.HadLookupFailures);
        Assert.DoesNotContain("FAILED TO LOOK UP", genuine.FallbackReport());
    }

    [Fact]
    public void Everyone_mentioned_means_no_report_to_chase()
    {
        var composition = new MentionComposition("…", new[]
        {
            Resolved("Morten", "Knudsen", "urn:li:person:AAA"),
        });

        Assert.Null(composition.FallbackReport());
        Assert.Empty(composition.Fallbacks);
    }
}
