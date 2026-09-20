namespace CommunityHub.Core.Integrations;

// Sessionize speaker / session DTOs shared by the v2 view API client
// (SessionizeApiClient) and the import pipeline (SessionizeImportService /
// SessionizeImportPreviewService). The legacy Excel/.xlsx parser that also
// produced these was removed — §82, API-only now.

/// <summary>A speaker row read from the Sessionize v2 view API.</summary>
public sealed record SessionizeSpeaker(
    string Email,
    string FirstName,
    string LastName,
    string? TagLine,
    string? Biography = null,
    string? Blog = null,
    string? LinkedIn = null,
    string? Twitter = null,
    string? ProfilePictureUrl = null,
    // The Sessionize speaker id (GUID) from the v2 view API. Used to link
    // sessions to their speaker(s) by id (the Sessionize session carries a
    // speakers id array).
    string SessionizeId = "");

/// <summary>The outcome of parsing a Sessionize speaker source.</summary>
public sealed record SessionizeParseResult(
    IReadOnlyList<SessionizeSpeaker> Speakers,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    /// <summary>
    /// §204 — speakers the source carried WITHOUT an email but WITH a Sessionize
    /// speaker id (almost always: they haven't accepted their Sessionize invite
    /// yet). These are NOT importable as login-capable participants (no email),
    /// but instead of being silently dropped they are surfaced here so the import
    /// pipeline can land them in the PRE-SELECTION QUEUE (inactive, keyed by
    /// Sessionize id) — the organizer can then SEE and act on them. Empty by
    /// default so every existing construction is unaffected. An email-less speaker
    /// with NO Sessionize id stays a plain skip+warning (there is no stable key to
    /// dedup/reconcile it on).
    /// </summary>
    public IReadOnlyList<SessionizeSpeaker> EmailLessSpeakers { get; init; }
        = Array.Empty<SessionizeSpeaker>();
}

/// <summary>
/// A session read from the Sessionize v2 view API (the <c>All</c>/<c>Sessions</c>
/// view, alongside speakers). <see cref="SpeakerIds"/> holds the Sessionize speaker
/// ids the session is delivered by; the session importer resolves each to the
/// participant the speaker import created.
/// </summary>
public sealed record SessionizeSession(
    string SessionizeId,
    string Title,
    string? Abstract,
    string? Room,
    // The CLEAN track (§154): resolved from the Sessionize "Suggested Event Track"
    // category GROUP (NOT the first/Format category, which the parser used to grab
    // by mistake). Null when the source carries no track group for the session.
    string? Track,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    bool IsServiceSession,
    IReadOnlyList<string> SpeakerIds,
    // Source category / format label (Sessionize Format group / Backstage format),
    // when present, used to derive the hub SessionType (see SessionDefaultsMapper).
    // Null when the source carries no category/format for the session.
    string? Category = null,
    // §154: audience level from the Sessionize "Level" category GROUP (e.g.
    // "Expert (400)"). Null when the source carries no level group.
    string? Level = null,
    // §154: numeric length in minutes, parsed from the Format label's "(NN min)"
    // (or the scheduled duration when the grid is published). Null when no minutes
    // can be determined (e.g. a Master Class with no "(NN min)" hint).
    int? LengthMinutes = null,
    // §299.8/b7: comma-separated tag labels from the Sessionize "Tags" category
    // GROUP, when the v2 payload provides one. Null when the API omits tags (the
    // default — every existing construction is unaffected).
    string? Tags = null,
    // 🔴 §1241 — the Sessionize `isConfirmed` flag: the SPEAKER has confirmed they will
    // deliver this session. It is the red "not confirmed" badge on the Sessionize sessions
    // grid, alongside the green "informed" date.
    //
    // 🔒 DEFAULTS TO TRUE, AND THAT DIRECTION IS DELIBERATE. The importer now REFUSES to
    // import a session with this false (operator 2026-09-20: *"importer should only import
    // if IsConfirmed = true"*), so a payload shape that does not carry the field — an older
    // export, a different view, a test constructing this record — must read as "confirmed"
    // and import. Defaulting to false would turn one missing JSON key into an import that
    // silently brings in NOTHING, which is the §326ax failure: an absent signal must never
    // be read as a negative one.
    bool IsConfirmed = true);

/// <summary>The outcome of parsing the Sessionize sessions view.</summary>
public sealed record SessionizeSessionsParseResult(
    IReadOnlyList<SessionizeSession> Sessions,
    IReadOnlyList<string> Warnings,
    string? Error);
