namespace CommunityHub.Core.Domain;

/// <summary>
/// The kind of a <see cref="Session"/>. Imported sessions are mapped from the source
/// category / format / duration to one of these (see <c>SessionDefaultsMapper</c>);
/// hub-added sessions set it explicitly. A filter on the session views narrows by
/// this. Stored as <c>int</c> (see <c>CommunityHubDbContext</c>), so the numeric
/// values below are part of the persisted contract — a data migration remaps the
/// legacy values (see migration <c>SessionTypeStandardize</c>).
/// </summary>
public enum SessionType
{
    /// <summary>A keynote (typically the opening / closing main-stage session).</summary>
    Keynote = 0,

    /// <summary>A regular technical session / talk (the default for imports with no type).</summary>
    TechnicalSession = 1,

    /// <summary>A master class / workshop (typically full-day, in-hub signup + landing page).</summary>
    MasterClass = 2,

    /// <summary>An "Ask the Experts" panel / clinic.</summary>
    AskTheExperts = 3,

    /// <summary>A panel discussion.</summary>
    PanelDiscussion = 4,

    /// <summary>A welcome / opening logistics session.</summary>
    Welcome = 5,

    /// <summary>Neutral fallback when no type can be derived (never inferred by the importer).</summary>
    Other = 6,
}

/// <summary>
/// LEGACY coarse length bucket of a <see cref="Session"/>. §299.8/b7: the SOURCE OF
/// TRUTH for a session's length is the integer <see cref="Session.LengthMinutes"/>
/// (any positive minutes up to the configured max; quick-picks are per-edition
/// config) — this enum survives ONLY as a DERIVED display bucket for legacy display
/// sites (map minutes → nearest bucket via <c>SessionDefaultsMapper.ToLengthBucket</c>).
/// No filter, validation or push logic may branch on it any more.
/// </summary>
public enum SessionLength
{
    /// <summary>A full-day session (master class / workshop).</summary>
    FullDay = 0,

    /// <summary>A 20-minute session (lightning / short talk).</summary>
    TwentyMin = 20,

    /// <summary>A 50-minute session.</summary>
    FiftyMin = 50,

    /// <summary>A 60-minute session.</summary>
    SixtyMin = 60,
}

/// <summary>
/// One Sessionize session (talk / workshop), scoped to an event edition. Imported
/// from the Sessionize v2 view API (the <c>All</c>/<c>Sessions</c> view, alongside
/// speakers) - see <c>SessionImportService</c> - OR <b>added directly in the hub</b>
/// (e.g. a sponsor session) by an organizer. A session is linked to one or more
/// speakers through <see cref="SessionSpeaker"/> (many-to-many): a session can have
/// several co-speakers, and a speaker can deliver several sessions.
///
/// <b>Two origins (see <see cref="IsHubAdded"/>):</b>
///  - <b>Imported</b> sessions are import-driven: the import UPSERTS by the Sessionize
///    session id (<see cref="SessionizeId"/>) and NEVER deletes. Import-owned fields
///    (Title/Abstract/Room/Track/times/Type/Length) are refreshed on each pull, with
///    Type/Length derived from the Sessionize data (a default mapping).
///  - <b>Hub-added</b> sessions have a synthetic <see cref="SessionizeId"/>
///    (<c>hub-&lt;guid&gt;</c>) that the Sessionize import never matches, so a re-import
///    never touches or deletes them. The organizer sets every field, including
///    <see cref="Room"/>, <see cref="Type"/> and <see cref="Length"/>.
/// </summary>
public class Session
{
    public int Id { get; set; }

    /// <summary>The edition this session belongs to. Every query is scoped by this.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>
    /// The Sessionize session id (the stable identity from the API). This is the
    /// upsert match key, so a re-import updates the existing row in place rather
    /// than duplicating. Unique within an edition.
    /// </summary>
    public string SessionizeId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>The session abstract / description (Sessionize <c>description</c>).</summary>
    public string? Abstract { get; set; }

