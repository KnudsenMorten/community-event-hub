using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// The facts the PUBLIC, anonymous landing page (<c>/</c> for a signed-out visitor)
/// renders: the active edition's name + dates + venue, plus how much of the public
/// programme is live yet (session count, and whether the speaker lineup has been
/// selected for publish). Read-only projection of the active <see cref="Event"/>.
///
/// The session count covers the same non-service sessions the public
/// <c>/Sessions</c> overview lists. <see cref="HasSelectedSpeakers"/> reflects the
/// SAME hard gate the public <c>/Speakers</c> page uses (an organizer-selected,
/// active, speaker-role profile), so the landing's "meet the speakers" CTA only
/// promises a lineup once one actually exists.
/// </summary>
public sealed record PublicLandingView(
    string CommunityName,
    string EventDisplayName,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly? PreDayDate,
    string? VenueName,
    int SessionCount,
    bool HasSelectedSpeakers);

/// <summary>
/// Builds the data for the PUBLIC, anonymous landing page. Scoped to the currently
/// <b>active</b> edition (the same active-event resolution the public sessions /
/// speakers overviews use), so a signed-out visitor sees the live edition's facts.
///
/// Read-only: it never writes. Returns <c>null</c> when there is no active event,
/// so the page can render a friendly "no live event" empty state.
/// </summary>
public sealed class PublicLandingService
{
    private readonly CommunityHubDbContext _db;

    public PublicLandingService(CommunityHubDbContext db) => _db = db;

    /// <summary>
    /// Build the landing facts for the active edition, or <c>null</c> when no event
    /// is active. The speaker-lineup flag honours the public-speakers HARD GATE
    /// (SelectedForPublish + active + speaker role) so the landing never advertises
    /// an unselected lineup.
    /// </summary>
    public async Task<PublicLandingView?> BuildAsync(CancellationToken ct = default)
    {
        var active = await _db.Events
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.Id)
            .Select(e => new
            {
                e.Id,
                e.CommunityName,
                e.DisplayName,
                e.StartDate,
                e.EndDate,
                e.PreDayDate,
                e.VenueName,
            })
            .FirstOrDefaultAsync(ct);
        if (active is null) return null;

        var sessionCount = await _db.Sessions
            .CountAsync(s => s.EventId == active.Id && !s.IsServiceSession, ct);

        // The SAME gate as PublicSpeakersService: only a selected, active,
        // speaker-role profile counts — so the "meet the speakers" CTA is honest.
        var hasSpeakers = await _db.SpeakerProfiles
            .AnyAsync(sp => sp.EventId == active.Id
                            && sp.SelectedForPublish
                            && sp.Participant.IsActive
                            && (sp.Participant.Role == ParticipantRole.Speaker
                                || sp.Participant.Role == ParticipantRole.MasterclassSpeaker), ct);

        return new PublicLandingView(
            string.IsNullOrWhiteSpace(active.CommunityName) ? active.DisplayName : active.CommunityName,
            active.DisplayName,
            active.StartDate,
            active.EndDate,
            active.PreDayDate,
            string.IsNullOrWhiteSpace(active.VenueName) ? null : active.VenueName,
            sessionCount,
            hasSpeakers);
    }
}
