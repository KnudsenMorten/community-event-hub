using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunityHub.Core.Surveys;

/// <summary>
/// THE parser for a survey definition JSON file. One implementation, used by every host.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This exists because §773.1 happened.</b> The three post-event surveys 404'd in
/// production for a day: the options used to parse them lacked
/// <see cref="JsonStringEnumConverter"/>, the whole definition failed to deserialize, and the
/// provider logged and returned null. The test that should have caught it deserialized the same
/// files with options it supplied ITSELF — two parsers, and the green one was not the one that
/// shipped.</para>
///
/// <para>⚠️ <b>So there is exactly one <see cref="Options"/> here and nobody builds their own.</b>
/// The web host loads these files from its content root and the jobs host from its own output
/// folder; if each had brought its own <c>JsonSerializerOptions</c>, the two hosts could disagree
/// about whether a survey is valid — and the one that reads it wrong is the one that publishes a
/// summary for a survey it thinks has no questions.</para>
/// </remarks>
public static class SurveyDefinitionJsonLoader
{
    /// <summary>The ONLY options used to read a survey definition, in any host.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 🔒 §6.5 — question kinds are written as WORDS ("Rating", "FreeText") because a human edits
        // this file. Without this converter the WHOLE definition fails to deserialize and the survey
        // answers 404. That is not hypothetical — see §773.1.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Parse one definition. Returns null when the text cannot be read as a definition.
    /// </summary>
    /// <param name="expectedSlug">
    /// The slug the caller asked for. A file whose own slug disagrees is loaded under the REQUESTED
    /// one — a definition that answers on a URL nobody links to is invisible.
    /// </param>
    public static SurveyDefinition? Parse(string json, string expectedSlug)
    {
        var def = JsonSerializer.Deserialize<SurveyDefinition>(json, Options);
        if (def is null) return null;

        if (!string.Equals(def.Slug, expectedSlug, StringComparison.OrdinalIgnoreCase))
            def.Slug = expectedSlug;

        return def;
    }

    /// <summary>True when the file's own slug does not match the one it was loaded as.</summary>
    public static bool SlugMismatch(string json, string expectedSlug)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return doc.RootElement.TryGetProperty("slug", out var slug)
                   && slug.ValueKind == JsonValueKind.String
                   && !string.Equals(slug.GetString(), expectedSlug, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
