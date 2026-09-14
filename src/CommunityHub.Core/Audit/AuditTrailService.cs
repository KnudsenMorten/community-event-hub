using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Audit;

/// <summary>
/// Stable action codes for the audit trail (REQUIREMENTS §24). Auto-captured user
/// actions use a <c>METHOD path</c> code; these name the explicit engine/backend events
/// so usage can be counted reliably regardless of UI wording.
/// </summary>
public static class AuditActions
{
    public const string EmailSent = "email.sent";
    public const string CalendarSubscribe = "calendar.subscribe";
    public const string CalendarTokenReset = "calendar.token-reset";
    public const string SignIn = "auth.sign-in";
    public const string SignOut = "auth.sign-out";
    public const string MagicLink = "auth.magic-link";

    /// <summary>REQUIREMENTS §551 — the edition's SESSION sync stage was changed (from → to).</summary>
    public const string SessionSyncDirectionChanged = "sync.direction.session";

    /// <summary>REQUIREMENTS §551 — the edition's SPEAKER sync stage was changed (from → to).</summary>
    public const string SpeakerSyncDirectionChanged = "sync.direction.speaker";

    // ===================================================================
    //  §728 — what a PERSON did, NAMED. Operator 2026-07-31: *"i would like also to see things
    //  like selected master class, cancelled master class, signed up for waitlist, party sign-up,
    //  etc"*.
    //
    //  🔑 None of this was ever MISSING from the trail — AuditPageFilter captures every POST. The
    //  problem was that every wizard step posts to ONE handler, so choosing a Master Class, joining
    //  a waitlist, signing up for the party, saving a profile and accepting the Code of Conduct
    //  were 302 identical `POST /Forms/Wizard` rows. The auto-capture answers "who posted to what
    //  page"; he is asking "what did this person DO". A stable code per action is what makes the
    //  trail filterable at that volume.
    // ===================================================================

    /// <summary>A Get-Started wizard step was saved — the summary names WHICH step.</summary>
    public const string WizardStepSaved = "wizard.step-saved";

    /// <summary>An attendee gave up their confirmed Master Class seat.</summary>
    public const string MasterClassCancel = "masterclass.cancel";

    /// <summary>A task was ticked off / re-opened from the task list.</summary>
    public const string TaskComplete = "task.complete";
    public const string TaskReopen = "task.reopen";

    /// <summary>§1216 — one LinkedIn company-page post went out (per post, not per run).</summary>
    public const string SoMePostPublished = "some.post-published";

    /// <summary>§1216 — one LinkedIn company-page post failed to publish.</summary>
    public const string SoMePostFailed = "some.post-failed";
}

/// <summary>
/// The single seam every audited action funnels through (REQUIREMENTS §24). Append-only,
/// edition-scoped, never throws into the caller (an audit-write failure must never break
/// the action it records). Mirrors the shape of <c>ImpersonationAuditService</c>.
/// </summary>
public interface IAuditTrail
{
    /// <summary>Record one audit entry. Best-effort: swallows its own write errors.</summary>
    Task RecordAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>
    /// RETENTION (REQUIREMENTS §24): delete audit entries OLDER than
    /// <paramref name="cutoffUtc"/> across all editions. Returns the number removed.
    /// Called by the daily purge job; uses a set-based delete (no entity load).
    /// </summary>
    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class AuditTrailService : IAuditTrail
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public AuditTrailService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task RecordAsync(AuditEntry entry, CancellationToken ct = default)
    {
        try
        {
            if (entry.OccurredUtc == default) entry.OccurredUtc = _clock.GetUtcNow();
            // Defensive truncation so an oversized summary/detail never trips a column
            // length constraint and loses the whole entry.
            entry.Action = Trim(entry.Action, 200);
            entry.ActorEmail = Trim(entry.ActorEmail, 256);
            entry.Summary = Trim(entry.Summary, 1024);
            entry.Detail = entry.Detail is null ? null : Trim(entry.Detail, 4000);
            entry.Path = entry.Path is null ? null : Trim(entry.Path, 512);

            _db.AuditEntries.Add(entry);
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            // Auditing is observational — it must never break or fail the audited action.
        }
    }

    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        // Batched delete: bounded memory at scale + provider-agnostic (works on SQL in
        // prod AND the in-memory test provider, unlike ExecuteDeleteAsync). The daily
        // job deletes a moderate slice; the first run after 6 months may be larger.
        const int batchSize = 5000;
        var total = 0;
        while (true)
        {
            var batch = await _db.AuditEntries
                .Where(e => e.OccurredUtc < cutoffUtc)
                .OrderBy(e => e.Id)
                .Take(batchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            _db.AuditEntries.RemoveRange(batch);
            await _db.SaveChangesAsync(ct);
            total += batch.Count;
            if (batch.Count < batchSize) break;
        }
        return total;
    }

    private static string Trim(string? s, int max)
    {
        s ??= string.Empty;
        return s.Length <= max ? s : s.Substring(0, max);
    }
}
