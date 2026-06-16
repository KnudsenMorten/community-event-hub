using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// The real, gated LLM implementation of <see cref="ITaskGuidanceGenerator"/>.
/// Calls the Claude Messages API (<c>POST /v1/messages</c>) with model
/// <c>claude-opus-4-8</c> over a plain <see cref="HttpClient"/> — the same
/// no-SDK-dependency approach the rest of the hub uses for integrations.
///
/// GATED: this type is only registered/active when <see cref="TaskGuidanceOptions.IsConfigured"/>
/// (a real API key was supplied via the secret mechanism). It NEVER hard-fails the
/// caller: on any error (no key, network, bad response, parse failure) it logs and
/// falls back to the injected <see cref="HeuristicTaskGuidanceGenerator"/>, so an
/// import or a "regenerate" click degrades gracefully instead of throwing.
/// </summary>
public sealed class LlmTaskGuidanceGenerator : ITaskGuidanceGenerator
{
    private readonly HttpClient _http;
    private readonly TaskGuidanceOptions _options;
    private readonly HeuristicTaskGuidanceGenerator _fallback;
    private readonly ILogger<LlmTaskGuidanceGenerator>? _log;

    public LlmTaskGuidanceGenerator(
        HttpClient http,
        TaskGuidanceOptions options,
        HeuristicTaskGuidanceGenerator fallback,
        ILogger<LlmTaskGuidanceGenerator>? log = null)
    {
        _http = http;
        _options = options;
        _fallback = fallback;
        _log = log;
    }

    public bool IsAiBacked => _options.IsConfigured;

    public async Task<TaskGuidance> GenerateAsync(
        string taskTitle,
        string? bucketName = null,
        string? responsibleTeam = null,
        CancellationToken ct = default)
    {
        var title = (taskTitle ?? string.Empty).Trim();
        if (title.Length == 0) return TaskGuidance.Empty;

        // Gate: no key ⇒ never call out, just use the heuristic.
        if (!_options.IsConfigured)
            return await _fallback.GenerateAsync(title, bucketName, responsibleTeam, ct);

        try
        {
            var guidance = await CallClaudeAsync(title, bucketName, responsibleTeam, ct);
            // If the model returned nothing useful for a field, backfill from the heuristic.
            if (string.IsNullOrWhiteSpace(guidance.Prerequisites) ||
                string.IsNullOrWhiteSpace(guidance.Expectations))
            {
                var h = await _fallback.GenerateAsync(title, bucketName, responsibleTeam, ct);
                guidance = new TaskGuidance(
                    string.IsNullOrWhiteSpace(guidance.Prerequisites) ? h.Prerequisites : guidance.Prerequisites,
                    string.IsNullOrWhiteSpace(guidance.Expectations) ? h.Expectations : guidance.Expectations);
            }
            return guidance;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "AI task-guidance generation failed for {Title}; using heuristic fallback.", title);
            return await _fallback.GenerateAsync(title, bucketName, responsibleTeam, ct);
        }
    }

    private async Task<TaskGuidance> CallClaudeAsync(
        string title, string? bucketName, string? team, CancellationToken ct)
    {
        var context = string.Join(" ", new[]
            {
                string.IsNullOrWhiteSpace(bucketName) ? null : $"Bucket: {bucketName}.",
                string.IsNullOrWhiteSpace(team) ? null : $"Responsible team: {team}.",
            }.Where(s => s is not null));

        // Structured-output request: ask for strict JSON with the two fields.
        var prompt =
            $"You help organize volunteers for a community tech conference. " +
            $"For the volunteer task titled \"{title}\"{(context.Length > 0 ? $" ({context})" : "")}, " +
            "write a short Pre-requisite (what must be true/ready before the task can start) " +
            "and a short Expectation (what 'done' looks like). " +
            "Each must be 1-2 plain sentences, practical and specific. " +
            "Respond ONLY with JSON of the form " +
            "{\"prerequisites\":\"...\",\"expectations\":\"...\"}.";

        var request = new MessagesRequest
        {
            Model = _options.Model,
            MaxTokens = _options.MaxTokens,
            Messages = new[] { new RequestMessage { Role = "user", Content = prompt } },
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(request, options: JsonOpts),
        };
        // Secret header — value comes from config/secret store, never logged.
        msg.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        msg.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        using var resp = await _http.SendAsync(msg, ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<MessagesResponse>(JsonOpts, ct);
        var text = body?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
        if (string.IsNullOrWhiteSpace(text)) return TaskGuidance.Empty;

        // The model is asked for raw JSON; tolerate stray prose by extracting the object.
        var json = ExtractJsonObject(text);
        if (json is null) return TaskGuidance.Empty;

        var parsed = JsonSerializer.Deserialize<GuidanceJson>(json, JsonOpts);
        return new TaskGuidance(
            (parsed?.Prerequisites ?? string.Empty).Trim(),
            (parsed?.Expectations ?? string.Empty).Trim());
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return (start >= 0 && end > start) ? text.Substring(start, end - start + 1) : null;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // --- Minimal Messages API DTOs (no SDK dependency) ----------------------
    private sealed class MessagesRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("messages")] public RequestMessage[] Messages { get; set; } = Array.Empty<RequestMessage>();
    }

    private sealed class RequestMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }

    private sealed class MessagesResponse
    {
        [JsonPropertyName("content")] public ContentBlock[]? Content { get; set; }
    }

    private sealed class ContentBlock
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }

    private sealed class GuidanceJson
    {
        [JsonPropertyName("prerequisites")] public string? Prerequisites { get; set; }
        [JsonPropertyName("expectations")] public string? Expectations { get; set; }
    }
}
