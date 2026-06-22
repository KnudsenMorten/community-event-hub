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
    public async Task<IReadOnlyList<RingResourceRow>> GetParticipantsAsync(
        int eventId, RingResourceKind kind, CancellationToken ct = default)
    {
        var roles = RolesFor(kind);
        var people = await _db.Participants
            .Where(p => p.EventId == eventId && roles.Contains(p.Role))
            .OrderBy(p => p.FullName)
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

    /// <summary>Set a participant's own ring (sponsor contact / speaker / volunteer). Edition-scoped.</summary>
    public async Task<bool> SetParticipantRingAsync(
        int eventId, int participantId, Ring ring, CancellationToken ct = default)
    {
        var p = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null) return false;
        p.Ring = ring;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Set a sponsor COMPANY's default ring (the fallback for its contacts). Edition-scoped.</summary>
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
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static ParticipantRole[] RolesFor(RingResourceKind kind) => kind switch
    {
        RingResourceKind.SponsorContact => new[] { ParticipantRole.Sponsor },
        RingResourceKind.Speaker => new[]
            { ParticipantRole.Speaker },
        RingResourceKind.Volunteer => new[] { ParticipantRole.Volunteer },
        _ => Array.Empty<ParticipantRole>(),
    };
}
