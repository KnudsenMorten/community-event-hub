using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// The §58 STAGE 2 (CehToZoho) SPEAKER PUSH engine. For each CEH speaker it CREATES the
/// speaker in Zoho Backstage when it has no <see cref="SpeakerProfile.BackstageSpeakerId"/>
/// (then stores the returned id). The Backstage v3 speakers API is CREATE-ONLY (no update
/// endpoint, verified 2026-06-25), so an already-linked speaker is left ALONE (no
/// duplicate, no delete) — manual Backstage edits handle in-place changes (see
/// <see cref="SpeakerBioBackstageSyncService"/> for the alert-on-existing path). This keeps
/// the link 1:1 by the stored id (idempotent) and NEVER deletes.
///
/// <b>§58 DIRECTION GATE.</b> Active only when the edition's SPEAKER sync direction is
/// stage 2 (<see cref="SessionSyncDirection.CehToZoho"/>, on
/// <see cref="SessionSourceSetting.SpeakerSyncDirection"/> — SEPARATE from the session
/// direction). At the default stage 1 and at stage 3 it is INERT.
///
/// <b>Publish gate.</b> <c>featured</c> tracks <see cref="SpeakerProfile.SelectedForPublish"/>
/// so an unselected speaker is created non-featured (never highlighted/published).
/// </summary>
public sealed class SpeakerBackstagePushService
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _zohoOptions;

    private readonly Func<CancellationToken, Task<string?>>? _tokenOverride;

    // §59: when an ALREADY-LINKED speaker would be UPDATED, ENQUEUE a CehToZoho Update delta
    // for operator approval instead of acting inline (NEW speakers still create directly). LAZY
    // (Func) so DI builds this push service WITHOUT eagerly constructing the queue (the queue
    // depends back on this service for apply-on-approve). Null ⇒ no queue wired.
    private readonly Func<SyncDeltaQueueService>? _queueFactory;

    public SpeakerBackstagePushService(
        CommunityHubDbContext db, ZohoClient zoho, ZohoOptions zohoOptions,
        Func<CancellationToken, Task<string?>>? tokenOverride = null,
        Func<SyncDeltaQueueService>? queueFactory = null)
    {
        _db = db; _zoho = zoho; _zohoOptions = zohoOptions;
        _tokenOverride = tokenOverride; _queueFactory = queueFactory;
    }

    public enum PushAction { Skipped = 0, Created = 1, AlreadyLinked = 2, Failed = 3, Enqueued = 4 }

    public sealed record SpeakerPushResult(
        int ParticipantId, string Email, PushAction Action, string? BackstageId = null, string? Error = null);

    public sealed record Result(
        bool DirectionActive,
        string? InactiveReason,
        bool SourceAvailable,
        string? UnavailableReason,
        int Created,
        int AlreadyLinked,
        int Failed,
        int Skipped,
        IReadOnlyList<SpeakerPushResult> Items,
        int Enqueued = 0)
    {
        public static Result Inactive(string reason) =>
            new(false, reason, false, null, 0, 0, 0, 0, Array.Empty<SpeakerPushResult>());

        public static Result Unavailable(string reason) =>
            new(true, null, false, reason, 0, 0, 0, 0, Array.Empty<SpeakerPushResult>());
    }

    /// <summary>
    /// Run one push pass for an edition. Gated on §58 stage 2 (SpeakerSyncDirection ==
    /// CehToZoho). Creates each not-yet-linked speaker in Zoho and stores the id; leaves
    /// already-linked speakers untouched. Never deletes.
    /// </summary>
    public async Task<Result> RunAsync(int eventId, CancellationToken ct = default)
    {
        // §58 DIRECTION GATE — only stage 2 (CehToZoho) on the SPEAKER direction is active.
        var direction = await _db.SessionSourceSettings.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => (SessionSyncDirection?)s.SpeakerSyncDirection)
            .FirstOrDefaultAsync(ct) ?? SessionSyncDirection.SessionizeToCeh;
        if (direction != SessionSyncDirection.CehToZoho)
        {
            return Result.Inactive(
                $"speaker sync direction is stage {(int)direction} ({direction}) — CEH→Zoho push inactive");
        }

        var token = await GetTokenAsync(ct);
        if (token is null)
            return Result.Unavailable("No Zoho access token (token refresh failed).");

        // Each speaker profile + its participant identity (email/name).
        var speakers = await _db.SpeakerProfiles
            .Where(p => p.EventId == eventId)
            .Join(_db.Participants, p => p.ParticipantId, pa => pa.Id,
                (p, pa) => new { Profile = p, pa.Email })
            .ToListAsync(ct);

        int created = 0, alreadyLinked = 0, failed = 0, skipped = 0, enqueued = 0;
        var items = new List<SpeakerPushResult>(speakers.Count);
        var queue = _queueFactory?.Invoke();
        var prevPending = queue is not null ? await queue.CountPendingAsync(eventId, ct) : 0;

        foreach (var row in speakers)
        {
            var p = row.Profile;
            var email = row.Email;

            if (string.IsNullOrWhiteSpace(email))
            {
                skipped++;
                items.Add(new SpeakerPushResult(p.ParticipantId, email ?? string.Empty, PushAction.Skipped,
                    p.BackstageSpeakerId, "speaker has no email — not pushed"));
                continue;
            }

            // Already linked. §59: when a queue is wired, an UPDATE of a linked speaker is
            // ENQUEUED as a CehToZoho Update delta for operator approval rather than acted on
            // inline; the apply arm pushes on approve. Without a queue, keep the legacy
            // create-only behaviour (leave it alone — no duplicate, no delete).
            if (!string.IsNullOrWhiteSpace(p.BackstageSpeakerId))
            {
                if (queue is not null)
                {
                    await queue.EnqueueAsync(new SyncDelta
                    {
                        EventId = eventId,
                        EntityType = SyncDeltaEntityType.Speaker,
                        EntityId = p.ParticipantId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        EntityLabel = string.IsNullOrWhiteSpace(p.FirstName) && string.IsNullOrWhiteSpace(p.LastName)
                            ? email : $"{p.FirstName} {p.LastName}".Trim(),
                        Source = SessionSyncDirection.CehToZoho,
                        ChangeKind = SyncDeltaChangeKind.Update,
                        Changes = BuildSpeakerPushChanges(p, email),
                    }, ct);
                    enqueued++;
                    items.Add(new SpeakerPushResult(p.ParticipantId, email, PushAction.Enqueued, p.BackstageSpeakerId,
                        "update enqueued for operator approval"));
                    continue;
                }

                alreadyLinked++;
                items.Add(new SpeakerPushResult(p.ParticipantId, email, PushAction.AlreadyLinked, p.BackstageSpeakerId));
                continue;
            }

            var id = await _zoho.CreateSpeakerAsync(
                token, email, p.FirstName, p.LastName, p.Country,
                p.Tagline, p.Biography, p.LinkedIn, p.Twitter,
                skills: p.MvpCategories ?? p.Accreditation,
                featured: p.SelectedForPublish, ct);

            if (id is null)
            {
                failed++;
                items.Add(new SpeakerPushResult(p.ParticipantId, email, PushAction.Failed, null,
                    "Zoho speaker create failed (see logs)"));
                continue;
            }

            // id == "" means created but id not parsed — record the create without storing a link.
            if (id.Length > 0)
            {
                p.BackstageSpeakerId = id;
                p.UpdatedAt = DateTimeOffset.UtcNow;
            }
            created++;
            items.Add(new SpeakerPushResult(p.ParticipantId, email, PushAction.Created,
                id.Length > 0 ? id : null));
        }

        if (created > 0) await _db.SaveChangesAsync(ct);

        // §59: notify the operator if any linked-speaker update was enqueued (throttled).
        if (queue is not null && enqueued > 0)
            await queue.NotifyNewAsync(eventId, prevPending, ct);

        return new Result(true, null, true, null, created, alreadyLinked, failed, skipped, items, enqueued);
    }

    /// <summary>
    /// Build the {Field, Old, New} diff list for a CehToZoho speaker UPDATE delta. CEH is the
    /// source of truth, so NewValue carries the current CEH value an approve would push;
    /// OldValue is null. Only non-blank fields are included.
    /// </summary>
    private static IReadOnlyList<SyncFieldChange> BuildSpeakerPushChanges(SpeakerProfile p, string email)
    {
        var list = new List<SyncFieldChange>();
        var name = $"{p.FirstName} {p.LastName}".Trim();
        if (!string.IsNullOrWhiteSpace(name))
            list.Add(new SyncFieldChange(SyncDeltaQueueService.FieldName, null, name));
        if (!string.IsNullOrWhiteSpace(p.Tagline))
            list.Add(new SyncFieldChange("Tagline", null, p.Tagline));
        if (!string.IsNullOrWhiteSpace(p.Biography))
            list.Add(new SyncFieldChange("Biography", null, p.Biography));
        return list;
    }

    /// <summary>
    /// Apply an approved CehToZoho speaker UPDATE on approve (REQUIREMENTS §59). The Backstage
    /// v3 speakers API is CREATE-ONLY (no update endpoint, verified 2026-06-25), so there is no
    /// in-place push: this acknowledges the approval and reports that the Backstage edit must be
    /// made manually. It NEVER creates a duplicate and NEVER deletes. Returns (ok, message).
    /// </summary>
    public async Task<(bool Ok, string Message)> UpdateLinkedSpeakerAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var profile = await _db.SpeakerProfiles
            .FirstOrDefaultAsync(p => p.ParticipantId == participantId && p.EventId == eventId, ct);
        if (profile is null)
            return (false, "The speaker no longer exists in this edition.");
        if (string.IsNullOrWhiteSpace(profile.BackstageSpeakerId))
            return (false, "The speaker is not linked to a Zoho speaker (nothing to update).");

        // Create-only API: record the acknowledgement; the Backstage speaker edit is manual.
        return (true,
            "Acknowledged — the Backstage speaker API is create-only; apply the bio/profile "
            + "change manually in Backstage. CEH never creates a duplicate or deletes.");
    }

    private async Task<string?> GetTokenAsync(CancellationToken ct) =>
        _tokenOverride is not null ? await _tokenOverride(ct) : await _zoho.GetAccessTokenAsync(ct);
}
