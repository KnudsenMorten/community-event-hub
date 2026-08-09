using static CommunityHub.Core.Integrations.IntegrationFieldMap;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §302b/§303 — the ZOHO BACKSTAGE instance of the generic
/// <see cref="IntegrationFieldMap"/> engine (ExternalEventSystem). One row per synced
/// field: the CEH source, the Zoho v3 API field name the payload builders send, and
/// the ZOHO GUI label the ops mails show (the operator applies changes manually in the
/// Backstage UI, so mails must speak the names the panel shows — e.g. CEH "Microsoft
/// accreditation" is Zoho "Skills"). Every consumer — the speaker/session push
/// payloads, the profile-edit change mail, the linked-session diff mail — reads THIS
/// map, never its own literals.
///
/// <para><b>§303 layer 2 (§323: ALL slices):</b> the Speaker, Session, Sponsor and
/// Exhibitor rows are DATA-DRIVEN — <see cref="MapFilePath"/> is loaded at host
/// startup (<see cref="ApplyMapFile"/>, fail-soft) and OVERRIDES the code defaults
/// below; the code rows remain the fallback so a missing/broken file never takes the
/// engine down.</para>
///
/// <para><b>Value derivations</b> live in code by design (the operator's "3rd check" —
/// a CEH checkbox set must derive into the Zoho string it compares/pushes):
/// <see cref="SpeakerSkills"/> and <see cref="SessionTags"/>; the JSON rows reference
/// them by derivation KEY ("zoho.speakerSkills" / "zoho.sessionTags").</para>
/// </summary>
public static class ZohoFieldMap
{
    // ---- §303: JSON overrides (speakers slice) --------------------------------

    /// <summary>The shipped per-integration map file (SCHEMA v2, §303c — the file name
    /// carries the DIRECTION: ceh-to-zoho-backstage; relative, resolved against the
    /// app base directory when deployed).</summary>
    public const string MapFilePath = "config/integrations/ceh-to-zoho-backstage.fieldmap.json";

    private static IReadOnlyDictionary<string, Field>? _speakerOverrides;
    private static IReadOnlyDictionary<string, Field>? _sessionOverrides;
    private static IReadOnlyDictionary<string, Field>? _sponsorOverrides;
    private static IReadOnlyDictionary<string, Field>? _exhibitorOverrides;

    /// <summary>
    /// Apply a loaded map file's rows over the code defaults (called once at host
    /// startup; no-op on null). §323: consumes ALL FOUR field groups — Speaker,
    /// Session, Sponsor and Exhibitor (the sessions/sponsors slices joined the file;
    /// sponsor vs exhibitor are two DIFFERENT Zoho records, split accordingly).
    /// </summary>
    public static void ApplyMapFile(MapFile? map)
    {
        if (map is null) return;
        _speakerOverrides = map.Fields.TryGetValue("Speaker", out var sp) ? sp : null;
        _sessionOverrides = map.Fields.TryGetValue("Session", out var se) ? se : null;
        _sponsorOverrides = map.Fields.TryGetValue("Sponsor", out var sn) ? sn : null;
        _exhibitorOverrides = map.Fields.TryGetValue("Exhibitor", out var ex) ? ex : null;
    }

    /// <summary>Test/diagnostics: clear applied overrides (back to code defaults).</summary>
    public static void ResetOverrides() =>
        _speakerOverrides = _sessionOverrides = _sponsorOverrides = _exhibitorOverrides = null;

    private static Field Sp(string cehField, Field dflt) =>
        _speakerOverrides?.GetValueOrDefault(cehField) ?? dflt;

    private static Field Se(string cehField, Field dflt) =>
        _sessionOverrides?.GetValueOrDefault(cehField) ?? dflt;

    private static Field Sn(string cehField, Field dflt) =>
        _sponsorOverrides?.GetValueOrDefault(cehField) ?? dflt;

    private static Field Ex(string cehField, Field dflt) =>
        _exhibitorOverrides?.GetValueOrDefault(cehField) ?? dflt;

