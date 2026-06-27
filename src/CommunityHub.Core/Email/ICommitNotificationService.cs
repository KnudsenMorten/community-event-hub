using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;

namespace CommunityHub.Core.Email;

/// <summary>
/// §150 commit-notification chokepoint used by the allocation page handlers. The
/// allocation QUEUE is SILENT — the step-2 availability proposals and every lead /
/// organizer draft edit emit NO mail. Only a COMMIT notifies, exactly once per
/// affected person, batching their FINAL committed assignment set into a single
/// summary email.
///
/// The interface keeps the page handlers decoupled from the concrete
/// <see cref="CommitNotificationService"/> (and lets the web tests substitute a
/// counting double to prove "emails ONLY on commit"). The target role selects the
/// committing queue's feature key (Volunteer → <c>volunteer-allocation</c>,
/// Organizer → <c>organizer-allocation</c>) so the shared ring-gated email sender
/// applies the SAME ring gate + kill switch as all prod mail.
/// </summary>
public interface ICommitNotificationService
{
    /// <summary>
    /// Notify each person whose committed assignment set changed by a commit. No-op
    /// on an empty affected set; never mails the committing actor about their own
    /// action. The <paramref name="targetRole"/> picks the queue feature key used for
    /// the ring gate.
    /// </summary>
    Task NotifyCommitAsync(
        VolunteerStructureService.ActorContext actor,
        IReadOnlyList<int> affectedParticipantIds,
        ParticipantRole targetRole,
        CancellationToken ct = default);
}
