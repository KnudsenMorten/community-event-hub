using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Data;

/// <summary>
/// §1307 — the speaker "hat": someone whose ROLE is not Speaker but who speaks (has a <see cref="SpeakerProfile"/>),
/// and the panel-only rule.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-26: a confirmed volunteer joins a panel — <i>"so she will get speaker emails, welcome mails as
/// speaker, etc. But i don't her counted double"</i>. One participant row stays one person for every headcount; the hat
/// adds the speaker welcome (its own once-ever ledger key) and the SESSION tasks, never a second set of logistics.</para>
/// <para>🔒 Only these roles. A sponsor contact or an attendee with a speaker profile is NOT included — their own flows
/// (exhibitor speaker, ticket) are different and were not part of the decision.</para>
/// </remarks>
public static class SpeakerHat
{
    /// <summary>Roles that may carry a speaker hat on the same row.</summary>
    // 🔒 VOLUNTEERS only (2026-09-27, before the PROD release): organizers who speak stay OUTSIDE the speaker programme,
    // as the operator decided in §1255g ("we will not touch the organizer / speaker problem") — otherwise the PROD deploy
    // would send Martin (#8) and Morten (#10) the speaker welcome, which cannot be recalled. Add Organizer here only on his word.
    public static readonly ParticipantRole[] Roles = { ParticipantRole.Volunteer };

    /// <summary>
    /// Participants whose every non-service session in the edition is a panel discussion — they get no presentation
    /// upload tasks (operator 2026-09-26: panels are "discussions only"). Someone with a talk AND a panel is not here.
    /// </summary>
    public static async Task<IReadOnlySet<int>> PanelOnlySpeakerIdsAsync(
        CommunityHubDbContext db, int eventId, CancellationToken ct = default)
    {
        var links = await db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .SelectMany(s => s.SessionSpeakers.Select(ss => new { ss.ParticipantId, IsPanel = s.Type == SessionType.PanelDiscussion }))
            .ToListAsync(ct);
        return links.GroupBy(l => l.ParticipantId)
            .Where(g => g.All(l => l.IsPanel))
            .Select(g => g.Key)
            .ToHashSet();
    }
}
