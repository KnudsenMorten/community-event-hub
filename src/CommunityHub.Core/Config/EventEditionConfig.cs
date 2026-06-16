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

    /// <summary>
    /// Optional <c>resources</c> block loaded from
    /// <c>event.&lt;edition&gt;.json -&gt; resources</c> (a SIBLING of
    /// <c>edition</c>). Drives the shared, read-only <c>/Resources</c> page —
    /// the practical-info / links / downloads organizers maintain entirely in
    /// config (no schema, no migration). Empty when the section is absent, so
    /// the page renders a friendly "nothing here yet" state instead of crashing.
    /// </summary>
    [JsonIgnore]
    public ResourcesConfig Resources { get; set; } = new();
}

/// <summary>
/// The shared-resources content for an edition (<c>/Resources</c>). A small,
/// flat, editorial structure: an optional intro paragraph plus an ordered list
/// of grouped link/download sections. Organizers edit this in
/// <c>event.&lt;edition&gt;.json</c>; nothing here is a secret and nothing is a
/// database row.
/// </summary>
public sealed class ResourcesConfig
{
    /// <summary>Optional lead paragraph shown above the sections. May be empty.</summary>
    [JsonPropertyName("intro")]
    public string Intro { get; set; } = string.Empty;

    /// <summary>Ordered content sections. Empty list = render the empty state.</summary>
    [JsonPropertyName("sections")]
    public List<ResourceSection> Sections { get; set; } = new();

    /// <summary>True when there is no displayable content at all.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Intro)
        && (Sections is null || Sections.TrueForAll(s => s is null || s.Links.Count == 0));
}

/// <summary>One titled group of resource links on the <c>/Resources</c> page.</summary>
public sealed class ResourceSection
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional one-line description under the section title.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("links")]
    public List<ResourceLink> Links { get; set; } = new();
}

/// <summary>A single practical link or download on the <c>/Resources</c> page.</summary>
public sealed class ResourceLink
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>Optional short note shown next to the link.</summary>
    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    /// <summary>True = a downloadable file (PDF etc.); false = a web link. Cosmetic only.</summary>
    [JsonPropertyName("isDownload")]
    public bool IsDownload { get; set; }
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

        // Pull the SIBLING "resources" object that drives the shared
        // /Resources page. Defensively drop empty/garbage entries so the page
        // never renders a blank "•" with no label or a dead empty-href link.
        if (doc.RootElement.TryGetProperty("resources", out var r)
            && r.ValueKind == JsonValueKind.Object)
        {
            var res = r.Deserialize<ResourcesConfig>(Options) ?? new ResourcesConfig();
            res.Sections = (res.Sections ?? new List<ResourceSection>())
                .Where(s => s is not null)
                .Select(s =>
                {
                    s.Links = (s.Links ?? new List<ResourceLink>())
                        .Where(l => l is not null
                                    && !string.IsNullOrWhiteSpace(l.Label)
                                    && !string.IsNullOrWhiteSpace(l.Url))
                        .ToList();
                    return s;
                })
                .Where(s => !string.IsNullOrWhiteSpace(s.Title) || s.Links.Count > 0)
                .ToList();
            cfg.Resources = res;
        }

        return cfg;
    }
}
