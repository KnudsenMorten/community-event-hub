using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// REQUIREMENTS §551 — the answer to "which stage is active, and did anyone actually
/// choose it?", which the plain <c>GetSyncDirectionAsync</c> reads cannot give.
///
/// <b>Why this type exists.</b> The stage reads end in <c>?? SessionizeToCeh</c>, so a
/// MISSING settings row is indistinguishable from a deliberate stage 1 — same value,
/// same UI, same job behaviour. When the row does not match the current active edition
/// (a new/changed <c>Event</c> row, a restore, a re-seed) the CEH→Zoho push silently
/// switches itself off with nobody having edited anything. That is exactly what the
/// operator hit while live: <i>"we have been at stage 2, something took it wrongly back
/// to stage 1"</i>. <see cref="IsConfigured"/> is the missing bit.
/// </summary>
/// <param name="Effective">The stage the engines obey (the stored one, or the stage-1 default).</param>
/// <param name="IsConfigured">
/// True when a settings row exists for this edition — i.e. the stage is a stored value and
/// not the fallback. False means NOTHING has ever set a stage for this edition.
/// </param>
/// <param name="LastChangeBy">Who last changed this stage, per the audit trail (null when never audited).</param>
/// <param name="LastChangeAt">When that change happened (null when never audited).</param>
/// <param name="LastChangeSummary">The audited from → to sentence (null when never audited).</param>
public sealed record SyncStageState(
    SessionSyncDirection Effective,
    bool IsConfigured,
    string? LastChangeBy = null,
    DateTimeOffset? LastChangeAt = null,
    string? LastChangeSummary = null);

/// <summary>
/// Reads + persists the per-edition active session source (REQUIREMENTS §6). No
/// row ⇒ the shipped default (<see cref="SessionSourceKinds.Default"/>). An unknown
/// stored value also falls back to the default, so a bad write can never wedge the
/// import.
/// </summary>
public sealed class SessionSourceSettingsService
{
    private readonly CommunityHubDbContext _db;
    private readonly IAuditTrail _audit;

    /// <param name="audit">
    /// REQUIREMENTS §551 — every stage change is audited (who, when, from → to). NOT
    /// optional: an <c>IAuditTrail?</c> defaulting to null is precisely the shape that
    /// leaves a host silently unaudited, which is the class of bug §657.1 is about.
    /// <see cref="IAuditTrail.RecordAsync"/> already swallows its own write errors, so a
    /// required dependency cannot break a stage change.
    /// </param>
    public SessionSourceSettingsService(CommunityHubDbContext db, IAuditTrail audit)
    {
        _db = db;
        _audit = audit;
    }