    /// <summary>Room / location name, when the Sessionize grid assigns one.</summary>
    public string? Room { get; set; }

    /// <summary>
    /// Track / category label, when available. Sourced from the Sessionize
    /// "Suggested Event Track" category GROUP (called just "Track" in CEH, §154),
    /// e.g. "Security". Import-owned (refreshed each pull). Null when the source
    /// carries no track for the session.
    /// </summary>
    public string? Track { get; set; }

    /// <summary>
    /// 🔴 §1011 — this session belongs to <b>every</b> track, so it has no track of its own:
    /// the plenary marker (welcome, keynote, closing, breaks). Ticked, it <b>OVERRULES</b>
    /// <see cref="Track"/> everywhere the track is compared or pushed.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-09: *"I need the ability to overrule the track with an extra field
    /// Common for all Tracks which can be ticked of."*</para>
    ///
    /// <para>✅ <b>LIVE-VERIFIED against PROD, 2026-08-09</b> (`GET /sessions?day=2`, session
    /// *ELDK27 Welcome* `14880000004329032`, the one in his screenshot): <b>Zoho has NO
    /// "common for all tracks" field.</b> The complete record is <c>agenda, created_by,
    /// created_time, description, duration, featured, hidden, id, language, last_modified_by,
    /// last_modified_time, session_type, speaker_to_be_announced, speakers, start_time, title,
    /// track, venue, venue_to_be_announced</c>, and the per-id GET returns the IDENTICAL set —
    /// unlike the §623 sponsor record, there is no second place to look.</para>
    ///
    /// <para>🔑 <b>The chip IS <c>track: null</c>.</b> "Common for All Tracks" is not a field at
    /// all — it is how Backstage RENDERS a session with no track. Every session carrying the chip
    /// has a null track and nothing else does (*ELDK27 Welcome* and *Pre-keynote* null; all 8
    /// master classes carry a real track id; 16 of 25 live sessions are track-less).</para>
    ///
    /// <para>⇒ This flag therefore needs no Zoho field — it means "expect NO track over there".
    /// And because <c>track</c> IS readable, the difference check can <b>verify</b> that rather
    /// than ignore it: ticked + null is confirmed-correct and mails nothing; ticked + a track id
    /// is a <b>closable</b> "clear the track" line. He had asked, reasonably, for the field to be
    /// ignored if the API were silent — it is not silent about the thing that matters.</para>
    ///
    /// <para>⚠️ <b>The create still sends the track</b> (operator decision 2026-08-09). Zoho's
    /// create REQUIRES a session type and has been observed to require a track; the 16 track-less
    /// sessions above were all made by hand in the GUI and are no evidence about the API. So a
    /// ticked session is created WITH its track and the create mail tells him to clear it — the
    /// sessions API cannot delete, so a refused create is cheaper to avoid than to recover from.</para>
    /// </remarks>
    public bool IsCommonForAllTracks { get; set; }

    /// <summary>
    /// Audience level label (§154), sourced from the Sessionize "Level" category
    /// GROUP, e.g. "Expert (400)". Import-owned (refreshed each pull). Null when the
    /// source carries no level for the session.
    /// </summary>
    public string? Level { get; set; }

    /// <summary>
    /// §299.8/b7 — the NUMERIC level code derived from <see cref="Level"/> against
    /// the per-edition <c>sessionLevels</c> config (label match, else the "(NNN)"
    /// digits in the label): Advanced 300 / Expert 400 / Black Belt 500 for this
    /// edition. All level SORTING/COMPARISON uses this code, never the label
    /// alphabetically (alphabetical puts Black Belt before Expert). Null for an
    /// unknown/unmatched label — such labels keep their string-only behaviour.
    /// Import-owned (re-derived each pull).
    /// </summary>
    public int? LevelCode { get; set; }

    /// <summary>
    /// §299.8/b7 — comma-separated tag labels from the source's "Tags" category
    /// group (synced through from the call-for-speakers system when its v2 payload
    /// provides them; stays null when the API omits tags). Import-owned (refreshed
    /// each pull). Displayed as small chips on the public session detail page.
    /// </summary>
    public string? Tags { get; set; }

