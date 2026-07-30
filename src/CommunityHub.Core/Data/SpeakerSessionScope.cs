using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Data;

/// <summary>
/// THE definition of "a session that belongs to this speaker" — one predicate, used by
/// every surface that asks the question.
///
/// <para>§428 (operator 2026-07-27, on a slide upload that succeeded for a session
/// <i>"My sessions"</i> said was not linked): two checks meant the same thing and were
/// written twice. <c>SpeakerPresentationService.UploadAsync</c> accepted an upload whenever
/// a <see cref="SessionSpeaker"/> row existed, while <c>SpeakerSessionsService</c> and the
/// upload page's own slot list ADDITIONALLY required <c>!IsServiceSession</c>. A
/// service-flagged session was therefore <b>uploadable but invisible</b> — you could file a
/// deck against something the page told you did not exist.</para>
///
/// <para>That was not the cause of the incident he reported (the test speakers simply were
/// not on the session — see §428 RESOLVED), but it is a real divergence, and the fix is not
/// to add the missing clause in one more place: it is to make there be only one place. Two
/// checks that answer <i>"is this my session?"</i> must not be able to drift apart.</para>
/// </summary>
public static class SpeakerSessionScope
{
    /// <summary>
    /// The sessions <paramref name="participantId"/> speaks on in
    /// <paramref name="eventId"/>: their own <see cref="SessionSpeaker"/> rows, this
    /// edition only, service sessions (breaks / lunch) excluded. Composable — callers add
    /// their own ordering / projection / a further <c>.Where(s =&gt; s.Id == …)</c>.
    /// </summary>
    public static IQueryable<Session> MineAsSpeaker(
        this IQueryable<Session> sessions, int eventId, int participantId) =>
        sessions.Where(s => s.EventId == eventId
                            && !s.IsServiceSession
                            && s.SessionSpeakers.Any(ss => ss.ParticipantId == participantId));
}
