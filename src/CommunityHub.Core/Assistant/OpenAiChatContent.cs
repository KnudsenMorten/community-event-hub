using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommunityHub.Core.Assistant;

/// <summary>
/// The ONE place a chat-completions body is shaped for the configured model (operator 2026-11-08: <i>"eldk open ai is
/// old 41, i propose that you update to a newer version, while keeping payg still"</i>).
/// </summary>
/// <remarks>
/// <para>Every caller writes its body the classic way (<c>max_tokens</c>, <c>temperature</c>). For a reasoning model
/// (<see cref="OpenAiOptions.ReasoningModel"/>, the gpt-5 family) this rewrites it into the form those models accept:
/// <c>max_tokens</c> → <c>max_completion_tokens</c> plus <see cref="OpenAiOptions.ReasoningTokenHeadroom"/> (the limit
/// covers the model's hidden reasoning as well as the answer, so a 60-token limit would leave an empty answer),
/// <c>temperature</c> removed (only the default is accepted), and <c>reasoning_effort</c> added.</para>
/// <para>🔑 The switch is a SETTING, not a code path per caller: moving a host between model generations is an app
/// setting change, and a caller added later cannot forget the conversion if it uses this.</para>
/// </remarks>
public static class OpenAiChatContent
{
    private static readonly JsonSerializerOptions DefaultOpts = new(JsonSerializerDefaults.Web);

    /// <summary>The request body for <paramref name="payload"/>, shaped for <paramref name="options"/>' model.</summary>
    public static HttpContent Create(object payload, OpenAiOptions options, JsonSerializerOptions? json = null)
    {
        var node = Shape(payload, options, json);
        return JsonContent.Create(node, options: json ?? DefaultOpts);
    }

    /// <summary>The shaped body as a JSON object — exposed for tests.</summary>
    public static JsonObject Shape(object payload, OpenAiOptions options, JsonSerializerOptions? json = null)
    {
        var node = JsonSerializer.SerializeToNode(payload, payload.GetType(), json ?? DefaultOpts) as JsonObject
                   ?? throw new ArgumentException("A chat payload must serialize to a JSON object.", nameof(payload));
        if (!options.ReasoningModel) return node;

        var max = node["max_tokens"]?.GetValue<int>();
        node.Remove("max_tokens");
        if (max is int m)
            node["max_completion_tokens"] = m + Math.Max(0, options.ReasoningTokenHeadroom);
        node.Remove("temperature");
        node.Remove("top_p");
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort) && !node.ContainsKey("reasoning_effort"))
            node["reasoning_effort"] = options.ReasoningEffort;
        return node;
    }
}
