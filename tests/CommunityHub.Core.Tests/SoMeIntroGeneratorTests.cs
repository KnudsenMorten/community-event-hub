using System.Net;
using CommunityHub.Core.Assistant;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §824.2D — the AI-written <c>{IntroText}</c>.
/// </summary>
/// <remarks>
/// <para>🔒 The property most of these hold: <b>every failure resolves to null</b>, and null is a
/// perfectly good outcome. Not configured, endpoint down, a reply that broke the house rules — the
/// post is composed without its opening line, the template closes the gap, and it still queues for
/// approval. A missing paragraph is a small loss; a scheduler that stops planning because an AI
/// endpoint had a bad minute is an absence nobody notices.</para>
///
/// <para>⚠️ The other half is the OUTPUT GUARD. The template already supplies the hashtags, the link
/// and the organizer credit, so a model that helpfully adds them produces a post with everything
/// twice — which reads as broken rather than as enthusiastic.</para>
/// </remarks>
public sealed class SoMeIntroGeneratorTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public string? LastRequestBody { get; private set; }
        public int Calls { get; private set; }

        public StubHandler(string content, HttpStatusCode status = HttpStatusCode.OK)
        {
            _status = status;
            var encoded = System.Text.Json.JsonSerializer.Serialize(content);
            _body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" + encoded + "}}]}";
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => throw new HttpRequestException("the endpoint is having a bad minute");
    }

    private static OpenAiOptions Configured() => new()
    {
        Enabled = true, Endpoint = "https://example.openai.azure.com",
        Deployment = "gpt", ApiKey = "k",
    };

    private static SoMeIntroGenerator Gen(HttpMessageHandler h, OpenAiOptions? o = null)
        => new(new HttpClient(h), o ?? Configured());

    private static readonly SoMeIntroRequest Session =
        new(SoMeTemplateKind.Session, "Persistence for fun & profit", "Real-world persistence techniques.");

    [Fact]
    public async Task A_good_reply_comes_back_as_the_intro()
    {
        var gen = Gen(new StubHandler("Join us to explore how attackers stay hidden 🔐"));

        Assert.Equal("Join us to explore how attackers stay hidden 🔐", await gen.GenerateAsync(Session));
    }

    [Fact]
    public async Task An_unconfigured_endpoint_is_a_no_op_not_an_error()
    {
        var handler = new StubHandler("should never be called");
        var gen = Gen(handler, new OpenAiOptions { Enabled = false });

        Assert.Null(await gen.GenerateAsync(Session));
        Assert.Equal(0, handler.Calls);   // and it does not even try
    }

    [Fact]
    public async Task An_endpoint_that_throws_yields_null_rather_than_taking_the_run_down()
    {
        // 🔒 The scheduler's job is to plan the campaign. An AI outage must not stop it.
        Assert.Null(await Gen(new ThrowingHandler()).GenerateAsync(Session));
    }

    [Fact]
    public async Task A_non_success_status_yields_null()
    {
        Assert.Null(await Gen(new StubHandler("x", HttpStatusCode.TooManyRequests)).GenerateAsync(Session));
    }

    [Theory]
    [InlineData("Join us! #ELDK27")]                             // would double the tag block
    [InlineData("Register at https://eldk27.expertslive.dk")]     // would double the link
    public async Task A_reply_that_would_duplicate_the_template_is_refused(string reply)
    {
        // ⚠️ REFUSED, not stripped. Removing the hashtag would leave a sentence written around
        // something that is no longer there; composing without an intro is the honest outcome, and
        // he sees the post in the queue either way.
        Assert.Null(await Gen(new StubHandler(reply)).GenerateAsync(Session));
    }

    [Fact]
    public async Task A_reply_that_ignored_the_length_rule_is_refused()
    {
        Assert.Null(await Gen(new StubHandler(new string('x', 900))).GenerateAsync(Session));
    }

    [Theory]
    [InlineData("\"Wrapped in quotes\"", "Wrapped in quotes")]
    [InlineData("  padded  ", "padded")]
    [InlineData("two\nlines", "two lines")]   // the templates supply their own blank line
    public async Task The_reply_is_tidied_into_one_clean_paragraph(string reply, string expected)
    {
        Assert.Equal(expected, await Gen(new StubHandler(reply)).GenerateAsync(Session));
    }

    [Fact]
    public async Task An_empty_reply_is_null_rather_than_an_empty_intro()
    {
        Assert.Null(await Gen(new StubHandler("   ")).GenerateAsync(Session));
    }

    [Fact]
    public async Task The_session_abstract_is_sent_as_context()
    {
        // His words: generate the intro "based on session title and session abstract".
        var handler = new StubHandler("ok");
        await Gen(handler).GenerateAsync(Session);

        Assert.Contains("Persistence for fun", handler.LastRequestBody);
        Assert.Contains("Real-world persistence techniques", handler.LastRequestBody);
    }

    [Fact]
    public void The_prompt_forbids_what_the_template_already_supplies()
    {
        // The prohibitions matter more than the instructions — this is what stops a post carrying
        // two tag blocks, two links and the organizer credit twice.
        foreach (var forbidden in new[] { "hashtags", "URLs", "organizer credit" })
        {
            Assert.Contains(forbidden, SoMeIntroGenerator.SystemPrompt);
        }
    }

    [Fact]
    public void The_prompt_names_what_the_post_is_about()
    {
        Assert.Contains("'AI' track", SoMeIntroGenerator.BuildUserPrompt(
            new SoMeIntroRequest(SoMeTemplateKind.SpeakerTracks, "AI", null)));
        Assert.Contains("a sponsor of the conference", SoMeIntroGenerator.BuildUserPrompt(
            new SoMeIntroRequest(SoMeTemplateKind.Sponsor, "Truesec", null)));
    }

    [Fact]
    public void A_very_long_abstract_is_truncated_before_it_is_sent()
    {
        // A 4000-character abstract costs tokens and adds nothing: the opening line is written from
        // the gist, and the full abstract is a click away.
        var prompt = SoMeIntroGenerator.BuildUserPrompt(
            new SoMeIntroRequest(SoMeTemplateKind.Session, "T", new string('a', 4000)));

        Assert.True(prompt.Length < 1500, $"prompt was {prompt.Length} chars");
    }
}
