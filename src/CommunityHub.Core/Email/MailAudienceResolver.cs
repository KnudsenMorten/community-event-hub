using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>One person a campaign would write to.</summary>
/// <param name="Email">Lower-cased; the dedupe and suppression key.</param>
/// <param name="Name">For the greeting, when we have one.</param>
/// <param name="ParticipantId">Null for an imported external recipient.</param>
public sealed record MailRecipient(string Email, string? Name, int? ParticipantId);

/// <summary>
/// §1080 — THE TWELVE AUDIENCES, each resolved in exactly one place.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-12 listed them; this is that list turned into queries.</para>
///
/// <para>🔑 <b>One definition per audience.</b> "All attendees" must mean the same set in the
/// preview an organizer approves and in the send that follows — a campaign whose recipient list is
/// recomputed by different code in two places is a campaign that mails somebody the approver never
/// saw.</para>
///
/// <para>🔒 <b>Every audience is de-duplicated by address.</b> A person can be a speaker AND a
/// volunteer; a company can have two contacts on one address. Sending twice is the fastest way to
/// look like a machine.</para>
///
/// <para>⚠️ <b>Suppression is NOT applied here.</b> It is applied at send time (see
/// <see cref="MailSuppression"/>), because an audience resolved on Monday must respect an
/// unsubscribe that arrives on Tuesday. The preview shows both numbers so the difference is never a
/// surprise.</para>
/// </remarks>
public sealed class MailAudienceResolver
{
    private readonly CommunityHubDbContext _db;

    public MailAudienceResolver(CommunityHubDbContext db) => _db = db;

    /// <summary>The operator-facing name of an audience — used on the page and in the preview.</summary>
    public static string Describe(MailAudience audience) => audience switch
    {
        MailAudience.AllParticipants => "All participants (everyone with a role in the hub)",
        MailAudience.AllAttendees => "All attendees (active tickets)",
        MailAudience.TwoDayTicketHolders => "All 2-day ticket holders",
        MailAudience.OneDayTicketHolders => "All 1-day ticket holders",
        MailAudience.SponsorsAndExhibitors => "All sponsors and exhibitors",
        MailAudience.ExhibitorsOnly => "All exhibitors",
        MailAudience.Volunteers => "All volunteers",
        MailAudience.Speakers => "All speakers",
        MailAudience.MasterClassSpeakers => "All Master Class speakers",
        MailAudience.Media => "All media",
        MailAudience.EventPartners => "All event partners",
        MailAudience.PreviousAttendeesNotThisEdition =>
            "Previous ELDK attendees, excluding this edition's attendees",
        _ => audience.ToString(),
    };

    /// <summary>
    /// 🔴 True for the one audience that reaches people who are NOT participants of this edition —
    /// the only one the transport's ring gate would refuse, and the reason campaigns needed their
    /// own switch and an acknowledged dry-run at all.
    /// </summary>
    public static bool ReachesOutsideTheHub(MailAudience audience) =>
        audience == MailAudience.PreviousAttendeesNotThisEdition;

