using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// One of the signed-in speaker's OWN sessions, projected for the "My sessions"
/// card on the Speaker hub (REQUIREMENTS §20 Speaker — "self-service … 'My
/// sessions' (room/time/AV/master-class/attendee-questions)"). Read-only.
/// </summary>
/// <param name="SessionId">The session id, for cross-linking to the public detail page (<c>/Sessions/{id}</c>).</param>
/// <param name="Title">The session title.</param>
/// <param name="Room">The assigned room, when the grid is published (else null).</param>
/// <param name="StartsAt">Scheduled start, when the grid is published (else null).</param>
/// <param name="EndsAt">Scheduled end, when the grid is published (else null).</param>
/// <param name="IsMasterClass">True for a community master class (links to its logistics page).</param>
/// <param name="IsScheduled">True once the session has a start time (so the agenda + .ics make sense).</param>
/// <param name="OpenQuestionCount">How many attendee questions on this session are still open.</param>
/// <param name="CoSpeakerNames">The other speakers on the same session (excluding the viewer).</param>
public sealed record MySpeakerSession(
    int SessionId,
    string Title,
    string? Room,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    bool IsMasterClass,
    bool IsScheduled,
    int OpenQuestionCount,
    IReadOnlyList<string> CoSpeakerNames,
    /// <summary>The organizer-provided evaluation results link (Session.EvaluationFormUrl),
    /// or null. When set, the speaker grid shows an "Evaluations" download link.</summary>
    string? EvaluationUrl = null);

/// <summary>
/// Builds the signed-in speaker's OWN session list for the Speaker hub
/// "My sessions" card. <b>Own-row scoped, server-enforced:</b> every query is
/// filtered to <c>EventId == eventId</c> AND the session is one the
/// <c>participantId</c> is actually a <see cref="SessionSpeaker"/> on — a
/// speaker can never see another speaker's sessions through this service. Only
/// speaker roles get a non-empty list; any other role returns an empty list
/// (the hub anyway gates non-speakers out, but the service is defensive).
///
/// Read-only: never writes. Service sessions (breaks/lunch) are excluded.
/// </summary>
public sealed class SpeakerSessionsService
{
    private readonly CommunityHubDbContext _db;

    public SpeakerSessionsService(CommunityHubDbContext db) => _db = db;

    private static readonly ParticipantRole[] SpeakerRoles =
    {
        ParticipantRole.Speaker,
        ParticipantRole.MasterclassSpeaker,
    };

    /// <summary>
    /// The speaker's own (non-service) sessions in this edition, scheduled ones
    /// first (by start), then by title. Returns an empty list for a non-speaker
    /// role or a speaker with no linked sessions.
    /// </summary>
    public async Task<IReadOnlyList<MySpeakerSession>> GetMySessionsAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default)
    {
        if (!SpeakerRoles.Contains(role)) return Array.Empty<MySpeakerSession>();

        // Own-row scope: only sessions this participant is a SessionSpeaker on,
        // in this edition, excluding service sessions.
        var rows = await _db.Sessions
            .Where(s => s.EventId == eventId
                        && !s.IsServiceSession
                        && s.SessionSpeakers.Any(ss => ss.ParticipantId == participantId))
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.Room,
                s.StartsAt,
                s.EndsAt,
                s.Type,
                s.EvaluationFormUrl,
                OpenQuestionCount = s.Questions.Count(q => q.Status == SessionQuestionStatus.Open),
                CoSpeakers = s.SessionSpeakers
                    .Where(ss => ss.ParticipantId != participantId)
                    .Select(ss => ss.Participant.FullName)
                    .ToList(),
            })
            .ToListAsync(ct);

        return rows
            .OrderBy(r => r.StartsAt == null)            // scheduled first
            .ThenBy(r => r.StartsAt)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .Select(r => new MySpeakerSession(
                r.Id,
                r.Title,
                string.IsNullOrWhiteSpace(r.Room) ? null : r.Room,
                r.StartsAt,
                r.EndsAt,
                r.Type == SessionType.CommunityMasterClass,
                r.StartsAt is not null,
                r.OpenQuestionCount,
                r.CoSpeakers
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                string.IsNullOrWhiteSpace(r.EvaluationFormUrl) ? null : r.EvaluationFormUrl))
            .ToList();
    }
}
