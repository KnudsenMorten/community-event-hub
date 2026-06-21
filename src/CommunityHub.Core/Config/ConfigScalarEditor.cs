using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommunityHub.Core.Config;

/// <summary>
/// The kind of a scalar config leaf — drives the input control the editor
/// renders and how a posted string is validated + re-typed before it is written
/// back into the override JSON (Phase 2 of the admin-editable-config epic).
/// </summary>
public enum ScalarKind
{
    /// <summary>A free-text string (the default for any string leaf).</summary>
    String = 0,

    /// <summary>A string that must be a valid absolute URI (or blank).</summary>
    Url = 1,

    /// <summary>An integer or decimal number.</summary>
    Number = 2,

    /// <summary>A boolean (true/false) — rendered as a checkbox.</summary>
    Bool = 3,
}

/// <summary>
/// One editable SCALAR leaf of a config section, resolved for the editor UI.
/// Carries the dotted JSON path (e.g. <c>edition.expectedAttendees</c>,
/// <c>woocommerce.baseUrl</c>), the current EFFECTIVE value (shipped default
/// deep-merged with any override), whether that value is currently OVERRIDDEN
/// vs the shipped default, and the inferred <see cref="ScalarKind"/>.
/// </summary>
public sealed class ScalarField
{
    /// <summary>Dotted JSON path to the leaf (object segments joined by '.').</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>The leaf's own key (last path segment) — a friendly default label.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>The EFFECTIVE value as text (default merged with any override).</summary>
    public string EffectiveValue { get; init; } = string.Empty;

    /// <summary>The shipped DEFAULT value as text (override removed).</summary>
    public string DefaultValue { get; init; } = string.Empty;

    /// <summary>True when an override changes this leaf from the shipped default.</summary>
    public bool IsOverridden { get; init; }

    /// <summary>Inferred control/validation kind.</summary>
    public ScalarKind Kind { get; init; }
}

/// <summary>
/// Flattens a config section's JSON into the editable SCALAR leaves the Phase 2
/// GUI exposes, and rebuilds the per-edition OVERRIDE fragment when a leaf is
/// changed or reset — without disturbing sibling keys (the override is a partial
/// fragment the loaders deep-merge on top of the shipped default).
///
/// <para>SCOPE (Phase 2): SCALARS ONLY. Arrays and the objects nested under them
/// (resources.sections, sponsor taskSets, …) are Phase 3 and are skipped here.
/// Objects ARE descended into so their scalar leaves are reachable by a dotted
/// path, but an array stops the walk (its contents are an editorial unit).</para>
///
/// <para>SECRETS: leaves whose key is documentation (<c>_*</c>) or secret-bearing
/// (name ends with <c>SecretName</c> or contains secret/password/token/apikey)
/// are NEVER surfaced — secret VALUES live in Key Vault and must never enter the
/// override store. <see cref="IsExcludedKey"/> is the single gate.</para>
///
/// Pure, dependency-free, built on <see cref="System.Text.Json"/> to match the
/// loaders. Never throws on a malformed override — it falls back to the default.
/// </summary>
public static class ConfigScalarEditor
{
    /// <summary>
    /// Enumerate the editable scalar leaves of a section: parse the shipped
    /// <paramref name="defaultJson"/>, deep-merge any <paramref name="overrideJson"/>
    /// to get the EFFECTIVE tree, and walk both in lock-step so each leaf knows its
    /// effective value, its shipped default, and whether it is overridden. Returns
    /// an empty list (never throws) when the default does not parse to an object.
    /// </summary>
    public static IReadOnlyList<ScalarField> Enumerate(
        string defaultJson, string? overrideJson)
    {
        JsonObject? defaultObj = TryParseObject(defaultJson);
        if (defaultObj is null) return Array.Empty<ScalarField>();

        // EFFECTIVE = default merged with override (fail-safe to default).
        var effectiveObj =
            (JsonDeepMerge.MergeNodes(defaultObj.DeepClone(), TryParse(overrideJson))
                as JsonObject)
            ?? defaultObj;

        var fields = new List<ScalarField>();
        Walk(defaultObj, effectiveObj, prefix: string.Empty, fields);
        return fields;
    }

