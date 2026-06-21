using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommunityHub.Core.Config;

/// <summary>
/// Where the integrations config file lives, mirrors
/// <see cref="EventConfigOptions"/> / <see cref="SponsorConfigOptions"/>. The
/// file (<c>config/integrations.&lt;edition&gt;.json</c>) is per-edition shipped
/// content carrying the DEFAULTS for every external integration (each one
/// individually toggleable). Secrets are NEVER in the file — only the non-secret
/// settings and Key Vault secret NAMES (e.g. <c>consumerKeySecretName</c>).
/// </summary>
public sealed class IntegrationsConfigOptions
{
    public const string SectionName = "IntegrationsConfig";

    public string IntegrationsConfigPath { get; set; } =
        "config/integrations.eldk27.json";
}

/// <summary>
/// Loads the integrations config (<c>integrations.&lt;edition&gt;.json</c>) as a
/// raw <see cref="JsonNode"/> tree and, when given a per-edition override
/// fragment, DEEP-MERGES it on top of the shipped default (HYBRID config model —
/// see <see cref="JsonDeepMerge"/>).
///
/// Unlike the event/sponsor sections there is no single strongly-typed POCO for
/// the whole integrations file today — its many sections (woocommerce,
/// companyManager, sessionize, …) are bound piecemeal as
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> sections. So
/// this loader returns the merged JSON tree; callers read the section they need.
/// This keeps the merge path uniform across all three editable sections without
/// disturbing the existing per-section IConfiguration binding (which continues to
/// read the shipped defaults — additive, no behaviour change when no override
/// exists).
///
/// SECRETS: an override fragment for this section may carry only non-secret
/// settings and Key Vault secret NAMES, never secret values; the existing Key
/// Vault handling is unchanged.
/// </summary>
public sealed class IntegrationsConfigLoader
{
    /// <summary>
    /// Load + parse the integrations config file into a mutable
    /// <see cref="JsonObject"/>. Returns an empty object when the file is missing
    /// (rather than throwing) so a fully-specified override can still produce
    /// config and callers never crash on an absent file.
    /// </summary>
    public JsonObject Load(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }
        return ParseObject(File.ReadAllText(path));
    }

    /// <summary>
    /// Load the shipped default from <paramref name="path"/> then DEEP-MERGE a
    /// per-edition <paramref name="overrideJson"/> fragment on top. A
    /// null/blank/invalid override is ignored and the result equals
    /// <see cref="Load(string)"/> (fail-safe to the shipped default — never
    /// throws on a bad override).
    /// </summary>
    public JsonObject Load(string path, string? overrideJson)
    {
        if (string.IsNullOrWhiteSpace(overrideJson))
        {
            return Load(path);
        }
        var defaultJson = File.Exists(path) ? File.ReadAllText(path) : "{}";
        return ParseObject(JsonDeepMerge.Merge(defaultJson, overrideJson));
    }

    private static JsonObject ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            // A shipped file is expected to be valid; if it somehow is not, fail
            // safe to an empty object rather than crash a consumer.
            return new JsonObject();
        }
    }
}
