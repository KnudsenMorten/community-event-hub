using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// §1257b — what a "these two rows are one human" link would COST, checked before it is made.
///
/// <para><b>Why this exists.</b> §1257 put the duplicate rule in <c>LogisticsAudience</c>, so a
/// linked row is now excluded from the hotel, swag, lunch, food and expo files and from the Excel
/// exports. That is the point — one human, one room — but it makes the DIRECTION of a link
/// load-bearing in a way it was not before: link the row that holds the hotel booking, and the bed
/// leaves the order while the person still turns up expecting it.</para>
///
/// <para>🔴 The live case this was written from: Ronni Pedersen holds a SPEAKER row with hotel,
/// swag, lunch and dinner, and a SPONSOR row with none of them. The only linking control that
/// existed (<c>/Organizer/SpeakerReview</c>) always makes the SPEAKER row the duplicate — i.e.
/// exactly the wrong way round for him.</para>
///
/// <para>🔒 So the guard REFUSES rather than guesses, and names what would be lost. §1253c called
/// this out as the sharp edge of "the primary owns the entitlements": taken literally at the moment
/// of linking, it destroys data. Refusing costs one re-read of the screen; guessing costs a bed.</para>
/// </summary>
public static class DuplicateLinkGuard
{
    /// <summary>One per-person fact a row holds, in the words an organizer would use.</summary>
    public sealed record Holding(string Label);

    /// <summary>
    /// The per-person facts <paramref name="duplicateId"/> holds that <paramref name="primaryId"/>
    /// does NOT — i.e. what would stop being counted the moment the link is made. Empty means the
    /// link is safe.
    /// </summary>
    public static async Task<IReadOnlyList<Holding>> WouldLoseAsync(
        CommunityHubDbContext db, int eventId, int duplicateId, int primaryId,
        CancellationToken ct = default)
    {
        var lost = new List<Holding>();

        async Task CheckAsync(string label, Func<int, Task<bool>> holds)
        {
            if (await holds(duplicateId) && !await holds(primaryId)) lost.Add(new Holding(label));
        }

        await CheckAsync("a hotel booking (needs a room)", id =>
            db.HotelBookings.AnyAsync(h => h.EventId == eventId && h.ParticipantId == id && h.NeedsRoom, ct));
        await CheckAsync("swag / polo / gift choices", id =>
            db.SwagPreferences.AnyAsync(s => s.EventId == eventId && s.ParticipantId == id, ct));
        await CheckAsync("a lunch signup", id =>
            db.LunchSignups.AnyAsync(l => l.EventId == eventId && l.ParticipantId == id, ct));
        await CheckAsync("an appreciation-dinner signup", id =>
            db.DinnerSignups.AnyAsync(d => d.EventId == eventId && d.ParticipantId == id, ct));
        await CheckAsync("a party RSVP (attending)", id =>
            db.PartyRsvps.AnyAsync(r => r.EventId == eventId && r.ParticipantId == id && r.Attending, ct));
        await CheckAsync("a hotel placement", id =>
            db.Participants.AnyAsync(p => p.Id == id && p.HotelId != null, ct));

        return lost;
    }

    /// <summary>
    /// The refusal message for <see cref="WouldLoseAsync"/>, naming both rows and every item —
    /// and saying what to do instead, because "refused" without a next step is just a wall.
    /// </summary>
    public static string Refusal(
        int duplicateId, string duplicateName, int primaryId, string primaryName,
        IReadOnlyList<Holding> lost) =>
        $"Not linked. #{duplicateId} ({duplicateName}) holds "
        + string.Join(", ", lost.Select(l => l.Label))
        + $" and #{primaryId} ({primaryName}) does not, so linking this way would remove "
        + "them from the hotel, swag, lunch and dinner counts. Link the other way round "
        + $"(make #{duplicateId} the primary), or move those answers to #{primaryId} first.";
}
