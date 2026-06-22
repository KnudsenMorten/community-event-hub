using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Auto-provisions login-capable Attendee Participants from the synced 2-day-ticket
/// holders (the <see cref="Attendee"/> rows with <see cref="TicketStatus.TwoDay"/>
/// that <see cref="AttendeeTicketSyncService"/> writes). For each holder that does
/// not yet have a Participant in the edition, it creates an ACTIVE, login-capable
/// Attendee-role Participant and returns the new participant ids so the caller can
/// send a one-click magic-link welcome to exactly the newly-created people.
///
/// <para>Idempotent: a holder who already has a Participant (any role) is skipped,
/// so re-runs create nothing and the welcome is never re-sent. Gated upstream by
/// the <c>attendee-welcome</c> feature flag (default OFF).</para>
///
/// <para>New Participants are created at <see cref="Ring.Ring1"/> — both so the
/// attendee can actually see their (Ring1-released) hub features AND so the
/// central email ring-gate does not drop the welcome (an unknown / Broad recipient
/// is dropped below the DEV release ceiling). Lifecycle is set Active (not the
/// legacy Inactive booking-queue state) so the magic-link sign-in works.</para>
/// </summary>
public sealed class AttendeeWelcomeProvisioningService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<AttendeeWelcomeProvisioningService> _log;

    public AttendeeWelcomeProvisioningService(
        CommunityHubDbContext db,
        TimeProvider clock,
        ILogger<AttendeeWelcomeProvisioningService> log)
    {
        _db = db;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Create an active Attendee Participant for every 2-day-ticket holder in the
    /// edition that lacks one. Returns the ids of the participants CREATED by this
    /// call (empty when there is nothing new to do).
    /// </summary>
    public async Task<IReadOnlyList<int>> ProvisionAsync(int eventId, CancellationToken ct = default)
    {
        var holders = await _db.Attendees
            .Where(a => a.EventId == eventId
                        && a.TicketStatus == TicketStatus.TwoDay
                        && a.Email != "")
            .Select(a => new { a.Email, a.FullName, a.FirstName, a.LastName })
            .ToListAsync(ct);
        if (holders.Count == 0) return Array.Empty<int>();

        var emails = holders
            .Select(h => h.Email.Trim().ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToList();

        var existing = (await _db.Participants
                .Where(p => p.EventId == eventId && emails.Contains(p.Email))
                .Select(p => p.Email)
                .ToListAsync(ct))
            .Select(e => e.ToLowerInvariant())
            .ToHashSet();

        var now = _clock.GetUtcNow();
        var created = new List<Participant>();
        var seen = new HashSet<string>();

        foreach (var h in holders)
        {
            var email = h.Email.Trim().ToLowerInvariant();
            if (email.Length == 0 || existing.Contains(email) || !seen.Add(email)) continue;

            var name = !string.IsNullOrWhiteSpace(h.FullName)
                ? h.FullName.Trim()
                : $"{h.FirstName} {h.LastName}".Trim();

            var p = new Participant
            {
                EventId = eventId,
                Email = email,
                FullName = string.IsNullOrWhiteSpace(name) ? email : name,
                Role = ParticipantRole.Attendee,
                // Login-capable: active + lifecycle Active (the legacy booking path
                // deliberately uses Inactive to BLOCK sign-in — we want the opposite).
                IsActive = true,
                LifecycleState = ParticipantLifecycleState.Active,
                QueueSource = ParticipantQueueSource.Manual,
                // RELEASE SAFETY: provision real attendees at Broad/GA, NOT Ring1.
                // The email ring-gate only delivers to recipients at/below the email
                // feature's released ring, so Broad attendees are NOT auto-welcomed
                // until the email feature is deliberately promoted to Broad — this
                // prevents a rogue mass-blast to all ~1000 ticket holders. To test
                // the welcome with a small cohort, mark specific test attendees Ring1
                // (via /Organizer/ResourceRings) while the email feature is at Ring1.
                Ring = Rings.Default,
                CreatedAt = now,
            };
            _db.Participants.Add(p);
            created.Add(p);
        }

        if (created.Count == 0) return Array.Empty<int>();

        await _db.SaveChangesAsync(ct);
        _log.LogInformation(
            "AttendeeWelcomeProvisioning: created {Count} active attendee participants for event {EventId}.",
            created.Count, eventId);

        return created.Select(p => p.Id).ToList();
    }
}
