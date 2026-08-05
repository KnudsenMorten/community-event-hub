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
    /// §270 — on/off switch for the attendee "fun IT games" quizzes (operator 2026-07-10).
    /// Default <b>false</b>: the <c>/Games</c> nav entry and surface are HIDDEN for attendees
    /// unless an edition explicitly opts in via <c>event.&lt;edition&gt;.json -&gt; attendeeGamesEnabled: true</c>.
    /// </summary>
    [JsonPropertyName("attendeeGamesEnabled")]
    public bool AttendeeGamesEnabled { get; set; }

    /// <summary>
    /// Zoho Backstage sponsor-category NAME → sponsorship_type id (pinned). Zoho's
    /// live /sponsorship_types endpoint returns 400 "No Sponsorship Categories
    /// Available" on this account, so the provision/create flow resolves the id
    /// from this map (e.g. "Diamond sponsors" → "14880000003485509").
    /// </summary>
    [JsonPropertyName("zohoSponsorCategoryIds")]
    public Dictionary<string, string> ZohoSponsorCategoryIds { get; set; } = new();

    /// <summary>
    /// Pinned Zoho BOOTH (exhibitor) category ids, keyed by lower-case booth tier
    /// (platinum/diamond/gold/feature). Zoho's `POST …/exhibitors` REQUIRES an
    /// `exhibitor_category_id` (else HTTP 400 "Booth category ID is required"); these
    /// ids are pinned in `zohoBoothCategoryIds` in the integrations config so we never
    /// depend on a runtime API lookup. See REQUIREMENTS §41a.
    /// </summary>
    [JsonPropertyName("zohoBoothCategoryIds")]
    public Dictionary<string, string> ZohoBoothCategoryIds { get; set; } = new();

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

    /// <summary>
    /// §299.8/b7 — the session-length QUICK-PICK list from the SIBLING
    /// <c>sessionLengths</c> array (label + integer minutes, e.g. 15/20/30/40/45/
    /// 50/60/420). Per-edition CONFIG (a closed length enum has already failed
    /// twice); the GUI offers these picks but accepts any positive integer up to
    /// <see cref="SessionLengthMaxMinutes"/>. Empty when the section is absent.
    /// </summary>
    [JsonIgnore]
    public List<SessionLengthOption> SessionLengths { get; set; } = new();

    /// <summary>
    /// §299.8/b7 — inclusive upper bound for a CUSTOM session length in minutes
    /// (sibling scalar <c>sessionLengthMaxMinutes</c>). Missing/invalid config
    /// falls back to the shipped default 600 (so the 420 full-day always passes).
    /// </summary>
    [JsonIgnore]
    public int SessionLengthMaxMinutes { get; set; } = DefaultSessionLengthMaxMinutes;

    /// <summary>The shipped default for <see cref="SessionLengthMaxMinutes"/>.</summary>
    public const int DefaultSessionLengthMaxMinutes = 600;

    /// <summary>
    /// §447 — Zoho <c>ticket_class_id</c>(s) that grant Master Class (pre-day) access, from the
    /// SIBLING <c>masterClassTickets.twoDayClassIds</c> array. AUTHORITATIVE and rename-proof: the
    /// class NAME is a label the operator can edit, the id is not. Empty ⇒ the name markers decide
    /// (unchanged pre-§447 behaviour).
    /// </summary>
    [JsonIgnore]
    public List<string> MasterClassTwoDayClassIds { get; set; } = new();

    /// <summary>
    /// §447 — per-edition override of the ticket-class NAME markers
    /// (<c>masterClassTickets.nameMarkers</c>). Used for historic orders and any ticket without a
    /// class id. Empty ⇒ <see cref="Domain.MasterClassTicketPolicy.DefaultMarkers"/>.
    /// </summary>
    [JsonIgnore]
    public List<string> MasterClassNameMarkers { get; set; } = new();

    /// <summary>
    /// §299.8/b7 — the configured audience-level list from the SIBLING
    /// <c>sessionLevels</c> array (label + NUMERIC code, e.g. Advanced/300,
    /// Expert/400, Black Belt/500). Sorting/comparison is ALWAYS by the numeric
    /// code, never alphabetically. Empty when the section is absent.
    /// </summary>
    [JsonIgnore]
    public List<SessionLevelOption> SessionLevels { get; set; } = new();

    /// <summary>
    /// §299.6/b5 — the per-edition room REGISTRY from the SIBLING
    /// <c>sessionRooms</c> array: venue rooms (name + floor + capacity, per-day
    /// records via <c>preDay</c>) and expo locations (free-form name, capacity
    /// NULL, <c>expo: true</c>). Config only — no DB, no FK;
    /// <c>Session.Room</c> stays a free-form string validated warn-only against
    /// this list. Empty when the section is absent (validation then stays quiet).
    /// </summary>
    [JsonIgnore]
    public List<SessionRoomOption> SessionRooms { get; set; } = new();
}

