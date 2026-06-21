using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Settings;

/// <summary>
/// Resolves the EFFECTIVE release ring of a resource (REQUIREMENTS §23). Pure +
/// EF-backed (constructor-injected <see cref="CommunityHubDbContext"/>,
/// SQL-translatable queries, no ambient state). The SAME resolver is used by the
/// GUI, the engines and the schedulers/jobs — and produces the SAME result in dev
/// and prod (it is NOT environment-specific; a person's ring is their access level
/// everywhere).
///
/// The rule (mirrors <see cref="Rings.Effective(Ring?, Ring?)"/>):
///   <c>effectiveRing(sponsorContact) = contact.Ring ?? company.Ring ?? Broad</c>
///   <c>effectiveRing(other resource) = resource.Ring ?? Broad</c>
/// The sponsor CONTACT's ring SUPERSEDES the company ring; the company ring is the
/// default for its contacts (linked by <see cref="Participant.SponsorCompanyId"/>
/// == <see cref="SponsorInfo.SponsorCompanyId"/>). Because the column default is
/// <see cref="Ring.Broad"/>, an unassigned resource resolves to Broad and sees
/// only fully-released features — today's behaviour.
/// </summary>
public sealed class RingResolver
{
    private readonly CommunityHubDbContext _db;

    public RingResolver(CommunityHubDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// The effective ring of one participant by id, joining their sponsor company
    /// default when they are a sponsor contact. An unknown participant resolves to
    /// the platform default (<see cref="Ring.Broad"/>) — fail-open, never an
    /// accidental restriction.
    /// </summary>
    public async Task<Ring> GetEffectiveRingAsync(
        int participantId, CancellationToken ct = default)
    {
        var p = await _db.Participants
            .Where(x => x.Id == participantId)
            .Select(x => new { x.EventId, x.Ring, x.Role, x.SponsorCompanyId })
            .FirstOrDefaultAsync(ct);

        if (p is null) return Rings.Default;

        // Non-sponsor (or sponsor with no company): own ring only.
        if (p.Role != ParticipantRole.Sponsor || string.IsNullOrWhiteSpace(p.SponsorCompanyId))
        {
            return Rings.Effective(p.Ring);
        }

        // Sponsor contact: the CONTACT ring supersedes the company default.
        // A column-stored Ring is non-nullable, so a contact ALWAYS has a value
        // (Broad by default). To honour "company is the default for its contacts",
        // a contact still on the platform default (Broad) inherits an EARLIER
        // company ring; an explicitly-narrowed contact (already < Broad) wins.
        var companyRing = await _db.SponsorInfos
            .Where(s => s.EventId == p.EventId && s.SponsorCompanyId == p.SponsorCompanyId)
            .Select(s => (Ring?)s.Ring)
            .FirstOrDefaultAsync(ct);

        return EffectiveForContact(p.Ring, companyRing);
    }

    /// <summary>
    /// The effective ring of a recipient identified by EMAIL ADDRESS within one
    /// edition — the lookup the email SENDER ring-gate uses (it knows the recipient
    /// only by address, not by participant id). Resolves the participant row for
    /// <paramref name="email"/> in <paramref name="eventId"/> (case-insensitively,
    /// trimmed — emails are stored lower-cased/trimmed) and applies the same
    /// effective-ring rule as <see cref="GetEffectiveRingAsync(int,CancellationToken)"/>:
    /// a sponsor contact inherits/overrides the company default; everyone else uses
    /// their own ring.
    ///
    /// Returns <c>(found:false, Broad)</c> when NO participant matches the address
    /// in this edition — the sender treats an UNKNOWN address as "not ring-gated"
    /// (it is not a participant), deferring to the allowlist floor rather than
    /// inventing a ring for a stranger. A known address always returns
    /// <c>(found:true, effectiveRing)</c>.
    /// </summary>
    public async Task<(bool found, Ring ring)> TryGetEffectiveRingByEmailAsync(
        int eventId, string? email, CancellationToken ct = default)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0) return (false, Rings.Default);

        var p = await _db.Participants
            .Where(x => x.EventId == eventId && x.Email == normalized)
            .Select(x => new { x.Ring, x.Role, x.SponsorCompanyId })
            .FirstOrDefaultAsync(ct);

        if (p is null) return (false, Rings.Default);

        // Non-sponsor (or sponsor with no company): own ring only.
        if (p.Role != ParticipantRole.Sponsor || string.IsNullOrWhiteSpace(p.SponsorCompanyId))
        {
            return (true, Rings.Effective(p.Ring));
        }

        var companyRing = await _db.SponsorInfos
            .Where(s => s.EventId == eventId && s.SponsorCompanyId == p.SponsorCompanyId)
            .Select(s => (Ring?)s.Ring)
            .FirstOrDefaultAsync(ct);

        return (true, EffectiveForContact(p.Ring, companyRing));
    }

    /// <summary>
    /// Pure effective-ring rule for a sponsor contact given the contact's stored
    /// (non-nullable) ring and the company's optional ring. The contact ring
    /// supersedes; but because the contact ring is column-backed (never null,
    /// defaulting to Broad), a contact left on the platform default INHERITS an
    /// earlier company ring — the company is the default for its contacts. An
    /// explicitly-narrowed contact (already earlier than Broad) always wins.
    /// </summary>
    public static Ring EffectiveForContact(Ring contactRing, Ring? companyRing)
    {
        if (companyRing is null) return contactRing;
        // Contact explicitly narrowed below the default ⇒ contact wins.
        if (contactRing != Rings.Default) return contactRing;
        // Contact on the default ⇒ inherit the company default.
        return companyRing.Value;
    }
}
