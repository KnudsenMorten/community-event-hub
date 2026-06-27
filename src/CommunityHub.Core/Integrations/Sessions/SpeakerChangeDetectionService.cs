using System.Globalization;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// The §38e/§58 SPEAKER CHANGE DETECTION engine (operator 2026-06-26) — the SPEAKER analogue
/// of <see cref="SessionChangeDetectionService"/>. It pulls the CURRENT Zoho Backstage
/// speakers, matches each CEH <see cref="SpeakerProfile"/> by its stored
/// <see cref="SpeakerProfile.BackstageSpeakerId"/> (the link), and — when the Backstage
/// name/tagline/bio/country/linkedin/twitter differs from the value CEH last stored AND that
/// stored baseline was already populated (a real CHANGE, not the first populate) — ENQUEUES a
/// Pending <see cref="SyncDelta"/> (EntityType=Speaker, ChangeKind=Update, Source=ZohoToCeh)
/// to the §59 approval queue. On every pass it refreshes the stored <c>Backstage*</c>
/// snapshot for first-populates + stamps <see cref="SpeakerProfile.BackstageChangeCheckedAt"/>.
///
/// <b>FIRST POPULATE is silent.</b> When the stored Backstage* values are all null the engine
/// SEEDS them and NEVER enqueues (there is no "previous" to have changed from).
///
/// <b>NEVER DELETES.</b> A Zoho speaker missing from CEH (or a CEH speaker missing from Zoho)
/// produces NO auto-action — the §58 never-auto-delete rule. The disappearance/queue handles
/// that separately; this engine only seeds + detects updates on LINKED speakers.
///
/// <b>GATES.</b> (1) the <c>speaker-change-alerts</c> feature kill switch is enabled; (2) the
/// edition's SPEAKER sync direction is stage 3 (<see cref="SessionSyncDirection.ZohoToCeh"/>)
/// — else the engine returns <see cref="Result.Inactive"/> and writes nothing, exactly as the
/// §38e session engine gates on the SESSION direction. The change is enqueued, not emailed
/// inline; any user-facing effect happens on operator APPROVE in the queue.
///
/// <b>SOURCE AVAILABILITY.</b> The speakers API needs the <c>ZohoBackstage.speaker.READ</c>
/// scope. Until granted (and <see cref="ZohoOptions.SpeakerReadEnabled"/> set) the pull
/// returns IsAvailable=false and this engine NO-OPS with a clear result — it never treats an
/// empty pull as "everything changed" (mirrors §38e Unavailable).
/// </summary>
public sealed class SpeakerChangeDetectionService
{
    /// <summary>The §58 feature kill switch for speaker change detection.</summary>
    public const string FeatureKey = "speaker-change-alerts";

    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _zohoOptions;
    private readonly FeatureGateService _gate;
    private readonly TimeProvider _clock;
    private readonly SyncDeltaQueueService? _queue;

    // Overridable pull seam (default = the real ZohoClient speakers call). Tests inject a
    // canned BackstageSpeakersResult so the engine is exercised without HTTP.
    private readonly Func<CancellationToken, Task<BackstageSpeakersResult>>? _pullOverride;

    public SpeakerChangeDetectionService(
        CommunityHubDbContext db, ZohoClient zoho, ZohoOptions zohoOptions,
        FeatureGateService gate,
        TimeProvider? clock = null,
        Func<CancellationToken, Task<BackstageSpeakersResult>>? pullOverride = null,
        SyncDeltaQueueService? queue = null)
    {
        _db = db; _zoho = zoho; _zohoOptions = zohoOptions; _gate = gate;
        _clock = clock ?? TimeProvider.System;
        _pullOverride = pullOverride;
        _queue = queue;
    }

    /// <summary>The outcome of one detection pass, for the job log + tests.</summary>
    public sealed record Result(
        bool SourceAvailable,
        string? UnavailableReason,
        int Matched,
        int Seeded,
        int Changed,
        int Unmatched = 0,
        bool DirectionInactive = false,
        int Enqueued = 0)
    {
        public static Result Unavailable(string reason) =>
            new(false, reason, 0, 0, 0, 0);

        /// <summary>
        /// §58 gate: the edition's SPEAKER sync direction is not stage 3 (Zoho→CEH), so this
        /// engine is INACTIVE — it pulls nothing, matches nothing, and writes nothing.
        /// </summary>
        public static Result Inactive(string reason) =>
            new(false, reason, 0, 0, 0, 0, DirectionInactive: true);
    }

    /// <summary>
    /// Run one detection pass for an edition. Pulls the current Backstage speakers, diffs each
    /// LINKED CEH speaker, seeds first-populates silently, and enqueues a Pending ZohoToCeh
    /// Update delta for each real change (gated on §58 stage 3 + the feature kill switch).
    /// </summary>
    public async Task<Result> RunAsync(int eventId, CancellationToken ct = default)
    {
        // §58 DIRECTION GATE — only stage 3 (SpeakerSyncDirection == ZohoToCeh) is active.
        var direction = await _db.SessionSourceSettings.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => (SessionSyncDirection?)s.SpeakerSyncDirection)
            .FirstOrDefaultAsync(ct) ?? SessionSyncDirection.SessionizeToCeh;
        if (direction != SessionSyncDirection.ZohoToCeh)
        {
            return Result.Inactive(
                $"speaker sync direction is stage {(int)direction} ({direction}) — Zoho→CEH change detection inactive");
        }