    public async Task<IReadOnlyList<MailRecipient>> ResolveAsync(
        int eventId, MailAudience audience, CancellationToken ct = default)
    {
        var people = audience switch
        {
            MailAudience.PreviousAttendeesNotThisEdition => await PreviousAsync(eventId, ct),
            MailAudience.AllAttendees => await AttendeesAsync(eventId, null, ct),
            MailAudience.TwoDayTicketHolders => await AttendeesAsync(eventId, TicketStatus.TwoDay, ct),
            MailAudience.OneDayTicketHolders => await AttendeesAsync(eventId, TicketStatus.Other, ct),
            _ => await ParticipantsAsync(eventId, audience, ct),
        };

        // 🔒 One address, one mail — whichever route found them.
        return people
            .Where(p => !string.IsNullOrWhiteSpace(p.Email) && p.Email.Contains('@'))
            .GroupBy(p => p.Email, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The role-based audiences. ⚠️ ACTIVE people only: a deactivated participant is somebody who
    /// left, and a campaign is not the moment to rediscover them.
    /// </summary>
    private async Task<List<MailRecipient>> ParticipantsAsync(
        int eventId, MailAudience audience, CancellationToken ct)
    {
        var q = _db.Participants
            .AsNoTracking()
            .Where(p => p.EventId == eventId && p.IsActive
                        && p.LifecycleState == ParticipantLifecycleState.Active
                        && !p.IsTestUser);

        q = audience switch
        {
            MailAudience.AllParticipants => q,
            MailAudience.Volunteers => q.Where(p => p.Role == ParticipantRole.Volunteer),
            MailAudience.Speakers => q.Where(p => p.Role == ParticipantRole.Speaker),
            MailAudience.Media => q.Where(p => p.Role == ParticipantRole.Media),
            MailAudience.EventPartners => q.Where(p => p.Role == ParticipantRole.EventPartner),
            MailAudience.SponsorsAndExhibitors => q.Where(p => p.Role == ParticipantRole.Sponsor),
            MailAudience.ExhibitorsOnly => q.Where(p => p.Role == ParticipantRole.Sponsor),
            // A Master Class speaker is a speaker who is ON a master class — see below.
            MailAudience.MasterClassSpeakers => q.Where(p => p.Role == ParticipantRole.Speaker),
            _ => q,
        };

        var rows = await q
            .Select(p => new { p.Id, p.Email, p.FullName, p.SponsorCompanyId })
            .ToListAsync(ct);

        // 🔑 The two audiences that need a second fact about the person, applied in memory because
        // the fact lives in another table.
        if (audience == MailAudience.ExhibitorsOnly)
        {
            // §1034 — "exhibitor" is a FLAG on the sponsor company, not a role. A digital-only
            // sponsor (IsSponsor = 1, IsExhibitor = 0) must not receive booth logistics.
            var exhibitorCompanies = (await _db.SponsorInfos
                    .AsNoTracking()
                    .Where(s => s.EventId == eventId && s.IsExhibitor)
                    .Select(s => s.SponsorCompanyId)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            rows = rows
                .Where(r => r.SponsorCompanyId is not null
                            && exhibitorCompanies.Contains(r.SponsorCompanyId))
                .ToList();
        }
        else if (audience == MailAudience.MasterClassSpeakers)
        {
            var mcSpeakerIds = await _db.SessionSpeakers
                .AsNoTracking()
                .Where(s => s.Session!.EventId == eventId
                            && s.Session.Type == SessionType.MasterClass)
                .Select(s => s.ParticipantId)
                .ToListAsync(ct);
            var set = mcSpeakerIds.ToHashSet();
            rows = rows.Where(r => set.Contains(r.Id)).ToList();
        }

        return rows
            .Select(r => new MailRecipient(r.Email.Trim().ToLowerInvariant(), r.FullName, r.Id))
            .ToList();
    }

    /// <summary>
    /// The attendee audiences, from the live mirror.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>MirrorState.Active</c> only — a cancelled ticket is not an attendee, and §128 keeps
    /// those rows around with their ticket status intact precisely so nothing treats them as live.
    /// </remarks>
    private async Task<List<MailRecipient>> AttendeesAsync(
        int eventId, TicketStatus? ticket, CancellationToken ct)
    {
        var q = _db.Attendees
            .AsNoTracking()
            .Where(a => a.EventId == eventId && a.MirrorState == MirrorState.Active);

        if (ticket is { } t) q = q.Where(a => a.TicketStatus == t);

        return (await q.Select(a => new { a.Email, a.FullName }).ToListAsync(ct))
            .Select(a => new MailRecipient(a.Email.Trim().ToLowerInvariant(), a.FullName, null))
            .ToList();
    }

    /// <summary>
    /// 🔴 The imported list MINUS this edition's attendees — his twelfth audience, and the only one
    /// that leaves the hub's own people.
    /// </summary>
    /// <remarks>
    /// ⚠️ The exclusion is by ADDRESS, computed at resolve time. Somebody who bought a ticket
    /// yesterday drops out today without anybody editing a list, which is the whole point of
    /// expressing it as "excluding" rather than as a second import.
    /// </remarks>
    private async Task<List<MailRecipient>> PreviousAsync(int eventId, CancellationToken ct)
    {
        var thisEdition = (await _db.Attendees
                .AsNoTracking()
                .Where(a => a.EventId == eventId && a.MirrorState == MirrorState.Active)
                .Select(a => a.Email)
                .ToListAsync(ct))
            .Select(e => e.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var imported = await _db.ExternalRecipients
            .AsNoTracking()
            .Where(r => r.EventId == eventId)
            .Select(r => new { r.Email, r.FullName })
            .ToListAsync(ct);

        return imported
            .Select(r => new MailRecipient(r.Email.Trim().ToLowerInvariant(), r.FullName, null))
            .Where(r => !thisEdition.Contains(r.Email))
            .ToList();
    }
}
