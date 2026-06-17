using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Read/write side of the organizer "action queue" -- the list of things a
/// participant changed late (after the event lock date) that an organizer must
/// chase with a downstream vendor (hotel, dinner caterer, ...). The form-save
/// handlers <see cref="UpsertOpenAsync"/> an item; the organizer drains it from
/// the Action Queue page via <see cref="ResolveAsync"/>.
/// </summary>
public sealed class OrganizerActionItemService
{
    public const string TypeHotelChanged   = "hotel-changed";
    public const string TypeDinnerChanged  = "dinner-changed";
    public const string TypeSwagChanged    = "swag-changed";
    public const string TypeTravelChanged  = "travel-changed";
    public const string TypeLunchChanged   = "lunch-changed";
    public const string TypeSpeakerChanged = "speaker-changed";
    /// <summary>An organizer re-opened an onboarding wizard step (flip-to-0) — the
    /// person must be reminded by email to complete that step.</summary>
    public const string TypeOnboardingStepReset = "onboarding-step-reset";
    /// <summary>A volunteer declined, or asked to swap, a shift they were assigned —
    /// a coordinator must reassign it.</summary>
    public const string TypeVolunteerShiftReassign = "volunteer-shift-needs-reassignment";
    /// <summary>Type PREFIX for a participant change-request raised AFTER the edition
    /// lock date (the form is read-only). The concrete type carries the form topic
    /// (e.g. "change-requested:hotel") so each form keeps its own queue row, while
    /// <see cref="LabelFor"/> still recognises the family. See <see cref="FormChangeRequestService"/>.</summary>
    public const string TypeChangeRequestedPrefix = "change-requested";

    /// <summary>Human label for a known type code (falls back to the raw code).</summary>
    public static string LabelFor(string type)
    {
        // Change-requests carry a per-form topic suffix; label the whole family.
        if (type.StartsWith(TypeChangeRequestedPrefix + ":", StringComparison.Ordinal)
            || type == TypeChangeRequestedPrefix)
        {
            return "Change requested (after lock)";
        }

        return type switch
        {
            TypeHotelChanged   => "Hotel changed",
            TypeDinnerChanged  => "Dinner RSVP changed",
            TypeSwagChanged    => "Swag changed",
            TypeTravelChanged  => "Travel claim changed",
            TypeLunchChanged   => "Lunch changed",
            TypeSpeakerChanged => "Speaker info changed",
            TypeOnboardingStepReset => "Onboarding step re-opened",
            TypeVolunteerShiftReassign => "Volunteer shift needs reassignment",
            _                  => type,
        };
    }

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

    /// <summary>
    /// Days before the edition lock date inside which a CHANGE to an existing
    /// submission is treated as "late" and surfaced to organizers. Before this
    /// window edits stay quiet (no queue noise); after the lock date the forms
    /// are read-only so no edit reaches here at all. Implements the documented
    /// "late-change alerts done right" behaviour without a schema change.
    /// </summary>
    public const int LateChangeWindowDays = 14;

    /// <summary>
    /// Convenience used by self-service form handlers when a participant edits
    /// an <b>already-submitted</b> record. Raises (or refreshes) an action item
    /// only when "today" falls inside the late-change window before the edition
    /// lock date -- the final stretch where a change likely contradicts what was
    /// already sent to a downstream vendor. First-time submissions and edits
    /// well before the lock date stay quiet. Returns true if an item was
    /// opened/refreshed.
    /// </summary>
    public async Task<bool> RaiseIfLateAsync(
        int eventId, string type, int? participantId, string summary,
        CancellationToken ct = default)
    {
        var lockDate = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => e.LockDate)
            .FirstOrDefaultAsync(ct);
        if (lockDate is null) return false;

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var windowOpens = lockDate.Value.AddDays(-LateChangeWindowDays);

        // Quiet before the window; on/after the lock date the form is read-only
        // so we never actually get here -- but guard anyway.
        if (today < windowOpens || today > lockDate.Value) return false;

        await UpsertOpenAsync(eventId, type, participantId, summary, ct);
        return true;
    }

    /// <summary>
    /// Open (unresolved) action items for an edition, optionally filtered by
    /// type, newest activity first. Includes the participant for display.
    /// </summary>
    public async Task<IReadOnlyList<OrganizerActionItem>> GetOpenAsync(
        int eventId, string? type = null, CancellationToken ct = default)
    {
        var q = _db.OrganizerActionItems
            .Include(a => a.Participant)
            .Where(a => a.EventId == eventId && a.ResolvedAt == null);
        if (!string.IsNullOrWhiteSpace(type))
        {
            q = q.Where(a => a.Type == type);
        }
        return await q
            .OrderByDescending(a => a.UpdatedAt ?? a.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>Recently resolved items (for an "already handled" audit list).</summary>
    public async Task<IReadOnlyList<OrganizerActionItem>> GetResolvedAsync(
        int eventId, int take = 50, CancellationToken ct = default)
    {
        return await _db.OrganizerActionItems
            .Include(a => a.Participant)
            .Where(a => a.EventId == eventId && a.ResolvedAt != null)
            .OrderByDescending(a => a.ResolvedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <summary>Count of open items, for the dashboard badge.</summary>
    public async Task<int> CountOpenAsync(int eventId, CancellationToken ct = default)
        => await _db.OrganizerActionItems
            .CountAsync(a => a.EventId == eventId && a.ResolvedAt == null, ct);

    /// <summary>
    /// Mark one item resolved (idempotent: a re-resolve keeps the first
    /// timestamp). Scoped to the edition so an organizer can never resolve
    /// another edition's item. Returns false if the id was not an open item of
    /// this edition.
    /// </summary>
    public async Task<bool> ResolveAsync(
        int eventId, int id, string? notes, CancellationToken ct = default)
    {
        var item = await _db.OrganizerActionItems.FirstOrDefaultAsync(
            a => a.Id == id && a.EventId == eventId && a.ResolvedAt == null, ct);
        if (item is null) return false;

        item.ResolvedAt = _clock.GetUtcNow();
        item.ResolvedNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Re-open a previously resolved item (scoped to the edition). Clears the
    /// resolved stamp + notes and refreshes UpdatedAt. Returns false if the id
    /// was not a resolved item of this edition.
    /// </summary>
    public async Task<bool> ReopenAsync(int eventId, int id, CancellationToken ct = default)
    {
        var item = await _db.OrganizerActionItems.FirstOrDefaultAsync(
            a => a.Id == id && a.EventId == eventId && a.ResolvedAt != null, ct);
        if (item is null) return false;

        item.ResolvedAt = null;
        item.ResolvedNotes = null;
        item.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
