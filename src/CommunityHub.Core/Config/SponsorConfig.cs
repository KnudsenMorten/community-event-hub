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

    /// <summary>
    /// Optional longer help text shown when the sponsor clicks the task in
    /// /Sponsor/Index to expand it. Edit in sponsor.&lt;edition&gt;.json -- a
    /// new pull will reflect the change for newly-created tasks; existing
    /// rows keep their original description (org can DELETE + rerun to refresh).
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// True = part of the agreed deliverables (default). False = optional /
    /// paid add-on / nice-to-have; UI shows an "Optional" badge. Persisted
    /// to <see cref="CommunityHub.Core.Domain.ParticipantTask.IsMandatory"/>.
    /// </summary>
    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; set; } = true;

    /// <summary>
    /// Optional per-task upload-folder + notification block. When present, the
    /// pull engine creates <c>{rootFolderPath}/{companyName}/{subfolder}</c>
    /// on the SharePoint site (event.&lt;edition&gt;.json -&gt; sharepoint),
    /// mints an anonymous edit-link URL, and substitutes it into the task's
    /// title / description via <c>{{placeholder}}</c>. The watcher then polls
    /// the folder and emails <see cref="SponsorTaskUploadDefinition.NotifyEmails"/>
    /// whenever a file appears or changes -- so the team gets a heads-up
    /// without the sponsor having to "tell us it's uploaded".
    /// </summary>
    [JsonPropertyName("upload")]
    public SponsorTaskUploadDefinition? Upload { get; set; }
}

/// <summary>
/// Per-task upload-folder + notification config. Drives both provisioning
/// (engine creates the SharePoint folder and substitutes the resulting URL
/// into the task description) and the upload watcher (engine emails
/// <see cref="NotifyEmails"/> on file create/change).
/// </summary>
public sealed class SponsorTaskUploadDefinition
{
    /// <summary>Subfolder name under <c>{rootFolderPath}/{companyName}/</c>. e.g. "LOGO".</summary>
    [JsonPropertyName("subfolder")]
    public string Subfolder { get; set; } = string.Empty;

    /// <summary>Placeholder key substituted into task description (sans braces). e.g. "logoFolderUrl".</summary>
    [JsonPropertyName("placeholder")]
    public string Placeholder { get; set; } = string.Empty;

    /// <summary>Recipients of "file uploaded/updated" notifications. Empty = no notifications.</summary>
    [JsonPropertyName("notifyEmails")]
    public List<string> NotifyEmails { get; set; } = new();

    /// <summary>Subject template; <c>{{companyName}}</c> is resolved per notification.</summary>
    [JsonPropertyName("notifySubject")]
    public string NotifySubject { get; set; } = string.Empty;
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
/// One product-classification rule (entry in sponsor.&lt;edition&gt;.json -
/// productClassification.rules). The classifier walks rules in order; the
/// first match wins. <c>generatesTasks</c> defaults true; the addon entry
/// sets it false so logistics line items don't create deliverables.
/// </summary>
public sealed class ProductClassificationRule
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("matchCategoryContains")]
    public List<string> MatchCategoryContains { get; set; } = new();

    [JsonPropertyName("matchNameRegex")]
    public string? MatchNameRegex { get; set; }

    [JsonPropertyName("tierFromCategorySuffix")]
    public Dictionary<string, string> TierFromCategorySuffix { get; set; } = new();

    [JsonPropertyName("defaultTier")]
    public string? DefaultTier { get; set; }

    [JsonPropertyName("generatesTasks")]
    public bool GeneratesTasks { get; set; } = true;
}

/// <summary>
/// Parsed <c>productClassification</c> section of sponsor.&lt;edition&gt;.json.
/// </summary>
public sealed class ProductClassification
{
    [JsonPropertyName("rules")]
    public List<ProductClassificationRule> Rules { get; set; } = new();
}

/// <summary>One tier in <c>boothWallSpecs.tiers</c>.</summary>
public sealed class BoothWallSpecTier
{
    [JsonPropertyName("wallSize")]
    public string WallSize { get; set; } = string.Empty;

    [JsonPropertyName("specUrl")]
    public string SpecUrl { get; set; } = string.Empty;

    [JsonPropertyName("coupon")]
    public string Coupon { get; set; } = string.Empty;

    /// <summary>
    /// Per-tier furniture allowance block (coupon value EUR, chair count,
    /// available chair/table SKUs + prices). Substituted into task
    /// descriptions via <c>{{furnitureSpec}}</c>; may itself contain
    /// nested placeholders like <c>{{couponCode}}</c> which are resolved
    /// in the next substitution pass.
    /// </summary>
    [JsonPropertyName("furnitureSpec")]
    public string FurnitureSpec { get; set; } = string.Empty;
}

/// <summary>Parsed <c>boothWallSpecs</c> section.</summary>
public sealed class BoothWallSpecs
{
    [JsonPropertyName("tiers")]
    public Dictionary<string, BoothWallSpecTier> Tiers { get; set; } =
        new(System.StringComparer.OrdinalIgnoreCase);
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

    [JsonPropertyName("productClassification")]
    public ProductClassification? ProductClassification { get; set; }

    [JsonPropertyName("boothWallSpecs")]
    public BoothWallSpecs? BoothWallSpecs { get; set; }

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
        path = ConfigPaths.Resolve(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Sponsor config not found: {path}");
        }
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SponsorConfig>(json, Options)
               ?? new SponsorConfig();
    }

    /// <summary>
    /// Load the shipped default from <paramref name="path"/> then DEEP-MERGE a
    /// per-edition <paramref name="overrideJson"/> fragment on top (HYBRID config
    /// model — see <see cref="JsonDeepMerge"/>). A null/blank/invalid override is
    /// ignored and the result is identical to <see cref="Load(string)"/>
    /// (fail-safe to the shipped default — never throws on a bad override). Still
    /// throws when the shipped file itself is missing, exactly as
    /// <see cref="Load(string)"/> does (an override cannot stand in for a missing
    /// template).
    /// </summary>
    public SponsorConfig Load(string path, string? overrideJson)
    {
        if (string.IsNullOrWhiteSpace(overrideJson))
        {
            return Load(path); // common path: no override, unchanged behaviour.
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Sponsor config not found: {path}");
        }
        var merged = JsonDeepMerge.Merge(File.ReadAllText(path), overrideJson);
        return JsonSerializer.Deserialize<SponsorConfig>(merged, Options)
               ?? new SponsorConfig();
    }
}
