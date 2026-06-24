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
    /// Zoho Backstage sponsor-category NAME → sponsorship_type id (pinned). Zoho's
    /// live /sponsorship_types endpoint returns 400 "No Sponsorship Categories
    /// Available" on this account, so the provision/create flow resolves the id
    /// from this map (e.g. "Diamond sponsors" → "14880000003485509").
    /// </summary>
    [JsonPropertyName("zohoSponsorCategoryIds")]
    public Dictionary<string, string> ZohoSponsorCategoryIds { get; set; } = new();

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
    /// Optional <c>volunteer</c> block — volunteer-specific edition facts, e.g.
    /// EXTRA availability days (move-in / packing / setup days that fall outside
    /// the public event range) shown on My Availability + the sign-up wizard.
    /// Null when the section is absent.
    /// </summary>
    [JsonIgnore]
    public VolunteerEditionConfig? Volunteer { get; set; }

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

    /// <summary>
    /// Optional <c>ticketSale</c> block loaded from
    /// <c>event.&lt;edition&gt;.json -&gt; ticketSale</c> (a SIBLING of
    /// <c>edition</c>). Drives the site-wide topbar ticket banner so the
    /// "ticket sale starts &lt;date&gt; &lt;time&gt;" copy can never drift out of
    /// the config and auto-switches once the sale opens. Null when the section
    /// is absent — the topbar then falls back to its static resx literal.
    /// </summary>
    [JsonIgnore]
    public TicketSaleConfig? TicketSale { get; set; }
}

/// <summary>
/// The site-wide topbar ticket-banner facts pulled from
/// <c>event.&lt;edition&gt;.json -&gt; ticketSale</c>. Pure operator config so
/// the topbar copy (previously the hardcoded <c>Layout.TicketInfo</c> resx
/// literal that silently went stale — read "2028" once) is driven from the
/// active edition. The <see cref="TicketBannerBuilder"/> turns this + a clock
/// into what the topbar renders; the timezone for "before/after open" is the
/// edition's own (<c>dates.timezone</c>), reusing <see cref="EventLocalTime"/>.
/// </summary>
public sealed class TicketSaleConfig
{
    /// <summary>
    /// Master on/off switch. <c>false</c> ⇒ the topbar shows nothing
    /// config-driven (and the layout keeps its static fallback). Default true
    /// so a present block is live unless explicitly disabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When the sale opens, as an ISO-8601 local wall-clock datetime WITHOUT a
    /// zone offset (e.g. <c>2026-08-11T08:00:00</c>). It is interpreted in the
    /// edition timezone (<c>dates.timezone</c>) so an operator writes plain
    /// Danish wall time and never juggles UTC. Blank ⇒ the banner can't compute
    /// a before/after state and falls back to the static literal.
    /// </summary>
    [JsonPropertyName("opensAtLocal")]
    public string OpensAtLocal { get; set; } = string.Empty;

    /// <summary>
    /// Optional public ticket URL the banner links to once the sale is open
    /// (e.g. the Backstage ticket page). Blank ⇒ no link is rendered.
    /// </summary>
    [JsonPropertyName("ticketUrl")]
    public string TicketUrl { get; set; } = string.Empty;

    /// <summary>
    /// What the topbar does AFTER the open moment passes. <c>"onsale"</c>
    /// (default) shows a "tickets on sale" message (a link when
    /// <see cref="TicketUrl"/> is set); <c>"hide"</c> removes the banner once
    /// the sale has opened (e.g. when ticketing moves entirely to the event
    /// site). Case-insensitive; any unknown value is treated as "onsale".
    /// </summary>
    [JsonPropertyName("afterOpen")]
    public string AfterOpen { get; set; } = "onsale";
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
    // NOTE: the role-tagged KEY-DATES / schedule now lives in the ScheduleEntries DB
    // table (organizer-editable at /Organizer/Schedule, seeded from a 6-day default).
    // It is no longer a static edition-config list.
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

    /// <summary>
    /// Optional drive-relative folder where every volunteer's uploaded PHOTO lands
    /// (anonymous sign-up wizard, operator 2026-06-23). Same site + drive. Empty
    /// disables the photo step's upload.
    /// </summary>
    [JsonPropertyName("volunteerPhotoFolderPath")]
    public string VolunteerPhotoFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Optional drive-relative folder where every sponsor's uploaded LOGO file is
    /// ALSO copied (collected) so the organizers have all logos in one place
    /// (operator 2026-06-23). On the same site + drive as the per-company upload
    /// folders. Each copy is named <c>{CompanyName} - {fileName}</c> so logos from
    /// different sponsors never collide and a re-upload overwrites cleanly. Empty
    /// disables the collection copy (only the per-company folder is written).
    /// </summary>
    [JsonPropertyName("logoCollectionFolderPath")]
    public string LogoCollectionFolderPath { get; set; } = string.Empty;

    // --- Sponsor Company Details uploads (operator 2026-06-24) ----------------
    // Drive-relative folders for the versioned logo / exhibitor-wall uploads on
    // /Sponsor/CompanyDetails. Same site + drive. Empty disables that upload button.
    [JsonPropertyName("logoSoMeBrandingFolderPath")]
    public string LogoSoMeBrandingFolderPath { get; set; } = string.Empty;

