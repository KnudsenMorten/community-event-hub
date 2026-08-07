using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Settings;

/// <summary>
/// The kind of resource a ring is assigned to in the admin interface
/// (REQUIREMENTS §23). A sponsor COMPANY carries a default ring for its contacts;
/// the other kinds are individual participants whose own ring supersedes any
/// company default.
/// </summary>
public enum RingResourceKind
{
    SponsorCompany = 0,
    SponsorContact = 1,
    Speaker = 2,
    Volunteer = 3,

    /// <summary>
    /// §706 — ATTENDEES. Operator 2026-08-11 go-live plan: synced attendees default to Broad, he
    /// assigns a few to Ring 2 when ticket sales open, tests attendee mail on them, then raises the
    /// mails to Broad once proven.
    /// </summary>
    /// <remarks>
    /// 🔑 An attendee is a PARTICIPANT with <see cref="ParticipantRole.Attendee"/> — operator
    /// 2026-07-29: *"a participant is anyone that are part of the event"*. The ring therefore lives on
    /// <c>Participant.Ring</c> like every other role; the separate <c>Attendees</c> table is the Zoho
    /// TICKET mirror, not the person record, and carries no ring.
    /// </remarks>
    Attendee = 4,
}

/// <summary>One assignable resource row for the admin ring surface.</summary>
/// <param name="Kind">Company / sponsor-contact / speaker / volunteer.</param>
/// <param name="Id">Participant id, or 0 for a sponsor company (keyed by <paramref name="CompanyId"/>).</param>
/// <param name="CompanyId">The sponsor company id (company rows + sponsor-contact rows).</param>
/// <param name="DisplayName">Person full name or company id (the admin label).</param>
/// <param name="Ring">The resource's own ring (the company default for a company row).</param>
/// <param name="EffectiveRing">
/// For a sponsor contact, the resolved effective ring after the company-default
/// rule; for the others it equals <paramref name="Ring"/>.
/// </param>
public sealed record RingResourceRow(
    RingResourceKind Kind,
    int Id,
    string? CompanyId,
    string DisplayName,
    Ring Ring,
    Ring EffectiveRing);

