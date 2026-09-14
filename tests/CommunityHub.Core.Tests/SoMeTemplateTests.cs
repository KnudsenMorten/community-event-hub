using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §824.2C — the five post templates and the variable substitution behind them.
/// </summary>
/// <remarks>
/// The behaviour worth pinning is not "it replaces tokens" — it is the two ways a post can go wrong
/// in public: a typo'd placeholder printed verbatim to 400+ followers, and an ordinary empty value
/// leaving a hole that reads as a system failure. Those are opposite fixes, so they are tested apart.
/// </remarks>
public sealed class SoMeTemplateTests
{
    private static Dictionary<string, string?> Values(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(SoMeTemplateKind.SpeakerTracks)]
    [InlineData(SoMeTemplateKind.Session)]
    [InlineData(SoMeTemplateKind.SponsorCategory)]
    [InlineData(SoMeTemplateKind.Sponsor)]
    [InlineData(SoMeTemplateKind.EventPost)]
    public void Every_shipped_template_uses_only_variables_the_renderer_knows(SoMeTemplateKind kind)
    {
        // 🔒 The guard that matters most: a shipped template mentioning a token nothing resolves
        // publishes the literal "{TrackName}" to the company page. Catching that here is why
        // KnownVariables is a list rather than a comment.
        Assert.Empty(SoMeTemplateRenderer.UnknownPlaceholders(SoMeTemplateCatalog.DefaultBody(kind)));
    }

    [Theory]
    [InlineData(SoMeTemplateKind.SpeakerTracks)]
    [InlineData(SoMeTemplateKind.Session)]
    [InlineData(SoMeTemplateKind.SponsorCategory)]
    [InlineData(SoMeTemplateKind.Sponsor)]
    [InlineData(SoMeTemplateKind.EventPost)]
    public void Every_shipped_template_ends_with_the_tags_and_the_organizer_credit(SoMeTemplateKind kind)
    {
        // The house style, read off all three ELDK26 samples: tags, then "ELDKnn Organizers:".
        var body = SoMeTemplateCatalog.DefaultBody(kind);

        // 🔒 §834.6 — TYPE 5 IS THE ONE EXCEPTION, and it was proved on PROD rather than reasoned.
        // The imported deck is FINISHED COPY: all 27 of his event posts already end with the tag
        // block themselves, and 23 of 27 already carry the event link. Emitting {EventTags} here
        // printed the WHOLE BLOCK TWICE in a real composed post. The only thing his copy never
        // carries is the organizer credit (0 of 27), so that is all Type 5 adds.
        if (kind == SoMeTemplateKind.EventPost)
        {
            Assert.DoesNotContain("{EventTags}", body);
        }
        else
        {
            Assert.Contains("{EventTags}", body);
        }

        Assert.Contains("{EventNameShort} Organizers:", body);   // §933 — {EditionCode} retired
        Assert.EndsWith("{Organizers}", body);   // §935 — his spelling, same value
    }

    [Fact]
    public void A_track_post_renders_the_shape_of_his_ELDK26_sample()
    {
        var body = SoMeTemplateRenderer.Render(
            SoMeTemplateCatalog.DefaultBody(SoMeTemplateKind.SpeakerTracks),
            Values(
                ("TrackName", "AI"),
                ("IntroText", "📣 Calling all tech wizards!"),
                ("EventSystemUrl", "https://eldk27.expertslive.dk"),
                // §1224 — the track template tags: {Speakers} carries the mentions.
                ("Speakers", "Andreas Sobczyk | Sherry List"),
                ("EventTags", "#ELDK27 #ExpertsLiveDK"),
                ("EventNameShort", "ELDK27"),
                ("Organizers", "Organizer One | Organizer Two")));

        Assert.StartsWith("✨ Track Speakers: AI ✨", body);
        Assert.Contains("🤘 Meet our tech legends: Andreas Sobczyk | Sherry List", body);
        Assert.Contains("#ELDK27 #ExpertsLiveDK", body);
        // §935 — the credit is supplied under his spelling, {Organizers}. Same value as before.
        Assert.EndsWith("ELDK27 Organizers:\nOrganizer One | Organizer Two", body);
        Assert.DoesNotContain("{", body);
    }

    [Fact]
    public void An_unknown_placeholder_survives_into_the_output_where_someone_will_see_it()
    {
        // ⚠️ Deliberately NOT erased. A silently-dropped {SponsorNmae} produces a post that reads
        // fine to whoever wrote it and is missing the sponsor's name to everyone else.
        var body = SoMeTemplateRenderer.Render(
            "Hello {SponsorNmae} and {SponsorName}",
            Values(("SponsorName", "Truesec")));

        Assert.Equal("Hello {SponsorNmae} and Truesec", body);
    }

