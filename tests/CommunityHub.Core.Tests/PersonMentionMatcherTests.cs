using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §858.16 — speaker→mention resolution. Every case here is a rule MEASURED against the live
/// LinkedIn API on 2026-08-06, and most of them are traps that already cost a wrong answer.
/// </summary>
public sealed class PersonMentionMatcherTests
{
    // ---------- keyword construction (§858.16e / §858.16h) ----------

    /// <summary>
    /// The defect that made me report two followers as non-followers: `keywords` accepts at most
    /// ONE space, so a three-part name 400s. We must send a single token.
    /// </summary>
    [Theory]
    [InlineData("Jan Vidar", "Elven", "Elven")]
    [InlineData("Morten Leth", "Hedegaard", "Hedegaard")]
    [InlineData("Morten", "Waltorp Knudsen", "Knudsen")]
    public void The_keyword_is_a_single_token_because_more_than_one_space_is_rejected(
        string first, string last, string expected)
    {
        var keyword = PersonMentionMatcher.BuildKeyword(first, last);

        Assert.Equal(expected, keyword);
        Assert.DoesNotContain(' ', keyword!);
    }

    /// <summary>
    /// Danish letters 400 the call. Strip the diacritic — do NOT expand it: measured live,
    /// "Norregaard" matched Nørregaard while the orthographically-correct "Noerregaard" matched nobody.
    /// </summary>
    [Theory]
    [InlineData("Nørregaard", "Norregaard")]
    [InlineData("Bøtkjær", "Botkjar")]
    [InlineData("Jörgen", "Jorgen")]
    [InlineData("Søborg", "Soborg")]
    public void Danish_and_nordic_letters_are_folded_to_the_sendable_alphabet(string input, string expected)
        => Assert.Equal(expected, PersonMentionMatcher.Fold(input));

    /// <summary>Real speakers carry decorations on LinkedIn; none of them are sendable.</summary>
    [Fact]
    public void Decorations_and_disallowed_characters_are_stripped()
    {
        Assert.Equal("Thomas Marcussen", PersonMentionMatcher.Fold("Thomas Marcussen [MVP] 🇩🇰"));
        // A trailing credential survives as an extra token — harmless, because matching asks whether
        // our surname is AMONG their name parts, not whether the whole string is equal.
        Assert.Equal("Dorthe K. Thidemann MBA", PersonMentionMatcher.Fold("Dorthe K. Thidemann, MBA"));
        // Apostrophes and hyphens ARE allowed by LinkedIn and must survive.
        Assert.Equal("O'Brien-Smith", PersonMentionMatcher.Fold("O'Brien-Smith"));
    }

    [Fact]
    public void A_name_with_nothing_searchable_yields_no_keyword()
        => Assert.Null(PersonMentionMatcher.BuildKeyword("", "🇩🇰"));

    // ---------- candidate selection (§858.16f) ----------

    /// <summary>
    /// The live case: our data says "Morten Knudsen", LinkedIn says "Morten Waltorp Knudsen [MVP]".
    /// A strict equality match would MISS him — and he is the person the whole feature was proven on.
    /// </summary>
    [Fact]
    public void A_middle_name_and_a_suffix_on_LinkedIn_still_match_our_stored_name()
    {
        var candidates = new[]
        {
            new TypeaheadCandidate("urn:li:person:c3hh-hQmZ3", "Morten", "Waltorp Knudsen [MVP]"),
        };

        var result = PersonMentionMatcher.SelectMatch(candidates, "Morten", "Knudsen");

        Assert.True(result.IsResolved);
        Assert.Equal("urn:li:person:c3hh-hQmZ3", result.Urn);
    }

    /// <summary>
    /// `keywords=Kasper` returned 7 different Kaspers live. Taking elements[0] would mention a
    /// stranger from the company page — the worst possible failure. Ambiguity must be reported.
    /// </summary>
    [Fact]
    public void Several_matching_followers_are_ambiguous_and_never_guessed()
    {
        var candidates = new[]
        {
            new TypeaheadCandidate("urn:li:person:AAA", "Peter", "Schmidt"),
            new TypeaheadCandidate("urn:li:person:BBB", "Peter", "Schmidt"),
        };

        var result = PersonMentionMatcher.SelectMatch(candidates, "Peter", "Schmidt");

        Assert.False(result.IsResolved);
        Assert.Equal(MentionResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.Urn);
    }

