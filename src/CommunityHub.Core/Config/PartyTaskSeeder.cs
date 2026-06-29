using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Config;

/// <summary>
/// Seeds the per-participant "party sign-up" task (REQUIREMENTS §164). Every STAFF
/// role — Sponsor, Speaker, Volunteer, Event Partner, Organizer — must end up with a
/// YES/NO answer to the 9 Feb party, so each gets one <see cref="ParticipantTask"/>
/// (SourceKey <c>party-form:{participantId}</c>, dated so the existing reminder job
/// nags them while it stays Open) that links to <c>/Party</c>. Plain attendees do NOT
/// get a task — they sign up via the menu, not a tracked staff task.
///
/// <para>The task auto-completes when the person submits a Party RSVP (Yes or No) and
/// reopens if they un-answer — that two-way reconciliation lives in
/// <see cref="Participants.FormTaskReconciler"/>; this class only ensures the task
/// EXISTS. Idempotent (SourceKey-keyed) so it is safe to call on every page-load and
/// every reminder run. Sponsors get head-count wording (they bring a company group).</para>
/// </summary>
public sealed class PartyTaskSeeder
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public PartyTaskSeeder(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>The SourceKey prefix for the party sign-up auto-task.</summary>
    public const string PartyTaskKey = "party-form";

    /// <summary>Stable SourceKey for a participant's party sign-up task.</summary>
    public static string SourceKeyFor(int participantId) => $"{PartyTaskKey}:{participantId}";

    /// <summary>
    /// The STAFF roles that get a party sign-up TASK + reminder (§164). Plain
    /// attendees are excluded (they use the menu, not a task); Media is not a tracked
    /// party role per the operator's list.
    /// </summary>
    public static bool RoleGetsPartyTask(ParticipantRole role) => role is
        ParticipantRole.Sponsor or ParticipantRole.Speaker or ParticipantRole.Volunteer
        or ParticipantRole.EventPartner or ParticipantRole.Organizer;

    /// <summary>
    /// Ensure the party sign-up task exists for EVERY active staff-role participant in
    /// the edition (the bulk path the reminder job uses). Returns the count created.
    /// </summary>
    public async Task<int> SeedAsync(int eventId, CancellationToken ct = default)
    {
        var party = await GetPartyAsync(eventId, ct);
        if (party is null) return 0; // no active party window for this edition

        // Active participants in a party-task role who don't yet have the task.
        var people = await _db.Participants
            .Where(p => p.EventId == eventId && p.IsActive)
            .Where(p => p.Role == ParticipantRole.Sponsor
                        || p.Role == ParticipantRole.Speaker
                        || p.Role == ParticipantRole.Volunteer
                        || p.Role == ParticipantRole.EventPartner
                        || p.Role == ParticipantRole.Organizer)
            .Select(p => new { p.Id, p.Role })
            .ToListAsync(ct);
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
            _db.Tasks.Add(BuildTask(eventId, p.Id, p.Role, party.DueDate, now));
            created++;
        }

        if (created > 0) await _db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>
    /// Ensure the party sign-up task exists for ONE participant (the per-request path a
    /// hub page-load uses so the task surfaces immediately, not only after the nightly
    /// job). No-op for a role that doesn't get a party task or when no party is active.
    /// </summary>
    public async Task EnsureForParticipantAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default)
    {
        if (!RoleGetsPartyTask(role)) return;

        var party = await GetPartyAsync(eventId, ct);
        if (party is null) return;

        var key = SourceKeyFor(participantId);
        var exists = await _db.Tasks.AnyAsync(
            t => t.EventId == eventId && t.AssignedParticipantId == participantId
                 && t.SourceKey == key, ct);
        if (exists) return;

        _db.Tasks.Add(BuildTask(eventId, participantId, role, party.DueDate, _clock.GetUtcNow()));
        await _db.SaveChangesAsync(ct);
    }

    private ParticipantTask BuildTask(
        int eventId, int participantId, ParticipantRole role, DateOnly dueDate, DateTimeOffset now) =>
        new()
        {
            EventId = eventId,
            AssignedParticipantId = participantId,
            Title = "Sign up for the Party",
            Description = role == ParticipantRole.Sponsor
                ? "RSVP yes/no for the party (16:00–18:30 on the pre-day) and tell us HOW MANY "
                  + "people will attend from your company. Submitting the Party form marks this done."
                : "RSVP yes/no for the party (16:00–18:30 on the pre-day). Submitting the Party "
                  + "form marks this done.",
            DueDate = dueDate,
            State = TaskState.Open,
            IsMandatory = false,
            SourceKey = SourceKeyFor(participantId),
            CreatedAt = now,
        };

    private sealed record PartyWindow(DateOnly Date, DateOnly DueDate);

    /// <summary>Resolve the active edition's party date + a sensible reminder due date
    /// (three weeks before, mirroring the dinner auto-task), or null when not active.</summary>
    private async Task<PartyWindow?> GetPartyAsync(int eventId, CancellationToken ct)
    {
        var date = await _db.Events
            .Where(e => e.Id == eventId && e.IsActive)
            .Select(e => (DateOnly?)e.StartDate)
            .FirstOrDefaultAsync(ct);
        return date is { } d ? new PartyWindow(d, d.AddDays(-21)) : null;
    }
}