    /// <summary>
    /// SPEAKER fields — the Backstage "Edit Speaker" panel (operator screenshots
    /// 2026-07-24). Full GUI inventory for reference: First Name*, Last Name,
    /// "Feature this speaker", Email (locked), Country, Profile Photo, Designation,
    /// Company Name, Skills (comma separated), Description (rich text),
    /// Social Pages/Handles (Facebook/LinkedIn/X/…), Phone Number, Alternative Phone
    /// Number, Address. The speakers API is CREATE-ONLY.
    /// Rows are JSON-overridable (§303) — code values below are the fallback.
    /// </summary>
    public static class Speaker
    {
        public static Field FirstName => Sp("FirstName", new("FirstName", "name", "First Name"));
        public static Field LastName => Sp("LastName", new("LastName", "last_name", "Last Name"));
        public static Field Company => Sp("CompanyName", new("CompanyName", "company", "Company Name"));
        public static Field Country => Sp("Country", new("Country", "country", "Country"));
        public static Field Tagline => Sp("Tagline", new("Tagline", "designation", "Designation"));
        public static Field Biography => Sp("Biography", new("Biography", "description", "Description",
            Kind: FieldKind.MultiLine));
        public static Field Skills => Sp("Accreditation", new("Accreditation", "skills", "Skills (comma separated)",
            Kind: FieldKind.Array, MergedFrom: new[] { "Accreditation", "MvpCategories" },
            Derivation: "zoho.speakerSkills"));
        // The two CEH social URLs MERGE into Zoho's ONE company_social_pages object —
        // Object kind ⇒ existence check only (operator: "just validate it exist").
        public static Field LinkedIn => Sp("LinkedIn", new("LinkedIn", "linkedin", "Social Pages/Handles → LinkedIn",
            Kind: FieldKind.Object, MergedFrom: new[] { "LinkedIn", "Twitter" }));
        public static Field Twitter => Sp("Twitter", new("Twitter", "twitter", "Social Pages/Handles → X (Twitter)",
            Kind: FieldKind.Object, MergedFrom: new[] { "LinkedIn", "Twitter" }));
        public static Field Featured => Sp("SelectedForPublish", new("SelectedForPublish", "featured", "Feature this speaker"));
        // CEH Blog / PhotoUrl have NO Zoho speaker field — never mailed as a "Zoho change".
    }

    /// <summary>
    /// SESSION fields — the Backstage "Edit Session" panel (operator screenshots
    /// 2026-07-24). Full GUI inventory for reference: Title*, Session Type, "Feature
    /// This Session", Event Day, Start Time, Duration (Hr/Min), Hall*, Speakers*,
    /// Track, Tags, Session Description (rich text). The sessions API is CREATE-ONLY;
    /// Tags + Session Description are refused even on create (GuiOnly).
    /// §323: rows are JSON-overridable — code values below are the fallback.
    /// </summary>
    public static class Session
    {
        public static Field Title => Se("Title", new("Title", "title", "Title"));
        public static Field StartTime => Se("StartsAt", new("StartsAt", "start_time", "Session Time"));
        public static Field Duration => Se("LengthMinutes", new("LengthMinutes", "duration", "Duration (Hr/Min)",
            Kind: FieldKind.Integer));
        public static Field Track => Se("Track", new("Track", "track", "Track"));
        public static Field Hall => Se("Room", new("Room", "venue", "Hall"));
        public static Field Speakers => Se("SessionSpeakers", new("SessionSpeakers", "speakers", "Speakers",
            Kind: FieldKind.Array));
        // §1012 — named ...Field because the CEH side is the `SessionType` ENUM, and a property
        // called SessionType next to it reads as the type rather than the mapping row.
        // Live-verified values: BREAK / KEYNOTE / PRESENTATION / REGISTRATION / WELCOMENOTE.
        public static Field SessionTypeField => Se("Type", new("Type", "session_type", "Session Type"));
        public static Field Tags => Se("Level", new("Level", "tags", "Tags",
            Capability.GuiOnly, FieldKind.Array,
            MergedFrom: new[] { "Level", "Tags", "(mandatory language)" },
            Derivation: "zoho.sessionTags"));
        public static Field Description => Se("Abstract", new("Abstract", "description", "Session Description",
            Capability.GuiOnly, FieldKind.MultiLine));
    }