        // Pull the current speakers. Unavailable ⇒ no-op (never fake / never enqueue).
        var pull = await PullAsync(ct);
        if (!pull.IsAvailable)
        {
            return Result.Unavailable(pull.UnavailableReason ?? "Backstage speakers unavailable.");
        }

        // LINKED CEH speaker profiles (those with a BackstageSpeakerId), keyed by that id +
        // joined to their participant email/name for the queue label. We match ONLY by the
        // stored link (never auto-create a link by name/email — the §58 push owns linking).
        var linked = await _db.SpeakerProfiles
            .Where(p => p.EventId == eventId && p.BackstageSpeakerId != null && p.BackstageSpeakerId != "")
            .Join(_db.Participants, p => p.ParticipantId, pa => pa.Id,
                (p, pa) => new { Profile = p, pa.Email, pa.FullName })
            .ToListAsync(ct);

        var byBackstageId = new Dictionary<string, (SpeakerProfile Profile, string? Email, string? FullName)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in linked)
            byBackstageId[row.Profile.BackstageSpeakerId!] = (row.Profile, row.Email, row.FullName);

        var featureEnabled = await _gate.IsFeatureEnabledAsync(FeatureKey, eventId, ct);
        var now = _clock.GetUtcNow();
        var pendingBefore = _queue is null ? 0 : await _queue.CountPendingAsync(eventId, ct);

        int matched = 0, seeded = 0, changed = 0, unmatched = 0, enqueued = 0;

        foreach (var current in pull.Speakers)
        {
            if (string.IsNullOrWhiteSpace(current.SpeakerId)) { unmatched++; continue; }
            if (!byBackstageId.TryGetValue(current.SpeakerId, out var hit))
            {
                // A Zoho speaker not linked to any CEH profile → no auto-action (never create,
                // never delete). Disappearance/linking is handled elsewhere.
                unmatched++;
                continue;
            }

            var profile = hit.Profile;
            matched++;

            // The stored Backstage* baseline. All-null ⇒ FIRST POPULATE.
            var hadAnyStored = profile.BackstageName is not null
                || profile.BackstageTagline is not null
                || profile.BackstageBio is not null
                || profile.BackstageCountry is not null
                || profile.BackstageLinkedIn is not null
                || profile.BackstageTwitter is not null;

            if (!hadAnyStored)
            {
                // FIRST populate: seed the baseline silently (auto-write is correct — there is
                // no "previous" to approve). Stamp the check time.
                profile.BackstageName = current.Name;
                profile.BackstageTagline = current.Tagline;
                profile.BackstageBio = current.Bio;
                profile.BackstageCountry = current.Country;
                profile.BackstageLinkedIn = current.LinkedIn;
                profile.BackstageTwitter = current.Twitter;
                profile.BackstageChangeCheckedAt = now;
                seeded++;
                continue;
            }

            // Always stamp the check time (proof the engine ran) — not a content write.
            profile.BackstageChangeCheckedAt = now;

            var fieldChanges = SyncDeltaQueueService.BuildSpeakerZohoChanges(
                profile.BackstageName, profile.BackstageTagline, profile.BackstageBio,
                profile.BackstageCountry, profile.BackstageLinkedIn, profile.BackstageTwitter,
                current.Name, current.Tagline, current.Bio,
                current.Country, current.LinkedIn, current.Twitter);

            if (fieldChanges.Count == 0) continue; // nothing changed

            // A REAL change: do NOT overwrite the stored Backstage* baseline here (so the
            // old→new diff survives until a decision); ENQUEUE a Pending Update delta for the
            // operator. Kept gated on the feature kill switch.
            changed++;
            if (_queue is null || !featureEnabled) continue;

            var label = string.IsNullOrWhiteSpace(hit.FullName)
                ? (string.IsNullOrWhiteSpace(hit.Email) ? "(unnamed speaker)" : hit.Email!)
                : hit.FullName!;

            await _queue.EnqueueAsync(new SyncDelta
            {
                EventId = eventId,
                EntityType = SyncDeltaEntityType.Speaker,
                EntityId = profile.ParticipantId.ToString(CultureInfo.InvariantCulture),
                EntityLabel = label,
                Source = SessionSyncDirection.ZohoToCeh,
                ChangeKind = SyncDeltaChangeKind.Update,
                Changes = fieldChanges,
            }, ct);
            enqueued++;
        }

        if (matched > 0) await _db.SaveChangesAsync(ct);

        // Notify the operator if this pass newly increased the pending queue.
        if (_queue is not null && enqueued > 0)
        {
            await _queue.NotifyNewAsync(eventId, pendingBefore, ct);
        }

        return new Result(true, null, matched, seeded, changed, unmatched, Enqueued: enqueued);
    }

    /// <summary>Pull the current Backstage speakers (test seam overrides the live call).</summary>
    private async Task<BackstageSpeakersResult> PullAsync(CancellationToken ct)
    {
        if (_pullOverride is not null) return await _pullOverride(ct);

        var token = await _zoho.GetAccessTokenAsync(ct);
        if (token is null)
        {
            return BackstageSpeakersResult.Unavailable("No Zoho access token (token refresh failed).");
        }
        return await _zoho.GetBackstageSpeakersAsync(token, ct);
    }
}
