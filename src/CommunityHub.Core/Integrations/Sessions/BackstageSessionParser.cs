using System.Text.Json;

namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// Maps Zoho Backstage v3 JSON (halls + sessions) into the hub's neutral
/// <see cref="SessionizeSession"/> shape so the existing
/// <c>SessionImportService.ImportSessionsAsync</c> consumes Backstage exactly like
/// Sessionize. Pure + static = unit-testable offline (the live fetch is gated on
/// the <c>ZohoBackstage.agenda.READ</c> scope, REQUIREMENTS §6).
///
/// Backstage delivers sessions <b>per agenda day</b> and rooms as <b>halls</b>;
/// each session references its hall by id, resolved here to a room NAME via the
/// halls map. Rooms are left blank when a session has no hall yet ("rooms TBD
/// until defined in Backstage"). Speakers are carried as their Backstage email so
/// the importer links by email → participant (Sessionize stays the speaker source).
/// </summary>
public static class BackstageSessionParser
{
    /// <summary>
    /// Parse a halls response into id → hall-name. Accepts a top-level array or an
    /// object carrying a <c>halls</c> array. Tolerant: bad JSON → empty map.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseHalls(string json)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryArray(json, "halls", out var doc, out var arr)) return map;
        using (doc)
        {
            foreach (var h in arr.EnumerateArray())
            {
                var id = Str(h, "id");
                var name = FirstNonEmpty(Str(h, "name"), Str(h, "title"));
                if (id.Length > 0 && name.Length > 0) map[id] = name;
            }
        }
        return map;
    }

    /// <summary>
    /// Parse a sessions response into <see cref="SessionizeSession"/> rows, resolving
    /// each session's hall id to a room name via <paramref name="hallNameById"/>.
    /// Accepts a top-level array or an object with a <c>sessions</c> array.
    /// </summary>
    public static SessionizeSessionsParseResult ParseSessions(
        string json, IReadOnlyDictionary<string, string>? hallNameById = null)
    {
        var sessions = new List<SessionizeSession>();
        var warnings = new List<string>();
        var halls = hallNameById ?? new Dictionary<string, string>();

        if (!TryArray(json, "sessions", out var doc, out var arr))
            return new SessionizeSessionsParseResult(sessions, warnings,
                "The Backstage response had no sessions array.");

        using (doc)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in arr.EnumerateArray())
            {
                var id = Str(s, "id");
                if (id.Length == 0) { warnings.Add("Session skipped — no id."); continue; }
                if (!seen.Add(id)) continue; // dedupe by id

                var hallId = FirstNonEmpty(Str(s, "hallId"), Str(s, "hall"));
                var room = hallId.Length > 0 && halls.TryGetValue(hallId, out var n) ? n : null;

                sessions.Add(new SessionizeSession(
                    SessionizeId: id,
                    Title: FirstNonEmpty(Str(s, "title"), Str(s, "name")),
                    Abstract: NullIfEmpty(FirstNonEmpty(Str(s, "description"), Str(s, "abstract"))),
                    Room: room,                                   // blank when no hall (rooms TBD)
                    Track: NullIfEmpty(Str(s, "track")),
                    StartsAt: ParseTime(s, "startTime", "startsAt", "startsOn"),
                    EndsAt: ParseTime(s, "endTime", "endsAt", "endsOn"),
                    IsServiceSession: Bool(s, "isServiceSession") || Bool(s, "isBreak"),
                    SpeakerIds: SpeakerKeys(s)));
            }
        }
        return new SessionizeSessionsParseResult(sessions, warnings, null);
    }

    // --- helpers -------------------------------------------------------------

    private static IReadOnlyList<string> SpeakerKeys(JsonElement s)
    {
        // Link by Backstage speaker EMAIL when present (Sessionize is the speaker
        // source), else fall back to the Backstage speaker id.
        var keys = new List<string>();
        if (s.TryGetProperty("speakers", out var sp) && sp.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in sp.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var v = el.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(v)) keys.Add(v);
                }
                else if (el.ValueKind == JsonValueKind.Object)
                {
                    var key = FirstNonEmpty(Str(el, "email"), Str(el, "id"));
                    if (key.Length > 0) keys.Add(key);
                }
            }
        }
        return keys;
    }

    private static bool TryArray(string json, string prop, out JsonDocument doc, out JsonElement arr)
    {
        arr = default;
        try { doc = JsonDocument.Parse(json); }
        catch { doc = JsonDocument.Parse("null"); return false; }
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array) { arr = root; return true; }
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array)
        { arr = a; return true; }
        return false;
    }

    private static DateTimeOffset? ParseTime(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (!e.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(v.GetString(), out var dto)) return dto;
        }
        return null;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!.Trim() : string.Empty;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
        && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    private static string FirstNonEmpty(params string[] xs)
    {
        foreach (var x in xs) if (!string.IsNullOrWhiteSpace(x)) return x.Trim();
        return string.Empty;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
