using System.Text.Json;
using CommunityHub.Core.Assistant;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Operator 2026-11-08: "eldk open ai is old 41, i propose that you update to a newer version, while keeping payg
/// still" — one request shaper so every AI caller works on both gpt-4.1 and the gpt-5 (reasoning) family.
/// </summary>
public sealed class OpenAiChatContentTests
{
    private static readonly object Payload = new
    {
        max_tokens = 60,
        temperature = 0.0,
        response_format = new { type = "json_object" },
        messages = new object[] { new { role = "user", content = "hi" } },
    };

    [Fact]
    public void A_gpt4_host_sends_the_body_unchanged()
    {
        var node = OpenAiChatContent.Shape(Payload, new OpenAiOptions { ReasoningModel = false });

        Assert.Equal(60, node["max_tokens"]!.GetValue<int>());
        Assert.Equal(0.0, node["temperature"]!.GetValue<double>());
        Assert.False(node.ContainsKey("max_completion_tokens"));
        Assert.False(node.ContainsKey("reasoning_effort"));
    }

    [Fact]
    public void A_reasoning_host_gets_completion_tokens_with_headroom_no_temperature_and_an_effort()
    {
        var node = OpenAiChatContent.Shape(Payload,
            new OpenAiOptions { ReasoningModel = true, ReasoningEffort = "low", ReasoningTokenHeadroom = 4000 });

        Assert.False(node.ContainsKey("max_tokens"));
        Assert.False(node.ContainsKey("temperature"));
        Assert.Equal(4060, node["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal("low", node["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("json_object", node["response_format"]!["type"]!.GetValue<string>());   // the rest is kept
    }

    [Fact]
    public void Snake_case_serializer_options_are_respected()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var node = OpenAiChatContent.Shape(new { max_tokens = 10, temperature = 0.2, messages = Array.Empty<object>() },
            new OpenAiOptions { ReasoningModel = true, ReasoningTokenHeadroom = 0 }, opts);

        Assert.Equal(10, node["max_completion_tokens"]!.GetValue<int>());
    }
}
