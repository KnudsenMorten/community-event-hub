using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Assistant;

/// <summary>
/// The Azure OpenAI implementation of <see cref="IAiHelperAssistant"/>. Calls the
/// chat-completions REST API over a plain <see cref="HttpClient"/> (no SDK — the same
/// no-extra-dependency approach the hub uses for every integration):
/// <c>POST {Endpoint}/openai/deployments/{Deployment}/chat/completions?api-version={ApiVersion}</c>
/// with the secret <c>api-key</c> header.
///
/// GROUNDING: the system prompt (built from the configurable display name via
/// <see cref="BuildSystemPrompt"/>) plus the already-authorized <see cref="AiHelperContext"/>
/// grounding are injected as the SYSTEM message; the user's question is the only USER
/// message. The model is given no raw DB/table access — just the assembled, role-scoped,
/// own-rows-only context.
///
/// GATED + SAFE: when <see cref="OpenAiOptions.IsConfigured"/> is false it never calls
/// out (returns a friendly "unavailable"); on any error (network / bad response /
/// parse) it logs and returns a friendly message instead of throwing to the page.
/// </summary>
public sealed class AiHelperAssistant : IAiHelperAssistant
{
    /// <summary>
    /// The role/scope guardrail injected ahead of the grounding (REQUIREMENTS §129),
    /// built from the configurable display <paramref name="assistantName"/>.
    /// </summary>
    public static string BuildSystemPrompt(string assistantName) =>
        $"You are {assistantName}, the friendly event assistant for Experts Live Denmark (ELDK27). " +
        "Answer ONLY from the provided context. If the answer isn't there, say you don't have that " +
        "info and suggest who to contact. Never reveal data about other people or roles.";

    private const string ErrorMessage =
        "Sorry, I'm having trouble answering right now. Please try again in a moment, " +
        "or contact the organizers.";

    private string UnavailableMessage =>
        $"{_options.AssistantName} isn't available right now. Please reach out to the organizers for help.";

    private readonly HttpClient _http;
    private readonly OpenAiOptions _options;
    private readonly ILogger<AiHelperAssistant>? _log;

    public AiHelperAssistant(HttpClient http, OpenAiOptions options, ILogger<AiHelperAssistant>? log = null)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    public bool Available => _options.IsConfigured;

    public async Task<AiHelperAnswer> AskAsync(
        string question, AiHelperContext context, CancellationToken ct = default)
    {
        var q = (question ?? string.Empty).Trim();
        if (q.Length == 0)
        {
            return AiHelperAnswer.Unavailable("Ask me something and I'll do my best to help. 😊");
        }

        // Gate: not configured ⇒ never call out.
        if (!_options.IsConfigured)
        {
            return AiHelperAnswer.Unavailable(UnavailableMessage);
        }

        try
        {
            var text = await CallAsync(q, context, ct);
            return string.IsNullOrWhiteSpace(text)
                ? AiHelperAnswer.Unavailable(UnavailableMessage)
                : new AiHelperAnswer(true, text!.Trim());
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "AI Community Helper call failed; returning friendly fallback.");
            return AiHelperAnswer.Unavailable(ErrorMessage);
        }
    }

    private async Task<string?> CallAsync(string question, AiHelperContext context, CancellationToken ct)
    {
        var grounding = context.ToGroundingText();
        var systemPrompt = BuildSystemPrompt(_options.AssistantName);
        var system = string.IsNullOrWhiteSpace(grounding)
            ? systemPrompt
            : systemPrompt + "\n\n# Context you may use to answer\n" + grounding;

        var request = new ChatRequest
        {
            MaxTokens = _options.MaxTokens,
            Temperature = _options.Temperature,
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = system },
                new ChatMessage { Role = "user", Content = question },
            },
        };

        var endpoint = _options.Endpoint!.TrimEnd('/');
        var url =
            $"{endpoint}/openai/deployments/{_options.Deployment}/chat/completions" +
            $"?api-version={_options.ApiVersion}";

        using var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request, options: JsonOpts),
        };
        // Secret header — value comes from config/Key Vault, never logged.
        msg.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);

        using var resp = await _http.SendAsync(msg, ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct);
        return body?.Choices?.FirstOrDefault()?.Message?.Content;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Relaxed escaping so the chat payload carries readable text (e.g. apostrophes
        // in the system prompt / grounding) instead of ' noise. This is a JSON API
        // body, never rendered as HTML, so relaxed escaping is safe here.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // --- Minimal Azure OpenAI chat-completions DTOs (no SDK dependency) --------
    private sealed class ChatRequest
    {
        [JsonPropertyName("messages")] public ChatMessage[] Messages { get; set; } = Array.Empty<ChatMessage>();
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }

    private sealed class ChatResponse
    {
        [JsonPropertyName("choices")] public Choice[]? Choices { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
    }
}