    /// <summary>The active source key for an edition (default when unset/unknown).</summary>
    public async Task<string> GetActiveKeyAsync(int eventId, CancellationToken ct = default)
    {
        var stored = await _db.SessionSourceSettings.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => s.Source)
            .FirstOrDefaultAsync(ct);
        return SessionSourceKinds.IsKnown(stored) ? stored! : SessionSourceKinds.Default;
    }

    /// <summary>
    /// Set the active source for an edition (upsert). Rejects an unknown key. Returns
    /// the stored key.
    /// </summary>
    public async Task<string> SetAsync(
        int eventId, string key, string? byEmail, CancellationToken ct = default)
    {
        if (!SessionSourceKinds.IsKnown(key))
            throw new ArgumentException($"Unknown session source '{key}'.", nameof(key));

        var row = await _db.SessionSourceSettings
            .FirstOrDefaultAsync(s => s.EventId == eventId, ct);
        if (row is null)
        {
            row = new SessionSourceSetting { EventId = eventId };
            _db.SessionSourceSettings.Add(row);
        }
        row.Source = key;
        row.UpdatedByEmail = byEmail;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return key;
    }

    /// <summary>
    /// The active §57 session sync direction/stage for an edition. No row (or a row
    /// that predates §57) ⇒ the default stage 1 (<see cref="SessionSyncDirection.SessionizeToCeh"/>),
    /// so §38e stays inert until an organizer advances to stage 3.
    ///
    /// <b>§551 — this read DELIBERATELY still collapses "unset" into stage 1</b>, because a gate must
    /// fail to the SAFEST stage: an edition with no settings row must never start pushing into Zoho
    /// on its own. Engines keep calling this. Anything that REPORTS the stage to a human must call
    /// <see cref="GetSyncDirectionStateAsync"/> instead, or it repeats the very bug §551 is about.
    /// </summary>
    public async Task<SessionSyncDirection> GetSyncDirectionAsync(
        int eventId, CancellationToken ct = default)
    {
        var stored = await _db.SessionSourceSettings.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => (SessionSyncDirection?)s.SyncDirection)
            .FirstOrDefaultAsync(ct);
        return stored ?? SessionSyncDirection.SessionizeToCeh;
    }

    /// <summary>
    /// REQUIREMENTS §551 — the session stage AND whether anybody actually chose it, plus the last
    /// audited change. Use this everywhere a human is told which stage is active.
    /// </summary>
    public async Task<SyncStageState> GetSyncDirectionStateAsync(
        int eventId, CancellationToken ct = default)
    {
        var stored = await _db.SessionSourceSettings.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => (SessionSyncDirection?)s.SyncDirection)
            .FirstOrDefaultAsync(ct);
        return await WithLastChangeAsync(
            eventId, stored, AuditActions.SessionSyncDirectionChanged, ct);
    }

    /// <summary>
    /// REQUIREMENTS §551 — the SPEAKER stage plus the same "was it ever chosen?" answer. The speaker
    /// stage has its own identical setting and its own identical stage-1 default, so it goes dark the
    /// same way and for the same reason.
    /// </summary>
    public async Task<SyncStageState> GetSpeakerSyncDirectionStateAsync(
        int eventId, CancellationToken ct = default)
    {
        var stored = await _db.SessionSourceSettings.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => (SessionSyncDirection?)s.SpeakerSyncDirection)
            .FirstOrDefaultAsync(ct);
        return await WithLastChangeAsync(
            eventId, stored, AuditActions.SpeakerSyncDirectionChanged, ct);
    }

    /// <summary>
    /// Builds the §551 state: the effective stage, whether it was stored or defaulted, and the most
    /// recent audited change. "Never audited" is reported as null rather than guessed — the row's own
    /// <c>UpdatedAt</c>/<c>UpdatedByEmail</c> stamp is SHARED by the source key and both stages, so it
    /// cannot answer "who moved THIS stage" and must not be shown as if it could.
    /// </summary>
    private async Task<SyncStageState> WithLastChangeAsync(
        int eventId, SessionSyncDirection? stored, string action, CancellationToken ct)
    {
        var last = await _db.AuditEntries.AsNoTracking()
            .Where(e => e.EventId == eventId && e.Action == action)
            .OrderByDescending(e => e.OccurredUtc).ThenByDescending(e => e.Id)
            .Select(e => new { e.ActorEmail, e.OccurredUtc, e.Summary })
            .FirstOrDefaultAsync(ct);

        return new SyncStageState(
            Effective: stored ?? SessionSyncDirection.SessionizeToCeh,
            IsConfigured: stored is not null,
            LastChangeBy: last?.ActorEmail,
            LastChangeAt: last?.OccurredUtc,
            LastChangeSummary: last?.Summary);
    }

    /// <summary>
    /// REQUIREMENTS §551 — record a stage change on the audit trail (who, when, from → to). An
    /// absent previous value is reported as NOT CONFIGURED rather than as stage 1, so the trail
    /// never repeats the ambiguity it exists to remove.
    /// </summary>
    private Task AuditStageChangeAsync(
        int eventId, string action, string subject,
        SessionSyncDirection? from, SessionSyncDirection to, string? byEmail, CancellationToken ct)
    {
        var summary =
            from is null
                ? $"{subject} sync stage set to stage {(int)to} ({to}) — previously NOT CONFIGURED "
                  + "for this edition, so the jobs were falling back to stage 1."
            : from == to
                ? $"{subject} sync stage re-saved as stage {(int)to} ({to}) — unchanged."
                : $"{subject} sync stage changed from stage {(int)from} ({from}) "
                  + $"to stage {(int)to} ({to}).";

        return _audit.RecordAsync(new AuditEntry
        {
            EventId = eventId,
            Category = AuditCategory.Admin,
            Action = action,
            ActorEmail = string.IsNullOrWhiteSpace(byEmail) ? "system" : byEmail!,
            TargetType = nameof(SessionSourceSetting),
            TargetId = eventId.ToString(),
            Summary = summary,
            Outcome = AuditOutcome.Success,
            Source = string.IsNullOrWhiteSpace(byEmail) ? AuditSource.System : AuditSource.Web,
        }, ct);
    }

    /// <summary>
    /// Set the active §57 sync direction/stage for an edition (upsert). Leaves the
    /// active <see cref="SessionSourceSetting.Source"/> untouched (it has a NOT NULL
    /// default of the shipped source) so flipping the stage alone is safe. Returns the
    /// stored direction.
    /// </summary>
    /// <summary>
    /// §1001 — the date speaker schedule-notices begin, seeding the default on first read.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>Seeded on READ, not by a migration.</b> The default is *event start − 60 days*
    /// (the operator's number), and a migration cannot compute it — the event dates live in another
    /// table and an edition created later would get nothing. Reading it here means every edition
    /// gets a sensible date the first time the page is opened, including future ones.</para>
    ///
    /// <para>🔒 Returns null (⇒ silent) when the edition has no start date to count back from.
    /// Silence is the safe failure: the alternative mails every speaker on the first agenda edit.</para>
    /// </remarks>
    public async Task<DateOnly?> GetSpeakerNoticeFromAsync(
        int eventId, CancellationToken ct = default)
    {
        var row = await _db.SessionSourceSettings
            .FirstOrDefaultAsync(s => s.EventId == eventId, ct);
        if (row?.SpeakerScheduleNoticeFrom is { } already) return already;

        var start = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId).Select(e => (DateOnly?)e.StartDate)
            .FirstOrDefaultAsync(ct);
        if (start is not { } s) return null;

        var seeded = s.AddDays(-SessionSourceSetting.DefaultSpeakerNoticeDaysBeforeEvent);
        if (row is null)
        {
            row = new SessionSourceSetting { EventId = eventId, Source = SessionSourceKinds.Default };
            _db.SessionSourceSettings.Add(row);
        }
        row.SpeakerScheduleNoticeFrom = seeded;
        await _db.SaveChangesAsync(ct);
        return seeded;
    }

    /// <summary>§1001 — set the date speaker schedule-notices begin. Null switches them off.</summary>
    public async Task SetSpeakerNoticeFromAsync(
        int eventId, DateOnly? from, string? byEmail, CancellationToken ct = default)
    {
        var row = await _db.SessionSourceSettings
            .FirstOrDefaultAsync(s => s.EventId == eventId, ct);
        if (row is null)
        {
            row = new SessionSourceSetting { EventId = eventId, Source = SessionSourceKinds.Default };
            _db.SessionSourceSettings.Add(row);
        }
        row.SpeakerScheduleNoticeFrom = from;
        row.UpdatedByEmail = byEmail;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<SessionSyncDirection> SetSyncDirectionAsync(
        int eventId, SessionSyncDirection direction, string? byEmail, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentException($"Unknown sync direction '{direction}'.", nameof(direction));

        var row = await _db.SessionSourceSettings
            .FirstOrDefaultAsync(s => s.EventId == eventId, ct);

        // §551 — capture the PREVIOUS stage before overwriting it. A null row is "never
        // configured", which the audit trail must not report as "was stage 1".
        var from = row?.SyncDirection;

        if (row is null)
        {
            // A fresh row needs a valid Source (NOT NULL). Seed it to the shipped default.
            row = new SessionSourceSetting { EventId = eventId, Source = SessionSourceKinds.Default };
            _db.SessionSourceSettings.Add(row);
        }
        row.SyncDirection = direction;
        row.UpdatedByEmail = byEmail;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await AuditStageChangeAsync(
            eventId, AuditActions.SessionSyncDirectionChanged, "Session", from, direction, byEmail, ct);
        return direction;
    }

    /// <summary>
    /// REQUIREMENTS §58 — the active SPEAKER sync direction/stage for an edition. No row
    /// (or a row that predates §58) ⇒ the default stage 1
    /// (<see cref="SessionSyncDirection.SessionizeToCeh"/>), so any future Zoho→CEH speaker
    /// change-detection stays inert until an organizer advances to stage 3. Independent of
    /// the session <see cref="GetSyncDirectionAsync"/>.
    /// </summary>
    public async Task<SessionSyncDirection> GetSpeakerSyncDirectionAsync(
        int eventId, CancellationToken ct = default)
    {
        var stored = await _db.SessionSourceSettings.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => (SessionSyncDirection?)s.SpeakerSyncDirection)
            .FirstOrDefaultAsync(ct);
        return stored ?? SessionSyncDirection.SessionizeToCeh;
    }

    /// <summary>
    /// REQUIREMENTS §58 — set the active SPEAKER sync direction/stage for an edition
    /// (upsert). Leaves the active <see cref="SessionSourceSetting.Source"/> and the session
    /// <see cref="SessionSourceSetting.SyncDirection"/> untouched, so flipping the speaker
    /// stage alone is safe. Returns the stored direction.
    /// </summary>
    public async Task<SessionSyncDirection> SetSpeakerSyncDirectionAsync(
        int eventId, SessionSyncDirection direction, string? byEmail, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentException($"Unknown sync direction '{direction}'.", nameof(direction));

        var row = await _db.SessionSourceSettings
            .FirstOrDefaultAsync(s => s.EventId == eventId, ct);

        // §551 — see SetSyncDirectionAsync: the previous stage is read before the overwrite.
        var from = row?.SpeakerSyncDirection;

        if (row is null)
        {
            // A fresh row needs a valid Source (NOT NULL). Seed it to the shipped default.
            row = new SessionSourceSetting { EventId = eventId, Source = SessionSourceKinds.Default };
            _db.SessionSourceSettings.Add(row);
        }
        row.SpeakerSyncDirection = direction;
        row.UpdatedByEmail = byEmail;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await AuditStageChangeAsync(
            eventId, AuditActions.SpeakerSyncDirectionChanged, "Speaker", from, direction, byEmail, ct);
        return direction;
    }

    /// <summary>
    /// REQUIREMENTS §58 GATE helper — true only when the edition's SPEAKER sync direction is
    /// stage 3 (<see cref="SessionSyncDirection.ZohoToCeh"/>). A future Zoho→CEH speaker
    /// change-detection engine MUST consult this before running, exactly as the §38e session
    /// engine gates on the session direction. At the default stage 1 (and stage 2) this is
    /// false, so the (not-yet-built) speaker engine stays inert.
    /// </summary>
    public async Task<bool> IsSpeakerZohoToCehActiveAsync(int eventId, CancellationToken ct = default) =>
        await GetSpeakerSyncDirectionAsync(eventId, ct) == SessionSyncDirection.ZohoToCeh;
}