    /// <summary>
    /// Followers sharing a first name must NOT match. Live, "Morten" returned nine different
    /// Mortens and none of them was Morten Bøtkjær Nilsen.
    /// </summary>
    [Fact]
    public void A_shared_first_name_alone_does_not_make_a_match()
    {
        var candidates = new[]
        {
            new TypeaheadCandidate("urn:li:person:AAA", "Morten", "Banke"),
            new TypeaheadCandidate("urn:li:person:BBB", "Morten", "Dalsgaard"),
        };

        var result = PersonMentionMatcher.SelectMatch(candidates, "Morten", "Nilsen");

        Assert.False(result.IsResolved);
        Assert.Equal(MentionResolutionStatus.NotAFollower, result.Status);
    }

    [Fact]
    public void An_empty_candidate_list_reports_not_a_follower_in_plain_words()
    {
        var result = PersonMentionMatcher.SelectMatch(
            Array.Empty<TypeaheadCandidate>(), "Nikki", "Chapple");

        Assert.Equal(MentionResolutionStatus.NotAFollower, result.Status);
        Assert.Contains("follow", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- slug tie-break (§858.17) ----------

    /// <summary>
    /// The slug is the only thing that separates two identically-named followers, so it must be read
    /// from every shape of URL the roster actually contains — country hosts, missing scheme, trailing
    /// slash, and percent-encoded Danish characters.
    /// </summary>
    [Theory]
    [InlineData("https://www.linkedin.com/in/petsch/", "petsch")]
    [InlineData("https://linkedin.com/in/janvidarelven", "janvidarelven")]
    [InlineData("https://se.linkedin.com/in/ccmexec", "ccmexec")]
    [InlineData("linkedin.com/in/merill", "merill")]
    [InlineData("https://www.linkedin.com/in/PetSch/", "petsch")]
    [InlineData("https://www.linkedin.com/in/morten-b%c3%b8tkj%c3%a6r", "morten-bøtkjær")]
    [InlineData("https://www.linkedin.com/company/expertslivedk/", null)]  // a COMPANY, not a person
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_person_slug_is_read_from_any_shape_of_profile_url(string? url, string? expected)
        => Assert.Equal(expected, PersonMentionMatcher.TryReadPersonSlug(url));

    /// <summary>
    /// Measured live: TWO followers are called exactly "Peter Schmidt" and both follow the page.
    /// The matcher must hand back both rather than choose — the choosing is done by slug, which
    /// needs a second call, so this method's job is to refuse and expose the candidates.
    /// </summary>
    [Fact]
    public void Matching_exposes_every_plausible_candidate_so_a_caller_can_disambiguate()
    {
        var candidates = new[]
        {
            new TypeaheadCandidate("urn:li:person:7ruvU3A_Oc", "Peter", "Schmidt"),
            new TypeaheadCandidate("urn:li:person:_7EnXKquV7", "Peter", "Schmidt"),
            new TypeaheadCandidate("urn:li:person:g7icXKPJU9", "Peter", "Schmitz"),   // NOT a match
            new TypeaheadCandidate("urn:li:person:sKN-Quu6Gt", "John", "Schmidt"),    // NOT a match
        };

        var matching = PersonMentionMatcher.Matching(candidates, "Peter", "Schmidt");

        Assert.Equal(2, matching.Count);
        Assert.All(matching, m => Assert.Equal("Peter", m.FirstName));
        // …and SelectMatch alone still refuses, because a name cannot separate these two.
        Assert.Equal(MentionResolutionStatus.Ambiguous,
            PersonMentionMatcher.SelectMatch(candidates, "Peter", "Schmidt").Status);
    }

    // ---------- rendering (§858.13d) ----------

    /// <summary>
    /// The rendered mention must be exactly the span EscapeCommentaryPreservingMentions keeps
    /// verbatim — otherwise it posts as visible markup, which is what happened live.
    /// </summary>
    [Fact]
    public void A_rendered_mention_survives_the_commentary_escaper_intact()
    {
        var mention = PersonMentionMatcher.RenderMention("Morten Knudsen", "urn:li:person:c3hh-hQmZ3");
        Assert.Equal("@[Morten Knudsen](urn:li:person:c3hh-hQmZ3)", mention);

        var escaped = LiveLinkedInPostPublisher.EscapeCommentaryPreservingMentions(
            $"Meet {mention} at ELDK27 (room 300)");

        Assert.Contains(mention, escaped);          // the mention is untouched…
        Assert.Contains(@"\(room 300\)", escaped);  // …and §326k still protects the prose
    }

    /// <summary>Brackets in a display name would terminate the span early and leak markup.</summary>
    [Fact]
    public void Brackets_in_a_display_name_cannot_break_the_mention_span()
    {
        var mention = PersonMentionMatcher.RenderMention("Thomas Marcussen [MVP]", "urn:li:person:XYZ");

        Assert.Equal("@[Thomas Marcussen](urn:li:person:XYZ)", mention);
        Assert.Contains(mention,
            LiveLinkedInPostPublisher.EscapeCommentaryPreservingMentions(mention));
    }
}
