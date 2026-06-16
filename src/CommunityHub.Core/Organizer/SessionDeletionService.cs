using CommunityHub.Core.Data;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// The single server-side authority for DELETING a session from an edition
/// (REQUIREMENTS §21 organizer "Sessions delete / CRUD gap"). It mirrors the
/// participant-delete pattern (<see cref="ParticipantDeletionService"/>): the safe
/// default is to refuse a delete that would destroy attendee-supplied engagement
/// (questions, ratings, master-class bookings) and tell the organizer why; a clean
/// delete removes the session and its always-safe import-state links.
///
/// Semantics (all enforced HERE, not in the page):
///   - Every operation is scoped to the caller's <c>eventId</c>; a session in
///     another edition is never found, never touched.
///   - <b>Speaker links</b> (<see cref="Domain.SessionSpeaker"/>) are import-state,
///     not engagement: they are cleaned first and never block a delete (mirrors the
///     "always-safe logistics links" the participant delete cleans).
///   - <b>Attendee engagement blocks the delete</b>: a session with attendee
///     questions, evaluations, or master-class bookings is NOT deleted (so a bad
///     row can't silently take real attendee input with it). The organizer is told
///     what blocks it; they remove/handle that data first. (These FKs are NoAction,
///     so a blind delete would fail the FK anyway — this turns that into a clear,
///     safe refusal.)
///   - <b>Imported sessions re-appear</b>: a Sessionize-imported session (NOT
///     <c>IsHubAdded</c>) is recreated by the next import on its Sessionize id, so a
///     delete of a clean imported session is reported with that caveat. A hub-added
///     session is gone for good.
///   - One <see cref="Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(System.Threading.CancellationToken)"/>
///     so the link-clean + remove commit together or not at all.
/// </summary>
public sealed class SessionDeletionService
{
    private readonly CommunityHubDbContext _db;

    public SessionDeletionService(CommunityHubDbContext db)
    {
        _db = db;
    }

    /// <summary>The outcome of a session delete call.</summary>
    public enum DeletionStatus
    {
        /// <summary>No session with that id exists in this edition.</summary>
        NotFound,
        /// <summary>Deleted: the session (and its speaker links) were removed.</summary>
        Deleted,
        /// <summary>
        /// Refused because the session has attendee engagement that must not be
        /// destroyed. The session is left untouched; the organizer handles that
        /// data first.
        /// </summary>
        Blocked,
    }

    /// <summary>Result of a session delete call.</summary>
    /// <param name="Status">What happened.</param>
    /// <param name="SessionId">The id acted on (0 when not found).</param>
    /// <param name="Title">The session title, for the confirmation message (null when not found).</param>
    /// <param name="WasImported">
    /// True when the deleted (or candidate) session came from Sessionize (not hub-added),
    /// so the caller can warn that a re-import will recreate it.
    /// </param>
    /// <param name="BlockingDependencies">
    /// When <see cref="DeletionStatus.Blocked"/>, the human-readable labels that
    /// blocked the delete (e.g. "attendee question(s)"). Empty otherwise.
    /// </param>
    public sealed record DeletionResult(
        DeletionStatus Status,
        int SessionId,
        string? Title,
        bool WasImported,
        IReadOnlyList<string> BlockingDependencies)
    {
        public bool Found => Status != DeletionStatus.NotFound;
    }

    private static readonly IReadOnlyList<string> NoBlockers = Array.Empty<string>();

    /// <summary>
    /// Probe whether a session could be deleted right now, without changing
    /// anything — lets the UI decide whether to even offer the delete (and to show
    /// the imported-session caveat). Empty list = safe to delete.
    /// </summary>
    public Task<IReadOnlyList<string>> GetBlockersAsync(
        int eventId, int sessionId, CancellationToken ct = default)
        => FindBlockingDependenciesAsync(sessionId, ct);

    /// <summary>
    /// Delete a session WHEN SAFE. If the session has attendee engagement
    /// (questions / evaluations / bookings) this refuses and returns
    /// <see cref="DeletionStatus.Blocked"/> with the blocking labels; otherwise the
    /// speaker links are cleaned and the session is removed.
    /// </summary>
    public async Task<DeletionResult> DeleteAsync(
        int eventId, int sessionId, CancellationToken ct = default)
    {
        var session = await _db.Sessions
            .Include(s => s.SessionSpeakers)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.EventId == eventId, ct);
        if (session is null)
        {
            return new DeletionResult(
                DeletionStatus.NotFound, 0 < sessionId ? sessionId : 0, null, false, NoBlockers);
        }

        var blockers = await FindBlockingDependenciesAsync(sessionId, ct);
        if (blockers.Count > 0)
        {
            return new DeletionResult(
                DeletionStatus.Blocked, session.Id, session.Title,
                !session.IsHubAdded, blockers);
        }

        // Speaker links are import-state, not engagement — clean them first (the FK
        // cascades from Session anyway; removing them explicitly keeps the intent
        // clear and the single SaveChanges atomic).
        if (session.SessionSpeakers.Count > 0)
        {
            _db.SessionSpeakers.RemoveRange(session.SessionSpeakers);
        }

        _db.Sessions.Remove(session);
        await _db.SaveChangesAsync(ct);

        return new DeletionResult(
            DeletionStatus.Deleted, session.Id, session.Title,
            !session.IsHubAdded, NoBlockers);
    }

    /// <summary>
    /// The attendee engagement that makes a delete unsafe. Each is data an attendee
    /// (not the organizer) supplied; losing it silently with a row-delete would be a
    /// data-loss bug, so we refuse and let the organizer deal with it first. Speaker
    /// links are deliberately NOT listed — they are import-state cleaned on delete.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindBlockingDependenciesAsync(
        int sessionId, CancellationToken ct)
    {
        var blockers = new List<string>();

        var questions = await _db.SessionQuestions.CountAsync(q => q.SessionId == sessionId, ct);
        if (questions > 0)
            blockers.Add($"{questions} attendee question(s)");

        var evaluations = await _db.SessionEvaluations.CountAsync(e => e.SessionId == sessionId, ct);
        if (evaluations > 0)
            blockers.Add($"{evaluations} attendee evaluation(s)");

        var bookings = await _db.MasterClassParticipants.CountAsync(m => m.SessionId == sessionId, ct);
        if (bookings > 0)
            blockers.Add($"{bookings} master-class booking(s)");

        return blockers;
    }
}
