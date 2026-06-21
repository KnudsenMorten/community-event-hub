using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommunityHub.Core.Config;

/// <summary>
/// A small, dependency-free deep-merge helper for the HYBRID config model: it
/// merges a partial OVERRIDE fragment on top of a shipped DEFAULT object. Built
/// on <see cref="System.Text.Json"/> (<see cref="JsonNode"/>) to match the
/// config loaders, which all use System.Text.Json.
///
/// Merge rules (deliberately simple + predictable):
/// <list type="bullet">
/// <item>Both values are JSON OBJECTS ⇒ merge recursively, key by key. Keys only
///   in the default are kept; keys only in the override are added; keys in both
///   take the override's (recursively merged) value.</item>
/// <item>The override value is an ARRAY ⇒ it REPLACES the default array wholesale
///   (no element-wise merge). This matches the existing loaders, which treat a
///   config array (resources.sections, productClassification.rules, …) as a
///   single editorial unit you replace rather than splice.</item>
/// <item>The override value is a SCALAR (string/number/bool) ⇒ it replaces the
///   default scalar.</item>
/// <item>The override value is JSON <c>null</c> ⇒ it replaces with null (an
///   explicit "clear this" is honoured).</item>
/// </list>
///
/// FAIL-SAFE: a missing, blank, or unparseable override is treated as "no
/// override" and the default is returned UNCHANGED — this helper never throws on
/// a bad override and never mutates the caller's default node.
/// </summary>
public static class JsonDeepMerge
{
    /// <summary>
    /// Deep-merge a partial <paramref name="overrideJson"/> fragment on top of a
    /// <paramref name="defaultJson"/> document, returning the merged JSON text.
    /// Both inputs are JSON text. On ANY problem (blank/invalid override, or an
    /// override that does not parse) the <paramref name="defaultJson"/> is
    /// returned verbatim — the caller can serialize the result with no risk of a
    /// throw. The default itself is assumed valid (it is a shipped, tested file);
    /// if the default is also unparseable the override is ignored and the raw
    /// default text is returned for the loader's own parser to deal with.
    /// </summary>
    public static string Merge(string defaultJson, string? overrideJson)
    {
        if (string.IsNullOrWhiteSpace(overrideJson))
        {
            return defaultJson; // no override → defaults pass through untouched.
        }

        JsonNode? defaultNode;
        JsonNode? overrideNode;
        try
        {
            defaultNode = JsonNode.Parse(defaultJson);
            overrideNode = JsonNode.Parse(overrideJson);
        }
        catch (JsonException)
        {
            // A bad override (or default) must never break config loading.
            return defaultJson;
        }

        if (overrideNode is null)
        {
            return defaultJson;
        }
        if (defaultNode is null)
        {
            // No meaningful default to merge onto; hand back the original text.
            return defaultJson;
        }

        var merged = MergeNodes(defaultNode, overrideNode);
        return merged is null ? defaultJson : merged.ToJsonString();
    }

    /// <summary>
    /// Deep-merge two already-parsed nodes, returning a NEW node (neither input
    /// is mutated). Public so loaders that already hold a <see cref="JsonNode"/>
    /// can merge without re-serializing. <paramref name="overrideNode"/> wins per
    /// the rules documented on the class.
    /// </summary>
    public static JsonNode? MergeNodes(JsonNode? defaultNode, JsonNode? overrideNode)
    {
        // Both objects → recursive key-by-key merge onto a fresh clone of default.
        if (defaultNode is JsonObject defObj && overrideNode is JsonObject ovrObj)
        {
            var result = (JsonObject)defObj.DeepClone();
            foreach (var kvp in ovrObj)
            {
                if (result.TryGetPropertyValue(kvp.Key, out var existing))
                {
                    result[kvp.Key] = MergeNodes(existing, kvp.Value);
                }
                else
                {
                    result[kvp.Key] = kvp.Value?.DeepClone();
                }
            }
            return result;
        }

        // Override is an array / scalar / null, OR shapes differ → override wins
        // wholesale. Clone so the returned tree never shares nodes with the input.
        return overrideNode?.DeepClone();
    }
}
