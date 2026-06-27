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
    string? Error);

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
    string? Track,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    bool IsServiceSession,
    IReadOnlyList<string> SpeakerIds,
    // Source category / format label (Sessionize categoryItems / Backstage format),
    // when present, used to derive the hub SessionType (see SessionDefaultsMapper).
    // Null when the source carries no category/format for the session.
    string? Category = null);

/// <summary>The outcome of parsing the Sessionize sessions view.</summary>
public sealed record SessionizeSessionsParseResult(
    IReadOnlyList<SessionizeSession> Sessions,
    IReadOnlyList<string> Warnings,
    string? Error);
