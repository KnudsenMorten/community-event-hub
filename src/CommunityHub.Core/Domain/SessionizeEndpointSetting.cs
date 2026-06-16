namespace CommunityHub.Core.Domain;

/// <summary>
/// How an organizer wants an endpoint CHANGE to be applied to the
/// already-imported speakers/sessions of an edition. Chosen on the
/// <c>/Organizer/SessionizeEndpointSettings</c> page when the endpoint id is
/// changed, persisted on <see cref="SessionizeEndpointSetting"/>, and mapped 1:1
/// to a <c>SessionizeImportMode</c> when the operator then runs the import.
///
/// The mapping is deliberate (see DESIGN §6 "Sessionize → Endpoint admin settings"):
///  - <see cref="Replace"/>  ⇒ import mode <c>Full</c>  (full refresh / re-seed)
///  - <see cref="Merge"/>    ⇒ import mode <c>Delta</c> (additive, edits preserved)
/// </summary>
public enum SessionizeChangeMode
{
    /// <summary>
    /// No change recorded yet (the endpoint was set for the first time, or no
    /// change-handling choice has been made). Not a re-import trigger.
    /// </summary>
    None = 0,

    /// <summary>
    /// REPLACE the existing data and re-import the accepted speakers from the NEW
    /// endpoint — the normal production path (e.g. the ELDK26→ELDK27 switch). Maps
    /// to import mode <c>Full</c>: force-refresh ALL bio fields from the new
    /// endpoint and clear each speaker's edited set (a true re-seed). Still matches
    /// on email, never changes roles, never deletes.
    /// </summary>
    Replace = 1,

    /// <summary>
    /// MERGE with the existing data — used ONLY for testing (e.g. validating a new
    /// endpoint's JSON schema against existing rows). Maps to import mode
    /// <c>Delta</c>: add new speakers, fill only empty/untouched bio fields, and
    /// NEVER flush a speaker's own edits.
    /// </summary>
    Merge = 2,
}

/// <summary>
/// Per-edition Sessionize endpoint admin setting: the operator-configured
/// endpoint id (the <c>&lt;your-event-id&gt;</c> segment of the v2 view URL) plus
/// the bookkeeping for endpoint-change handling.
///
/// The endpoint id is ordinary operator configuration, NOT a secret (it binds to
/// <c>Sessionize:EndpointId</c>). This row lets an organizer set/edit it from the
/// hub UI on top of the config-bound default; when present and non-blank it is the
/// effective endpoint id for the edition (the live <c>SessionizeApiOptions</c> is
/// updated in-process on save). It is never written to public/committed config —
/// placeholders only there.
///
/// On a CHANGE of the endpoint id the organizer must choose how the already-imported
/// data is treated (<see cref="SessionizeChangeMode"/>); that choice is recorded
/// here. This entity only persists the choice + the endpoint + the last-change
/// stamp — it never RUNS an import. The operator runs the import from the existing
/// <c>/Organizer/SessionizeImport</c> buttons (or the CLI / scheduled job), which
/// map the chosen mode to the importer's <c>Full</c>/<c>Delta</c> semantics.
/// </summary>
public class SessionizeEndpointSetting
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>
    /// The organizer-configured Sessionize v2 view endpoint id for this edition
    /// (the <c>&lt;your-event-id&gt;</c> segment). Ordinary operator config, NOT a
    /// secret. Blank = fall back to the config-bound <c>Sessionize:EndpointId</c>.
    /// </summary>
    public string? EndpointId { get; set; }

    /// <summary>
    /// The Sessionize v2 "view" to pull (Speakers / All / Sessions / …), stored as
    /// the enum name. Blank = use the config-bound default (<c>Speakers</c>).
    /// </summary>
    public string? View { get; set; }

    /// <summary>
    /// When the <see cref="EndpointId"/> last changed (the moment the organizer
    /// saved a different endpoint). Null until the first change. Used to surface
    /// "endpoint changed — choose how to re-import" on the settings page.
    /// </summary>
    public DateTimeOffset? EndpointLastChangedAt { get; set; }

    /// <summary>
    /// The endpoint id value the row held BEFORE the most recent change — kept so
    /// the UI can show "changed from … to …" and so a mistaken change can be
    /// identified. Null until the first change.
    /// </summary>
    public string? PreviousEndpointId { get; set; }

    /// <summary>
    /// The organizer's chosen change-handling mode for the most recent endpoint
    /// change (Replace ⇒ Full re-import; Merge ⇒ Delta merge). <see cref="None"/>
    /// until a choice is made. This is the choice that the import button maps to
    /// the importer's mode; it does NOT itself run an import.
    /// </summary>
    public SessionizeChangeMode PendingChangeMode { get; set; } = SessionizeChangeMode.None;

    /// <summary>When the organizer last recorded a change-handling choice.</summary>
    public DateTimeOffset? ChangeModeChosenAt { get; set; }

    /// <summary>Email of the organizer who last saved this setting (audit only).</summary>
    public string? LastUpdatedByEmail { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// True when an endpoint change has been recorded but no change-handling choice
    /// has been made yet — i.e. the organizer still needs to pick Replace or Merge.
    /// Drives the confirmation prompt on the settings page.
    /// </summary>
    public bool AwaitingChangeChoice =>
        EndpointLastChangedAt is not null
        && PendingChangeMode == SessionizeChangeMode.None;
}