    /// <summary>
    /// SPONSOR / EXHIBITOR fields (§302 change-only reconcile mails). FETCH STRATEGY
    /// (live-verified 2026-07-24): the SPONSOR per-id GET returns more than the list
    /// (description/website/socials ⇒ DetailOnly); the EXHIBITOR record NEVER echoes
    /// company_overview / company_social_pages at all — PUT-accepted, unreadable ⇒
    /// NotReadable, handled via the §302d ZohoSocialPushedHash stamp.
    /// §323: rows are JSON-overridable — the SPONSOR-record rows come from the file's
    /// "Sponsor" group, the EXHIBITOR-record rows from "Exhibitor" (two different Zoho
    /// records). Code values below are the fallback.
    /// </summary>
    public static class Sponsor
    {
        public static Field Description => Sn("CompanyDescription", new("CompanyDescription", "description", "Description",
            Capability.CreateAndUpdate, FieldKind.MultiLine, ReadSupport.DetailOnly));
        public static Field Website => Sn("WebsiteUrl", new("WebsiteUrl", "website_url", "Website",
            Capability.CreateAndUpdate, FieldKind.String, ReadSupport.List));
        public static Field Overview => Ex("CompanyDescription", new("CompanyDescription", "company_overview", "Company Overview",
            Capability.CreateAndUpdate, FieldKind.MultiLine, ReadSupport.NotReadable));
        public static Field LinkedIn => Ex("LinkedInUrl", new("LinkedInUrl", "company_social_pages.linkedin", "Social Pages (LinkedIn)",
            Capability.CreateAndUpdate, FieldKind.Object, ReadSupport.NotReadable,
            MergedFrom: new[] { "LinkedInUrl", "TwitterUrl" }));
        public static Field Twitter => Ex("TwitterUrl", new("TwitterUrl", "company_social_pages.twitter", "Social Pages (X/Twitter)",
            Capability.CreateAndUpdate, FieldKind.Object, ReadSupport.NotReadable,
            MergedFrom: new[] { "LinkedInUrl", "TwitterUrl" }));
        public static Field ContactEmail => Ex("EventCoordinatorEmail", new("EventCoordinatorEmail", "contact.email_id", "Contact Email",
            Capability.CreateAndUpdate, FieldKind.String, ReadSupport.NotReadable));   // §41a: 3-update cap ⇒ CEH stamp (ZohoContactEmail)
        public static Field Booth => Ex("BoothLabel", new("BoothLabel", "booth_id", "Booth",
            Capability.CreateAndUpdate, FieldKind.String, ReadSupport.List));
    }

    /// <summary>§302e — the live-verified Zoho Backstage v3 entity surfaces.
    /// (Duplicated in the JSON map file; these are the code fallback.)</summary>
    public static class Entities
    {
        public static readonly Entity Speakers = new(
            "Speakers", "/speakers", "/speakers/{id}", "/speakers", null,
            "CREATE-ONLY. List is rich (all mapped fields). Creating a speaker SENDS THE "
            + "INVITATION; attaching an unknown e-mail to a session AUTO-CREATES+INVITES. "
            + "E-mails stored lowercased; session attach matches CASE-SENSITIVELY.");

        public static readonly Entity Sessions = new(
            "Sessions", "/sessions?day={n}", null, "/sessions", null,
            "CREATE-ONLY; list is PER agenda DAY (1-based; enumerate /agendas first). "
            + "description+tags REFUSED on create (GuiOnly ⇒ ACTION mail). speakers "
            + "attach at create only (e-mail array). venue=hall id, track=track id.");

        public static readonly Entity Sponsors = new(
            "Sponsors", "/sponsors", "/sponsors/{id}", "/sponsors", "/sponsors/{id}",
            "PUT supported. The per-id DETAIL returns MORE than the list "
            + "(description/website/socials) — fetch detail before comparing DetailOnly fields.");

        public static readonly Entity Exhibitors = new(
            "Exhibitors", "/exhibitors", "/exhibitors/{id}", "/exhibitors", "/exhibitors/{id}",
            "PUT supported BUT company_overview + company_social_pages are accepted and "
            + "NEVER echoed by any GET (NotReadable ⇒ §302d CEH-side push-hash stamp). "
            + "booth_id/website_url/company_name/contact DO round-trip via the list.");

        public static readonly Entity Halls = new(
            "Halls", "/halls", null, "/halls", null,
            "POST {name, capacity} — capacity REQUIRED. Sessions reference the hall id via venue.");

        public static readonly Entity Tracks = new(
            "Tracks", "/tracks", null, "/tracks", null,
            "POST {name}. Sessions reference the track id. No /tags endpoint exists at all.");

