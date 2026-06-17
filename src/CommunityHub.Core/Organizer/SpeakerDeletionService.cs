using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// The single server-side authority for REMOVING a speaker from an edition's
/// speaker roster (REQUIREMENTS §22 "Speakers delete"). A "speaker" is a
/// <see cref="Domain.Participant"/> (role Speaker / MasterclassSpeaker) plus an
/// optional <see cref="Domain.SpeakerProfile"/>; this service "un-speakers" the
/// person — it deletes the <b>speaker profile</b> (bio, photo, accreditation,
/// publish flag, contact override) so they are no longer a speaker, WITHOUT
/// touching the participant row itself. Removing the whole person is the
/// Participants grid's job (<see cref="ParticipantDeletionService"/>); this is the
/// narrower, non-destructive "remove from speakers" the Speakers grid needs.
///
/// It mirrors the established delete-safely pattern
/// (<see cref="SessionDeletionService"/> / <see cref="ParticipantDeletionService"/>):
///   - The safe default is to REFUSE a delete that would orphan real agenda
///     engagement — a speaker who is still linked to a session (on the running
///     order) is NOT un-speakered, and the organizer is told to unlink the
///     session(s) first (no silent agenda corruption).
///   - A clean speaker (no session links) has their profile removed, plus the
///     always-safe propagation artifact (the Backstage bio-sync row, which is
///     pure outbound state, not engagement) cleaned with it. The participant —
///     their identity, login, logistics — is untouched.
///
/// Invariants (all enforced HERE, not in the page):
///   - EVERY operation is scoped to the caller's <c>eventId</c>; a speaker in
///     another edition is never found, never touched.
///   - A participant with NO profile is reported <see cref="DeletionStatus.NotFound"/>
///     (there is nothing to un-speaker).
///   - One <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> so the
///     profile + sync-artifact removal commit together or not at all.
///
/// No schema change — operates only on existing entities.
/// </summary>
public sealed class SpeakerDeletionService
{
    private readonly CommunityHubDbContext _db;

    public SpeakerDeletionService(CommunityHubDbContext db)
    {
        _db = db;
    }

    /// <summary>The outcome of a single un-speaker call.</summary>
    public enum DeletionStatus
    {
        /// <summary>No speaker profile for that participant exists in this edition.</summary>
        NotFound,
        /// <summary>Removed: the speaker profile (and its Backstage sync artifact) were deleted.</summary>
        Deleted,
        /// <summary>
        /// Refused because the speaker is still linked to one or more sessions (on
        /// the agenda). The profile is left untouched; the organizer unlinks the
        /// session(s) first so the running order is never silently orphaned.
        /// </summary>
        Blocked,
    }

    /// <summary>Result of a single un-speaker call.</summary>
    /// <param name="Status">What happened.</param>
    /// <param name="ParticipantId">The id acted on (0 when not found).</param>
    /// <param name="Name">The speaker's name, for the confirmation message (null when not found).</param>
    /// <param name="SessionCount">
    /// When <see cref="DeletionStatus.Blocked"/>, how many sessions the speaker is
    /// still linked to (so the message can say "linked to N session(s)"). 0 otherwise.
    /// </param>
    public sealed record DeletionResult(
        DeletionStatus Status, int ParticipantId, string? Name, int SessionCount)
    {
        public bool Found => Status != DeletionStatus.NotFound;
    }

    /// <summary>Outcome of a bulk un-speaker call.</summary>
    /// <param name="Matched">Distinct ids that resolved to a speaker profile in this event.</param>
    /// <param name="Deleted">How many clean speakers were un-speakered.</param>
    /// <param name="Blocked">
    /// How many were left untouched because they are still linked to a session.
    /// </param>
    public sealed record BulkDeleteResult(int Matched, int Deleted, int Blocked)
    {
        /// <summary>How many requested ids did NOT resolve to a profile in this event.</summary>
        public int Skipped(int requested) => Math.Max(0, requested - Matched);
    }

    /// <summary>
    /// Probe whether a speaker can be un-speakered right now without changing
    /// anything — the count of sessions blocking the delete. 0 = safe to remove.
    /// Lets the UI offer the delete (or show the "linked to N session(s)" note).
    /// </summary>
    public Task<int> GetBlockingSessionCountAsync(
        int eventId, int participantId, CancellationToken ct = default)
        => _db.SessionSpeakers
            .CountAsync(ss => ss.ParticipantId == participantId
                              && ss.Session.EventId == eventId, ct);