    /// <summary>
    /// §299.8/b7 — the session's length in NUMERIC minutes: the SOURCE OF TRUTH for
    /// length. Imported: parsed from the Sessionize Format label (e.g. "Technical
    /// Session (60 min)" → 60) or, once the grid is published, derived from the
    /// scheduled start/end. Hub-added / organizer-edited: any positive integer up
    /// to the configured max (quick-picks 15/20/30/40/45/50/60/420 are per-edition
    /// config). The coarse <see cref="Length"/> bucket is DERIVED from this for
    /// legacy display sites only. Null only for imports where no minutes can be
    /// determined (e.g. a Master Class with no "(NN min)" hint and no times →
    /// full-day via <see cref="Length"/>).
    /// </summary>
    public int? LengthMinutes { get; set; }

    /// <summary>
    /// The session kind. Imported sessions get a default mapping from the source
    /// category / format / duration; hub-added sessions set it. Defaults to
    /// <see cref="SessionType.TechnicalSession"/>.
    /// </summary>
    public SessionType Type { get; set; } = SessionType.TechnicalSession;

    /// <summary>
    /// True when an organizer has manually set <see cref="Type"/> from the session
    /// admin. A Sessionize / Backstage re-import RESPECTS this flag and never
    /// clobbers a manual override (it still refreshes the other import-owned fields).
    /// False for a freshly-imported session whose type was derived from the source.
    /// </summary>
    public bool TypeIsManualOverride { get; set; }

    /// <summary>
    /// LEGACY derived length bucket — kept ONLY for legacy display sites (§299.8/b7).
    /// Always derived from <see cref="LengthMinutes"/> (nearest bucket) when minutes
    /// are known; never the source of truth and never used for filtering/validation
    /// logic any more. Defaults to <see cref="SessionLength.SixtyMin"/>.
    /// </summary>
    public SessionLength Length { get; set; } = SessionLength.SixtyMin;

    /// <summary>Scheduled start, when the Sessionize grid is published.</summary>
    public DateTimeOffset? StartsAt { get; set; }

    /// <summary>Scheduled end, when the Sessionize grid is published.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>
    /// §299.8 ❓OPEN-20 (answered 2026-07-23) — TRUE when an organizer has MANUALLY
    /// set this session's schedule: an explicit StartsAt/EndsAt edit via
    /// <c>SessionManagementService.UpdateSessionAsync</c> / the organizer Sessions
    /// edit form, or the hub-add day choice (Pre-day / Main day) stamping the date.
    /// A Sessionize re-import RESPECTS this flag for the SCHEDULE fields — it skips
    /// the StartsAt/EndsAt refresh so the manual date survives (mirroring
    /// <see cref="TypeIsManualOverride"/>); every other import-owned field keeps
    /// refreshing. False for a purely import-scheduled session.
    /// </summary>
    public bool IsDateOverridden { get; set; }

    /// <summary>True for a Sessionize "service session" (break/lunch/etc.) - kept for fidelity.</summary>
    public bool IsServiceSession { get; set; }

    /// <summary>
    /// §909 — this session is TEST DATA: never announced on social media, never rendered into a
    /// graphic.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-06: <i>"remove test sessions"</i> — said while looking at
    /// "Test Master Class" and "Test Session" sitting in the SoMe queue, scheduled to publish.</para>
    ///
    /// <para>🔴 <b>§905's derived rule could not see them, and this is exactly why an explicit flag
    /// exists.</b> That rule reads "the session HAS speakers and every one is a test user" — true of
    /// the two exhibitor fixtures, and FALSE of these: <c>Test Master Class</c> and
    /// <c>Test Session</c> each carry <b>four REAL speakers</b>. A session is test because of what it
    /// IS, not because of who happens to be on it, and no amount of inference fixes that.</para>
    ///
    /// <para>🔒 The sibling of <see cref="SponsorInfo.IsTestData"/>, added for the same reason and
    /// with the same default: <c>false</c>, so every existing session stays real until it is marked.
    /// A real session wrongly hidden is the worse failure — it silently drops a speaker's
    /// announcement, and nobody notices an absence.</para>
    ///
    /// <para>⚠️ Deliberately NOT a title match. "Test" appears in legitimate session titles
    /// ("Penetration Testing", "A/B Testing"), and a heuristic that reads names would eventually
    /// delete a real talk from the campaign.</para>
    /// </remarks>
    public bool IsTestData { get; set; }