    [Fact]
    public void A_known_variable_with_no_value_empties_cleanly_instead_of_leaving_a_hole()
    {
        // A sponsor with no description is an ordinary state, not a fault. The post must close over
        // the gap rather than publish a three-line void that reads as a failed load.
        var body = SoMeTemplateRenderer.Render(
            "Top line\n\n{SponsorSocialMediaCompanyDescription}\n\nBottom line",
            Values(("SponsorSocialMediaCompanyDescription", null)));

        Assert.Equal("Top line\n\nBottom line", body);
    }

    [Fact]
    public void Trailing_spaces_left_by_an_emptied_variable_are_removed()
    {
        var body = SoMeTemplateRenderer.Render(
            "{SponsorHashtag} #ELDK27\nnext", Values(("SponsorHashtag", "")));

        Assert.Equal("#ELDK27\nnext", body);
    }

    [Fact]
    public void Emojis_and_non_ascii_names_pass_through_untouched()
    {
        // §824.2C requires emoji support, and the samples carry them mid-sentence plus names like
        // "Morten Bøtkjær Nilsen". Substitution must not normalise or re-encode either.
        var body = SoMeTemplateRenderer.Render(
            "✨ {SpeakerNames} 🛡️☁", Values(("SpeakerNames", "Morten Bøtkjær Nilsen | Femke Cornelissen ✨")));

        Assert.Equal("✨ Morten Bøtkjær Nilsen | Femke Cornelissen ✨ 🛡️☁", body);
    }

    [Fact]
    public void Substitution_is_case_insensitive_on_the_variable_name()
    {
        // He types these by hand in an editor; {eventtags} and {EventTags} must not behave differently.
        Assert.Equal("x", SoMeTemplateRenderer.Render("{eventtags}", Values(("EventTags", "x"))));
    }

    [Fact]
    public void A_substituted_value_containing_braces_is_not_re_scanned()
    {
        // A sponsor description that happens to contain "{" must not turn into a placeholder.
        var body = SoMeTemplateRenderer.Render(
            "{IntroText}", Values(("IntroText", "we love {curly} braces"), ("curly", "NO")));

        Assert.Equal("we love {curly} braces", body);
    }

    [Fact]
    public void The_five_types_are_numbered_the_way_he_numbers_them()
    {
        // "Type 1: SpeakerTracks … Type 5: Event posts" — so a conversation about "type 4" and a row
        // in the database mean the same thing with no translation in anyone's head.
        Assert.Equal(1, (int)SoMeTemplateKind.SpeakerTracks);
        Assert.Equal(2, (int)SoMeTemplateKind.Session);
        Assert.Equal(3, (int)SoMeTemplateKind.SponsorCategory);
        Assert.Equal(4, (int)SoMeTemplateKind.Sponsor);
        Assert.Equal(5, (int)SoMeTemplateKind.EventPost);
    }

    /// <summary>
    /// 🔴 §1224 — a session announcement TAGS its speakers. Operator 2026-09-14: <i>"the {speakers}
    /// were not included, so none of the speakers were tagged"</i> — two master class posts went out
    /// naming nobody, because this body had no speaker token.
    /// </summary>
    [Fact]
    public void A_session_announcement_tags_its_speakers_under_the_intro()
    {
        var body = SoMeTemplateCatalog.DefaultBody(SoMeTemplateKind.Session);

        // {Speakers} (mentions), not {SpeakerNames} (plain text) — tagging is the point.
        Assert.Contains("{Speakers}", body, StringComparison.Ordinal);
        Assert.True(body.IndexOf("{IntroText}", StringComparison.Ordinal)
                    < body.IndexOf(SoMeTemplateCatalog.SessionSpeakersLine, StringComparison.Ordinal));

        var rendered = SoMeTemplateRenderer.Render(body, Values(
            ("SessionTitle", "Identity Master Class"), ("IntroText", "A day on identity."),
            ("Speakers", "@[Alex Example](urn:li:person:1) | Sam Sample")));
        Assert.Contains("🎤 With @[Alex Example](urn:li:person:1) | Sam Sample", rendered, StringComparison.Ordinal);
    }

    /// <summary>🔴 §1224 — track posts tag their speakers too ("yes, tag speakers in track posts too").</summary>
    [Fact]
    public void A_track_post_tags_its_speakers_not_plain_names()
    {
        var body = SoMeTemplateCatalog.DefaultBody(SoMeTemplateKind.SpeakerTracks);
        Assert.Contains("{Speakers}", body, StringComparison.Ordinal);
        Assert.DoesNotContain("{SpeakerNames}", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔒 §1224 — the tag line is never published bare: {Speakers} is REQUIRED, so a session whose
    /// speakers cannot be resolved is held by the empty-variable gate instead of saying "With".
    /// </summary>
    [Fact]
    public void A_session_with_no_resolvable_speaker_is_held_not_published_with_a_bare_line()
    {
        Assert.DoesNotContain("Speakers", SoMeEmptyVariableGate.Optional);
        Assert.Contains("Speakers", SoMeEmptyVariableGate.MissingRequired(
            SoMeTemplateCatalog.SessionSpeakersLine, Values(("Speakers", null))));
    }
}
