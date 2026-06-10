using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunityHub.Core.Config;

/// <summary>
/// The slim subset of <c>event.&lt;edition&gt;.json -&gt; edition</c> that
/// the engine substitutes into task descriptions, email templates, etc.
/// Per-edition facts that drift year-to-year (attendee count, edition code,
/// brand colour) live here so the JSON is the single editorial knob.
/// </summary>
public sealed class EventEditionConfig
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("expectedAttendees")]
    public int ExpectedAttendees { get; set; }

    /// <summary>
    /// Optional free-form placeholder map loaded from
    /// <c>event.&lt;edition&gt;.json -&gt; placeholders</c> (note: a SIBLING
    /// of <c>edition</c>, not nested inside it). Populated by
    /// <see cref="EventEditionConfigLoader"/>. Keys starting with underscore
    /// are documentation and NEVER substituted.
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, string> Placeholders { get; set; } = new();

    /// <summary>
    /// Optional SharePoint Online block loaded from
    /// <c>event.&lt;edition&gt;.json -&gt; sharepoint</c>. Used to pre-create
    /// per-sponsor upload folders + mint anonymous edit-link URLs the sponsor
    /// clicks straight to. Null when the section is absent.
    /// </summary>
    [JsonIgnore]
    public SharePointEditionConfig? SharePoint { get; set; }

    /// <summary>
    /// Optional <c>dates</c> block (preDay / day1 / day2 / lockDate +
    /// timezone). Loaded as ISO-yyyy-MM-dd strings so the UI can format per
    /// culture. Empty when the section is absent.
    /// </summary>
    [JsonIgnore]
    public EditionDates? Dates { get; set; }
}

/// <summary>Per-edition date facts pulled from <c>event.&lt;edition&gt;.json -&gt; dates</c>.</summary>
public sealed class EditionDates
{
    [JsonPropertyName("preDay")]    public string PreDay   { get; set; } = string.Empty;
    [JsonPropertyName("day1")]      public string Day1     { get; set; } = string.Empty;
    [JsonPropertyName("day2")]      public string Day2     { get; set; } = string.Empty;
    [JsonPropertyName("timezone")]  public string Timezone { get; set; } = string.Empty;
    [JsonPropertyName("lockDate")]  public string LockDate { get; set; } = string.Empty;
}

/// <summary>SharePoint section of event.&lt;edition&gt;.json -- site / drive / root only.
/// Per-task upload subfolders + notification recipients live on the TASK
/// (sponsor.&lt;edition&gt;.json -&gt; taskSets.*[].upload).</summary>
public sealed class SharePointEditionConfig
{
    [JsonPropertyName("siteUrl")]
    public string SiteUrl { get; set; } = string.Empty;

    [JsonPropertyName("driveName")]
    public string DriveName { get; set; } = string.Empty;

    [JsonPropertyName("rootFolderPath")]
    public string RootFolderPath { get; set; } = string.Empty;
}

/// <summary>Where the event config file lives, mirrors SponsorConfigOptions.</summary>
public sealed class EventConfigOptions
{
    public const string SectionName = "EventConfig";

    public string EventConfigPath { get; set; } =
        "config/event.eldk27.json";
}

/// <summary>
/// Loads <see cref="EventEditionConfig"/> from disk. Returns an empty config
/// (zero values) when the file is missing rather than throwing, so callers
/// can substitute placeholders to blank strings rather than crash.
/// </summary>
public sealed class EventEditionConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public EventEditionConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new EventEditionConfig();
        }

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);

        EventEditionConfig cfg;
        if (doc.RootElement.TryGetProperty("edition", out var edition)
            && edition.ValueKind == JsonValueKind.Object)
        {
            cfg = edition.Deserialize<EventEditionConfig>(Options)
                  ?? new EventEditionConfig();
        }
        else
        {
            cfg = new EventEditionConfig();
        }

        // Pull the SIBLING "placeholders" object into the same config so
        // callers have one resolved map. Drop "_*" doc keys + skip any
        // non-string value (the substitution engine is string-only).
        if (doc.RootElement.TryGetProperty("placeholders", out var ph)
            && ph.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ph.EnumerateObject())
            {
                if (prop.Name.StartsWith("_", System.StringComparison.Ordinal)) continue;
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                cfg.Placeholders[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        // Pull the SIBLING "sharepoint" object. Null when absent.
        if (doc.RootElement.TryGetProperty("sharepoint", out var sp)
            && sp.ValueKind == JsonValueKind.Object)
        {
            cfg.SharePoint = sp.Deserialize<SharePointEditionConfig>(Options);
        }

        // Pull the SIBLING "dates" object so the front-end can show a
        // "key dates" panel without reading the raw JSON itself.
        if (doc.RootElement.TryGetProperty("dates", out var d)
            && d.ValueKind == JsonValueKind.Object)
        {
            cfg.Dates = d.Deserialize<EditionDates>(Options);
        }

        return cfg;
    }
}