    [JsonPropertyName("logoPrintFolderPath")]
    public string LogoPrintFolderPath { get; set; } = string.Empty;

    [JsonPropertyName("logoZohoFolderPath")]
    public string LogoZohoFolderPath { get; set; } = string.Empty;

    [JsonPropertyName("exhibitorWallFolderPath")]
    public string ExhibitorWallFolderPath { get; set; } = string.Empty;

    [JsonPropertyName("boothCollateralFolderPath")]
    public string BoothCollateralFolderPath { get; set; } = string.Empty;

    /// <summary>Recipients notified when a sponsor uploads a SoMe / print / wall file.</summary>
    [JsonPropertyName("sponsorUploadNotify")]
    public List<string> SponsorUploadNotify { get; set; } = new();

    /// <summary>Recipients notified when a sponsor uploads the Zoho lead-system logo.</summary>
    [JsonPropertyName("sponsorUploadNotifyZoho")]
    public List<string> SponsorUploadNotifyZoho { get; set; } = new();
}

/// <summary>Volunteer section of event.&lt;edition&gt;.json (sibling of <c>edition</c>).</summary>
public sealed class VolunteerEditionConfig
{
    /// <summary>
    /// Extra availability days that fall OUTSIDE the public event date range
    /// (move-in, logistics, packing, setup) but that volunteers can still mark
    /// availability for. Merged into My Availability + the sign-up wizard.
    /// </summary>
    [JsonPropertyName("extraAvailabilityDays")]
    public List<VolunteerExtraDay> ExtraAvailabilityDays { get; set; } = new();
}

/// <summary>One extra volunteer availability day (outside the public event range).</summary>
public sealed class VolunteerExtraDay
{
    /// <summary>ISO yyyy-MM-dd.</summary>
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    /// <summary>Display label, e.g. "Packing day (9–14)".</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
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
        path = ConfigPaths.Resolve(path);
        if (!File.Exists(path))
        {
            return new EventEditionConfig();
        }

        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Load the shipped default from <paramref name="path"/> then DEEP-MERGE a
    /// per-edition <paramref name="overrideJson"/> fragment on top (HYBRID config
    /// model — see <see cref="JsonDeepMerge"/>). A null/blank/invalid override is
    /// ignored and the result is byte-for-byte identical to <see cref="Load(string)"/>
    /// (fail-safe to the shipped default — never throws on a bad override). When
    /// the file itself is missing the shipped default is empty and the override
    /// (if any) is applied on top of an empty object so a fully-specified
    /// override still produces config.
    /// </summary>
    public EventEditionConfig Load(string path, string? overrideJson)
    {
        if (string.IsNullOrWhiteSpace(overrideJson))
        {
            return Load(path); // common path: no override, unchanged behaviour.
        }

        path = ConfigPaths.Resolve(path);
        var defaultJson = File.Exists(path) ? File.ReadAllText(path) : "{}";
        return Parse(JsonDeepMerge.Merge(defaultJson, overrideJson));
    }

    /// <summary>
    /// Parse a (possibly already override-merged) event-config JSON document into
    /// an <see cref="EventEditionConfig"/>. The parsing/sanitization logic is the
    /// single source shared by both <see cref="Load(string)"/> overloads.
    /// </summary>
    private static EventEditionConfig Parse(string json)
    {
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

        // Pull the SIBLING "zohoSponsorCategoryIds" map (name → id). Underscore
        // keys are documentation and skipped.
        if (doc.RootElement.TryGetProperty("zohoSponsorCategoryIds", out var zc)
            && zc.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in zc.EnumerateObject())
                if (!prop.Name.StartsWith("_") && prop.Value.ValueKind == JsonValueKind.String)
                    cfg.ZohoSponsorCategoryIds[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }

        // Pull the SIBLING "volunteer" object (extra availability days, etc.).
        if (doc.RootElement.TryGetProperty("volunteer", out var vol)
            && vol.ValueKind == JsonValueKind.Object)
        {
            var vc = vol.Deserialize<VolunteerEditionConfig>(Options) ?? new VolunteerEditionConfig();
            // Drop garbage extra-day rows (need a parseable date).
            vc.ExtraAvailabilityDays = (vc.ExtraAvailabilityDays ?? new List<VolunteerExtraDay>())
                .Where(x => x is not null && DateOnly.TryParse(x.Date, out _))
                .ToList();
            cfg.Volunteer = vc;
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

        // Pull the SIBLING "ticketSale" object that drives the site-wide topbar
        // ticket banner. Null when absent so the layout keeps its static
        // fallback literal (additive — nothing breaks if the block is missing).
        if (doc.RootElement.TryGetProperty("ticketSale", out var ts)
            && ts.ValueKind == JsonValueKind.Object)
        {
            cfg.TicketSale = ts.Deserialize<TicketSaleConfig>(Options);
        }

        return cfg;
    }
}
