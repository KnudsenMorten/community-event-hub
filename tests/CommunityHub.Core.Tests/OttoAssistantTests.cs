using System.Net;
using System.Text;
using System.Text.Json;
using CommunityHub.Core.Assistant;
using CommunityHub.Core.Content;
using CommunityHub.Core.Domain;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Otto (REQUIREMENTS §129) — the grounded AI Community Helper. These offline tests
/// prove the security-critical behaviour with NO network:
///   • AUTHORIZATION AT RETRIEVAL — a volunteer's grounding excludes speaker/organizer
///     content because the builder only ever asks for ContentPageRegistry.ForRole(role)
///     slugs; a speaker's grounding DOES include speaker content.
///   • the context is built from the SERVER-supplied role + participant id, and own
///     data is requested for THAT participant id only (no client-supplied id).
///   • disabled / unconfigured ⇒ the assistant no-ops (no HTTP) and reports unavailable.
///   • a configured assistant calls Azure OpenAI with the system prompt + grounding and
///     returns the model's answer (HTTP mocked).
/// </summary>
public sealed class OttoAssistantTests
{
    // A content provider that would happily return text for EVERY registered slug —
    // so if the builder leaked a speaker-only slug to a volunteer, the test would see it.
    private sealed class AllSlugsContentProvider : IOttoContentProvider
    {
        public readonly List<string> Requested = new();
        public string? GetContentMarkdown(string slug)
        {
            Requested.Add(slug);
            return $"CONTENT[{slug}]";
        }
    }

    private sealed class FakeOwnDataProvider : IOttoOwnDataProvider
    {
        public int? SeenParticipantId;
        public ParticipantRole? SeenRole;
        public Task<IReadOnlyList<OttoGroundingSection>> GetOwnDataAsync(
            int eventId, int participantId, ParticipantRole role, CancellationToken ct = default)
        {
            SeenParticipantId = participantId;
            SeenRole = role;
            IReadOnlyList<OttoGroundingSection> rows =
                new[] { new OttoGroundingSection("Your tasks", $"OWN-DATA[{participantId}]") };
            return Task.FromResult(rows);
        }
    }

    // Organizer-only ops provider (§133). Records whether it was invoked so the tests can
    // prove the builder gate: it must be called for an organizer and NEVER for anyone else.
    private sealed class FakeOpsProvider : IOttoOrganizerOpsProvider
    {
        public int Calls;
        public Task<IReadOnlyList<OttoGroundingSection>> GetOpsAggregatesAsync(
            int eventId, CancellationToken ct = default)
        {
            Calls++;
            IReadOnlyList<OttoGroundingSection> rows =
                new[] { new OttoGroundingSection("Event ops overview (organizer)", "OPS-AGGREGATE") };
            return Task.FromResult(rows);
        }
    }

    private static readonly string SpeakerOnlySlug =
        ContentPageRegistry.All.First(p => p.Roles.Contains(ParticipantRole.Speaker)).Slug;
    private static readonly string AllRolesSlug =
        ContentPageRegistry.All.First(p => p.Roles.Count == 0).Slug;

    [Fact]
    public async Task Volunteer_grounding_excludes_speaker_content()
    {
        var content = new AllSlugsContentProvider();
        var own = new FakeOwnDataProvider();
        var builder = new OttoGroundingBuilder(content, own);

        var ctx = await builder.BuildAsync(eventId: 7, participantId: 42, role: ParticipantRole.Volunteer);

        var text = ctx.ToGroundingText();
        // The builder never even ASKED for the speaker-only slug.
        Assert.DoesNotContain(SpeakerOnlySlug, content.Requested);
        Assert.DoesNotContain($"CONTENT[{SpeakerOnlySlug}]", text);
        // It DID include an all-roles page + the participant's own data.
        Assert.Contains($"CONTENT[{AllRolesSlug}]", text);
        Assert.Contains("OWN-DATA[42]", text);
    }

    [Fact]
    public async Task Speaker_grounding_includes_speaker_content()
    {
        var content = new AllSlugsContentProvider();
        var builder = new OttoGroundingBuilder(content, new FakeOwnDataProvider());

        var ctx = await builder.BuildAsync(eventId: 7, participantId: 1, role: ParticipantRole.Speaker);

        Assert.Contains(SpeakerOnlySlug, content.Requested);
        Assert.Contains($"CONTENT[{SpeakerOnlySlug}]", ctx.ToGroundingText());
    }

    [Fact]
    public async Task Grounding_requests_own_data_for_the_server_participant_id_only()
    {
        var own = new FakeOwnDataProvider();
        var builder = new OttoGroundingBuilder(new AllSlugsContentProvider(), own);

        await builder.BuildAsync(eventId: 7, participantId: 99, role: ParticipantRole.Volunteer);

        Assert.Equal(99, own.SeenParticipantId);
        Assert.Equal(ParticipantRole.Volunteer, own.SeenRole);
    }

