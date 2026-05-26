using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

public sealed class OrganizerActionItemService
{
    public const string TypeHotelChanged   = "hotel-changed";
    public const string TypeDinnerChanged  = "dinner-changed";
    public const string TypeSwagChanged    = "swag-changed";
    public const string TypeTravelChanged  = "travel-changed";
    public const string TypeLunchChanged   = "lunch-changed";
    public const string TypeSpeakerChanged = "speaker-changed";

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public OrganizerActionItemService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Open or update an action item. If an OPEN row already exists for
    /// (event, type, participant), its Summary + UpdatedAt are refreshed --
    /// the organizer keeps one entry per participant per topic regardless of
    /// how often the participant re-edits.
    /// </summary>
    public async Task UpsertOpenAsync(
        int eventId, string type, int? participantId, string summary,
        CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var open = await _db.OrganizerActionItems.FirstOrDefaultAsync(
            a => a.EventId == eventId
                 && a.Type == type
                 && a.ParticipantId == participantId
                 && a.ResolvedAt == null, ct);

        if (open is null)
        {
            _db.OrganizerActionItems.Add(new OrganizerActionItem
            {
                EventId = eventId,
                Type = type,
                ParticipantId = participantId,
                Summary = summary,
                CreatedAt = now,
            });
        }
        else
        {
            open.Summary = summary;
            open.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
    }
}
