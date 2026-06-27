namespace CommunityHub.Core.Assistant;

/// <summary>
/// Configuration for the grounded AI Community Helper (code-named AiHelper, display
/// name configurable via <see cref="AssistantName"/>; REQUIREMENTS §129). Bound from
/// the <c>OpenAI</c> config section (Azure OpenAI). The <see cref="ApiKey"/> is a
/// SECRET — committed config carries only a blank placeholder; the real value comes
/// from Key Vault (a KV reference app setting), NEVER from the repo.
///
/// This mirrors the gate pattern the rest of the hub uses for optional integrations
/// (e.g. <c>ZohoOptions.Enabled</c>): when <see cref="IsConfigured"/> is false the
/// assistant quietly no-ops (the widget hides / the endpoint returns a friendly
/// "unavailable"), so a missing key never breaks a page.
/// </summary>
public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    /// <summary>
    /// The assistant's user-facing DISPLAY NAME — shown in the widget header/greeting and
    /// spoken in the system prompt ("You are {AssistantName}, …"). Configurable so an
    /// operator can rebrand the helper without code changes; override via the
    /// <c>OpenAI:AssistantName</c> config key (app setting <c>OpenAI__AssistantName</c>).
    /// Defaults to "Otto". Purely cosmetic — the code identifiers are name-agnostic
    /// (AiHelper); only this string changes what users see/read.
    /// </summary>
    public string AssistantName { get; set; } = "Otto";

    /// <summary>Azure OpenAI resource endpoint, e.g. <c>https://my-aoai.openai.azure.com</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The chat-completions deployment name (the model deployment).</summary>
    public string? Deployment { get; set; }

    /// <summary>The API key (SECRET — placeholder only in committed config; KV ref in prod).</summary>
    public string? ApiKey { get; set; }

    /// <summary>The Azure OpenAI REST api-version. Defaults to the wired value.</summary>
    public string ApiVersion { get; set; } = "2025-01-01-preview";

    /// <summary>Operator master switch. False ⇒ the assistant is off regardless of the other fields.</summary>
    public bool Enabled { get; set; }

    /// <summary>Upper bound on generated tokens per answer.</summary>
    public int MaxTokens { get; set; } = 600;

    /// <summary>Sampling temperature — low, for grounded/factual answers.</summary>
    public double Temperature { get; set; } = 0.2;

    /// <summary>
    /// The gate: the assistant is live only when enabled AND fully configured. Any blank
    /// endpoint/deployment/key ⇒ no-op (no network, no secret needed).
    /// </summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(Deployment)
        && !string.IsNullOrWhiteSpace(ApiKey);
}
