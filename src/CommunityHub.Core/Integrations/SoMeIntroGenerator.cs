using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityHub.Core.Assistant;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>What an intro is being written about.</summary>
/// <param name="Kind">Which post type — the tone differs between a session and a sponsor.</param>
/// <param name="Title">Session title, track name, sponsor name or tier.</param>
/// <param name="Detail">The session abstract, or the sponsor's own description. May be empty.</param>
public sealed record SoMeIntroRequest(SoMeTemplateKind Kind, string Title, string? Detail);

/// <summary>
/// §824.2D — writes the <c>{IntroText}</c> paragraph from a session's title and abstract.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: <i>"come up with a suggestion to use open ai to generate text, which i
/// will approve per pst"</i>. It reuses CEH's EXISTING Azure OpenAI configuration
/// (<see cref="OpenAiOptions"/>) rather than introducing a second AI setup — one endpoint, one key,
/// one place to switch off.</para>
///
/// <para>🔒 <b>Every failure returns null, and null is a perfectly good outcome.</b> Not configured,
/// rate-limited, timed out, refused, returned something empty — the post is composed WITHOUT an
/// intro, the template closes the gap (§824.15), and it still queues for approval. An announcement
/// missing its opening line is a small loss; a scheduler that stops planning because an AI endpoint
/// was busy is an absence nobody notices.</para>
///
/// <para>⚠️ <b>Nothing this writes can publish by itself.</b> Generated text lands in a post that is
/// queued <c>IsActive = false</c> (§824.21a), so he reads every intro before it reaches the company
/// page — which is the arrangement he asked for, and the reason generating text automatically is
/// safe at all.</para>
/// </remarks>
public sealed class SoMeIntroGenerator
{
    private readonly HttpClient _http;
    private readonly OpenAiOptions _options;
    private readonly ILogger<SoMeIntroGenerator>? _log;

    public SoMeIntroGenerator(
        HttpClient http, OpenAiOptions options, ILogger<SoMeIntroGenerator>? log = null)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    /// <summary>True when an intro can even be attempted.</summary>
    public bool IsConfigured => _options.IsConfigured;

    /// <summary>
    /// The house rules the model writes under. Kept as a constant so the wording that reaches the
    /// company page is reviewable in a diff rather than buried in a string built at runtime.
    /// </summary>
    /// <remarks>
    /// The prohibitions matter more than the instructions. Hashtags, the event URL and the organizer
    /// credit are all added by the TEMPLATE — a model that helpfully includes them produces a post
    /// with two tag blocks and two links, which reads as broken rather than as enthusiastic.
    /// </remarks>
    public const string SystemPrompt =
        "You write one short opening paragraph for a LinkedIn post announcing part of a technical "
        + "community conference. Rules: 1-3 sentences, maximum 320 characters. Warm and energetic, "
        + "never corporate. You MAY use one or two emoji. Write in English. "
        + "Do NOT include hashtags, URLs, the event name, dates, the venue, speaker names or an "
        + "organizer credit - all of those are added around your text automatically, and repeating "
        + "them produces a post with everything twice. Do not use quotation marks around your answer. "
        + "Return only the paragraph.";

    /// <summary>
    /// Generate one intro, or <c>null</c> when it cannot be done. Never throws.
    /// </summary>
    public async Task<string?> GenerateAsync(SoMeIntroRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_options.IsConfigured) return null;
        if (string.IsNullOrWhiteSpace(request.Title)) return null;