    /// <summary>
    /// True when this session was added directly in the hub (not from Sessionize),
    /// e.g. a sponsor session. Hub-added sessions carry a synthetic
    /// <see cref="SessionizeId"/> (<c>hub-&lt;guid&gt;</c>) so the Sessionize import
    /// never matches, touches or deletes them.
    /// </summary>
    public bool IsHubAdded { get; set; }

    /// <summary>
    /// §299 4.5/b8 — CEH-only TEST session flag. When set the session (a) is NEVER
    /// pushed/synced into the external event system's public agenda, (b) never
    /// appears on any PUBLIC page (catalog, detail, ask, evaluate), and (c) inside
    /// the hub is visible only to ring 0 / ring 1 users (the standard
    /// <c>RingCap = 1</c> rule) — usable for testing in dev AND prod without ever
    /// being publicly visible. Organizer-set from the session admin.
    /// </summary>
    public bool UsedForTesting { get; set; }

    /// <summary>
    /// Unguessable per-session public token that addresses the session's PUBLIC
    /// attendee-question page (<c>/sessions/{token}/ask</c>). Not the sequential
    /// <see cref="Id"/> (which would be guessable), so the public ask URL cannot
    /// be enumerated. Minted on demand (256-bit, URL-safe) by
    /// <see cref="SessionQuestionService"/>; nullable until first minted.
    /// </summary>
    public string? PublicToken { get; set; }

    // --- QR code (per-room, stored on SharePoint via URL) -------------------

    /// <summary>
    /// The URL of the room's QR-code image, stored on SharePoint (REQUIREMENTS §
    /// session QR). Each physical room has one QR linked to the room; every session
    /// in a room shares the room's QR. Null until the QR seam has provisioned + stored
    /// the file and written back the URL. The "Download QR" button serves this.
    /// </summary>
    public string? RoomQrUrl { get; set; }

    /// <summary>When the <see cref="RoomQrUrl"/> was last (re)generated / stored.</summary>
    public DateTimeOffset? RoomQrGeneratedAt { get; set; }

    // --- Session evaluation -------------------------------------------------

    /// <summary>
    /// Optional URL of a QR-code evaluation form for this session (the QR-code
    /// evaluation option, REQUIREMENTS § evaluation). Distinct from the physical
    /// HappyOrNot box, whose results arrive manually and are emailed to the speakers.
    /// </summary>
    public string? EvaluationFormUrl { get; set; }

    /// <summary>
    /// When the last evaluation-results email was sent to this session's speakers
    /// (the HappyOrNot mail hook). Null = not yet sent. Used by the organizer UI to
    /// show "results emailed" state; the data arrives manually.
    /// </summary>
    public DateTimeOffset? EvaluationEmailedAt { get; set; }

    // --- Master class: public logistics page (REQUIREMENTS § 6c) ------------

    /// <summary>
    /// An unguessable, URL-safe public slug for the master-class logistics page
    /// (<c>GET /MasterClass/{slug}</c>, no auth). Shareable without exposing the
    /// numeric id; minted lazily the first time the page is needed (organizer /
    /// involved-speaker "show public link"). Null = no public page yet. Stored
    /// unique so a slug resolves to exactly one session.
    /// </summary>
    public string? PublicSlug { get; set; }

    /// <summary>
    /// The logistics + setup instructions an attendee reads before the master
    /// class (e.g. "bring your laptop charged", environment-prep steps). Edited
    /// by an involved speaker OR an organizer; rendered publicly (no auth) on
    /// the logistics page. Null/blank = nothing published yet. Plain text
    /// (rendered HTML-encoded, line breaks preserved) — no sensitive data.
    /// </summary>
    public string? LogisticsText { get; set; }

