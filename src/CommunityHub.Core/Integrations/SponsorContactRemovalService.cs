using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>What removing a sponsor contact did in CEH.</summary>
public enum SponsorContactRemovalOutcome
{
    /// <summary>No CEH person matched the removed contact — nothing to change here.</summary>
    NotInCeh,

    /// <summary>The person served only this company, so they were deactivated (the organizer cascade).</summary>
    Deactivated,

    /// <summary>
    /// The person still serves another company, or has another role in the event: only this company's
    /// link and roles were removed.
    /// </summary>
    RolesRemoved,

    /// <summary>The sponsor removed THEMSELVES: the link goes, the account stays — they are signed in.</summary>
    RemovedYourself,
}

/// <param name="ParticipantId">The CEH person behind the removed contact, when one matched.</param>
public sealed record SponsorContactRemoval(
    SponsorContactRemovalOutcome Outcome, int? ParticipantId, string? Name, string? Email);

/// <summary>
/// 🔴 §1369 — WHAT REMOVING A CONTACT IN GET STARTED MEANS IN CEH.
/// </summary>
/// <remarks>
/// <para>Operator 2026-10-26: <i>"build the §1369 fixes, deactivate as default"</i>. Measured that day: a
/// SquaredUp contact removed a colleague who had left the company. The Get Started step deleted her in
/// e-conomic and left her active in CEH as signer and event coordinator — so the hub went on mailing her,
/// and the next e-conomic sync warned the organizers about a mismatch the hub had created itself.</para>
///
/// <para>🔑 <b>Deactivate is the default</b> because "remove this contact" from a sponsor almost always
/// means the person left. ⚠️ But only when the person served THIS company alone and is a sponsor
/// contact: someone who coordinates another division, or is also a speaker, keeps their account and
/// loses only this company's link and roles (<see cref="SponsorCompanyMembershipService.UnlinkAsync"/>'s
/// rule). And a sponsor who removes themselves is never deactivated mid-session.</para>
///
/// <para>🔒 The deactivation is the organizer cascade (<see cref="ParticipantDeactivationService"/>):
/// their open tasks close, hotel and RSVPs are released — the same as when an organizer does it.</para>
/// </remarks>
public sealed class SponsorContactRemovalService
{
    private readonly CommunityHubDbContext _db;
    private readonly SponsorCompanyMembershipService _membership;
    private readonly ParticipantDeactivationService _deactivation;

    public SponsorContactRemovalService(
        CommunityHubDbContext db, SponsorCompanyMembershipService membership,
        ParticipantDeactivationService deactivation)
    {
        _db = db;
        _membership = membership;
        _deactivation = deactivation;
    }

    /// <summary>
    /// Apply a contact removal (already done in e-conomic) to CEH.
    /// </summary>
    /// <param name="erpContactNumber">The e-conomic contact number that was deleted.</param>
    /// <param name="email">The removed contact's e-mail, used when no CEH link carries the number.</param>
    /// <param name="companyName">For the deactivation reason, in words.</param>
    public async Task<SponsorContactRemoval> ApplyAsync(
        int eventId, string companyId, int erpContactNumber, string? email,
        int actorParticipantId, string? actorEmail, string? companyName, CancellationToken ct = default)
    {
        var participantId = await _membership.ParticipantIdByErpContactAsync(eventId, companyId, erpContactNumber, ct);

        // Fallback by e-mail, still scoped to this company: a link or a primary company must match.
        if (participantId is null && !string.IsNullOrWhiteSpace(email))
        {
            var normalized = email.Trim().ToLowerInvariant();
            participantId = await _db.Participants
                .Where(p => p.EventId == eventId && p.Email.ToLower() == normalized
                            && (p.SponsorCompanyId == companyId
                                || _db.SponsorCompanyContacts.Any(x => x.EventId == eventId
                                    && x.ParticipantId == p.Id && x.SponsorCompanyId == companyId)))
                .Select(p => (int?)p.Id)
                .FirstOrDefaultAsync(ct);
        }

        if (participantId is not { } id)
            return new SponsorContactRemoval(SponsorContactRemovalOutcome.NotInCeh, null, null, email);

        var person = await _db.Participants.FirstAsync(p => p.Id == id, ct);

        await _membership.UnlinkAsync(eventId, id, companyId, ct);
        // The person-level flags describe the PRIMARY company (ShopErpRoleSyncService), so they go with it.
        if (person.SponsorCompanyId == companyId)
        {
            person.IsSigner = false;
            person.IsEventCoordinator = false;
        }
        await _db.SaveChangesAsync(ct);

        if (id == actorParticipantId)
            return new SponsorContactRemoval(SponsorContactRemovalOutcome.RemovedYourself, id, person.FullName, person.Email);

        var remaining = await _membership.ForParticipantAsync(eventId, id, ct);
        if (remaining.Count > 0 || person.Role != ParticipantRole.Sponsor)
            return new SponsorContactRemoval(SponsorContactRemovalOutcome.RolesRemoved, id, person.FullName, person.Email);

        var who = string.IsNullOrWhiteSpace(actorEmail) ? "a company contact" : actorEmail;
        await _deactivation.DeactivateAsync(eventId, id,
            $"removed from {companyName ?? companyId}'s contacts by {who} in Get Started", actorEmail, ct);

        return new SponsorContactRemoval(SponsorContactRemovalOutcome.Deactivated, id, person.FullName, person.Email);
    }
}
