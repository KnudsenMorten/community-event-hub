using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Domain;

/// <summary>
/// §1255 — WHAT A PARTICIPANT ROW CAN DO, read from the rows that actually exist rather than
/// from the single-valued <see cref="Participant.Role"/> enum.
///
/// <para>The model conflates "a human" with "a role at one edition" (§1254), so one field decides
/// access for a person who legitimately holds several roles: an organizer who also speaks, a
/// volunteer accepted in Sessionize, an attendee who became a speaker. Their <c>Role</c> stays
/// whatever it was created as — the speaker import deliberately never overwrites it — while the
/// hub creates a <see cref="SpeakerProfile"/>, links sessions and seeds deadlines. The data is
/// right; only the gate is wrong.</para>
///
/// <para>🔑 So access is derived from the CAPABILITY that already exists — <i>has a speaker
/// profile ⇒ speaker hub</i> — and not from a new <c>Participant.Roles</c> set, which §1254 marks
/// a dead end (<c>Participation</c> replaces it in the target model). Same fix, no schema change,
/// and it is how the target model works: access derives from participations.</para>
///
/// <para>⚠️ A capability is granted by an IMPORT, so access widens without a human act. That is
/// deliberate here (the four live rows measured on 2026-09-20 were all genuine speakers locked out
/// of their own material) but it is an access change on a live edition — see
/// <c>docs/REQUIREMENTS.md</c> §1255 for the open "auto, or confirm?" decision.</para>
/// </summary>
public static class ParticipantCapabilities
{
    /// <summary>
    /// True when this row may use the SPEAKER hub: it is Role=Speaker, <b>or</b> it owns a
    /// <see cref="SpeakerProfile"/> in this edition (which is what every speaker page reads).
    /// <para>Deliberately NOT widened to a bare <see cref="SessionSpeaker"/> link: the profile is
    /// the row the hub is built on, and a person the import gave a profile to is a person the
    /// import decided is a speaker.</para>
    /// </summary>
    public static async Task<bool> HasSpeakerHubAccessAsync(
        CommunityHubDbContext db, int eventId, int participantId,
        ParticipantRole role, CancellationToken ct = default)
    {
        if (role == ParticipantRole.Speaker) return true;
        if (eventId <= 0 || participantId <= 0) return false;
        return await db.SpeakerProfiles.AsNoTracking()
            .AnyAsync(sp => sp.EventId == eventId && sp.ParticipantId == participantId, ct);
    }

    /// <summary>
    /// §1255b — the capabilities a row holds BESIDES its attendee ticket, named in plain words
    /// (empty when it holds none). Used by the attendee-ticket reconcile to refuse to deactivate
    /// a login that a person still needs for another reason: a speaker who bought a ticket and
    /// then cancels it must keep her speaker hub.
    /// <para>Deliberately GENEROUS — every signal here keeps a login alive, so a false positive
    /// costs an account that stays open one edition too long, while a false negative locks a
    /// speaker out of her own sessions days before the event.</para>
    /// </summary>
    public static async Task<IReadOnlyList<string>> NonAttendeeCapabilitiesAsync(
        CommunityHubDbContext db, Participant participant, CancellationToken ct = default)
    {
        var found = new List<string>();
        var eventId = participant.EventId;
        var id = participant.Id;

        if (await db.SpeakerProfiles.AsNoTracking()
                .AnyAsync(sp => sp.EventId == eventId && sp.ParticipantId == id, ct))
        {
            found.Add("speaker profile");
        }
        else if (await db.SessionSpeakers.AsNoTracking()
                .AnyAsync(ss => ss.ParticipantId == id && ss.Session.EventId == eventId, ct))
        {
            found.Add("session speaker link");
        }

        if (await db.VolunteerTaskAssignments.AsNoTracking()
                .AnyAsync(a => a.EventId == eventId && a.ParticipantId == id, ct))
        {
            found.Add("volunteer assignment");
        }

        if (!string.IsNullOrWhiteSpace(participant.SponsorCompanyId))
        {
            found.Add("sponsor company link");
        }

        return found;
    }
}

/// <summary>
/// §1255 — injectable form of <see cref="ParticipantCapabilities.HasSpeakerHubAccessAsync"/>, so
/// every speaker page asks the same question the same way (several of them hold no
/// <see cref="CommunityHubDbContext"/> of their own).
/// </summary>
public sealed class SpeakerAccessService
{
    private readonly CommunityHubDbContext _db;

    public SpeakerAccessService(CommunityHubDbContext db) => _db = db;

    /// <inheritdoc cref="ParticipantCapabilities.HasSpeakerHubAccessAsync"/>
    public Task<bool> HasSpeakerHubAccessAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default)
        => ParticipantCapabilities.HasSpeakerHubAccessAsync(_db, eventId, participantId, role, ct);
}