    private static void Walk(
        JsonObject defaultObj, JsonObject? effectiveObj,
        string prefix, List<ScalarField> sink)
    {
        foreach (var kvp in defaultObj)
        {
            var key = kvp.Key;
            if (IsExcludedKey(key)) continue;

            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            var defaultNode = kvp.Value;
            JsonNode? effectiveNode = null;
            effectiveObj?.TryGetPropertyValue(key, out effectiveNode);

            switch (defaultNode)
            {
                case JsonObject childObj:
                    // Descend so nested scalar leaves are reachable; the effective
                    // child (if an object) drives the merged values one level down.
                    Walk(childObj, effectiveNode as JsonObject, path, sink);
                    break;

                case JsonArray:
                    // Arrays are an editorial unit — deferred to Phase 3.
                    break;

                case JsonValue defVal:
                    var kind = InferKind(key, defVal);
                    var defText = ScalarText(defVal);
                    var effText = effectiveNode is JsonValue effVal
                        ? ScalarText(effVal)
                        : defText;
                    sink.Add(new ScalarField
                    {
                        Path = path,
                        Key = key,
                        DefaultValue = defText,
                        EffectiveValue = effText,
                        IsOverridden = !string.Equals(
                            defText, effText, StringComparison.Ordinal),
                        Kind = kind,
                    });
                    break;

                case null:
                    // A JSON null leaf — treat as an editable (blank) string.
                    sink.Add(new ScalarField
                    {
                        Path = path, Key = key,
                        DefaultValue = string.Empty, EffectiveValue = string.Empty,
                        IsOverridden = false, Kind = ScalarKind.String,
                    });
                    break;
            }
        }
    }

    /// <summary>
    /// True when a key must NOT be surfaced in the editor: documentation keys
    /// (<c>_*</c>) and secret-bearing keys (names that end with <c>SecretName</c>
    /// or contain secret/password/token/apikey/connectionstring). Conservative by
    /// design — when unsure, exclude (secrets stay in Key Vault).
    /// </summary>
    public static bool IsExcludedKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return true;
        if (key.StartsWith("_", StringComparison.Ordinal)) return true;

