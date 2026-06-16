using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// Server-side authority for a VOLUNTEER's self-service decisions about the
/// shifts they are assigned to (REQUIREMENTS §20 Volunteer "Shift availability +
/// decline/swap + per-task instructions"). A volunteer may, for a shift they are
/// assigned to:
///  - <b>Confirm</b> they can take it (availability = yes),
///  - <b>Decline</b> it (they cannot work it), or
///  - <b>Request a swap</b> (offer it back / to another volunteer),
/// each with an optional free-text note for the coordinator. Per-task
/// instructions are already carried on <see cref="VolunteerTask.Instructions"/>
/// and surfaced read-only by the existing schedule view; this service is the
/// WRITE side of the decision only.
///
/// EVERY mutation is scoped to the SIGNED-IN volunteer's OWN assignment — the
/// page passes the participant id from the session, never the client, and a
/// volunteer can only touch a (task, self) pair they are actually assigned to.
/// Declining / requesting a swap NEVER deletes the assignment (a coordinator
/// reassigns); instead it stamps <see cref="VolunteerTaskAssignment.DecisionStatus"/>
/// and raises a coordinator-visible signal on the existing organizer action
/// queue (<see cref="OrganizerActionItemService.TypeVolunteerShiftReassign"/>) so
/// the coordinator surface is reused, not reinvented.
///
/// Pure aggregation/mutation over the existing model. Authorization failures
/// throw <see cref="VolunteerAccessDeniedException"/> (→ 403); bad input throws
/// <see cref="VolunteerValidationException"/> (→ friendly message).
/// </summary>
public sealed class VolunteerShiftService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly OrganizerActionItemService _actions;

    public VolunteerShiftService(
        CommunityHubDbContext db, TimeProvider clock, OrganizerActionItemService actions)
    {
        _db = db;
        _clock = clock;
        _actions = actions;
    }

    /// <summary>Longest note we persist (matches the column width).</summary>
    public const int MaxNoteLength = 1000;

    /// <summary>
    /// Record the volunteer's decision about ONE of their OWN assigned shifts.
    /// <list type="bullet">
    /// <item><see cref="ShiftDecisionStatus.Confirmed"/> / <see cref="ShiftDecisionStatus.None"/>
    ///   clear any prior decline/swap and withdraw the coordinator signal if the
    ///   volunteer has nothing else flagged.</item>
    /// <item><see cref="ShiftDecisionStatus.Declined"/> / <see cref="ShiftDecisionStatus.SwapRequested"/>
    ///   stamp the assignment and (re)raise the coordinator signal.</item>
    /// </list>
    /// </summary>
    /// <param name="participantId">The SIGNED-IN volunteer (from the session — never the client).</param>
    public async Task<bool> SetDecisionAsync(
        int eventId, int participantId, int taskId,
        ShiftDecisionStatus decision, string? note, CancellationToken ct = default)
    {
        var assignment = await _db.VolunteerTaskAssignments
            .FirstOrDefaultAsync(
                a => a.EventId == eventId
                     && a.TaskId == taskId
                     && a.ParticipantId == participantId, ct);

        // Not assigned (or wrong edition) → the volunteer has no business here.
        if (assignment is null)
            throw new VolunteerAccessDeniedException(
                "You can only update a shift you are assigned to.");

        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > MaxNoteLength })
            note = note[..MaxNoteLength];

        var now = _clock.GetUtcNow();
        assignment.DecisionStatus = decision;
        assignment.DecisionNote = decision is ShiftDecisionStatus.None ? null : note;
        assignment.DecisionAt = decision is ShiftDecisionStatus.None ? null : now;
        await _db.SaveChangesAsync(ct);

        await SyncCoordinatorSignalAsync(eventId, participantId, ct);
        return true;
    }

    /// <summary>Convenience: the volunteer confirms availability for a shift.</summary>
    public Task<bool> ConfirmShiftAsync(
        int eventId, int participantId, int taskId, CancellationToken ct = default)
        => SetDecisionAsync(eventId, participantId, taskId, ShiftDecisionStatus.Confirmed, null, ct);

    /// <summary>Convenience: the volunteer declines a shift they cannot take.</summary>
    public Task<bool> DeclineShiftAsync(
        int eventId, int participantId, int taskId, string? note, CancellationToken ct = default)
        => SetDecisionAsync(eventId, participantId, taskId, ShiftDecisionStatus.Declined, note, ct);

    /// <summary>Convenience: the volunteer requests a swap (offer the shift back).</summary>
    public Task<bool> RequestSwapAsync(
        int eventId, int participantId, int taskId, string? note, CancellationToken ct = default)
        => SetDecisionAsync(eventId, participantId, taskId, ShiftDecisionStatus.SwapRequested, note, ct);

    /// <summary>Convenience: the volunteer withdraws a decline/swap (back to the
    /// default "I intend to work it").</summary>
    public Task<bool> WithdrawDecisionAsync(
        int eventId, int participantId, int taskId, CancellationToken ct = default)
        => SetDecisionAsync(eventId, participantId, taskId, ShiftDecisionStatus.None, null, ct);

    /// <summary>
    /// The shifts THIS volunteer has currently flagged as declined or
    /// swap-requested, newest decision first — the coordinator's "what does this
    /// person need help with" slice, and what the volunteer's own page reads back.
    /// </summary>
    public async Task<IReadOnlyList<VolunteerTaskAssignment>> GetFlaggedShiftsAsync(
        int eventId, int participantId, CancellationToken ct = default)
        => await _db.VolunteerTaskAssignments
            .Where(a => a.EventId == eventId
                        && a.ParticipantId == participantId
                        && (a.DecisionStatus == ShiftDecisionStatus.Declined
                            || a.DecisionStatus == ShiftDecisionStatus.SwapRequested))
            .Include(a => a.Task)
            .OrderByDescending(a => a.DecisionAt)
            .ToListAsync(ct);

    /// <summary>
    /// Keep the coordinator's action-queue item for this volunteer in step with
    /// their currently-flagged shifts. If they have one or more declined / swap
    /// shifts, (re)open ONE item summarising them; if they have none left, resolve
    /// any open item. The per-shift truth lives on the assignment — this is just
    /// the coordinator-visible nudge, reusing the existing surface.
    /// </summary>
    private async Task SyncCoordinatorSignalAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var flagged = await GetFlaggedShiftsAsync(eventId, participantId, ct);

        if (flagged.Count == 0)
        {
            // Nothing outstanding — close any open signal for this volunteer.
            var open = await _db.OrganizerActionItems.FirstOrDefaultAsync(
                a => a.EventId == eventId
                     && a.Type == OrganizerActionItemService.TypeVolunteerShiftReassign
                     && a.ParticipantId == participantId
                     && a.ResolvedAt == null, ct);
            if (open is not null)
                await _actions.ResolveAsync(eventId, open.Id, "All flagged shifts cleared.", ct);
            return;
        }

        var parts = flagged.Select(a =>
        {
            var verb = a.DecisionStatus == ShiftDecisionStatus.Declined ? "declined" : "swap requested";
            var title = a.Task?.Title ?? $"task #{a.TaskId}";
            return string.IsNullOrWhiteSpace(a.DecisionNote)
                ? $"{title} ({verb})"
                : $"{title} ({verb}: {a.DecisionNote})";
        });
        var summary = string.Join("; ", parts);

        await _actions.UpsertOpenAsync(
            eventId,
            OrganizerActionItemService.TypeVolunteerShiftReassign,
            participantId,
            summary,
            ct);
    }
}