        public static readonly Entity Booths = new(
            "Booths", "/booths", null, "/booths", null,
            "Label ('E-26') lives on the booth object; the exhibitor field is booth_id — "
            + "resolve label→id before assigning.");
    }

    /// <summary>
    /// DERIVE the Zoho "Skills" CSV for a speaker (§302b, derivation key
    /// "zoho.speakerSkills": "Skills are also missing in zoho — in ceh we capture
    /// 'Microsoft Accrediation' and it must be populated into skills"). The
    /// Microsoft-accreditation checkboxes are the primary source, the MVP categories
    /// complement them; FULL labels kept, distinct, joined with ", ". Null when the
    /// speaker has neither.
    /// </summary>
    public static string? SpeakerSkills(Domain.SpeakerProfile profile)
    {
        var parts = new List<string>();
        void AddCsv(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return;
            foreach (var p in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // §306: "None" is the mandatory-field placeholder for a non-accredited
                // speaker — it is never a Zoho skill.
                if (string.Equals(p, "None", StringComparison.OrdinalIgnoreCase)) continue;
                if (!parts.Contains(p, StringComparer.OrdinalIgnoreCase)) parts.Add(p);
            }
        }
        AddCsv(profile.Accreditation);
        AddCsv(profile.MvpCategories);
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    /// <summary>
    /// §302c — DERIVE the Zoho "Tags" a session must carry (derivation key
    /// "zoho.sessionTags"; the operator's canonical rule, REQUIREMENTS §302c):
    /// <list type="number">
    /// <item>Sessionize LEVEL → <c>"Session Level {level}"</c> — <b>no colon</b>, matching Zoho's
    /// existing fixed tag vocabulary (§594b). The level VALUE passes through verbatim so
    /// Sessionize and Zoho show the identical spelling. Source format:
    /// "Advanced (300)", "Expert (400)", "Black Belt (500)".</item>
    /// <item>MANDATORY <c>"Session Language English"</c> on EVERY session — again no colon
    /// (all sessions are in English).</item>
    /// <item>Sessionize labels/tags pass through verbatim and UNBOUNDED — the
    /// Sessionize "Labels/Tags" multiple-choice offers 100+ options; EVERY label
    /// chosen in the submission is pushed. The importer matches any category group
    /// whose title contains "tag" (so "Labels/Tags" qualifies) and keeps ALL items.</item>
    /// </list>
    /// Tags are GuiOnly (refused on create, no /tags endpoint), so this feeds the
    /// ACTION change mail, which tells the operator exactly what to add in the UI.
    /// </summary>
    public static IReadOnlyList<string> SessionTags(Domain.Session session)
    {
        var tags = new List<string>();
        void Add(string t)
        {
            if (!tags.Contains(t, StringComparer.OrdinalIgnoreCase)) tags.Add(t);
        }
        // 🔒 §594b — NO COLON. THE ZOHO TAG VOCABULARY IS FIXED AND WE CANNOT CHANGE IT.
        //
        // Operator 2026-07-28: *"in zoho we re-use tags tha existing (we cannot delete them) and
        // here we have Session Language English and Session Level Expert (400) + same for 300 + 500
        // - no : - we cannot add that or change that, so it must be in the code that there is
        // differences from sessionize"*.
        //
        // Zoho Backstage tags are a REUSABLE, EVENT-WIDE vocabulary: the existing entries are
        // "Session Level Expert (400)" / "Session Level Advanced (300)" / "Session Level Black Belt
        // (500)" and "Session Language English" — all WITHOUT a colon — and they cannot be renamed
        // or deleted. Emitting "Session Level: Expert (400)" therefore does not match an existing
        // tag; it would create a SECOND, near-identical tag in that shared vocabulary, permanently.
        //
        // So the colon is dropped HERE, in the derivation: this is the seam where the Sessionize
        // spelling is translated into Zoho's, which is exactly what he means by "it must be in the
        // code that there is differences from sessionize". The Sessionize LEVEL VALUE itself still
        // passes through verbatim ("Expert (400)"), so only the prefix separator differs.
        if (!string.IsNullOrWhiteSpace(session.Level))
            Add("Session Level " + session.Level.Trim());
        Add("Session Language English");
        if (!string.IsNullOrWhiteSpace(session.Tags))
            foreach (var t in session.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Add(t);
        return tags;
    }
}