    /// <summary>When <see cref="LogisticsText"/> was last edited. Null = never.</summary>
    public DateTimeOffset? LogisticsUpdatedAt { get; set; }

    /// <summary>
    /// The email of the involved speaker / organizer who last edited
    /// <see cref="LogisticsText"/> (audit only; never rendered publicly).
    /// </summary>
    public string? LogisticsUpdatedByEmail { get; set; }

    // --- Master class: attendee landing page prep content (FEATURE 2) -------

    /// <summary>
    /// Rich-text preparation content shown on the master-class attendee landing
    /// page (what to expect, "bring a laptop", things to set up in advance).
    /// Edited by a speaker LINKED to this master-class session (or an organizer);
    /// read by confirmed attendees + the MC's speakers. Null/blank = nothing
    /// published yet. Only meaningful for a <see cref="SessionType.MasterClass"/>
    /// session. Rendered HTML-encoded with line breaks preserved.
    /// </summary>
    public string? PrepContent { get; set; }

    /// <summary>When <see cref="PrepContent"/> was last edited. Null = never.</summary>
    public DateTimeOffset? PrepUpdatedAt { get; set; }

    /// <summary>
    /// The participant id (a linked speaker or organizer) who last edited
    /// <see cref="PrepContent"/> — audit only, never rendered to attendees.
    /// </summary>
    public int? PrepUpdatedByParticipantId { get; set; }

    // --- Master class: in-hub seat capacity (REQUIREMENTS §6) ----------------

    /// <summary>
    /// Seat capacity for the in-hub Master Class signup + waitlist (REQUIREMENTS §6).
    /// Organizer-set on a <see cref="SessionType.MasterClass"/> session.
    /// null = no cap configured (everyone who signs up is confirmed; no waitlist);
    /// when set, signups beyond the cap are waitlisted (FIFO).
    /// </summary>
    public int? MasterClassCapacity { get; set; }

    // --- Zoho Backstage agenda id + last-known time/location (REQUIREMENTS §38e/§52) ---

    /// <summary>
    /// The Zoho Backstage agenda/session id for this talk (REQUIREMENTS §38e/§52
    /// "store BOTH the Backstage agenda/session id AND the Sessionize id"). Distinct
    /// from <see cref="SessionizeId"/> (the Sessionize identity) — Sessionize remains
    /// the speaker/session SOURCE, while Backstage owns the finalized SCHEDULE (time +
    /// hall). Set when the session is matched to a Backstage agenda session; null until
    /// then. Used (a) to address the public Backstage session page from the Speaker hub
    /// (§52 "View public session page" → Zoho Backstage, not Sessionize), and (b) as the
    /// match key for the §38e change-detection engine. Filtered-unique within an edition.
    /// </summary>
    public string? BackstageSessionId { get; set; }

    /// <summary>
    /// §302 (operator 2026-07-24, ONE-WAY sync decision): the hash of the last CEH↔Zoho
    /// field DIFF the ops mailbox was notified about for this linked session. Zoho's
    /// sessions API is create-only, so a CEH change (room, time, title …) on an
    /// already-pushed session can only be applied manually in the Backstage UI — the
    /// engine mails info@ ONCE per distinct diff (this hash dedupes the hourly passes;
    /// operator: "I don't want an email at every sync"). Cleared when the diff
    /// disappears, so a LATER change mails again. Null = up to date / never notified.
    /// </summary>
/// <summary>
    /// §1002 — when the outstanding CEH↔Zoho difference for this session was last mailed.
    /// </summary>
    /// <remarks>
    /// 🔴 Operator 2026-08-09: *"i need the check between ceh and zoho to run every 1 hour and send
    /// email of every missing change … it is not a one time mai that dissapears, this is public
    /// information for 1500 people, which is incompliant/not valid."*
    /// <para>This REVERSES §302, where he asked for one mail per distinct difference. The reason
    /// changed, not his mind: a single mail that scrolls out of the inbox leaves a WRONG PUBLIC
    /// AGENDA in place with nothing chasing it. A difference is now re-mailed every hour until
    /// Zoho matches — the hash still forces an IMMEDIATE mail when the difference itself changes,
    /// so a new problem never waits up to an hour behind an old one.</para>
    /// </remarks>
    public DateTimeOffset? ZohoChangeNotifiedAt { get; set; }

