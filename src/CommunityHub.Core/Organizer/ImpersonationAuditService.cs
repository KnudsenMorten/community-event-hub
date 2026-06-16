using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// Append-only writer + reader for the <see cref="ImpersonationAudit"/> trail.
/// Every acting-as boundary (start / return) and every on-behalf write is
/// recorded so the organizer team can answer "who acted as whom, when, how, and
/// what did they change?". Records are never updated or deleted by the app.
/// </summary>
public sealed class ImpersonationAuditService
{
    /// <summary>Action codes (kept stable for the audit list filters/labels).</summary>
    public const string ActionStart        = "start";
    public const string ActionReturn       = "return";
    public const string ActionModifyHotel  = "modify-hotel";
    public const string ActionModifySwag   = "modify-swag";
    public const string ActionSecretaryUse = "secretary-use";
    public const string ActionDeactivate   = "deactivate";
    public const string ActionDelete       = "delete";

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public ImpersonationAuditService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Record one acting-as event. Always commits.</summary>
    public async Task RecordAsync(
        int eventId,
        ImpersonationActorKind actorKind,
        int? actorParticipantId,
        string actorLabel,
        int targetParticipantId,
        string action,
        string? detail = null,
        CancellationToken ct = default)
    {
        _db.ImpersonationAudits.Add(new ImpersonationAudit
        {
            EventId = eventId,
            ActorKind = actorKind,
            ActorParticipantId = actorParticipantId,
            ActorLabel = string.IsNullOrWhiteSpace(actorLabel) ? "(unknown)" : actorLabel.Trim(),
            TargetParticipantId = targetParticipantId,
            Action = action,
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Recent acting-as history for an edition, newest first.</summary>
    public async Task<IReadOnlyList<ImpersonationAudit>> RecentAsync(
        int eventId, int take = 100, CancellationToken ct = default)
        => await _db.ImpersonationAudits
            .Where(a => a.EventId == eventId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
}
