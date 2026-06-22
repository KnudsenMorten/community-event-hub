using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>Outcome of one master-class Booking sync run, surfaced on the admin page.</summary>
/// <param name="Ran">True when the fetch seam actually ran a Booking call.</param>
/// <param name="ParticipantsCreated">New hub participants created for first-time bookers.</param>
/// <param name="LinksCreated">New booking links created.</param>
/// <param name="LinksUpdated">Existing booking links updated in place (idempotent re-sync).</param>
/// <param name="Message">Human-readable status / reason.</param>
public sealed record MasterClassBookingSyncResult(
    bool Ran,
    int ParticipantsCreated,
    int LinksCreated,
    int LinksUpdated,
    string Message);

/// <summary>
/// Syncs master-class participants ONE-WAY from Zoho Booking into the hub
/// (REQUIREMENTS § 6c), linked to the right master class via
/// <see cref="MasterClassParticipant"/>.
///
/// <b>Idempotent:</b> upserts each booking by (EventId, SessionId,
/// BookingRecordId) — re-syncing the same booking updates the link in place,
/// never duplicates. The booked participant is matched/created by email within
/// the edition (the same identity key the rest of the hub uses).
///
/// <b>Lifecycle:</b> a brand-new booked participant is created
/// <see cref="ParticipantLifecycleState.Inactive"/> (so they cannot sign in
/// until an organizer validates them through the normal pre-selection queue) —
/// the sync NEVER activates anyone for login. An existing participant's role /
/// activation is left untouched. A cancelled booking flips the link's
/// <see cref="MasterClassParticipant.IsActive"/> to false rather than deleting
/// it (history preserved).
///
/// <b>Gated:</b> the <see cref="IMasterClassBookingFetcher"/> seam defaults to a
/// no-op Null fetcher (<see cref="IMasterClassBookingFetcher.CanFetch"/> =
/// false); until a real endpoint + creds are wired, the sync reports "not
/// configured" rather than faking participants.
/// </summary>
public sealed class MasterClassBookingSyncService
{
    /// <summary>Statuses (lower-cased) that mark a booking as no longer active.</summary>
    private static readonly string[] CancelledStatuses =
        { "cancelled", "canceled", "noshow", "no-show", "rejected" };

    private readonly CommunityHubDbContext _db;
    private readonly IMasterClassBookingFetcher _fetcher;
    private readonly TimeProvider _clock;
    private readonly ILogger<MasterClassBookingSyncService> _log;

    public MasterClassBookingSyncService(
        CommunityHubDbContext db,
        IMasterClassBookingFetcher fetcher,
        TimeProvider clock,
        ILogger<MasterClassBookingSyncService> log)
    {
        _db = db;
        _fetcher = fetcher;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Sync the bookings for ONE master-class session. The session must be a
    /// <see cref="SessionType.MasterClass"/> with a configured
    /// <see cref="Session.BookingEndpointUri"/>. Returns the counts, or why
    /// nothing ran (not a master class, no endpoint mapped, seam not wired).
    /// </summary>
    public async Task<MasterClassBookingSyncResult> SyncSessionAsync(
        int eventId, int sessionId, CancellationToken ct = default)
    {
        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.EventId == eventId, ct);
        if (session is null)
        {
            return new MasterClassBookingSyncResult(false, 0, 0, 0, "Session not found.");
        }
        if (session.Type != SessionType.MasterClass)
        {
            return new MasterClassBookingSyncResult(false, 0, 0, 0,
                "Booking sync only applies to a master-class session.");
        }
        if (string.IsNullOrWhiteSpace(session.BookingEndpointUri))
        {
            return new MasterClassBookingSyncResult(false, 0, 0, 0,
                "No Zoho Booking endpoint is mapped for this master class. "
                + "Configure it in master class management first.");
        }
        if (!_fetcher.CanFetch)
        {
            return new MasterClassBookingSyncResult(false, 0, 0, 0,
                "Zoho Booking is not configured (endpoint/creds are operator config, "
                + "pending wiring). No bookings were fetched.");
        }

        var bookings = await _fetcher.FetchAsync(session.BookingEndpointUri!, ct);
        _log.LogInformation(
            "MasterClassBookingSync: fetched {Count} booking(s) for session {SessionId}.",
            bookings.Count, sessionId);

        var now = _clock.GetUtcNow();
        int created = 0, linksCreated = 0, linksUpdated = 0;

        foreach (var b in bookings)
        {
            var bookingId = (b.BookingRecordId ?? string.Empty).Trim();
            var email = (b.Email ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(bookingId) || string.IsNullOrWhiteSpace(email))
            {
                // A booking with no stable id or no email cannot be linked safely.
                _log.LogWarning(
                    "MasterClassBookingSync: skipping booking with blank id/email "
                    + "(id='{Id}', email='{Email}').", bookingId, email);
                continue;
            }

            // Match (or create) the hub participant by email within the edition.
            var participant = await _db.Participants
                .FirstOrDefaultAsync(p => p.EventId == eventId && p.Email == email, ct);
            if (participant is null)
            {
                participant = new Participant
                {
                    EventId = eventId,
                    Email = email,
                    FullName = string.IsNullOrWhiteSpace(b.Name) ? email : b.Name.Trim(),
                    Role = ParticipantRole.Attendee,
                    // Lifecycle gate: booked, NOT yet validated — cannot sign in
                    // until an organizer activates them in the pre-selection queue.
                    IsActive = true,
                    LifecycleState = ParticipantLifecycleState.Inactive,
                    QueueSource = ParticipantQueueSource.Manual,
                    CreatedAt = now,
                };
                _db.Participants.Add(participant);
                await _db.SaveChangesAsync(ct); // assign Id for the link FK
                created++;
            }

            var statusActive = !CancelledStatuses.Contains(
                (b.Status ?? string.Empty).Trim().ToLowerInvariant());

            var link = await _db.MasterClassParticipants.FirstOrDefaultAsync(
                m => m.EventId == eventId
                     && m.SessionId == sessionId
                     && m.BookingRecordId == bookingId, ct);

            if (link is null)
            {
                link = new MasterClassParticipant
                {
                    EventId = eventId,
                    SessionId = sessionId,
                    ParticipantId = participant.Id,
                    BookingRecordId = bookingId,
                    CreatedAt = now,
                };
                _db.MasterClassParticipants.Add(link);
                linksCreated++;
            }
            else
            {
                linksUpdated++;
            }

            // Zoho owns the booking content on every sync; the hub owns lifecycle.
            link.ParticipantId = participant.Id;
            link.BookedEmail = email;
            link.BookedName = string.IsNullOrWhiteSpace(b.Name) ? participant.FullName : b.Name.Trim();
            link.BookingStatus = (b.Status ?? string.Empty).Trim();
            link.IsActive = statusActive;
            link.LastSyncedAt = now;
        }

        session.BookingLastSyncedAt = now;
        await _db.SaveChangesAsync(ct);

        var msg = $"Booking sync complete: {created} new participant(s), "
                  + $"{linksCreated} new link(s), {linksUpdated} updated.";
        _log.LogInformation("MasterClassBookingSync: {Message}", msg);
        return new MasterClassBookingSyncResult(true, created, linksCreated, linksUpdated, msg);
    }
}
