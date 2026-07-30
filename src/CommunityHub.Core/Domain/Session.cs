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
    public string? ZohoChangeNotifiedHash { get; set; }

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
