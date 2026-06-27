namespace CommunityHub.Core.Domain;

/// <summary>
/// The per-edition SESSION SYNC DIRECTION / stage (REQUIREMENTS §57). Exactly one
/// stage is active at a time; an organizer flips it from the Settings page. The §38e
/// Zoho→CEH change-detection engine is GATED on stage 3 — at the default
/// <see cref="SessionizeToCeh"/> (stage 1) §38e is inert and never writes Zoho→CEH.
/// </summary>
public enum SessionSyncDirection
{
    /// <summary>Stage 1 (DEFAULT / current) — import speakers + sessions from Sessionize into CEH.</summary>
    SessionizeToCeh = 1,

    /// <summary>Stage 2 (LATER) — push sessions from CEH to Zoho Backstage. Not yet implemented.</summary>
    CehToZoho = 2,

    /// <summary>Stage 3 (LATER) — pull Zoho Backstage session time/location into CEH (the §38e engine).</summary>
    ZohoToCeh = 3,
}

/// <summary>
/// Per-edition choice of which SESSION source is active (Sessionize vs Zoho
/// Backstage) — set by an organizer in Settings, so the switch is config/UI-driven
/// rather than a deploy (REQUIREMENTS §6). One row per edition (unique on
/// <see cref="EventId"/>); no row ⇒ the shipped default (Sessionize). Speakers are
/// always sourced from Sessionize; this governs only sessions. The same per-edition
/// row also carries the §57 <see cref="SyncDirection"/> stage.
/// </summary>
public class SessionSourceSetting
{
    public int Id { get; set; }

    /// <summary>Edition scope (unique).</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The active source key (<c>SessionSourceKinds</c>: sessionize | backstage).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// REQUIREMENTS §57 — the active session sync direction/stage. Defaults to
    /// <see cref="SessionSyncDirection.SessionizeToCeh"/> (stage 1) so the §38e
    /// Zoho→CEH engine stays inert until an organizer advances to stage 3.
    /// </summary>
    public SessionSyncDirection SyncDirection { get; set; } = SessionSyncDirection.SessionizeToCeh;

    /// <summary>
    /// REQUIREMENTS §58 — the active SPEAKER sync direction/stage, SEPARATE from the
    /// session <see cref="SyncDirection"/> and reusing the same <see cref="SessionSyncDirection"/>
    /// stages. Defaults to <see cref="SessionSyncDirection.SessionizeToCeh"/> (stage 1) so
    /// any future Zoho→CEH speaker change-detection engine stays inert until an organizer
    /// advances to stage 3. There is no speaker change-detection engine yet — this only
    /// persists the operator's choice + gates the (future) engine.
    /// </summary>
    public SessionSyncDirection SpeakerSyncDirection { get; set; } = SessionSyncDirection.SessionizeToCeh;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedByEmail { get; set; }
}
