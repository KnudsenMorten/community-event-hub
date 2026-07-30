using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Config;

/// <summary>
/// §207 — seeds the "Master Class selection" task for 2-day-ticket attendees. A 2-day
/// (<see cref="TicketStatus.TwoDay"/>) holder provisioned to an Attendee-role
/// <see cref="Participant"/> (by <c>AttendeeWelcomeProvisioningService</c>) gets ONE
/// <see cref="ParticipantTask"/> (SourceKey <c>masterclass-form:{participantId}</c>) that
/// links to the in-hub Master Class chooser (<c>/Attendee</c>). Together with the party
/// task (seeded by <see cref="PartyTaskSeeder"/>) this gives a 2-day attendee the TWO
/// tracked tasks §207 requires.
///
/// <para>Done-state is reconciled two-way by <c>FormTaskReconciler</c>: a CONFIRMED
/// Master Class signup for the attendee (matched by email) ⇒ Done; none ⇒ reopen.
/// Idempotent (SourceKey-keyed), so it is safe to call on every hub/job run. Carries NO
/// due date — the reminder cadence is the 2-week <c>AttendeeMasterClassReminderBuilder</c>.</para>
/// </summary>
public sealed class AttendeeMasterClassTaskSeeder
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public AttendeeMasterClassTaskSeeder(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>The SourceKey prefix for the Master Class selection auto-task.</summary>
    public const string MasterClassTaskKey = "masterclass-form";

    /// <summary>Stable SourceKey for a participant's Master Class selection task.</summary>
    public static string SourceKeyFor(int participantId) => $"{MasterClassTaskKey}:{participantId}";

    /// <summary>
    /// Ensure the Master Class selection task exists for every active Attendee-role
    /// participant in the edition whose email matches a 2-day-ticket attendee. Returns the
    /// count created.
    /// </summary>
    public async Task<int> SeedAsync(int eventId, CancellationToken ct = default)
    {
        // The set of 2-day-ticket emails (the only attendees with Master Class access).
        var twoDayEmails = (await _db.Attendees
                .Where(a => a.EventId == eventId
                            && a.TicketStatus == TicketStatus.TwoDay
                            && a.Email != "")
                .Select(a => a.Email)
                .ToListAsync(ct))
            .Select(e => e.Trim().ToLowerInvariant())
            .ToHashSet();
        if (twoDayEmails.Count == 0) return 0;

        var people = (await _db.Participants
                .Where(p => p.EventId == eventId && p.IsActive
                            && p.Role == ParticipantRole.Attendee)
                .Select(p => new { p.Id, p.Email })
                .ToListAsync(ct))
            .Where(p => twoDayEmails.Contains(p.Email.Trim().ToLowerInvariant()))
            .ToList();
        if (people.Count == 0) return 0;

        var keys = people.Select(p => SourceKeyFor(p.Id)).ToList();
        var have = (await _db.Tasks
                .Where(t => t.EventId == eventId && t.SourceKey != null && keys.Contains(t.SourceKey))
                .Select(t => t.SourceKey!)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var now = _clock.GetUtcNow();
        var created = 0;
        foreach (var p in people)
        {
            var key = SourceKeyFor(p.Id);
            if (have.Contains(key)) continue; // idempotent
            _db.Tasks.Add(new ParticipantTask
            {
                EventId = eventId,
                AssignedParticipantId = p.Id,
                Title = "Select your Master Class",
                Description = "Pick the Master Class you'll attend on the master-class day. "
                    + "Choosing one in the hub marks this done.\n\n[Open the Master Class chooser](/Attendee)",
                DueDate = null,
                State = TaskState.Open,
                IsMandatory = false,
                SourceKey = key,
                CreatedAt = now,
            });
            created++;
        }

        if (created > 0) await _db.SaveChangesAsync(ct);
        return created;
    }
}