/// <summary>
/// Lists + assigns the release ring of the rollout-relevant resources for the
/// admin interface (REQUIREMENTS §23): sponsor companies, sponsor contacts,
/// speakers and volunteers. EF-backed, edition-scoped. Reads use
/// <see cref="RingResolver.EffectiveForContact"/> so the admin sees the same
/// effective ring the gate enforces.
/// </summary>
public sealed class ResourceRingService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public ResourceRingService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Sponsor companies of an edition (the per-company default ring), by company id.</summary>
    public async Task<IReadOnlyList<RingResourceRow>> GetSponsorCompaniesAsync(
        int eventId, CancellationToken ct = default)
    {
        var rows = await _db.SponsorInfos
            .Where(s => s.EventId == eventId)
            .OrderBy(s => s.SponsorCompanyId)
            .Select(s => new { s.SponsorCompanyId, s.Ring })
            .ToListAsync(ct);

        return rows.Select(s => new RingResourceRow(
            RingResourceKind.SponsorCompany, 0, s.SponsorCompanyId,
            s.SponsorCompanyId, s.Ring, s.Ring)).ToList();
    }

    /// <summary>
    /// Participants of one role-set with their own + effective ring. Sponsor
    /// contacts resolve their effective ring against the company default; other
    /// roles' effective ring equals their own ring.
    /// </summary>
    /// <param name="search">
    /// §706 — optional name/email filter, applied SERVER-side. Needed for attendees: the operator
    /// picks a handful of ring-2 test recipients out of a list heading for ~1500 rows, so the page must
    /// not try to render them all.
    /// </param>
    /// <param name="take">
    /// §706 — optional cap on rows returned (after ordering). Null = no cap, which is the existing
    /// behaviour every other kind relies on.
    /// </param>
    public async Task<IReadOnlyList<RingResourceRow>> GetParticipantsAsync(
        int eventId, RingResourceKind kind, CancellationToken ct = default,
        string? search = null, int? take = null)
    {
        var roles = RolesFor(kind);
        var q = _db.Participants
            .Where(p => p.EventId == eventId && roles.Contains(p.Role));

        var term = (search ?? string.Empty).Trim();
        if (term.Length > 0)
        {
            // Name OR email, case-insensitive by the database collation.
            q = q.Where(p => p.FullName.Contains(term) || p.Email.Contains(term));
        }

        q = q.OrderBy(p => p.FullName);
        if (take is int n && n > 0) q = q.Take(n);

        var people = await q
            .Select(p => new
            {
                p.Id, p.FullName, p.Email, p.Ring, p.SponsorCompanyId, p.Role,
            })
            .ToListAsync(ct);

        // For sponsor contacts, fold in the per-company default ring.
        var companyRings = kind == RingResourceKind.SponsorContact
            ? await _db.SponsorInfos
                .Where(s => s.EventId == eventId)
                .ToDictionaryAsync(s => s.SponsorCompanyId, s => s.Ring, ct)
            : new Dictionary<string, Ring>();

        return people.Select(p =>
        {
            Ring effective;
            if (kind == RingResourceKind.SponsorContact
                && !string.IsNullOrWhiteSpace(p.SponsorCompanyId)
                && companyRings.TryGetValue(p.SponsorCompanyId, out var companyRing))
            {
                effective = RingResolver.EffectiveForContact(p.Ring, companyRing);
            }
            else
            {
                effective = Rings.Effective(p.Ring);
            }

            var label = string.IsNullOrWhiteSpace(p.FullName) ? p.Email : p.FullName;
            return new RingResourceRow(kind, p.Id, p.SponsorCompanyId, label, p.Ring, effective);
        }).ToList();
    }

    /// <summary>
    /// §706 — how many participants MATCH (before <c>take</c>), so a capped list can say so instead of
    /// silently looking complete. 🔒 A truncated list that does not admit it is how someone concludes
    /// "that attendee isn't in the system".
    /// </summary>
    public Task<int> CountParticipantsAsync(
        int eventId, RingResourceKind kind, string? search = null, CancellationToken ct = default)
    {
        var roles = RolesFor(kind);
        var q = _db.Participants.Where(p => p.EventId == eventId && roles.Contains(p.Role));

        var term = (search ?? string.Empty).Trim();
        if (term.Length > 0)
        {
            q = q.Where(p => p.FullName.Contains(term) || p.Email.Contains(term));
        }

        return q.CountAsync(ct);
    }

    /// <summary>
    /// Set a participant's own ring (sponsor contact / speaker / volunteer / attendee). Edition-scoped.
    /// §940 — routed through <see cref="TestUserRule.AssignRing"/>, so landing on Ring 1 also flags the
    /// person as test data.
    /// </summary>
    public async Task<bool> SetParticipantRingAsync(
        int eventId, int participantId, Ring ring, CancellationToken ct = default)
    {
        var p = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null) return false;
        TestUserRule.AssignRing(p, ring);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Set a sponsor COMPANY's default ring (the fallback for its contacts). Edition-scoped.
    /// </summary>
    /// <remarks>
    /// §940 — the company ring is an entry point for making PEOPLE Ring 1: a contact still on the
    /// platform default inherits the company default (<see cref="RingResolver.EffectiveForContact"/>),
    /// so a company moved to Ring 1 turns those contacts into Ring-1 people without their own column
    /// ever changing. Their flag is set from the EFFECTIVE ring; a contact who was explicitly narrowed
    /// to another ring keeps their own ring and is left alone.
    /// </remarks>
    public async Task<bool> SetSponsorCompanyRingAsync(
        int eventId, string companyId, Ring ring, string? byEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(companyId)) return false;
        var s = await _db.SponsorInfos
            .FirstOrDefaultAsync(x => x.EventId == eventId && x.SponsorCompanyId == companyId, ct);
        if (s is null) return false;
        s.Ring = ring;
        s.UpdatedAt = _clock.GetUtcNow();
        s.LastUpdatedByEmail = string.IsNullOrWhiteSpace(byEmail) ? s.LastUpdatedByEmail : byEmail.Trim();

        if (TestUserRule.ImpliesTestUser(ring))
        {
            var contacts = await _db.Participants
                .Where(p => p.EventId == eventId
                            && p.Role == ParticipantRole.Sponsor
                            && p.SponsorCompanyId == companyId)
                .ToListAsync(ct);
            foreach (var contact in contacts)
            {
                TestUserRule.ApplyEffectiveRing(
                    contact, RingResolver.EffectiveForContact(contact.Ring, ring));
            }
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static ParticipantRole[] RolesFor(RingResourceKind kind) => kind switch
    {
        RingResourceKind.SponsorContact => new[] { ParticipantRole.Sponsor },
        RingResourceKind.Speaker => new[]
            { ParticipantRole.Speaker },
        RingResourceKind.Volunteer => new[] { ParticipantRole.Volunteer },
        RingResourceKind.Attendee => new[] { ParticipantRole.Attendee },   // §706
        _ => Array.Empty<ParticipantRole>(),
    };
}
