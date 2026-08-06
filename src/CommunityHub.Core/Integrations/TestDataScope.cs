using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §905 — WHO IS TEST DATA, asked in ONE place so every publishing surface answers it the same way.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-06: <i>"all have the test flag but some service doesn't handle it"</i> —
/// and he was right. <see cref="Domain.Participant.IsTestUser"/> was set correctly on every seeded
/// account; the SoMe planner, the variable resolver and the graphics builders simply never read it.
/// Two test exhibitor speakers were rendered as frames into the Security track GIF, and two posts
/// announcing "Test Exhibitor Session Preday/MainDay" were sitting in the queue scheduled for
/// January and February 2027.</para>
///
/// <para>🔴 <b>A SPONSOR COMPANY HAS NO TEST FLAG OF ITS OWN.</b> The flag lives on the participant,
/// so a company is judged by its contacts: <b>test when it has contacts and EVERY one of them is a
/// test user.</b></para>
///
/// <para>🔒 <b>The "every one" is load-bearing, not defensive coding.</b> Measured on PROD
/// 2026-08-06: <c>2linkIT ApS</c> — a real, paying Gold sponsor — carries <b>6 test contacts
/// alongside 6 real ones</b>, because it is the operator's own company and the test accounts were
/// seeded onto it. A rule of "has any test contact" would have deleted a real sponsor from the
/// campaign and from the tier graphics. Only <c>Test-Silver</c> (1 contact, all test) is excluded
/// today.</para>
///
/// <para>⚠️ <b>A company with NO contacts is NOT test.</b> "All of them are test" is vacuously true
/// over an empty set, which would quietly drop a sponsor who has signed but not yet been onboarded —
/// exactly the state every new sponsor passes through.</para>
///
/// <para>🔑 <b>The flag he sets WINS; the contact rule is only the fallback.</b>
/// <see cref="Domain.SponsorInfo.IsTestData"/> exists precisely because the derived rule cannot
/// express a mixed company (operator 2026-08-06: <i>"we need to have that, so we fx can control
/// test sponsors"</i>). Keeping the fallback means every fixture seeded before that column existed
/// still behaves correctly with no data entry.</para>
/// </remarks>
public static class TestDataScope
{
    /// <summary>
    /// The <c>SponsorCompanyId</c>s that are test companies: <b>flagged</b>
    /// (<see cref="Domain.SponsorInfo.IsTestData"/>), or every contact is a test user.
    /// </summary>
    /// <remarks>
    /// Evaluated in memory over one small projection: the alternative is a correlated EXISTS per
    /// sponsor, and the contact list for one edition is a few dozen rows.
    /// </remarks>
    public static async Task<HashSet<string>> TestSponsorCompanyIdsAsync(
        CommunityHubDbContext db, int eventId, CancellationToken ct = default)
    {
        // The explicit flag — his direct control, and the only thing that can mark a company test
        // while it still has real contacts on it.
        var flagged = await db.SponsorInfos
            .AsNoTracking()
            .Where(s => s.EventId == eventId && s.IsTestData
                        && s.SponsorCompanyId != null && s.SponsorCompanyId != "")
            .Select(s => s.SponsorCompanyId!)
            .ToListAsync(ct);

        var contacts = await db.Participants
            .AsNoTracking()
            .Where(p => p.EventId == eventId
                        && p.SponsorCompanyId != null && p.SponsorCompanyId != "")
            .Select(p => new { CompanyId = p.SponsorCompanyId!, p.IsTestUser })
            .ToListAsync(ct);

        var derived = contacts
            .GroupBy(c => c.CompanyId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.All(c => c.IsTestUser))
            .Select(g => g.Key);

        return flagged
            .Concat(derived)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
