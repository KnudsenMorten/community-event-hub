using System.Collections.Concurrent;
using System.Text.Json;
using CommunityHub.Core.Surveys;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Surveys;

/// <summary>
/// Loads + caches survey definitions from JSON files under
/// {ContentRoot}/App_Data/Surveys/{slug}.json. First access reads + parses,
/// subsequent calls hit the in-memory cache. Restart the app to pick up
/// edits -- no DB migration needed.
/// </summary>
public sealed class SurveyDefinitionProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IHostEnvironment _env;
    private readonly ILogger<SurveyDefinitionProvider> _log;
    private readonly ConcurrentDictionary<string, SurveyDefinition> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SurveyDefinitionProvider(IHostEnvironment env, ILogger<SurveyDefinitionProvider> log)
    {
        _env = env;
        _log = log;
    }

    public SurveyDefinition? TryGet(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        // Slug is taken from a route — keep it tight so we cannot escape the
        // App_Data/Surveys/ folder via "..", absolute paths, or path separators.
        if (slug.Contains('/') || slug.Contains('\\') || slug.Contains("..")) return null;

        return _cache.GetOrAdd(slug, s =>
        {
            var path = Path.Combine(_env.ContentRootPath, "App_Data", "Surveys", $"{s}.json");
            if (!File.Exists(path))
            {
                _log.LogWarning("Survey definition not found: {Path}", path);
                return null!;
            }
            try
            {
                var json = File.ReadAllText(path);
                var def = JsonSerializer.Deserialize<SurveyDefinition>(json, JsonOpts);
                if (def is null) throw new InvalidOperationException("Deserialized to null");
                if (!string.Equals(def.Slug, s, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning("Survey slug mismatch in {Path}: file slug='{FileSlug}' but loaded as '{LoadedSlug}'. Using requested slug.", path, def.Slug, s);
                    def.Slug = s;
                }
                _log.LogInformation("Loaded survey '{Slug}' from {Path} ({Tracks} tracks)", s, path, def.Tracks.Count);
                return def;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to load survey definition from {Path}", path);
                return null!;
            }
        });
    }
}
