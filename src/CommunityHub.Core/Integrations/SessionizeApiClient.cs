using System.Text.Json;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// The Sessionize v2 "view" endpoint a pull targets. Each maps to a section of
/// the embed/JSON API: <c>/api/v2/&lt;endpoint-id&gt;/view/&lt;section&gt;</c>.
/// For the speaker import the hub uses <see cref="Speakers"/> (a flat speaker
/// array) by default; <see cref="All"/> returns a richer object with a nested
/// <c>speakers</c> array and is supported as a fallback.
/// </summary>
public enum SessionizeView
{
    All,
    Sessions,
    Speakers,
    SpeakerWall,
    GridSmart,
}

/// <summary>
/// Sessionize v2 view-API settings. The endpoint id is ordinary operator
/// configuration (NOT a secret) - it is bound from non-secret config
/// (<c>integrations.&lt;edition&gt;.json → sessionize</c> and/or the gitignored
/// per-edition <c>config/sessionize.&lt;edition&gt;.custom.json</c>).
/// </summary>
public sealed class SessionizeApiOptions
{
    public const string SectionName = "Sessionize";

    public bool Enabled { get; set; }

    /// <summary>
    /// Base of the Sessionize API. Almost always the default; exposed only so a
    /// test/mirror host can be pointed at. No trailing <c>/api/v2</c> - the
    /// client appends the version + path.
    /// </summary>
    public string BaseUrl { get; set; } = "https://sessionize.com";

    /// <summary>
    /// The Sessionize "view" endpoint id (a.k.a. the API code). This is plain
    /// operator configuration, NOT a secret. It is bound at runtime from
    /// non-secret config (<c>integrations.&lt;edition&gt;.json → sessionize</c>
    /// and/or the gitignored <c>config/sessionize.&lt;edition&gt;.custom.json</c>).
    /// The real per-edition id stays OUT of the public mirror (private /
    /// gitignored config), but it is ordinary config, not a Key Vault secret.
    /// Example shape only: <c>q1w2e3r4</c>.
    /// </summary>
    public string EndpointId { get; set; } = string.Empty;

    /// <summary>
    /// Which view to pull speakers from. Default <see cref="SessionizeView.Speakers"/>.
    /// </summary>
    public SessionizeView View { get; set; } = SessionizeView.Speakers;
}

/// <summary>
/// Read-only Sessionize v2 view-API client. Pulls speaker JSON from one of the
/// configured event's view endpoints and maps each speaker into the existing
/// <see cref="SessionizeSpeaker"/> shape, so the API path drives the SAME
/// import semantics as the legacy Excel upload (match on email, never overwrite
/// roles, report skipped rows).
///
/// The endpoint id is ordinary operator configuration (NOT a secret); it is
/// bound from non-secret config (<see cref="SessionizeApiOptions.EndpointId"/>).
/// The real per-edition id stays out of the public mirror via private /
/// gitignored config, but it is not a Key Vault secret.
///
/// Email handling: the public Sessionize JSON does NOT include emails by
/// default. The organizer must enable the "speaker emails" advanced field on
/// the API view (required, or every speaker is skipped - see README/DESIGN
/// how-to); once enabled, Sessionize emits an <c>email</c> property on each
/// speaker. Email is the import match key and is mandatory: a speaker with no
/// email is skipped and reported as a warning (a participant needs an email to
/// log in) - exactly like the Excel parser. The email is then stored as a
/// normal string in the <c>Participant.Email</c> column (not encrypted, not a
/// secret).
/// </summary>
public sealed class SessionizeApiClient
{
    private readonly HttpClient _http;
    private readonly SessionizeApiOptions _options;