    /// <summary>
    /// Un-speaker WHEN SAFE: a speaker still linked to a session is refused
    /// (<see cref="DeletionStatus.Blocked"/>); a clean speaker has their profile
    /// + Backstage sync artifact removed. The participant row is never touched.
    /// </summary>
    public async Task<DeletionResult> DeleteAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var profile = await _db.SpeakerProfiles
            .Include(sp => sp.Participant)
            .FirstOrDefaultAsync(
                sp => sp.EventId == eventId && sp.ParticipantId == participantId, ct);
        if (profile is null)
        {
            return new DeletionResult(
                DeletionStatus.NotFound, 0 < participantId ? participantId : 0, null, 0);
        }

        var name = profile.Participant?.FullName ?? string.Empty;

        var sessionCount = await GetBlockingSessionCountAsync(eventId, participantId, ct);
        if (sessionCount > 0)
        {
            return new DeletionResult(
                DeletionStatus.Blocked, participantId, name, sessionCount);
        }

        await CleanSyncArtifactAsync(eventId, participantId, ct);
        _db.SpeakerProfiles.Remove(profile);
        await _db.SaveChangesAsync(ct);

        return new DeletionResult(DeletionStatus.Deleted, participantId, name, 0);
    }

    /// <summary>
    /// Un-speaker every selected participant that is SAFE (no session links). A
    /// speaker still linked to a session is left untouched (counted in
    /// <see cref="BulkDeleteResult.Blocked"/>); clean speakers have their profile
    /// + sync artifact removed. The whole batch is one transaction.
    /// </summary>
    public async Task<BulkDeleteResult> DeleteManyAsync(
        int eventId, IEnumerable<int> participantIds, CancellationToken ct = default)
    {
        var ids = participantIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return new BulkDeleteResult(0, 0, 0);

        var profiles = await _db.SpeakerProfiles
            .Where(sp => sp.EventId == eventId && ids.Contains(sp.ParticipantId))
            .ToListAsync(ct);
        if (profiles.Count == 0) return new BulkDeleteResult(0, 0, 0);

        var matchedIds = profiles.Select(p => p.ParticipantId).ToList();

        // Which of the matched speakers are still on the agenda (blocked).
        var linkedIds = await _db.SessionSpeakers
            .Where(ss => ss.Session.EventId == eventId && matchedIds.Contains(ss.ParticipantId))
            .Select(ss => ss.ParticipantId)
            .Distinct()
            .ToListAsync(ct);
        var blocked = new HashSet<int>(linkedIds);

        var deletable = profiles.Where(p => !blocked.Contains(p.ParticipantId)).ToList();
        if (deletable.Count > 0)
        {
            var deletableIds = deletable.Select(p => p.ParticipantId).ToList();
            var syncs = await _db.SpeakerBackstageEmailSyncs
                .Where(s => s.EventId == eventId && deletableIds.Contains(s.ParticipantId))
                .ToListAsync(ct);
            if (syncs.Count > 0) _db.SpeakerBackstageEmailSyncs.RemoveRange(syncs);

            _db.SpeakerProfiles.RemoveRange(deletable);
            await _db.SaveChangesAsync(ct);
        }

        return new BulkDeleteResult(
            Matched: profiles.Count, Deleted: deletable.Count, Blocked: blocked.Count);
    }

    /// <summary>
    /// Remove the speaker's Backstage bio-sync propagation row (if any). It is
    /// pure outbound state about the now-deleted profile, not engagement — keeping
    /// it would leave a stale sync record pointing at a speaker who no longer
    /// exists. Cleaned in the same SaveChanges as the profile removal.
    /// </summary>
    private async Task CleanSyncArtifactAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var syncs = await _db.SpeakerBackstageEmailSyncs
            .Where(s => s.EventId == eventId && s.ParticipantId == participantId)
            .ToListAsync(ct);
        if (syncs.Count > 0) _db.SpeakerBackstageEmailSyncs.RemoveRange(syncs);
    }
}
