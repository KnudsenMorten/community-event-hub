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

    /// <summary>
    /// §1001 — the date from which a room/time change starts NOTIFYING SPEAKERS. Before it,
    /// schedule changes are applied silently. Null ⇒ notifications are off entirely.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-09: *"we need to define a date in the settings which is where
    /// notifications starts to flow to speakers in case of room or session time changes … Before
    /// that date, no session notifications must go to speakers … reason we make lots of schedule
    /// changes and we dont want to make unnessary noice to speakers."*</para>
    ///
    /// <para>🔑 <b>The quiet period is the point, not a side effect.</b> Building the agenda means
    /// moving sessions repeatedly, and a speaker mailed on every move learns to ignore the mail
    /// before the one that matters arrives. The gate protects the CREDIBILITY of the notification,
    /// which is the same §594 reasoning applied to timing rather than to content.</para>
    ///
    /// <para>🔒 <b>The change still APPLIES during the quiet period</b> — only the mail is withheld.
    /// The hub, the public agenda and the speaker's own page always show the truth; nobody is
    /// looking at a stale schedule, they are simply not being pinged about each step.</para>
    ///
    /// <para>⚠️ <b>A DATE, not "days before".</b> Stored absolute so it cannot silently move when
    /// the event dates are edited — an organizer who set "quiet until 12 Dec" means that day. It is
    /// SEEDED at event start − 60 days (the operator's number) and is editable from there.</para>
    ///
    /// <para>🗑 There was a date gate here once (§38e) and §234 deleted it as dead code — it was a
    /// hardcoded constant tied to a go-live that had passed. This one is operator-set, which is why
    /// it is a settings field and not a <c>const</c>.</para>
    /// </remarks>
    public DateOnly? SpeakerScheduleNoticeFrom { get; set; }

    /// <summary>§1001 — the default quiet period: notifications begin 60 days before the event.</summary>
    public const int DefaultSpeakerNoticeDaysBeforeEvent = 60;

    /// <summary>
    /// §1001 — true when a speaker may be told about a schedule change right now.
    /// </summary>
    /// <remarks>
    /// 🔒 A NULL date means SILENT, deliberately. The alternative — null meaning "always notify" —
    /// would make a settings row that has never been saved mail every speaker on the first agenda
    /// edit, which is the exact noise the operator asked to remove. Silence is the safe failure.
    /// </remarks>
    public bool MayNotifySpeakers(DateOnly today) =>
        SpeakerScheduleNoticeFrom is { } from && today >= from;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedByEmail { get; set; }
}