    public SessionizeApiClient(HttpClient http, SessionizeApiOptions options)
    {
        _http = http;
        _options = options;

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CommunityHub/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>
    /// Fetch + parse speakers from the configured Sessionize view endpoint.
    /// Never throws for a bad response or bad data - problems come back in the
    /// result, matching <c>SessionizeExcelParser.Parse</c>.
    /// </summary>
    public async Task<SessionizeParseResult> FetchSpeakersAsync(
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.EndpointId))
        {
            return new SessionizeParseResult(
                Array.Empty<SessionizeSpeaker>(), Array.Empty<string>(),
                "Sessionize endpoint id is not configured. Set "
                + "Sessionize:EndpointId in integrations.<edition>.json "
                + "(sessionize.endpointId) or the gitignored "
                + "config/sessionize.<edition>.custom.json.");
        }

        string json;
        try
        {
            var url = BuildUrl(_options.View);
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new SessionizeParseResult(
                    Array.Empty<SessionizeSpeaker>(), Array.Empty<string>(),
                    $"Sessionize API returned HTTP {(int)resp.StatusCode} "
                    + $"({resp.ReasonPhrase}). Check the endpoint id and that "
                    + "the API view is enabled.");
            }
            json = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            return new SessionizeParseResult(
                Array.Empty<SessionizeSpeaker>(), Array.Empty<string>(),
                $"Could not reach the Sessionize API: {ex.Message}");
        }

        return ParseSpeakers(json);
    }

    /// <summary>
    /// Build the view URL: <c>{BaseUrl}/api/v2/{endpointId}/view/{view}</c>.
    /// </summary>
    public string BuildUrl(SessionizeView view) =>
        $"{_options.BaseUrl.TrimEnd('/')}/api/v2/" +
        $"{Uri.EscapeDataString(_options.EndpointId)}/view/{view}";

    /// <summary>
    /// Parse a Sessionize view JSON document into <see cref="SessionizeSpeaker"/>
    /// rows. Accepts either the <c>Speakers</c>-view shape (a top-level speaker
    /// array) or the <c>All</c>-view shape (an object with a nested
    /// <c>speakers</c> array). Public + static so it is unit-testable without a
    /// network call.
    /// </summary>
    public static SessionizeParseResult ParseSpeakers(string json)
    {
        var speakers = new List<SessionizeSpeaker>();
        var warnings = new List<string>();
        // Dedupe on email; keep the first non-empty value for each field.
        var seen = new Dictionary<string, SessionizeSpeaker>(
            StringComparer.OrdinalIgnoreCase);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            return new SessionizeParseResult(
                speakers, warnings,
                $"The Sessionize response was not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Locate the speaker array. Speakers/SpeakerWall views return a
            // top-level array; the All view returns an object carrying a
            // "speakers" array.
            JsonElement speakerArray;
            if (root.ValueKind == JsonValueKind.Array)
            {
                speakerArray = root;
            }
            else if (root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("speakers", out var sp)
                     && sp.ValueKind == JsonValueKind.Array)
            {
                speakerArray = sp;
            }
            else
            {
                return new SessionizeParseResult(
                    speakers, warnings,
                    "The Sessionize response had no speakers array. Use the "
                    + "Speakers or All view and ensure speakers are published.");
            }

            var index = 0;
            foreach (var s in speakerArray.EnumerateArray())
            {
                index++;
                var parsed = ParseOne(s);

                if (string.IsNullOrWhiteSpace(parsed.Email))
                {
                    var name = $"{parsed.FirstName} {parsed.LastName}".Trim();
                    warnings.Add(string.IsNullOrEmpty(name)
                        ? $"Speaker #{index}: skipped - no email address. "
                          + "Enable the 'speaker emails' advanced field on the "
                          + "Sessionize API endpoint."
                        : $"Speaker '{name}': skipped - no email address. "
                          + "Enable the 'speaker emails' advanced field on the "
                          + "Sessionize API endpoint.");
                    continue;
                }

                seen[parsed.Email] = seen.TryGetValue(parsed.Email, out var prev)
                    ? Merge(prev, parsed)
                    : parsed;
            }
        }

        speakers.AddRange(seen.Values);
        return new SessionizeParseResult(speakers, warnings, null);
    }

    /// <summary>
    /// Fetch + parse SESSIONS from the configured Sessionize view endpoint. Pulls
    /// from the <c>All</c> view (which carries both speakers and sessions) so a
    /// single config drives both imports; never throws for a bad response or bad
    /// data - problems come back in the result, matching <see cref="FetchSpeakersAsync"/>.
    /// </summary>
    public async Task<SessionizeSessionsParseResult> FetchSessionsAsync(
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.EndpointId))
        {
            return new SessionizeSessionsParseResult(
                Array.Empty<SessionizeSession>(), Array.Empty<string>(),
                "Sessionize endpoint id is not configured. Set "
                + "Sessionize:EndpointId in integrations.<edition>.json "
                + "(sessionize.endpointId) or the gitignored "
                + "config/sessionize.<edition>.custom.json.");
        }

        string json;
        try
        {
            // The flat "Sessions" view returns a grouped session list; the "All"
            // view carries the same sessions array. Either works for the parser;
            // pull the same view the speakers do not use a top-level sessions
            // array for, so prefer the configured view when it is Sessions/All,
            // otherwise fall back to the sessions-bearing "All" view.
            var view = _options.View is SessionizeView.Sessions or SessionizeView.All
                ? _options.View
                : SessionizeView.All;
            var url = BuildUrl(view);
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new SessionizeSessionsParseResult(
                    Array.Empty<SessionizeSession>(), Array.Empty<string>(),
                    $"Sessionize API returned HTTP {(int)resp.StatusCode} "
                    + $"({resp.ReasonPhrase}). Check the endpoint id and that "
                    + "the API view is enabled.");
            }
            json = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            return new SessionizeSessionsParseResult(
                Array.Empty<SessionizeSession>(), Array.Empty<string>(),
                $"Could not reach the Sessionize API: {ex.Message}");
        }

        return ParseSessions(json);
    }

    /// <summary>
    /// Parse a Sessionize view JSON document into <see cref="SessionizeSession"/>
    /// rows. Accepts both shapes:
    ///  - the <c>All</c> view (an object with a top-level <c>sessions</c> array and
    ///    a <c>categories</c> array used to label tracks), and
    ///  - the grouped <c>Sessions</c>/<c>GridSmart</c> view (a top-level array of
    ///    group objects, each with a nested <c>sessions</c> array; group rooms/dates
    ///    are flattened down onto each session).
    /// Public + static so it is unit-testable without a network call.
    /// </summary>
    public static SessionizeSessionsParseResult ParseSessions(string json)
    {
        var sessions = new List<SessionizeSession>();
        var warnings = new List<string>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            return new SessionizeSessionsParseResult(
                sessions, warnings,
                $"The Sessionize response was not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            // category id -> category item name, so a session's categoryItems
            // can be resolved to a human track label (All view only).
            var categoryItemNames = BuildCategoryItemNames(root);

            // Dedupe on the Sessionize session id, keeping the first occurrence.
            var seen = new Dictionary<string, SessionizeSession>(
                StringComparer.OrdinalIgnoreCase);

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("sessions", out var flat)
                && flat.ValueKind == JsonValueKind.Array)
            {
                // All view: a flat sessions array.
                foreach (var sess in flat.EnumerateArray())
                {
                    AddSession(ParseOneSession(sess, categoryItemNames), seen);
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                // Grouped Sessions / GridSmart view: array of groups, each with a
                // nested sessions array. Some groups carry a "room"/"groupName".
                foreach (var group in root.EnumerateArray())
                {
                    if (!group.TryGetProperty("sessions", out var grouped)
                        || grouped.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    foreach (var sess in grouped.EnumerateArray())
                    {
                        AddSession(ParseOneSession(sess, categoryItemNames), seen);
                    }
                }
            }
            else
            {
                return new SessionizeSessionsParseResult(
                    sessions, warnings,
                    "The Sessionize response had no sessions array. Use the All or "
                    + "Sessions view and ensure sessions are published.");
            }

            foreach (var s in seen.Values)
            {
                if (string.IsNullOrWhiteSpace(s.SessionizeId))
                {
                    warnings.Add(
                        $"Session '{s.Title}': skipped - no Sessionize id.");
                    continue;
                }
                sessions.Add(s);
            }
        }

        return new SessionizeSessionsParseResult(sessions, warnings, null);
    }

    private static void AddSession(
        SessionizeSession s, Dictionary<string, SessionizeSession> seen)
    {
        if (string.IsNullOrWhiteSpace(s.SessionizeId))
        {
            // Keep emptyish ids out of the dedupe map (they surface as warnings).
            seen[Guid.NewGuid().ToString()] = s;
            return;
        }
        if (!seen.ContainsKey(s.SessionizeId)) seen[s.SessionizeId] = s;
    }

    private static SessionizeSession ParseOneSession(
        JsonElement sess, IReadOnlyDictionary<string, string> categoryItemNames)
    {
        var speakerIds = new List<string>();
        if (sess.TryGetProperty("speakers", out var sp)
            && sp.ValueKind == JsonValueKind.Array)
        {
            foreach (var spk in sp.EnumerateArray())
            {
                // Two shapes: a bare id string, or an object { id, name }.
                if (spk.ValueKind == JsonValueKind.String)
                {
                    var id = spk.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(id)) speakerIds.Add(id);
                }
                else if (spk.ValueKind == JsonValueKind.Object)
                {
                    var id = GetString(spk, "id").Trim();
                    if (!string.IsNullOrEmpty(id)) speakerIds.Add(id);
                }
            }
        }

        // Track: first category item label, if any (All view "categories").
        string? track = null;
        if (sess.TryGetProperty("categoryItems", out var ci)
            && ci.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ci.EnumerateArray())
            {
                var key = item.ValueKind == JsonValueKind.Number
                    ? item.GetRawText()
                    : item.GetString() ?? string.Empty;
                if (categoryItemNames.TryGetValue(key, out var name))
                {
                    track = name;
                    break;
                }
            }
        }

        return new SessionizeSession(
            SessionizeId:     GetString(sess, "id").Trim(),
            Title:            GetString(sess, "title").Trim(),
            Abstract:         NullIfEmpty(GetString(sess, "description")),
            Room:             NullIfEmpty(GetString(sess, "room")),
            Track:            track,
            StartsAt:         GetDateTimeOffset(sess, "startsAt"),
            EndsAt:           GetDateTimeOffset(sess, "endsAt"),
            IsServiceSession: GetBool(sess, "isServiceSession"),
            SpeakerIds:       speakerIds);
    }

    /// <summary>
    /// Build a category-item-id -> name map from the All view's <c>categories</c>
    /// array, so a session's categoryItems ids resolve to a track label. Empty for
    /// the grouped Sessions view (no categories block) - track is then left null.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildCategoryItemNames(
        JsonElement root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("categories", out var cats)
            || cats.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (var cat in cats.EnumerateArray())
        {
            if (!cat.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl)) continue;
                var id = idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetRawText()
                    : idEl.GetString() ?? string.Empty;
                var name = GetString(item, "name").Trim();
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                {
                    map[id] = name;
                }
            }
        }
        return map;
    }

    private static SessionizeSpeaker ParseOne(JsonElement s)
    {
        var email = FirstNonEmpty(
            GetString(s, "email"),
            // Some endpoints surface the email through a question-answer rather
            // than a top-level property; probe a "Speaker Email" style Q/A too.
            EmailFromQuestionAnswers(s)).Trim().ToLowerInvariant();

        // Split links by linkType into the slots the hub stores.
        string? linkedIn = null, twitter = null, blog = null;
        if (s.TryGetProperty("links", out var links)
            && links.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in links.EnumerateArray())
            {
                var url = GetString(link, "url").Trim();
                if (string.IsNullOrEmpty(url)) continue;
                var type = GetString(link, "linkType").Trim();

                switch (type.ToLowerInvariant())
                {
                    case "linkedin":
                        linkedIn ??= url; break;
                    case "twitter":
                    case "x":
                        twitter ??= url; break;
                    case "blog":
                    case "company_website":
                    case "companywebsite":
                    case "website":
                        blog ??= url; break;
                    default:
                        // Fall back to URL host heuristics for unlabelled links.
                        if (linkedIn is null && url.Contains("linkedin.", StringComparison.OrdinalIgnoreCase))
                            linkedIn = url;
                        else if (twitter is null
                                 && (url.Contains("twitter.", StringComparison.OrdinalIgnoreCase)
                                     || url.Contains("//x.com", StringComparison.OrdinalIgnoreCase)))
                            twitter = url;
                        break;
                }
            }
        }

        return new SessionizeSpeaker(
            Email:             email,
            FirstName:         GetString(s, "firstName").Trim(),
            LastName:          GetString(s, "lastName").Trim(),
            TagLine:           NullIfEmpty(GetString(s, "tagLine")),
            Biography:         NullIfEmpty(GetString(s, "bio")),
            Blog:              blog,
            LinkedIn:          linkedIn,
            Twitter:           twitter,
            ProfilePictureUrl: NullIfEmpty(GetString(s, "profilePicture")),
            // The Sessionize speaker id (GUID) links sessions -> this speaker.
            SessionizeId:      GetString(s, "id").Trim());
    }

    /// <summary>
    /// Best-effort email extraction from a speaker's questionAnswers, for
    /// endpoints configured to surface the email as a custom question rather
    /// than the top-level <c>email</c> property. Recognises a question whose
    /// text contains "email".
    /// </summary>
    private static string EmailFromQuestionAnswers(JsonElement s)
    {
        if (!s.TryGetProperty("questionAnswers", out var qas)
            || qas.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var qa in qas.EnumerateArray())
        {
            var q = GetString(qa, "question");
            if (q.Contains("email", StringComparison.OrdinalIgnoreCase))
            {
                var answer = FirstNonEmpty(
                    GetString(qa, "answer"), GetString(qa, "answerValue"));
                if (answer.Contains('@')) return answer;
            }
        }
        return string.Empty;
    }

    private static SessionizeSpeaker Merge(SessionizeSpeaker a, SessionizeSpeaker b) =>
        a with
        {
            FirstName         = NonEmpty(a.FirstName, b.FirstName),
            LastName          = NonEmpty(a.LastName, b.LastName),
            TagLine           = a.TagLine           ?? b.TagLine,
            Biography         = a.Biography         ?? b.Biography,
            Blog              = a.Blog              ?? b.Blog,
            LinkedIn          = a.LinkedIn          ?? b.LinkedIn,
            Twitter           = a.Twitter           ?? b.Twitter,
            ProfilePictureUrl = a.ProfilePictureUrl ?? b.ProfilePictureUrl,
            SessionizeId      = NonEmpty(a.SessionizeId, b.SessionizeId),
        };

    private static string GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static DateTimeOffset? GetDateTimeOffset(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)
            || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var raw = v.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Sessionize emits local wall-clock ("2027-02-04T09:00:00") with no
        // offset; treat it as unspecified rather than fabricating a zone.
        return DateTimeOffset.TryParse(
            raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var dto)
            ? dto
            : null;
    }

    private static bool GetBool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v)
        && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String
                && bool.TryParse(v.GetString(), out var b) && b));

    private static string? NullIfEmpty(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string NonEmpty(string a, string b) =>
        string.IsNullOrWhiteSpace(a) ? b : a;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
