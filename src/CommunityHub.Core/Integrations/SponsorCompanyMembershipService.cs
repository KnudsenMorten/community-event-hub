using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1235 — the one place that answers "which sponsor companies does this person serve, and as what?".
/// </summary>
/// <remarks>
/// <para>🔑 <b>A choke point on purpose.</b> `Participant.SponsorCompanyId` is read in ~106 files, each
/// re-running its own inline lookup — which is precisely why "one person, one company" became impossible
/// to relax. Everything that needs the MULTI-company answer asks here, so stage 2 (the portal company
/// switcher) has one thing to change rather than a hundred.</para>
///
/// <para>🔒 <b>Stage 1 does not move anybody.</b> `Participant.SponsorCompanyId` remains the PRIMARY
/// company and keeps serving every existing reader unchanged; this service only adds the ability to hold
/// additional links. A sync that meets a person under a second company now LINKS them
/// (<see cref="LinkAsync"/>) instead of reassigning them.</para>
/// </remarks>
public sealed class SponsorCompanyMembershipService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public SponsorCompanyMembershipService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Every company this person serves, primary first then by company id.</summary>
    public async Task<IReadOnlyList<SponsorCompanyContact>> ForParticipantAsync(
        int eventId, int participantId, CancellationToken ct = default) =>
        await _db.SponsorCompanyContacts
            .Where(x => x.EventId == eventId && x.ParticipantId == participantId)
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.SponsorCompanyId)
            .ToListAsync(ct);

    /// <summary>The contacts linked to a company (all roles).</summary>
    public async Task<IReadOnlyList<SponsorCompanyContact>> ForCompanyAsync(
        int eventId, string sponsorCompanyId, CancellationToken ct = default) =>
        await _db.SponsorCompanyContacts
            .Where(x => x.EventId == eventId && x.SponsorCompanyId == sponsorCompanyId)
            .ToListAsync(ct);

    /// <summary>True when this person serves this company (any role).</summary>
    public Task<bool> IsLinkedAsync(
        int eventId, int participantId, string sponsorCompanyId, CancellationToken ct = default) =>
        _db.SponsorCompanyContacts.AnyAsync(
            x => x.EventId == eventId && x.ParticipantId == participantId
                 && x.SponsorCompanyId == sponsorCompanyId, ct);

    /// <summary>
    /// Create or refresh the link between a person and a company. Returns the link (tracked; the
    /// CALLER saves, so a sync pass keeps its single transaction).
    /// </summary>
    /// <remarks>
    /// ⚠️ Flag arguments are null-means-leave-alone, so a source that only knows about signers cannot
    /// silently clear a coordinator an organizer set by hand — the same rule the CM pass has always
    /// applied to the person-level flags.
    /// </remarks>
    public async Task<SponsorCompanyContact> LinkAsync(
        int eventId,
        int participantId,
        string sponsorCompanyId,
        bool? isSigner = null,
        bool? isEventCoordinator = null,
        bool? isBoothMember = null,
        int? erpContactNumber = null,
        bool? isPrimary = null,
        CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var link = await _db.SponsorCompanyContacts.FirstOrDefaultAsync(
            x => x.EventId == eventId && x.ParticipantId == participantId
                 && x.SponsorCompanyId == sponsorCompanyId, ct);

        if (link is null)
        {
            link = new SponsorCompanyContact
            {
                EventId = eventId,
                ParticipantId = participantId,
                SponsorCompanyId = sponsorCompanyId,
                CreatedAt = now,
            };
            _db.SponsorCompanyContacts.Add(link);
        }
        else
        {
            link.UpdatedAt = now;
        }

        if (isSigner is bool s) link.IsSigner = s;
        if (isEventCoordinator is bool c) link.IsEventCoordinator = c;
        if (isBoothMember is bool b) link.IsBoothMember = b;
        if (erpContactNumber is int n) link.ErpContactNumber = n;
        if (isPrimary is bool p) link.IsPrimary = p;

        return link;
    }

    /// <summary>
    /// Remove a person's link to one company — "they left THIS division", not "they left".
    /// Returns true when a link was removed.
    /// </summary>
    /// <remarks>
    /// 🔴 This is what a roster departure means once a person can serve several companies. Deactivating
    /// the PERSON because one company's roster dropped them would switch off someone who is still the
    /// coordinator of another division — the mirror image of the flap this feature removes. The caller
    /// deactivates only when <see cref="ForParticipantAsync"/> comes back empty.
    /// </remarks>
    public async Task<bool> UnlinkAsync(
        int eventId, int participantId, string sponsorCompanyId, CancellationToken ct = default)
    {
        var link = await _db.SponsorCompanyContacts.FirstOrDefaultAsync(
            x => x.EventId == eventId && x.ParticipantId == participantId
                 && x.SponsorCompanyId == sponsorCompanyId, ct);
        if (link is null) return false;

        _db.SponsorCompanyContacts.Remove(link);
        return true;
    }

    /// <summary>
    /// The participant behind a company's ERP contact number.
    /// 🔴 Scoped to the company: e-conomic numbers are per-customer sequences, so two accounts both
    /// have a contact 6. Matching event-wide returns the wrong person as soon as two accounts overlap.
    /// </summary>
    public async Task<int?> ParticipantIdByErpContactAsync(
        int eventId, string sponsorCompanyId, int erpContactNumber, CancellationToken ct = default) =>
        await _db.SponsorCompanyContacts
            .Where(x => x.EventId == eventId && x.SponsorCompanyId == sponsorCompanyId
                        && x.ErpContactNumber == erpContactNumber)
            .Select(x => (int?)x.ParticipantId)
            .FirstOrDefaultAsync(ct);
}
