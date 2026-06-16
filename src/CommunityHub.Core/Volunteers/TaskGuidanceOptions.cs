namespace CommunityHub.Core.Volunteers;

/// <summary>
/// Configuration for the AI task-guidance generator. Bound from the
/// <c>VolunteerGuidance</c> config section (e.g. integrations.&lt;edition&gt;.json /
/// the gitignored *.custom.json, or environment / Key Vault). The API KEY is a
/// SECRET and must come from the existing secret mechanism — it is NEVER committed;
/// the shipped sample carries only an empty placeholder.
///
/// When <see cref="ApiKey"/> is blank the generator is the heuristic fallback;
/// when it is set the LLM provider activates. This is the gate.
/// </summary>
public sealed class TaskGuidanceOptions
{
    public const string SectionName = "VolunteerGuidance";

    /// <summary>The LLM API key (SECRET — placeholder only in committed config).
    /// Blank ⇒ AI is disabled and the heuristic fallback is used.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The Messages API base URL. Defaults to the public Anthropic API.</summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>The model id. Defaults to Claude Opus 4.8.</summary>
    public string Model { get; set; } = "claude-opus-4-8";

    /// <summary>Upper bound on generated tokens per call.</summary>
    public int MaxTokens { get; set; } = 400;

    /// <summary>True when a non-blank key is configured (the AI gate).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
