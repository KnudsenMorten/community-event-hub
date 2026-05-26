using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunityHub.Core.Config;

/// <summary>
/// One task definition inside a task set in sponsor.&lt;edition&gt;.json.
/// </summary>
public sealed class SponsorTaskDefinition
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Name of the deadline rule that dates this task.</summary>
    [JsonPropertyName("deadline")]
    public string Deadline { get; set; } = string.Empty;
}

/// <summary>
/// A named deadline rule. <c>basis</c> "eventMinus" = event date minus
/// <c>days</c>; "contractPlus" = first-order date plus <c>days</c>, falling
/// back to now-plus-<c>fallbackNowPlus</c> when no order date is known.
/// </summary>
public sealed class DeadlineRule
{
    [JsonPropertyName("basis")]
    public string Basis { get; set; } = string.Empty;

    [JsonPropertyName("days")]
    public int Days { get; set; }

    [JsonPropertyName("fallbackNowPlus")]
    public int? FallbackNowPlus { get; set; }
}

/// <summary>
/// The sponsor configuration (sponsor.&lt;edition&gt;.json) - the parts the
/// task-expansion needs. Other sections of the file are ignored here.
/// </summary>
public sealed class SponsorConfig
{
    [JsonPropertyName("deadlineRules")]
    public Dictionary<string, JsonElement> DeadlineRulesRaw { get; set; } = new();

    [JsonPropertyName("taskSets")]
    public Dictionary<string, JsonElement> TaskSetsRaw { get; set; } = new();

    // --- Parsed views (the *Raw maps include "_doc" string entries) ---------

    /// <summary>Deadline rules by name, with the "_doc" entry skipped.</summary>
    public IReadOnlyDictionary<string, DeadlineRule> DeadlineRules()
    {
        var result = new Dictionary<string, DeadlineRule>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (name, element) in DeadlineRulesRaw)
        {
            if (element.ValueKind != JsonValueKind.Object) continue; // "_doc"
            var rule = element.Deserialize<DeadlineRule>();
            if (rule is not null) result[name] = rule;
        }
        return result;
    }

    /// <summary>A task set by name, or an empty list if the set is unknown.</summary>
    public IReadOnlyList<SponsorTaskDefinition> TaskSet(string name)
    {
        if (!TaskSetsRaw.TryGetValue(name, out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SponsorTaskDefinition>();
        }
        return element.Deserialize<List<SponsorTaskDefinition>>()
               ?? new List<SponsorTaskDefinition>();
    }
}

/// <summary>
/// Where the sponsor config file is. The file is per-edition deployed
/// content (config/sponsor.&lt;edition&gt;.json).
/// </summary>
public sealed class SponsorConfigOptions
{
    public const string SectionName = "SponsorConfig";

    public string SponsorConfigPath { get; set; } =
        "config/sponsor.eldk27.json";
}

/// <summary>
/// Loads <see cref="SponsorConfig"/> from a JSON file on disk.
/// </summary>
public sealed class SponsorConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Load and parse a sponsor config file. Throws if missing.</summary>
    public SponsorConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Sponsor config not found: {path}");
        }
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SponsorConfig>(json, Options)
               ?? new SponsorConfig();
    }
}
