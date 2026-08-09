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

    /// <summary>
    /// Sessionize keeps speaker EMAILS off the public Speakers/All view (PII) and
    /// exposes them only through a separate, token-protected side-view
    /// (e.g. <c>.../view/SpeakersEmails?s=&lt;token&gt;</c>) that returns
    /// <c>id, firstName, lastName, email</c>. When this token is set the client
    /// pulls that view too and joins the email onto each speaker by Sessionize
    /// <c>id</c> — so the main view supplies bio/links/sessions and this supplies
    /// the match-key email. Blank ⇒ the client expects the email inline on the
    /// main view (older endpoints with the "speaker emails" field enabled).
    /// SECRET: it gates PII, so it lives in Key Vault / an app setting, never in
    /// committed config (placeholder only).
    /// </summary>
    public string EmailsToken { get; set; } = string.Empty;

    /// <summary>
    /// The view name that carries emails when <see cref="EmailsToken"/> is set.
    /// Default <c>SpeakersEmails</c> (the Sessionize standard secured-email view).
    /// </summary>
    public string EmailsView { get; set; } = "SpeakersEmails";

    /// <summary>
    /// §971 — TRACK RENAMES, APPLIED AT IMPORT. Sessionize track label ⇒ the name CEH stores.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-09: he renamed a track in Zoho — <i>"Data Compliance &amp; Security"</i>
    /// became <i>"Data Compliance"</i> — and wants any Sessionize session on the old label to carry
    /// the new one <b>inside CEH and onward to Zoho</b>.</para>
    ///
    /// <para>🔑 <b>Renamed on the way IN, not on the way out</b>, and that is the whole design. CEH
    /// then holds one name, so the organizer grid, the agenda, the public programme, the graphics and
    /// the Zoho push all say the same thing. <see cref="ZohoClient.TrackNameMap"/> (§2026-07-23) is
    /// the OTHER hop and stays what it is: it rewrites a name only as it is pushed to Backstage,
    /// which leaves CEH showing the Sessionize spelling — right for the two AI tracks, where Zoho
    /// deliberately carries a shorter name, and wrong here, where the rename is the actual fact.</para>
    ///
    /// <para>🔒 <b>EMPTY BY DEFAULT — the labels are EDITION facts, not code.</b> Populated from
    /// <c>integrations.&lt;edition&gt;.json → sessionize.trackAliases</c>, or an app setting
    /// (<c>Sessionize__TrackAliases__&lt;from&gt;</c>). A rename is then a config edit, exactly as
    /// §323 intends for a Sessionize label change; hardcoding ELDK's tracks in Core would break the
    /// evergreen rule and put the next edition's rename in a deploy.</para>
    ///
    /// <para>⚠️ Applies to sessions imported or re-imported AFTER it is set. Sessions already stored
    /// under the old label are corrected by the next sync of those sessions, not retroactively by
    /// setting this alone — check the grid rather than assuming.</para>
    /// </remarks>
    public Dictionary<string, string> TrackAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// §971 — the same map as ONE app-setting value, e.g.
    /// <c>Sessionize__TrackAliasesJson = {"Data Compliance &amp; Security":"Data Compliance"}</c>.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>This exists because the per-key form cannot be set safely here.</b> Config reaches this
    /// app through AZURE APP SETTINGS — there is no <c>AddJsonFile</c>, so
    /// <c>integrations.&lt;edition&gt;.json</c> is reference documentation, not a live source
    /// (verified 2026-08-09: <c>Sessionize__EndpointId</c> is an app setting). The nested form would
    /// therefore need a setting literally named
    /// <c>Sessionize__TrackAliases__Data Compliance &amp; Security</c> — an environment-variable name
    /// containing spaces and an ampersand. One JSON string sidesteps that entirely, and keeps a
    /// rename to a single setting rather than one per track.
    /// ⚠️ Invalid JSON is IGNORED rather than thrown — a malformed alias must not take the import
    /// down — but it is also therefore SILENT, so verify the rename landed on a real session.
    /// </remarks>
    public string TrackAliasesJson { get; set; } = string.Empty;

    private IReadOnlyDictionary<string, string>? _resolvedAliases;

    /// <summary>
    /// The alias map actually used: <see cref="TrackAliases"/> plus anything in
    /// <see cref="TrackAliasesJson"/> (the JSON wins on a clash — it is the deployable one).
    /// </summary>
    /// <remarks>
    /// 🔑 Computed here rather than merged by each host after <c>Bind()</c>. A "remember to call
    /// Normalize()" step in two registrations is precisely the two-copies drift this codebase keeps
    /// getting bitten by — one of them eventually does not.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ResolvedTrackAliases
    {
        get
        {
            if (_resolvedAliases is not null) return _resolvedAliases;

            var merged = new Dictionary<string, string>(TrackAliases, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(TrackAliasesJson))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(TrackAliasesJson);
                    if (parsed is not null)
                    {
                        foreach (var (from, to) in parsed)
                        {
                            if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
                                merged[from.Trim()] = to.Trim();
                        }
                    }
                }
                catch (JsonException) { /* see the ⚠️ above: ignored, never fatal */ }
            }
            return _resolvedAliases = merged;
        }
    }
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
    /// result.
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

        // When configured, pull the token-protected emails side-view and join the
        // address onto each speaker by Sessionize id (the main view omits PII).
        IReadOnlyDictionary<string, string>? emailById = null;
        var emailWarnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.EmailsToken))
        {
            try
            {
                var emailUrl = BuildEmailsUrl();
                using var eresp = await _http.GetAsync(emailUrl, ct);
                if (eresp.IsSuccessStatusCode)
                {
                    var ejson = await eresp.Content.ReadAsStringAsync(ct);
                    emailById = ParseEmailMap(ejson);
                }
                else
                {
                    emailWarnings.Add(
                        $"Sessionize emails view returned HTTP {(int)eresp.StatusCode} "
                        + $"({eresp.ReasonPhrase}). Check Sessionize:EmailsToken / EmailsView; "
                        + "speakers will be skipped without an email.");
                }
            }
            catch (Exception ex)
            {
                emailWarnings.Add(
                    $"Could not reach the Sessionize emails view: {ex.Message}");
            }
        }

        var result = ParseSpeakers(json, emailById);
        return emailWarnings.Count == 0
            ? result
            : result with { Warnings = result.Warnings.Concat(emailWarnings).ToList() };
    }

    /// <summary>
    /// Build the token-protected emails view URL:
    /// <c>{BaseUrl}/api/v2/{endpointId}/view/{EmailsView}?s={EmailsToken}</c>.
    /// </summary>
    public string BuildEmailsUrl() =>
        $"{_options.BaseUrl.TrimEnd('/')}/api/v2/" +
        $"{Uri.EscapeDataString(_options.EndpointId)}/view/" +
        $"{Uri.EscapeDataString(_options.EmailsView)}" +
        $"?s={Uri.EscapeDataString(_options.EmailsToken)}";

    /// <summary>
    /// Parse the Sessionize <c>SpeakersEmails</c> side-view (a top-level array of
    /// <c>{ id, firstName, lastName, email }</c>) into a Sessionize-id → email map.
    /// Tolerant: a bad document yields an empty map, never throws.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseEmailMap(string json)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return map; }
        using (doc)
        {
            var root = doc.RootElement;
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array) arr = root;
            else if (root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("speakers", out var sp)
                     && sp.ValueKind == JsonValueKind.Array) arr = sp;
            else return map;

            foreach (var s in arr.EnumerateArray())
            {
                var id = GetString(s, "id").Trim();
                var email = GetString(s, "email").Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(email))
                    map[id] = email;
            }
        }
        return map;
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
    public static SessionizeParseResult ParseSpeakers(
        string json,
        IReadOnlyDictionary<string, string>? emailById = null)
    {
        var speakers = new List<SessionizeSpeaker>();
        var warnings = new List<string>();
        // Dedupe on email; keep the first non-empty value for each field.
        var seen = new Dictionary<string, SessionizeSpeaker>(
            StringComparer.OrdinalIgnoreCase);
        // §204: email-less speakers that DO carry a Sessionize id — deduped by that
        // id — so the importer can land them in the pre-selection queue instead of
        // dropping them. (Still also reported as a warning, for §189 wording.)
        var emailLess = new Dictionary<string, SessionizeSpeaker>(
            StringComparer.OrdinalIgnoreCase);

        // §189: distinguish the two causes of a missing email. The secured
        // SpeakersEmails view is "readable" iff a non-empty join map was supplied
        // (a token was configured AND at least one speaker email resolved this run).
        // When it IS readable, a speaker with no address simply isn't in that view
        // yet — almost always because they haven't accepted the Sessionize speaker
        // invite — so the "enable / configure EmailsToken" guidance would be wrong.
        var emailsViewReadable = emailById is { Count: > 0 };

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

                // The main Speakers/All view omits email (PII); fill it from the
                // token-protected emails side-view, joined on the Sessionize id.
                if (string.IsNullOrWhiteSpace(parsed.Email)
                    && emailById is not null
                    && !string.IsNullOrEmpty(parsed.SessionizeId)
                    && emailById.TryGetValue(parsed.SessionizeId, out var joinedEmail)
                    && !string.IsNullOrWhiteSpace(joinedEmail))
                {
                    parsed = parsed with { Email = joinedEmail };
                }

                if (string.IsNullOrWhiteSpace(parsed.Email))
                {
                    var name = $"{parsed.FirstName} {parsed.LastName}".Trim();
                    // §189: pick the message by CAUSE. If the SpeakersEmails view is
                    // readable, THIS speaker just isn't in it yet (invite not accepted);
                    // only point at the EmailsToken / "speaker emails" config when the
                    // view is genuinely unreadable. Either way it stays a non-fatal skip.
                    var detail = emailsViewReadable
                        ? "no email - likely hasn't accepted the Sessionize speaker "
                          + "invite yet (not in the SpeakersEmails view)."
                        : "skipped - no email address. Enable the 'speaker emails' field "
                          + "on the Sessionize endpoint, or configure Sessionize:EmailsToken "
                          + "so the hub can read the secured SpeakersEmails view.";
                    warnings.Add(string.IsNullOrEmpty(name)
                        ? $"Speaker #{index}: {detail}"
                        : $"Speaker '{name}': {detail}");

                    // §204: if this email-less speaker has a stable Sessionize id we can
                    // still bring them into the pre-selection queue (keyed by that id) so
                    // the organizer sees them; without an id there is no key to reconcile
                    // on later, so it stays a plain skip.
                    if (!string.IsNullOrEmpty(parsed.SessionizeId))
                    {
                        emailLess[parsed.SessionizeId] =
                            emailLess.TryGetValue(parsed.SessionizeId, out var prevNoEmail)
                                ? Merge(prevNoEmail, parsed)
                                : parsed;
                    }
                    continue;
                }

                seen[parsed.Email] = seen.TryGetValue(parsed.Email, out var prev)
                    ? Merge(prev, parsed)
                    : parsed;
            }
        }

        speakers.AddRange(seen.Values);
        return new SessionizeParseResult(speakers, warnings, null)
        {
            EmailLessSpeakers = emailLess.Values.ToList(),
        };
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

        // §971 — the live pull carries the edition's track renames; ParseSessions itself stays
        // usable without them (tests, ad-hoc parsing), where the map is simply absent.
        return ParseSessions(json, _options.ResolvedTrackAliases);
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
    /// <param name="trackAliases">
    /// §971 — optional Sessionize-track rename map (<see cref="SessionizeApiOptions.TrackAliases"/>).
    /// Null/empty ⇒ labels pass through untouched, so every existing caller is unaffected.
    /// </param>
    public static SessionizeSessionsParseResult ParseSessions(
        string json, IReadOnlyDictionary<string, string>? trackAliases = null)
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

            // roomId -> room name, so a scheduled session's numeric roomId resolves to
            // a human room label. The All view carries the room ONLY as a numeric
            // "roomId" plus a top-level "rooms" map (the inline "room" string is empty),
            // so without this every scheduled session would still read "Room TBD".
            var roomNames = BuildRoomNames(root);

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
                    AddSession(ParseOneSession(sess, categoryItemNames, roomNames, trackAliases), seen);
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
                        AddSession(ParseOneSession(sess, categoryItemNames, roomNames, trackAliases), seen);
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
        JsonElement sess,
        IReadOnlyDictionary<string, CategoryItem> categoryItemNames,
        IReadOnlyDictionary<string, string> roomNames,
        IReadOnlyDictionary<string, string>? trackAliases = null)
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

        // §154: resolve each categoryItems id to its GROUP, then route by group TITLE
        // instead of joining every label. The Sessionize "All" view exposes a
        // category GROUP per facet — "Format" (the kind + "(NN min)"), "Suggested
        // Event Track" (the track), "Level" (the audience level). Old behaviour set
        // Track = the FIRST resolved category, which grabbed the Format — fixed here.
        //  - Format group  → the Category (drives Type + LengthMinutes via the mapper).
        //  - Track group    → Track (in CEH this is just "Track", §154).
        //  - Level group    → Level.
        //  - Tags group     → Tags (§299.8/b7 — comma-joined; null when absent).
        //  - anything else  → an extra label kept ONLY as a Category fallback (below).
        string? track = null, level = null, formatLabel = null;
        var tagLabels = new List<string>();
        var otherLabels = new List<string>();
        if (sess.TryGetProperty("categoryItems", out var ci)
            && ci.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ci.EnumerateArray())
            {
                var key = item.ValueKind == JsonValueKind.Number
                    ? item.GetRawText()
                    : item.GetString() ?? string.Empty;
                if (!categoryItemNames.TryGetValue(key, out var cat)) continue;

                // §323: the routing keywords are MAP-DRIVEN (SessionizeFieldMap reads
                // them from sessionize-to-ceh.fieldmap.json, code fallback here) — a
                // renamed Sessionize category group is a config edit, not a deploy.
                var group = cat.GroupTitle.ToLowerInvariant();
                if (group.Contains(SessionizeFieldMap.FormatKeyword))
                    formatLabel ??= cat.Name;
                // "Suggested Event Track" and a plainly-titled "Track" both map to Track.
                // §971: and a renamed track is normalised HERE, on the way in, so CEH stores one
                // name and every downstream reader (grid, agenda, graphics, Zoho push) agrees.
                else if (group.Contains(SessionizeFieldMap.TrackKeyword))
                    track ??= ApplyTrackAlias(cat.Name, trackAliases);
                else if (group.Contains(SessionizeFieldMap.LevelKeyword))
                    level ??= cat.Name;
                // §299.8/b7: a "Tags" group (any title containing the tag keyword)
                // carries the session's tag chips; ALL its items are kept (comma-joined).
                else if (group.Contains(SessionizeFieldMap.TagKeyword))
                    tagLabels.Add(cat.Name);
                else
                    otherLabels.Add(cat.Name);
            }
        }

        // The Category that drives the hub Type/Length mapping: the Format label when
        // we found one, else the joined remaining labels (a grouped view with no group
        // titles still gets a best-effort Category, preserving the old detection).
        var category = !string.IsNullOrEmpty(formatLabel)
            ? formatLabel
            : (otherLabels.Count > 0 ? string.Join(" | ", otherLabels) : null);

        // Room: prefer an inline "room" string (grouped views sometimes carry it);
        // otherwise resolve the numeric "roomId" via the top-level rooms map (the All view
        // only gives roomId + a rooms array — the inline string is empty there).
        var room = NullIfEmpty(GetString(sess, "room"));
        if (room is null && sess.TryGetProperty("roomId", out var roomIdEl))
        {
            var roomId = roomIdEl.ValueKind == JsonValueKind.Number
                ? roomIdEl.GetRawText()
                : roomIdEl.ValueKind == JsonValueKind.String ? roomIdEl.GetString() : null;
            if (!string.IsNullOrEmpty(roomId)
                && roomNames.TryGetValue(roomId, out var roomName))
            {
                room = roomName;
            }
        }

        var startsAt = GetDateTimeOffset(sess, "startsAt");
        var endsAt = GetDateTimeOffset(sess, "endsAt");

        return new SessionizeSession(
            SessionizeId:     GetString(sess, "id").Trim(),
            Title:            GetString(sess, "title").Trim(),
            Abstract:         NullIfEmpty(GetString(sess, "description")),
            Room:             room,
            Track:            NullIfEmpty(track ?? string.Empty),
            StartsAt:         startsAt,
            EndsAt:           endsAt,
            IsServiceSession: GetBool(sess, "isServiceSession"),
            SpeakerIds:       speakerIds,
            Category:         category,
            Level:            NullIfEmpty(level ?? string.Empty),
            // §154: numeric minutes from the scheduled times when published, else the
            // Format label's "(NN min)" hint.
            LengthMinutes:    SessionDefaultsMapper.MapLengthMinutes(startsAt, endsAt, formatLabel),
            // §299.8/b7: comma-joined "Tags" group labels; null when the payload has none.
            Tags:             tagLabels.Count > 0 ? string.Join(", ", tagLabels) : null);
    }

    /// <summary>
    /// One resolved Sessionize category item: its display <paramref name="Name"/>
    /// (e.g. "Security") plus the TITLE of the GROUP it belongs to (e.g. "Format",
    /// "Suggested Event Track", "Level"). §154 routes the item to Type/Length, Track
    /// or Level by its group title instead of joining every label together.
    /// </summary>
    private readonly record struct CategoryItem(string GroupTitle, string Name);

    /// <summary>
    /// Build a category-item-id -> {group title, name} map from the All view's
    /// <c>categories</c> array (each entry is a GROUP with a <c>title</c> and an
    /// <c>items</c> list). So a session's categoryItems ids resolve to both their
    /// label AND which group (Format / Suggested Event Track / Level / …) they came
    /// from. Empty for the grouped Sessions view (no categories block) — Track/Level
    /// are then left null.
    /// </summary>
    private static IReadOnlyDictionary<string, CategoryItem> BuildCategoryItemNames(
        JsonElement root)
    {
        var map = new Dictionary<string, CategoryItem>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("categories", out var cats)
            || cats.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (var cat in cats.EnumerateArray())
        {
            var groupTitle = GetString(cat, "title").Trim();
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
                    map[id] = new CategoryItem(groupTitle, name);
                }
            }
        }
        return map;
    }

    /// <summary>
    /// Build a roomId -> room name map from the All view's top-level <c>rooms</c> array
    /// (<c>[{ id, name }]</c>), so a scheduled session's numeric <c>roomId</c> resolves to a
    /// human room label. Empty for views without a rooms block (room is then left null).
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildRoomNames(JsonElement root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("rooms", out var rooms)
            || rooms.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (var room in rooms.EnumerateArray())
        {
            if (!room.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetRawText()
                : idEl.GetString() ?? string.Empty;
            var name = GetString(room, "name").Trim();
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
            {
                map[id] = name;
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
        // §305 CRITICAL FIX (operator 2026-07-24): Sessionize emits the EVENT's local
        // wall-clock ("2027-02-04T09:00:00") with NO offset. The old AssumeUniversal
        // read it as UTC, so every session landed +1h/+2h late in Zoho (9:00 Danish
        // became "09:00Z" = 10:00 CET). EventTimezone attaches the REAL Danish offset
        // for that date (CET/CEST, DST-correct); explicit offsets are honoured as-is.
        return EventTimezone.ParseEventLocal(raw);
    }

    private static bool GetBool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v)
        && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String
                && bool.TryParse(v.GetString(), out var b) && b));

    /// <summary>
    /// §971 — map a Sessionize track label through <see cref="SessionizeApiOptions.TrackAliases"/>.
    /// Unmapped labels pass through untouched, so an empty map is a true no-op.
    /// </summary>
    /// <remarks>
    /// 🔒 Trimmed and case-insensitive, because the alias is typed by a human into config and the
    /// label is typed by a human into Sessionize. A rename that fails to match because one side has
    /// a trailing space would be invisible: the session simply keeps the old track and nothing
    /// reports it.
    /// </remarks>
    internal static string ApplyTrackAlias(
        string label, IReadOnlyDictionary<string, string>? aliases)
    {
        var name = (label ?? string.Empty).Trim();
        if (name.Length == 0) return name;
        return aliases is { Count: > 0 }
            && aliases.TryGetValue(name, out var mapped)
            && !string.IsNullOrWhiteSpace(mapped)
                ? mapped.Trim()
                : name;
    }

    private static string? NullIfEmpty(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string NonEmpty(string a, string b) =>
        string.IsNullOrWhiteSpace(a) ? b : a;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