        public string? ZohoChangeNotifiedHash { get; set; }

    /// <summary>
    /// 🔴 §1024 — the dedupe key for the SESSIONIZE→CEH deviation mail (§999): the same set of
    /// disagreements is reported <b>once</b>, not on every import pass.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-09: *"lets leave it for now, as it is a good reminder to validate
    /// again, but only 1 time mail"*. He kept the mail — it is a useful prompt to go and check —
    /// but a standing disagreement that CEH is deliberately winning is not news twice.</para>
    ///
    /// <para>🔑 <b>Deliberately NOT the §1002 treatment.</b> The CEH↔Zoho difference re-mails every
    /// hour because it describes a <b>wrong public agenda</b> that somebody must go and fix. This
    /// one describes a state that is CORRECT by design — CEH owns the schedule and Sessionize is
    /// simply out of date — so the right cadence is once, and again only if the disagreement
    /// CHANGES.</para>
    ///
    /// <para>🔒 Cleared when the deviation goes away, so a later re-occurrence mails again rather
    /// than being silently swallowed by a stale hash.</para>
    /// </remarks>
    public string? SessionizeDeviationNotifiedHash { get; set; }

    /// <summary>
    /// The LAST-KNOWN Backstage start time for this session (the value CEH stored on
    /// the previous change-detection pass). The §38e engine compares the CURRENT Zoho
    /// Backstage start against this; a difference (when this was already non-null) is a
    /// real schedule CHANGE that emails the affected speaker(s). Null = not yet seeded
    /// (the first populate seeds silently and NEVER emails).
    /// </summary>
    public DateTimeOffset? BackstageStartsAt { get; set; }

    /// <summary>The LAST-KNOWN Backstage end time — see <see cref="BackstageStartsAt"/>.</summary>
    public DateTimeOffset? BackstageEndsAt { get; set; }

    /// <summary>
    /// The LAST-KNOWN Backstage room / hall name (the value CEH stored on the previous
    /// pass). A change vs the current Backstage hall (when already non-null) triggers a
    /// §38e change email. Null = not yet seeded.
    /// </summary>
    public string? BackstageRoom { get; set; }

    /// <summary>
    /// When the §38e change-detection engine last compared this session against Zoho
    /// Backstage (and refreshed the stored <c>Backstage*</c> values). Null = never
    /// checked. Stamped on every pass whether or not anything changed.
    /// </summary>
    public DateTimeOffset? BackstageChangeCheckedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>When this session was last touched by a Sessionize import.</summary>
    public DateTimeOffset? LastSessionizeImportAt { get; set; }

    // --- Navigation ---------------------------------------------------------
    public ICollection<SessionSpeaker> SessionSpeakers { get; set; } = new List<SessionSpeaker>();

    /// <summary>Attendee questions asked for this session (hub-only; never public).</summary>
    public ICollection<SessionQuestion> Questions { get; set; } = new List<SessionQuestion>();
}

/// <summary>
/// The many-to-many link between a <see cref="Session"/> and a speaker
/// (<see cref="Participant"/>). A session may have multiple speakers and a speaker
/// multiple sessions. The link is established from the Sessionize session's
/// <c>speakers</c> id array: each Sessionize speaker id is matched to the
/// participant the speaker import created for that speaker. A session speaker whose
/// id has no matching participant (e.g. emailless, so skipped by the speaker
/// import) is reported and left unlinked rather than silently dropped.
/// </summary>
public class SessionSpeaker
{
    public int Id { get; set; }

    public int SessionId { get; set; }
    public Session Session { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;
}
