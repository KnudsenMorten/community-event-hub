using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Organizer;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Tasks;

/// <summary>
/// §708 / §707.57d — derives the two presentation-upload tasks' state from the DECKS THAT EXIST.
/// </summary>
/// <remarks>
/// <para>🔥 <b>This is the fix for the photographed defect.</b> Operator screenshots, PROD, same
/// speaker, same moment: <c>/Speaker/Tasks</c> showing <i>"Upload final presentation ✓ done"</i>
/// beside <c>/Speaker</c> showing <i>"Final: not uploaded yet"</i>. The task was <c>Manual</c>, the
/// speaker ticked it, and nothing ever checked. <c>SpeakerPresentationService.UploadCoreAsync</c>
/// already closes the task when a deck ARRIVES — the missing half is the other direction: a task
/// reading Done with no deck behind it must REOPEN.</para>
///
/// <para>🔒 <b>A FAILED LOOKUP MUST NEVER REOPEN A TASK.</b> The decks live in SharePoint, so
/// "no files listed" and "could not read the folder" arrive looking identical — and treating the
/// second as the first would silently un-complete every speaker's upload task and re-chase them all
/// during an outage. That is §664.1's shape at scale, and the same rule
/// <c>TaskCompletion.Purchase</c> states for the webshop. When the store cannot read a kind's
/// folder, that kind is left exactly as it is.</para>
///
/// <para>🔒 <b>Done means EVERY session has a deck.</b> A speaker's decks are per SESSION and the
/// task is per SPEAKER, so a two-session speaker who uploaded one deck is not finished. Accepting
/// "at least one" would be the same lie in a smaller size — which is the whole thing being removed
/// here.</para>
///
/// <para>⚠️ <b>A speaker with NO sessions cannot complete these tasks.</b> There is no artefact to
/// observe and the manual tick is gone by construction, so the task stays Open and keeps chasing.
/// That is the honest reading of the data — an active speaker with no linked session is itself the
/// problem to fix — but it is a real edge and is recorded in REQUIREMENTS §708.5 rather than
/// papered over with a special case that would re-admit the lie.</para>
/// </remarks>
public sealed class SpeakerPresentationTaskReconciler
{
    private readonly CommunityHubDbContext _db;
    private readonly SpeakerPresentationService _presentations;
    private readonly TimeProvider _clock;
    private readonly ILogger<SpeakerPresentationTaskReconciler> _log;

    public SpeakerPresentationTaskReconciler(
        CommunityHubDbContext db,
        SpeakerPresentationService presentations,
        TimeProvider clock,
        ILogger<SpeakerPresentationTaskReconciler> log)
    {
        _db = db;
        _presentations = presentations;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// The artefact KINDS this speaker has fully satisfied — the set
    /// <see cref="TaskArtefactRules.HasArtefact"/> reads, and what the row renders its badge from.
    /// </summary>
    /// <remarks>
    /// A kind whose folder could not be read is ABSENT from the set rather than present-but-false,
    /// and <see cref="ReconcileAsync"/> is told which kinds were unreadable so it can decline to
    /// change them. The display then errs toward "not uploaded", which is the safe direction for a
    /// prompt; only the STORED state is protected from the ambiguity.
    /// </remarks>
    public async Task<SpeakerArtefactState> ReadStateAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var satisfied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var canReadPreview = _presentations.CanRead(PresentationKind.Preview);
        var canReadFinal = _presentations.CanRead(PresentationKind.Final);
        if (!canReadPreview) unreadable.Add("preview");
        if (!canReadFinal) unreadable.Add("final");

        IReadOnlyList<PresentationSessionSlot> slots;
        try
        {
            slots = await _presentations.GetSessionSlotsAsync(eventId, participantId, ct);
        }
        catch (Exception ex)
        {
            // §682 — a SharePoint problem must not stop the task page rendering, and per the rule
            // above it must not change any stored state either. Treat BOTH kinds as unreadable.
            _log.LogError(
                ex, "Could not read presentation decks for speaker {ParticipantId}; the two upload "
                + "tasks are left exactly as they are.", participantId);
            return new SpeakerArtefactState(
                satisfied, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "preview", "final" });
        }

        // No sessions ⇒ nothing to satisfy. Deliberately NOT "vacuously complete": a task that
        // completes because the speaker has no work is the false confidence §687.3 removes.
        if (slots.Count == 0) return new SpeakerArtefactState(satisfied, unreadable);

        if (canReadPreview && slots.All(s => !string.IsNullOrWhiteSpace(s.PreviewFileName)))
            satisfied.Add("preview");
        if (canReadFinal && slots.All(s => !string.IsNullOrWhiteSpace(s.FinalFileName)))
            satisfied.Add("final");

        return new SpeakerArtefactState(satisfied, unreadable);
    }

    /// <summary>
    /// Bring this speaker's artefact-backed task rows in line with the decks. Idempotent; writes
    /// only when a row actually disagrees with the files.
    /// </summary>
    public async Task ReconcileAsync(
        IReadOnlyList<ParticipantTask> tasks, SpeakerArtefactState state, CancellationToken ct = default)
    {
        var changed = false;

        foreach (var task in tasks)
        {
            var kind = TaskArtefactRules.UploadKindFor(task);
            if (kind is null) continue;

            // 🔒 THE OUTAGE GUARD. "Could not check" leaves the stored state exactly as it is.
            if (state.Unreadable.Contains(kind)) continue;

            var hasDeck = state.Satisfied.Contains(kind);

            if (hasDeck && task.State != TaskState.Done)
            {
                task.State = TaskState.Done;
                task.CompletedAt ??= _clock.GetUtcNow();
                changed = true;
            }
            else if (!hasDeck && task.State == TaskState.Done)
            {
                // 🔥 THE REOPEN §707.57d ASKS FOR. A task reading Done with no deck behind it was
                // never really done; §707.57e settled that reopening is fine here — "noone uses it
                // yet" — so this ships with the migration rather than behind a gate.
                task.State = TaskState.Open;
                task.CompletedAt = null;
                task.CompletedByParticipantId = null;
                changed = true;

                _log.LogInformation(
                    "Reopened speaker task {TaskId} ('{Title}'): it was marked Done but no {Kind} "
                    + "deck exists for every session.", task.Id, task.Title, kind);
            }
        }

        if (changed) await _db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// What the deck folders say about one speaker — and, separately, what they could not say.
/// </summary>
/// <param name="Satisfied">Kinds with a deck for EVERY one of the speaker's sessions.</param>
/// <param name="Unreadable">
/// Kinds whose folder could not be read. 🔒 Kept apart from <paramref name="Satisfied"/> on purpose:
/// collapsing "no files" and "could not look" into one boolean is exactly how an outage would
/// un-complete and re-chase every speaker.
/// </param>
public sealed record SpeakerArtefactState(
    IReadOnlySet<string> Satisfied,
    IReadOnlySet<string> Unreadable);
