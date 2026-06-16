using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// The single server-side authority for REMOVING a participant from an edition
/// (REQUIREMENTS §21 participant CRUD). It mirrors the Hotels delete pattern:
/// the safe, default action is a <b>soft-delete</b> (deactivate) for anyone who
/// has any dependent data, and a <b>hard-delete</b> is offered only when it is
/// safe — i.e. the row has never engaged (no dependents that would be lost), in
/// which case the always-safe logistics links are cleaned first and the row is
/// physically removed.
///
/// Why soft-delete is the default: a participant fans out to ~25 dependent
/// tables (hotel bookings, swag, dinner/lunch sign-ups, sessions-as-speaker,
/// volunteer assignments, the acting-as audit trail, …). Many of those FKs are
/// <c>Restrict</c>/<c>NoAction</c>, so a blind hard-delete would either fail or
/// silently destroy real engagement. Deactivating keeps the history intact and
/// stops the person signing in — exactly what cancellation needs. Hard-delete is
/// reserved for the "added by mistake, never used" row.
///
/// Invariants (all enforced here, not in the page):
///   - Every operation is scoped to the caller's <c>eventId</c>; a participant in
///     another edition is never found, never touched.
///   - Soft-delete is idempotent: an already-inactive row stays inactive and is
///     reported as <see cref="DeletionStatus.AlreadyInactive"/>.
///   - Hard-delete first nulls the participant's hotel placement and removes the
///     handful of always-safe per-person logistics rows, then removes the
///     participant — one <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
///     so the whole thing commits together or not at all.
///   - Hard-delete is refused (status <see cref="DeletionStatus.HardDeleteBlocked"/>)
///     when the row has engagement that must not be destroyed; the caller is told
///     to deactivate instead.
/// </summary>
public sealed class ParticipantDeletionService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public ParticipantDeletionService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>The outcome of a delete/deactivate call.</summary>
    public enum DeletionStatus
    {
        /// <summary>No participant with that id exists in this edition.</summary>
        NotFound,
        /// <summary>Soft-deleted: the row was active and is now deactivated.</summary>
        Deactivated,
        /// <summary>Soft-delete was a no-op: the row was already inactive.</summary>
        AlreadyInactive,
        /// <summary>Hard-deleted: the row was safe to remove and is gone.</summary>
        HardDeleted,
        /// <summary>
        /// Hard-delete refused because the row has engagement that must not be
        /// destroyed. The row is left untouched; deactivate it instead.
        /// </summary>
        HardDeleteBlocked,
    }

    /// <summary>Result of a delete/deactivate call.</summary>
    /// <param name="Status">What happened.</param>
    /// <param name="ParticipantId">The id acted on (0 when not found).</param>
    /// <param name="FullName">The participant's name, for the confirmation message (null when not found).</param>
    /// <param name="BlockingDependencies">
    /// When <see cref="DeletionStatus.HardDeleteBlocked"/>, the human-readable
    /// dependency labels that blocked the hard-delete (e.g. "session(s) as speaker").
    /// Empty otherwise.
    /// </param>
    public sealed record DeletionResult(
        DeletionStatus Status,
        int ParticipantId,
        string? FullName,
        IReadOnlyList<string> BlockingDependencies)
    {
        public bool Found => Status != DeletionStatus.NotFound;
    }

    private static readonly IReadOnlyList<string> NoBlockers = Array.Empty<string>();

    /// <summary>
    /// Soft-delete: deactivate the participant so they can no longer sign in.
    /// Sets <see cref="Participant.IsActive"/> false and parks the lifecycle at
    /// <see cref="ParticipantLifecycleState.Inactive"/>. Keeps every dependent
    /// row intact. This is the safe default the grid offers for anyone.
    /// </summary>
    public async Task<DeletionResult> DeactivateAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var p = await FindAsync(eventId, participantId, ct);
        if (p is null) return NotFoundResult(participantId);

        if (!p.IsActive && p.LifecycleState == ParticipantLifecycleState.Inactive)
        {
            return new DeletionResult(
                DeletionStatus.AlreadyInactive, p.Id, p.FullName, NoBlockers);
        }

        p.IsActive = false;
        p.LifecycleState = ParticipantLifecycleState.Inactive;
        await _db.SaveChangesAsync(ct);
        return new DeletionResult(
            DeletionStatus.Deactivated, p.Id, p.FullName, NoBlockers);
    }

    /// <summary>
    /// Hard-delete WHEN SAFE: if the row has any engagement that must not be
    /// destroyed (the dependency probe finds blockers), this refuses and returns
    /// <see cref="DeletionStatus.HardDeleteBlocked"/> with the blocking labels —
    /// the caller should deactivate instead. When there are no blockers, the
    /// always-safe per-person logistics rows + hotel placement are cleaned and
    /// the participant is physically removed.
    /// </summary>
    public async Task<DeletionResult> HardDeleteAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var p = await FindAsync(eventId, participantId, ct);
        if (p is null) return NotFoundResult(participantId);

        var blockers = await FindBlockingDependenciesAsync(eventId, participantId, ct);
        if (blockers.Count > 0)
        {
            return new DeletionResult(
                DeletionStatus.HardDeleteBlocked, p.Id, p.FullName, blockers);
        }

        // Clean the always-safe per-person logistics links first (mirror the
        // Hotels delete pattern: un-assign / remove dependents, then remove the
        // row). These carry no history worth keeping once the person is gone.
        await CleanSafeDependentsAsync(eventId, participantId, ct);

        // Null the hotel placement (NoAction FK — must be cleared, not cascaded).
        p.HotelId = null;

        _db.Participants.Remove(p);
        await _db.SaveChangesAsync(ct);
        return new DeletionResult(
            DeletionStatus.HardDeleted, p.Id, p.FullName, NoBlockers);
    }

    /// <summary>
    /// Probe whether a participant could be hard-deleted right now, without
    /// changing anything — lets the UI decide whether to even offer the
    /// hard-delete choice. Empty list = safe to hard-delete.
    /// </summary>
    public Task<IReadOnlyList<string>> GetHardDeleteBlockersAsync(
        int eventId, int participantId, CancellationToken ct = default)
        => FindBlockingDependenciesAsync(eventId, participantId, ct);

    // ----- internals --------------------------------------------------------

    private Task<Participant?> FindAsync(int eventId, int participantId, CancellationToken ct)
        => _db.Participants.FirstOrDefaultAsync(
            p => p.Id == participantId && p.EventId == eventId, ct);

    private static DeletionResult NotFoundResult(int participantId)
        => new(DeletionStatus.NotFound, 0 < participantId ? participantId : 0, null, NoBlockers);

    /// <summary>
    /// The engagement that makes a hard-delete unsafe. Each is a row class whose
    /// loss would destroy real history or which is protected by a Restrict FK.
    /// Logistics-only links (hotel placement, swag, dinner/lunch sign-ups, login
    /// PINs, secretary tokens) are deliberately NOT listed — they are cleaned in
    /// <see cref="CleanSafeDependentsAsync"/> and never block the delete.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindBlockingDependenciesAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var blockers = new List<string>();

        if (await _db.SessionSpeakers.AnyAsync(
                s => s.ParticipantId == participantId, ct))
            blockers.Add("session(s) as speaker");

        if (await _db.SpeakerProfiles.AnyAsync(
                s => s.ParticipantId == participantId, ct))
            blockers.Add("speaker profile");

        if (await _db.VolunteerTaskAssignments.AnyAsync(
                v => v.ParticipantId == participantId, ct))
            blockers.Add("volunteer task assignment(s)");

        if (await _db.TravelReimbursements.AnyAsync(
                t => t.ParticipantId == participantId, ct))
            blockers.Add("travel reimbursement claim(s)");

        if (await _db.ImpersonationAudits.AnyAsync(
                a => a.EventId == eventId
                     && (a.TargetParticipantId == participantId
                         || a.ActorParticipantId == participantId), ct))
            blockers.Add("acting-as audit history");

        return blockers;
    }

    /// <summary>
    /// Remove the handful of always-safe per-person logistics rows that have no
    /// history worth keeping once the participant is gone. Kept narrow on
    /// purpose: only rows that are pure preference/placement for THIS person.
    /// </summary>
    private async Task CleanSafeDependentsAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        _db.HotelBookings.RemoveRange(
            await _db.HotelBookings
                .Where(x => x.EventId == eventId && x.ParticipantId == participantId)
                .ToListAsync(ct));

        _db.SwagPreferences.RemoveRange(
            await _db.SwagPreferences
                .Where(x => x.ParticipantId == participantId)
                .ToListAsync(ct));

        _db.DinnerSignups.RemoveRange(
            await _db.DinnerSignups
                .Where(x => x.ParticipantId == participantId)
                .ToListAsync(ct));

        _db.LunchSignups.RemoveRange(
            await _db.LunchSignups
                .Where(x => x.ParticipantId == participantId)
                .ToListAsync(ct));

        _db.LoginPins.RemoveRange(
            await _db.LoginPins
                .Where(x => x.ParticipantId == participantId)
                .ToListAsync(ct));

        _db.ParticipantSecretaryTokens.RemoveRange(
            await _db.ParticipantSecretaryTokens
                .Where(x => x.ParticipantId == participantId)
                .ToListAsync(ct));
    }
}
