using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// One row in the PUBLIC speakers overview (<c>/Speakers</c>). Read-only projection
/// of a published speaker: their display name, tagline, photo, and the title(s) of
/// the session(s) they are linked to. Only ever built for a speaker the organizer
/// has explicitly selected for publish — see <see cref="PublicSpeakersService"/>.
/// </summary>
public sealed record PublicSpeakerRow(
    int ParticipantId,
    string Name,
    string? Tagline,
    string? PhotoUrl,
    IReadOnlyList<PublicSpeakerSession> Sessions)
{
    /// <summary>
    /// Up to two uppercase initials from the speaker name, for the monogram shown
    /// when there is no photo (the graceful photo fallback).
    /// </summary>
    public string Initials => PublicInitials.From(Name);
}

/// <summary>
/// One session linked to a published speaker, for cross-linking from the speaker
/// card / detail page to the public session-detail page (<c>/Sessions/{id}</c>).
/// </summary>
public sealed record PublicSpeakerSession(int SessionId, string Title);

/// <summary>
/// The PUBLIC, no-login detail of a single published speaker (<c>/Speakers/{id}</c>):
/// the same fields as a row plus their bio, when one is on file. Only ever built for
/// a speaker that passes the HARD GATE; see <see cref="PublicSpeakersService"/>.
/// </summary>
public sealed record PublicSpeakerDetail(
    int ParticipantId,
    string EventDisplayName,
    string Name,
    string? Tagline,
    string? Bio,
    string? PhotoUrl,
    IReadOnlyList<PublicSpeakerSession> Sessions)
{
    /// <summary>Monogram initials for the no-photo fallback.</summary>
    public string Initials => PublicInitials.From(Name);
}

/// <summary>Shared monogram-initials helper for the public overview pages.</summary>
public static class PublicInitials
{
    /// <summary>Up to two uppercase initials from a display name (e.g. "Alice Adams" → "AA").</summary>
    public static string From(string? name)
    {
        var words = (name ?? string.Empty)
            .Split(new[] { ' ', '-', '/', '.', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        if (words.Length == 1)
            return words[0].Length >= 2
                ? words[0].Substring(0, 2).ToUpperInvariant()
                : words[0].ToUpperInvariant();
        return (words[0][..1] + words[1][..1]).ToUpperInvariant();
    }
}

/// <summary>The whole public speaker lineup: the published speakers (may be empty).</summary>
public sealed record PublicSpeakersView(
    string EventDisplayName,
    IReadOnlyList<PublicSpeakerRow> Speakers);

/// <summary>
/// Builds the data for the PUBLIC, no-login speakers overview page (REQUIREMENTS § 6 —
/// "never publish an unselected speaker"). Scoped to the currently <b>active</b>
/// edition (the same active-event resolution the public sessions overview uses).
///
/// <b>HARD GATE.</b> A speaker is included ONLY when their
/// <see cref="SpeakerProfile.SelectedForPublish"/> flag is true AND their
/// <see cref="Participant.IsActive"/> is true AND they hold a speaker role. The flag
/// defaults to false for everyone, so until the lineup is selected the view returns
/// zero speakers and the page renders a "lineup coming soon" empty state. As soon as
/// an organizer flips the flag the speaker appears automatically — no second switch.
///
/// Read-only: it never writes. An unselected, withdrawn, or non-speaker row can never
/// leak into the result by construction.
/// </summary>
public sealed class PublicSpeakersService
{
    private readonly CommunityHubDbContext _db;

    public PublicSpeakersService(CommunityHubDbContext db) => _db = db;

    /// <summary>
    /// Build the public speaker lineup for the active edition. Returns <c>null</c>
    /// when there is no active event (the page then renders a friendly "no event"
    /// empty state). When an event is active but no speaker is selected for publish
    /// yet, returns a view with an empty <see cref="PublicSpeakersView.Speakers"/>
    /// list (the "lineup coming soon" empty state).
    /// </summary>
    public async Task<PublicSpeakersView?> BuildAsync(CancellationToken ct = default)
    {
        var active = await _db.Events
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.Id)
            .Select(e => new { e.Id, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (active is null) return null;

        var eventId = active.Id;

        // THE HARD GATE lives in this Where clause: published + active + a speaker
        // role. SelectedForPublish defaults false, so an unselected lineup yields
        // an empty list here (never an unselected speaker).
        var rows = await _db.SpeakerProfiles
            .Where(sp => sp.EventId == eventId
                         && sp.SelectedForPublish
                         && sp.Participant.IsActive
                         && sp.Participant.Role == ParticipantRole.Speaker)
            .Select(sp => new PublicSpeakerRow(
                sp.ParticipantId,
                sp.Participant.FullName,
                sp.Tagline,
                sp.PhotoUrl,
                _db.SessionSpeakers
                    .Where(ss => ss.ParticipantId == sp.ParticipantId
                                 && ss.Session.EventId == eventId
                                 && !ss.Session.IsServiceSession)
                    .OrderBy(ss => ss.Session.StartsAt)
                    .ThenBy(ss => ss.Session.Title)
                    .Select(ss => new PublicSpeakerSession(ss.SessionId, ss.Session.Title))
                    .ToList()))
            .ToListAsync(ct);

        // Deterministic, human-friendly order: by display name.
        var ordered = rows
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PublicSpeakersView(active.DisplayName, ordered);
    }

    /// <summary>
    /// Resolve one published speaker's PUBLIC detail by participant id, scoped to the
    /// active edition. Enforces the SAME HARD GATE as the lineup: returns <c>null</c>
    /// when there is no active event, OR the participant is not a selected, active,
    /// speaker-role profile in this edition (so an unselected/withdrawn/non-speaker id
    /// can never be poked to leak a profile). Includes the bio + linked session(s).
    /// </summary>
    public async Task<PublicSpeakerDetail?> GetByIdAsync(int participantId, CancellationToken ct = default)
    {
        var active = await _db.Events
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.Id)
            .Select(e => new { e.Id, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (active is null) return null;

        var eventId = active.Id;

        var sp = await _db.SpeakerProfiles
            .Where(p => p.ParticipantId == participantId
                        && p.EventId == eventId
                        && p.SelectedForPublish
                        && p.Participant.IsActive
                        && p.Participant.Role == ParticipantRole.Speaker)
            .Select(p => new
            {
                p.ParticipantId,
                p.Participant.FullName,
                p.Tagline,
                p.Biography,
                p.PhotoUrl,
                Sessions = _db.SessionSpeakers
                    .Where(ss => ss.ParticipantId == p.ParticipantId
                                 && ss.Session.EventId == eventId
                                 && !ss.Session.IsServiceSession)
                    .OrderBy(ss => ss.Session.StartsAt)
                    .ThenBy(ss => ss.Session.Title)
                    .Select(ss => new PublicSpeakerSession(ss.SessionId, ss.Session.Title))
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (sp is null) return null;

        return new PublicSpeakerDetail(
            sp.ParticipantId,
            active.DisplayName,
            sp.FullName,
            sp.Tagline,
            sp.Biography,
            sp.PhotoUrl,
            sp.Sessions);
    }
}
