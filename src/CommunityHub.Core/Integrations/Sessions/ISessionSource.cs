namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// The source the hub pulls SESSIONS from for an edition. Today the only active
/// source is <see cref="SessionSourceKinds.Sessionize"/>; the design lets a
/// community switch to <see cref="SessionSourceKinds.ZohoBackstage"/> (the
/// finalized agenda) later from an organizer Settings switch, with no
/// re-architecting (REQUIREMENTS §6 "pluggable session source").
///
/// Speakers are always imported from Sessionize (speaker profiles/bios); a
/// session source only supplies the SESSIONS + the speaker list used to LINK each
/// session to its already-imported participants.
/// </summary>
public interface ISessionSource
{
    /// <summary>Stable key matching the persisted setting (e.g. <c>sessionize</c>).</summary>
    string Key { get; }

    /// <summary>
    /// False when the source can't currently pull (not configured / missing scope).
    /// A resolver may still hand it back; <see cref="FetchSessionsAsync"/> then
    /// returns a result whose <see cref="SessionSourceResult.Error"/> explains why.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Fetch the edition's sessions + the speaker list to link them by.
    /// <paramref name="importedSpeakers"/> are the speakers the Sessionize speaker
    /// import just produced (some sources reuse them for id→email→participant
    /// linkage; a source with its own speaker identity returns its own list in
    /// <see cref="SessionSourceResult.LinkSpeakers"/>). Never throws — problems
    /// come back in the result.
    /// </summary>
    Task<SessionSourceResult> FetchSessionsAsync(
        int eventId,
        IReadOnlyList<SessionizeSpeaker> importedSpeakers,
        CancellationToken ct = default);
}

/// <summary>The well-known session-source keys (persisted + shown in Settings).</summary>
public static class SessionSourceKinds
{
    public const string Sessionize = "sessionize";
    public const string ZohoBackstage = "backstage";

    /// <summary>The default when no per-edition setting is stored.</summary>
    public const string Default = Sessionize;

    public static bool IsKnown(string? key) =>
        key is Sessionize or ZohoBackstage;
}

/// <summary>
/// What a session source returns: the sessions, the speaker list to link them by
/// (id/email → participant), any warnings, and a top-level error (sessions empty
/// when set). Mirrors <see cref="SessionizeSessionsParseResult"/> so the existing
/// <c>SessionImportService.ImportSessionsAsync</c> consumes it unchanged.
/// </summary>
public sealed record SessionSourceResult(
    IReadOnlyList<SessionizeSession> Sessions,
    IReadOnlyList<SessionizeSpeaker> LinkSpeakers,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    public static SessionSourceResult Failed(string error) =>
        new(Array.Empty<SessionizeSession>(), Array.Empty<SessionizeSpeaker>(),
            Array.Empty<string>(), error);
}