    // --- §133 ORGANIZER OPS MODE: ops aggregates are role-gated in the BUILDER ---------

    [Fact]
    public async Task Organizer_grounding_includes_ops_aggregates()
    {
        var ops = new FakeOpsProvider();
        var builder = new OttoGroundingBuilder(
            new AllSlugsContentProvider(), new FakeOwnDataProvider(), ops);

        var ctx = await builder.BuildAsync(eventId: 7, participantId: 1, role: ParticipantRole.Organizer);

        Assert.Equal(1, ops.Calls);
        Assert.Contains("OPS-AGGREGATE", ctx.ToGroundingText());
    }

    [Theory]
    [InlineData(ParticipantRole.Volunteer)]
    [InlineData(ParticipantRole.Speaker)]
    [InlineData(ParticipantRole.Sponsor)]
    [InlineData(ParticipantRole.Attendee)]
    [InlineData(ParticipantRole.Media)]
    [InlineData(ParticipantRole.EventPartner)]
    public async Task NonOrganizer_grounding_never_calls_ops_provider_and_excludes_aggregates(
        ParticipantRole role)
    {
        var ops = new FakeOpsProvider();
        var builder = new OttoGroundingBuilder(
            new AllSlugsContentProvider(), new FakeOwnDataProvider(), ops);

        var ctx = await builder.BuildAsync(eventId: 7, participantId: 1, role: role);

        // The gate is in the builder: a non-organizer never even invokes the ops provider…
        Assert.Equal(0, ops.Calls);
        // …so no ops/aggregate grounding can leak into a non-organizer's context.
        Assert.DoesNotContain("OPS-AGGREGATE", ctx.ToGroundingText());
    }

    [Fact]
    public async Task Builder_without_ops_provider_still_works_for_organizer()
    {
        // Backward-compatible: the ops provider is optional (null when not wired).
        var builder = new OttoGroundingBuilder(
            new AllSlugsContentProvider(), new FakeOwnDataProvider());

        var ctx = await builder.BuildAsync(eventId: 7, participantId: 1, role: ParticipantRole.Organizer);

        Assert.DoesNotContain("OPS-AGGREGATE", ctx.ToGroundingText());
        Assert.True(ctx.HasContent); // content + own data still present
    }

    // --- The assistant itself (HTTP mocked) ---------------------------------

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public HttpRequestMessage? LastRequest;
        public string? LastRequestBody;
        public int Calls;

        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static OpenAiOptions LiveOptions() => new()
    {
        Enabled = true,
        Endpoint = "https://unit-test.openai.azure.com",
        Deployment = "gpt-test",
        ApiKey = "secret-key",
        ApiVersion = "2025-01-01-preview",
    };

    private static string ChatJson(string answer) =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = answer } } },
        });

    [Fact]
    public async Task Disabled_config_is_a_noop_no_http_call()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ChatJson("should not be called"));
        using var http = new HttpClient(handler);
        var otto = new OttoAssistant(http, new OpenAiOptions { Enabled = false });

        Assert.False(otto.Available);
        var ans = await otto.AskAsync("hi", OttoContext.Empty(ParticipantRole.Volunteer, 1));

        Assert.False(ans.Available);
        Assert.Equal(0, handler.Calls); // never called the network
    }

    [Fact]
    public async Task Configured_assistant_calls_azure_openai_with_grounding_and_returns_answer()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ChatJson("Your first task is due Friday."));
        using var http = new HttpClient(handler);
        var otto = new OttoAssistant(http, LiveOptions());

        var ctx = new OttoContext(ParticipantRole.Speaker, 5, new[]
        {
            new OttoGroundingSection("Your tasks", "GROUNDED-TASK-TEXT"),
        });

        Assert.True(otto.Available);
        var ans = await otto.AskAsync("When is my task due?", ctx);

        Assert.True(ans.Available);
        Assert.Equal("Your first task is due Friday.", ans.Text);
        Assert.Equal(1, handler.Calls);
        // Correct Azure OpenAI route + secret header.
        Assert.Contains("/openai/deployments/gpt-test/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("api-version=2025-01-01-preview", handler.LastRequest!.RequestUri!.ToString());
        Assert.True(handler.LastRequest!.Headers.Contains("api-key"));
        // The system message carries the guardrail + the grounding.
        Assert.Contains(OttoAssistant.SystemPrompt, handler.LastRequestBody);
        Assert.Contains("GROUNDED-TASK-TEXT", handler.LastRequestBody);
    }

    [Fact]
    public async Task Backend_error_returns_friendly_unavailable_not_throw()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "boom");
        using var http = new HttpClient(handler);
        var otto = new OttoAssistant(http, LiveOptions());

        var ans = await otto.AskAsync("hi", OttoContext.Empty(ParticipantRole.Volunteer, 1));

        Assert.False(ans.Available);
        Assert.False(string.IsNullOrWhiteSpace(ans.Text)); // a friendly message, no exception
    }
}
