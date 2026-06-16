using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// "Modify on behalf" — an organizer (from the grid) changes a couple of
/// per-person logistics fields FOR a participant, writing the <b>same</b>
/// <see cref="HotelBooking"/> / <see cref="SwagPreference"/> rows the
/// participant's own self-service pages read. Because both sides read/write the
/// same rows, the change shows up on that person's own view immediately — there
/// is no separate "organizer copy".
///
/// Scope + safety (mirrors <see cref="ParticipantBulkOperationService"/>):
///   - every operation is scoped to the caller's <c>eventId</c>; a participant
///     id from another edition is rejected (returns
///     <see cref="ModifyResult.NotFound"/>), never touched;
///   - upsert semantics: the booking / preference row is created on first write
///     and updated thereafter;
///   - a change to an ALREADY-submitted row raises the existing
///     <see cref="OrganizerActionItemService"/> late-change item, exactly as a
///     self-service edit would, so a downstream-affecting edit is never silent.
/// </summary>
public sealed class ModifyOnBehalfService
{
    public enum ModifyResult { Ok, NotFound }

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly OrganizerActionItemService _actions;

    public ModifyOnBehalfService(
        CommunityHubDbContext db, TimeProvider clock, OrganizerActionItemService actions)
    {
        _db = db;
        _clock = clock;
        _actions = actions;
    }

    /// <summary>
    /// Set whether the participant needs a hotel room (on their own
    /// <see cref="HotelBooking"/>). Clearing the need wipes dates/sharing so the
    /// participant's own view is consistent. Returns Ok with a human summary of
    /// what changed (for the audit), or NotFound for a cross-edition id.
    /// </summary>
    public async Task<(ModifyResult Result, string Summary)> SetHotelNeededAsync(
        int eventId, int participantId, bool needsRoom, CancellationToken ct = default)
    {
        var participant = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == participantId && p.EventId == eventId, ct);
        if (participant is null) return (ModifyResult.NotFound, string.Empty);

        var booking = await _db.HotelBookings.FirstOrDefaultAsync(
            h => h.EventId == eventId && h.ParticipantId == participantId, ct);

        var isNew = booking is null;
        if (booking is null)
        {
            booking = new HotelBooking
            {
                EventId = eventId,
                ParticipantId = participantId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.HotelBookings.Add(booking);
        }
        else
        {
            booking.UpdatedAt = _clock.GetUtcNow();
        }

        booking.NeedsRoom = needsRoom;
        if (!needsRoom)
        {
            booking.CheckInDate = null;
            booking.CheckOutDate = null;
            booking.RoomShareWith = null;
        }
        await _db.SaveChangesAsync(ct);

        var summary = needsRoom ? "Hotel set to: room needed" : "Hotel set to: no room needed";
        if (!isNew)
        {
            await _actions.RaiseIfLateAsync(
                eventId, OrganizerActionItemService.TypeHotelChanged,
                participantId, $"{summary} (changed on the participant's behalf)", ct);
        }
        return (ModifyResult.Ok, summary);
    }

    /// <summary>
    /// Set the participant's polo size on their own <see cref="SwagPreference"/>.
    /// A null/blank/"no polo" size clears the polo want. The size must be one of
    /// the offered options, or it is rejected as NotFound-of-input (returns Ok
    /// only on a real change). Returns a human summary for the audit.
    /// </summary>
    public async Task<(ModifyResult Result, string Summary)> SetPoloSizeAsync(
        int eventId, int participantId, string? poloSize,
        IReadOnlyCollection<string> validSizes, string noPoloLabel,
        CancellationToken ct = default)
    {
        var participant = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == participantId && p.EventId == eventId, ct);
        if (participant is null) return (ModifyResult.NotFound, string.Empty);

        var pref = await _db.SwagPreferences.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.ParticipantId == participantId, ct);

        var isNew = pref is null;
        if (pref is null)
        {
            pref = new SwagPreference
            {
                EventId = eventId,
                ParticipantId = participantId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.SwagPreferences.Add(pref);
        }
        else
        {
            pref.UpdatedAt = _clock.GetUtcNow();
        }

        string summary;
        if (string.IsNullOrWhiteSpace(poloSize) || poloSize == noPoloLabel)
        {
            pref.WantsPolo = false;
            pref.PoloSize = null;
            summary = "Swag polo set to: none";
        }
        else if (validSizes.Contains(poloSize))
        {
            pref.WantsPolo = true;
            pref.PoloSize = poloSize;
            summary = $"Swag polo size set to: {poloSize}";
        }
        else
        {
            // Unknown size: treat as no-op rather than persist garbage.
            return (ModifyResult.NotFound, string.Empty);
        }

        await _db.SaveChangesAsync(ct);

        if (!isNew)
        {
            await _actions.RaiseIfLateAsync(
                eventId, OrganizerActionItemService.TypeSwagChanged,
                participantId, $"{summary} (changed on the participant's behalf)", ct);
        }
        return (ModifyResult.Ok, summary);
    }
}
