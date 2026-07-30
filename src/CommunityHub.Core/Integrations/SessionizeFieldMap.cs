using System.Text.RegularExpressions;
using static CommunityHub.Core.Integrations.IntegrationFieldMap;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §323 — the SESSIONIZE→CEH instance of the generic <see cref="IntegrationFieldMap"/>
/// engine (CallForSpeakersSystem, INBOUND: Sessionize is the source of speaker/session
/// submissions; CEH mirrors them and then owns the data as master toward Zoho).
///
/// <para><b>Consumed by the importer</b> (<see cref="SessionizeApiClient"/>): the
/// category-group ROUTING KEYWORDS (which group title means Format / Track / Level /
/// Tags) come from THIS map — the JSON rows carry them as
/// <c>"categoryItems: group title contains 'track'"</c> and
/// <see cref="ContainsKeyword"/> extracts the quoted token, so an operator can re-route
/// a renamed Sessionize category group by editing the file, no deploy.</para>
///
/// <para><b>Layer 2:</b> <see cref="MapFilePath"/> is loaded at host startup
/// (<see cref="ApplyMapFile"/>, fail-soft) and OVERRIDES the code defaults below; the
/// code rows remain the fallback so a missing/broken file never takes the import down.</para>
/// </summary>
public static class SessionizeFieldMap
{
    /// <summary>The shipped per-integration map file (SCHEMA v2; the file name carries
    /// the DIRECTION: sessionize-to-ceh).</summary>
    public const string MapFilePath = "config/integrations/sessionize-to-ceh.fieldmap.json";

    private static IReadOnlyDictionary<string, Field>? _sessionOverrides;
    private static IReadOnlyDictionary<string, Field>? _speakerOverrides;

    /// <summary>Apply a loaded map file's rows over the code defaults (called once at
    /// host startup; no-op on null).</summary>
    public static void ApplyMapFile(MapFile? map)
    {
        if (map is null) return;
        _sessionOverrides = map.Fields.TryGetValue("Session", out var se) ? se : null;
        _speakerOverrides = map.Fields.TryGetValue("Speaker", out var sp) ? sp : null;
    }

    /// <summary>Test/diagnostics: clear applied overrides (back to code defaults).</summary>
    public static void ResetOverrides() => _sessionOverrides = _speakerOverrides = null;

    private static Field Se(string sourceField, Field dflt) =>
        _sessionOverrides?.GetValueOrDefault(sourceField) ?? dflt;

    private static Field Sp(string sourceField, Field dflt) =>
        _speakerOverrides?.GetValueOrDefault(sourceField) ?? dflt;

    /// <summary>
    /// SESSION rows (Sessionize <c>sessions[]</c> → CEH Session). For the INBOUND
    /// direction the record's SOURCE side is Sessionize: <see cref="Field.CehField"/>
    /// carries the Sessionize field expression and <see cref="Field.ApiField"/> the CEH
    /// column; <see cref="Field.GuiLabel"/> is the CEH label the value lands on.
    /// </summary>
    public static class Session
    {
        public static Field Title => Se("title", new("title", "Title", "Session title", Direction: Direction.Inbound));
        public static Field Abstract => Se("description", new("description", "Abstract", "Session abstract",
            Kind: FieldKind.MultiLine, Direction: Direction.Inbound));
        public static Field Room => Se("room / roomId → rooms[]", new("room / roomId → rooms[]", "Room", "Room",
            Direction: Direction.Inbound,
            Limitations: "room NAMES must be byte-identical across Sessionize/CEH/Zoho"));
        public static Field Track => Se("categoryItems: group title contains 'track'",
            new("categoryItems: group title contains 'track'", "Track", "Track", Direction: Direction.Inbound));
        public static Field Level => Se("categoryItems: group title contains 'level'",
            new("categoryItems: group title contains 'level'", "Level", "Level", Direction: Direction.Inbound,
                Limitations: "VERBATIM passthrough — 'Advanced (300)', 'Expert (400)', 'Black Belt (500)'"));
        public static Field Tags => Se("categoryItems: group title contains 'tag' (the 'Labels/Tags' multiple-choice, 100+ options)",
            new("categoryItems: group title contains 'tag' (the 'Labels/Tags' multiple-choice, 100+ options)",
                "Tags", "Tags (chips on the session detail)", Kind: FieldKind.Array, Direction: Direction.Inbound,
                Limitations: "1:1 UNBOUNDED — every chosen label kept"));
        public static Field Format => Se("categoryItems: group title contains 'format'",
            new("categoryItems: group title contains 'format'", "LengthMinutes", "Length (minutes)",
                Kind: FieldKind.Integer, Direction: Direction.Inbound, Derivation: "sessionize.lengthMinutes"));
        public static Field Schedule => Se("startsAt / endsAt", new("startsAt / endsAt", "StartsAt / EndsAt", "Schedule",
            Direction: Direction.Inbound,
            Limitations: "event-local Danish wall-clock WITHOUT offset — EventTimezone attaches the correct CET/CEST offset (§305); skipped when IsDateOverridden"));
        public static Field Speakers => Se("speakers[]", new("speakers[]", "SessionSpeakers", "Speakers",
            Kind: FieldKind.Array, Direction: Direction.Inbound));
    }

    /// <summary>SPEAKER rows (Sessionize <c>speakers[]</c> → CEH SpeakerProfile).</summary>
    public static class Speaker
    {
        public static Field FirstName => Sp("firstName", new("firstName", "FirstName", "First name", Direction: Direction.Inbound));
        public static Field LastName => Sp("lastName", new("lastName", "LastName", "Last name", Direction: Direction.Inbound));
        public static Field Tagline => Sp("tagLine", new("tagLine", "Tagline", "Tagline", Direction: Direction.Inbound));
        public static Field Biography => Sp("bio", new("bio", "Biography", "Biography",
            Kind: FieldKind.MultiLine, Direction: Direction.Inbound));
        public static Field Links => Sp("links[] (LinkedIn / Twitter / Blog)",
            new("links[] (LinkedIn / Twitter / Blog)", "LinkedIn / Twitter / Blog", "Bio & links",
                Kind: FieldKind.Object, Direction: Direction.Inbound));
        public static Field Photo => Sp("profilePicture", new("profilePicture", "PhotoUrl", "Photo",
            Kind: FieldKind.Object, Direction: Direction.Inbound));
    }

    /// <summary>
    /// The category-group ROUTING KEYWORD embedded in a row's source expression
    /// ("categoryItems: group title contains 'track'" → "track"). Falls back to
    /// <paramref name="fallback"/> when the row carries no recognisable token, so a
    /// hand-edited file can never silence a facet entirely.
    /// </summary>
    public static string ContainsKeyword(Field row, string fallback)
    {
        var m = Regex.Match(row.CehField ?? string.Empty, @"contains '([^']+)'");
        return m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value)
            ? m.Groups[1].Value.Trim().ToLowerInvariant()
            : fallback;
    }

    /// <summary>The importer's live routing keywords (map-driven, code fallback).</summary>
    public static string FormatKeyword => ContainsKeyword(Session.Format, "format");
    public static string TrackKeyword => ContainsKeyword(Session.Track, "track");
    public static string LevelKeyword => ContainsKeyword(Session.Level, "level");
    public static string TagKeyword => ContainsKeyword(Session.Tags, "tag");
}
