using System.Collections.Concurrent;
using CommunityHub.Core.Surveys;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §6.5 — the jobs host's view of the survey definitions, read from its own output folder.
/// </summary>
/// <remarks>
/// <para>The definition JSON lives once in the repo, under the web project's
/// <c>App_Data/Surveys/</c>, and is <b>linked</b> into this project so it is copied beside the
/// Functions binaries. One source file, two outputs — not two copies to keep in step.</para>
///
/// <para>🔒 <b>It parses with <see cref="SurveyDefinitionJsonLoader"/>, the same parser the web host
/// uses.</b> §773.1 was two parsers disagreeing; here the stakes are the same shape — a host that
/// read a definition as "no questions" would publish a summary saying nobody answered anything.</para>
///
/// <para>⚠️ <b>A missing folder is a WARNING, not an exception.</b> The job reports the surveys it
/// could not summarise by name, which is the honest outcome; throwing would take out the other two.
/// </para>
/// </remarks>
public sealed class SurveyDefinitionFileSource : ISurveyDefinitionSource
{
    private readonly ILogger<SurveyDefinitionFileSource> _log;
    private readonly ConcurrentDictionary<string, SurveyDefinition?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public SurveyDefinitionFileSource(ILogger<SurveyDefinitionFileSource> log) => _log = log;

    public SurveyDefinition? TryGet(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        if (slug.Contains('/') || slug.Contains('\\') || slug.Contains("..")) return null;

        return _cache.GetOrAdd(slug, s =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "App_Data", "Surveys", $"{s}.json");
            if (!File.Exists(path))
            {
                _log.LogWarning("Survey definition not found in the jobs host: {Path}", path);
                return null;
            }

            try
            {
                return SurveyDefinitionJsonLoader.Parse(File.ReadAllText(path), s);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to load survey definition from {Path}", path);
                return null;
            }
        });
    }
}