/// <summary>One session-length quick-pick (§299.8/b7): display label + integer minutes.</summary>
public sealed class SessionLengthOption
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("minutes")]
    public int Minutes { get; set; }
}

/// <summary>
/// One configured audience level (§299.8/b7): display label + NUMERIC code
/// (Advanced 300 / Expert 400 / Black Belt 500 for this edition). All level
/// sorting/comparison uses <see cref="Code"/>, never the label alphabetically.
/// </summary>
public sealed class SessionLevelOption
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    public int Code { get; set; }
}

/// <summary>
/// One registry entry from <c>sessionRooms</c> (§299.6/b5). A VENUE room carries
/// a parsed-out floor + capacity (and <see cref="PreDay"/> marks the "-MC"
/// pre-day set — the same physical room may appear per-day with a different
/// capacity as two distinct entries). An EXPO location (<see cref="Expo"/>) is a
/// free-form name whose <see cref="Capacity"/> is NULL by design — all
/// capacity-based logic must tolerate that null (never default it to 0).
/// </summary>
public sealed class SessionRoomOption
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Floor label for a venue room (e.g. "0", "1", "0-1"); null for expo.</summary>
    [JsonPropertyName("floor")]
    public string? Floor { get; set; }

    /// <summary>Seat capacity; NULL for expo locations (tolerated everywhere, never 0).</summary>
    [JsonPropertyName("capacity")]
    public int? Capacity { get; set; }

    /// <summary>True for the "-MC" pre-day (master-class) room records.</summary>
    [JsonPropertyName("preDay")]
    public bool PreDay { get; set; }

    /// <summary>True for an expo location (free-form name, no floor, null capacity).</summary>
    [JsonPropertyName("expo")]
    public bool Expo { get; set; }
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

    // 🔒 §768.14 — volunteerPhotoFolderPath and speakerPhotoFolderPath are GONE. They are registry
    // keys now: DocLibraryPaths.VolunteerPhotos and DocLibraryPaths.SpeakerPhotos.
    //
    // The speaker one especially had to move. §764 established "speaker photos are in 1 place only",
    // and this config key was that place — but THREE separate code paths looked it up independently
    // (the sponsor upload, the archive job, the read proxy). Three lookups of one string is one
    // edit away from being two places again, which is the exact regression §764 was raised to fix.
    // One key, resolved once, cannot drift.

    // 🔒 §768.10 D7 / §768.14 — logoCollectionFolderPath is GONE, and has NO successor key. The
    // collection copy is retired rather than migrated: Sponsors/Logo/Web is already the one place
    // every sponsor logo lives, holding one current file per sponsor under a machine-readable name.
    // A second copy under a third naming convention is what §767 caught a reader matching instead
    // of the real upload — silently, for four production runs.

    // --- Sponsor Company Details uploads (operator 2026-06-24) ----------------
    //
    // 🔒 §768.14 — the five FOLDER keys that lived here are GONE:
    //   logoSoMeBrandingFolderPath · logoPrintFolderPath · logoZohoFolderPath
    //   exhibitorWallFolderPath    · boothCollateralFolderPath
    //
    // They are now registry keys (DocLibraryPaths.SponsorLogoWeb / SponsorLogoPrint /
    // SponsorExhibitorWall / SponsorBoothCollateral) resolved against the ONE configured root, which
    // is what lets PROD and DEV differ in a single setting. A path defined in two rival systems is
    // how they drifted apart in the first place (§768). logoZoho has no successor at all — §6.7
    // merged the lead-system logo into Logo/Web.
    //
    // What legitimately REMAINS below is not a path: who gets told about an upload.

    /// <summary>Recipients notified when a sponsor uploads a web / print / wall file.</summary>
    [JsonPropertyName("sponsorUploadNotify")]
    public List<string> SponsorUploadNotify { get; set; } = new();
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
    /// <summary>
    /// §447 — read a string array from a config object, dropping blanks and trimming. Missing or
    /// wrong-typed ⇒ empty list, so a malformed block degrades to "not configured" rather than
    /// throwing during startup config load.
    /// </summary>
    private static List<string> StringArray(JsonElement obj, string propertyName)
    {
        var result = new List<string>();
        if (!obj.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var v = item.GetString();
            if (!string.IsNullOrWhiteSpace(v)) result.Add(v.Trim());
        }
        return result;
    }

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

        // §447 — the SIBLING "masterClassTickets" object: which Zoho ticket class grants Master
        // Class (pre-day) access. Absent ⇒ empty lists ⇒ MasterClassTicketPolicy falls back to its
        // built-in name markers, i.e. behaviour before §447 is unchanged.
        if (doc.RootElement.TryGetProperty("masterClassTickets", out var mct)
            && mct.ValueKind == JsonValueKind.Object)
        {
            cfg.MasterClassTwoDayClassIds = StringArray(mct, "twoDayClassIds");
            cfg.MasterClassNameMarkers = StringArray(mct, "nameMarkers");
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

        // Pull the SIBLING "zohoBoothCategoryIds" map (tier → exhibitor category id).
        if (doc.RootElement.TryGetProperty("zohoBoothCategoryIds", out var zb)
            && zb.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in zb.EnumerateObject())
                if (!prop.Name.StartsWith("_") && prop.Value.ValueKind == JsonValueKind.String)
                    cfg.ZohoBoothCategoryIds[prop.Name.ToLowerInvariant()] = prop.Value.GetString() ?? string.Empty;
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

        // §299.8/b7: the SIBLING "sessionLengths" quick-pick array. Defensively
        // drop garbage rows (blank label or non-positive minutes) so the forms
        // never render an empty pick.
        if (doc.RootElement.TryGetProperty("sessionLengths", out var sl)
            && sl.ValueKind == JsonValueKind.Array)
        {
            cfg.SessionLengths = (sl.Deserialize<List<SessionLengthOption>>(Options)
                                  ?? new List<SessionLengthOption>())
                .Where(x => x is not null
                            && !string.IsNullOrWhiteSpace(x.Label)
                            && x.Minutes > 0)
                .ToList();
        }

        // §299.8/b7: the SIBLING "sessionLengthMaxMinutes" scalar; invalid or
        // missing keeps the shipped default (600) so 420 always validates.
        if (doc.RootElement.TryGetProperty("sessionLengthMaxMinutes", out var maxMin)
            && maxMin.ValueKind == JsonValueKind.Number
            && maxMin.TryGetInt32(out var maxMinutes)
            && maxMinutes > 0)
        {
            cfg.SessionLengthMaxMinutes = maxMinutes;
        }

        // §299.8/b7: the SIBLING "sessionLevels" array (label + numeric code).
        // Rows without a label or a positive code are dropped.
        if (doc.RootElement.TryGetProperty("sessionLevels", out var lv)
            && lv.ValueKind == JsonValueKind.Array)
        {
            cfg.SessionLevels = (lv.Deserialize<List<SessionLevelOption>>(Options)
                                 ?? new List<SessionLevelOption>())
                .Where(x => x is not null
                            && !string.IsNullOrWhiteSpace(x.Label)
                            && x.Code > 0)
                .ToList();
        }

        // §299.6/b5: the SIBLING "sessionRooms" registry. Only the NAME is
        // mandatory (expo entries have no floor and a null capacity BY DESIGN —
        // never coerce that null to 0).
        if (doc.RootElement.TryGetProperty("sessionRooms", out var rooms)
            && rooms.ValueKind == JsonValueKind.Array)
        {
            cfg.SessionRooms = (rooms.Deserialize<List<SessionRoomOption>>(Options)
                                ?? new List<SessionRoomOption>())
                .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Name))
                .ToList();
        }

        return cfg;
    }
}