        // Secret-bearing: a Key Vault secret NAME, never a value, but we still
        // refuse to expose it so the editor can never become a secret surface.
        if (key.EndsWith("SecretName", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var marker in SecretMarkers)
        {
            if (key.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static readonly string[] SecretMarkers =
    {
        "secret", "password", "token", "apikey", "connectionstring", "credential",
    };

    /// <summary>
    /// Build a partial override FRAGMENT that sets <paramref name="path"/> to
    /// <paramref name="newValue"/> (typed per <paramref name="kind"/>) deep-merged
    /// onto the EXISTING <paramref name="existingOverrideJson"/> — so unrelated keys
    /// already in the override survive untouched. Returns the serialized fragment.
    /// Throws <see cref="FormatException"/> when the value is invalid for the kind
    /// (the caller validates first and surfaces the error inline).
    /// </summary>
    public static string ApplyChange(
        string? existingOverrideJson, string path, string newValue, ScalarKind kind)
    {
        var existing = TryParseObject(existingOverrideJson) ?? new JsonObject();
        var leaf = ToNode(newValue, kind); // may throw FormatException
        SetByPath(existing, path, leaf);
        return existing.ToJsonString();
    }

    /// <summary>
    /// Remove <paramref name="path"/> from the override fragment (reset-to-default),
    /// pruning any now-empty parent objects so the override does not accumulate
    /// hollow scaffolding. Returns the serialized fragment, or an empty string when
    /// nothing remains (the caller can then delete the row entirely).
    /// </summary>
    public static string RemovePath(string? existingOverrideJson, string path)
    {
        var existing = TryParseObject(existingOverrideJson);
        if (existing is null) return string.Empty;
        RemoveByPath(existing, path);
        return existing.Count == 0 ? string.Empty : existing.ToJsonString();
    }

    /// <summary>
    /// Validate a posted value for a kind. Returns null when valid, otherwise a
    /// short human message. URL fields accept blank or an absolute http/https URI;
    /// numbers must parse; bool is anything (the checkbox is canonicalized).
    /// </summary>
    public static string? Validate(string value, ScalarKind kind)
    {
        switch (kind)
        {
            case ScalarKind.Url:
                if (string.IsNullOrWhiteSpace(value)) return null; // blank clears
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return "Enter a valid http(s) URL or leave it blank.";
                }
                return null;

            case ScalarKind.Number:
                if (string.IsNullOrWhiteSpace(value)) return "Enter a number.";
                return decimal.TryParse(
                    value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
                    ? null : "Enter a valid number.";

            default:
                return null;
        }
    }

    // --- internals -----------------------------------------------------------

    private static ScalarKind InferKind(string key, JsonValue value)
    {
        if (value.TryGetValue(out bool _)) return ScalarKind.Bool;
        if (value.TryGetValue(out double _)
            && value.GetValue<JsonElement>().ValueKind == JsonValueKind.Number)
        {
            return ScalarKind.Number;
        }
        // String — infer URL by key hint or by an http(s) value, else plain text.
        var s = value.GetValue<JsonElement>().ValueKind == JsonValueKind.String
            ? value.GetValue<string>() : string.Empty;
        if (key.EndsWith("Url", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("Uri", StringComparison.OrdinalIgnoreCase)
            || key.Equals("baseUrl", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return ScalarKind.Url;
        }
        return ScalarKind.String;
    }

    private static string ScalarText(JsonValue value)
    {
        var el = value.GetValue<JsonElement>();
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => el.GetRawText(),
            _ => el.GetRawText(),
        };
    }

    private static JsonNode ToNode(string value, ScalarKind kind)
    {
        switch (kind)
        {
            case ScalarKind.Bool:
                return JsonValue.Create(
                    string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

            case ScalarKind.Number:
                if (!decimal.TryParse(
                        value, NumberStyles.Number, CultureInfo.InvariantCulture,
                        out var dec))
                {
                    throw new FormatException($"'{value}' is not a number.");
                }
                // Preserve integer-ness so 500 stays 500, not 500.0.
                return dec == Math.Truncate(dec) && Math.Abs(dec) < long.MaxValue
                    ? JsonValue.Create((long)dec)
                    : JsonValue.Create(dec);

            case ScalarKind.Url:
                if (!string.IsNullOrWhiteSpace(value)
                    && (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                        || (uri.Scheme != Uri.UriSchemeHttp
                            && uri.Scheme != Uri.UriSchemeHttps)))
                {
                    throw new FormatException($"'{value}' is not a valid URL.");
                }
                return JsonValue.Create(value);

            default:
                return JsonValue.Create(value);
        }
    }

    private static void SetByPath(JsonObject root, string path, JsonNode leaf)
    {
        var segments = path.Split('.');
        var node = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            if (node[seg] is not JsonObject child)
            {
                child = new JsonObject();
                node[seg] = child;
            }
            node = child;
        }
        node[segments[^1]] = leaf;
    }

    private static void RemoveByPath(JsonObject root, string path)
    {
        var segments = path.Split('.');
        // Track the chain so we can prune emptied parents on the way back up.
        var chain = new List<(JsonObject parent, string key)>();
        var node = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            chain.Add((node, segments[i]));
            if (node[segments[i]] is not JsonObject child) return; // path absent
            node = child;
        }
        node.Remove(segments[^1]);

        // Prune empty parents bottom-up.
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var (parent, key) = chain[i];
            if (parent[key] is JsonObject obj && obj.Count == 0)
            {
                parent.Remove(key);
            }
            else
            {
                break;
            }
        }
    }

    private static JsonObject? TryParseObject(string? json) =>
        TryParse(json) as JsonObject;

    private static JsonNode? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }
}