        try
        {
            var user = BuildUserPrompt(request);

            var payload = new ChatRequest
            {
                // Shorter than the assistant's budget: this is one paragraph, and a large ceiling
                // just gives a chatty model room to write a second one that gets truncated.
                MaxTokens = 200,
                // Warmer than the AiHelper's 0.2 — that one answers questions about facts, this one
                // writes marketing copy, and identical intros for thirty sponsors would read worse
                // than none.
                Temperature = 0.8,
                Messages = new[]
                {
                    new ChatMessage { Role = "system", Content = SystemPrompt },
                    new ChatMessage { Role = "user", Content = user },
                },
            };

            var endpoint = _options.Endpoint!.TrimEnd('/');
            var url = $"{endpoint}/openai/deployments/{_options.Deployment}/chat/completions"
                      + $"?api-version={_options.ApiVersion}";

            using var msg = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload, options: JsonOpts),
            };
            // Secret header — from config / Key Vault, never logged.
            msg.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);

            using var resp = await _http.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.LogWarning(
                    "§824.2D: intro generation returned {Status} for '{Title}'; the post will be "
                    + "composed without an intro.", (int)resp.StatusCode, request.Title);
                return null;
            }

            var body = await resp.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct);
            return Clean(body?.Choices?.FirstOrDefault()?.Message?.Content);
        }
        // 🔴 §906 — A TIMEOUT IS A FAILURE, NOT A CANCELLATION, and this filter has to tell them
        // apart because .NET gives them the SAME TYPE.
        //
        // `HttpClient` throws TaskCanceledException — which derives from OperationCanceledException
        // — when its own timeout elapses. The filter below used to read
        // `when (ex is not OperationCanceledException)`, meaning to let a genuine host shutdown
        // through, and it let every endpoint timeout through with it.
        //
        // ⚠️ WHAT THAT COST, 2026-08-06: the moment the OpenAI settings were added to the JOBS host,
        // the planner started making one AI call per post. The endpoint did not answer, each call
        // threw after the default 100-second HttpClient timeout, the exception escaped
        // ComposeAsync and killed the run — AFTER §848.2 had already committed the deletion of the
        // 78 un-accepted proposals. His queue went from 83 posts to 5. Nothing was lost permanently
        // (the planner rebuilds proposals), but "every failure returns null" was not true, and this
        // is the line that made it false.
        //
        // 🔒 `ct` is the CALLER's token: cancelled ⇒ the host really is shutting down, so rethrow.
        // Not cancelled ⇒ it was our own HTTP timeout, which is an ordinary bad minute.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.LogWarning(
                "§824.2D: intro generation TIMED OUT for '{Title}'; composing without an intro.",
                request.Title);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 🔒 Swallowed on purpose. The scheduler's job is to plan the campaign; an AI endpoint
            // having a bad minute must not stop it.
            _log?.LogWarning(ex,
                "§824.2D: intro generation failed for '{Title}'; composing without an intro.",
                request.Title);
            return null;
        }
    }

    internal static string BuildUserPrompt(SoMeIntroRequest r)
    {
        var what = r.Kind switch
        {
            SoMeTemplateKind.SpeakerTracks =>
                $"the '{r.Title}' track at the conference — write something that makes people want "
                + "to follow that track",
            SoMeTemplateKind.Session =>
                $"a conference session titled '{r.Title}'",
            SoMeTemplateKind.SponsorCategory =>
                $"the conference's {r.Title} sponsors, as a group",
            SoMeTemplateKind.Sponsor =>
                $"{r.Title}, a sponsor of the conference",
            _ => $"'{r.Title}' at the conference",
        };

        var prompt = $"Write the opening paragraph for a post about {what}.";

        if (!string.IsNullOrWhiteSpace(r.Detail))
        {
            // Truncated: a 4000-character abstract costs tokens and adds nothing — the opening
            // paragraph is written from the gist, and the full abstract is a link away anyway.
            var detail = r.Detail!.Trim();
            if (detail.Length > 1200) detail = detail[..1200];
            prompt += $"\n\nFor context, here is what it is about:\n{detail}";
        }

        return prompt;
    }

    /// <summary>
    /// Tidy what came back: strip wrapping quotes, collapse it to a single paragraph, and refuse
    /// anything that broke the rules badly enough to spoil the post.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A hashtag in the intro is REJECTED, not stripped.</b> The template adds the tag block, so
    /// a model that opens with "#ELDK27" gives the post two of them. Removing the offending character
    /// would leave a sentence written around a hashtag that is no longer there — returning null and
    /// composing without an intro is the honest outcome, and he sees it in the queue either way.
    /// </remarks>
    internal static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();

        // One paragraph: the templates put their own blank line after it.
        s = string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (s.Length == 0) return null;
        if (s.Contains('#')) return null;                       // would double the tag block
        if (s.Contains("http", StringComparison.OrdinalIgnoreCase)) return null;   // would double the link
        if (s.Length > 600) return null;                        // ignored the length rule entirely

        return s;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // --- Minimal Azure OpenAI chat-completions DTOs (no SDK dependency, as AiHelperAssistant) ---
    private sealed class ChatRequest
    {
        [JsonPropertyName("messages")] public ChatMessage[] Messages { get; set; } = Array.Empty<ChatMessage>();
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "user";
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
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
