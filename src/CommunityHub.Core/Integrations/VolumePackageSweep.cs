using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>What one sweep did.</summary>
public sealed record VolumePackageSweepResult(
    int Companies, int Qualified, int NewlyQualified, int DroppedOut)
{
    public override string ToString() =>
        $"{Companies} companies, {Qualified} qualified, {NewlyQualified} newly, {DroppedOut} dropped out";
}

/// <summary>
/// §1077 — THE DAILY RE-CHECK. Operator 2026-08-11: <i>"Daily check of qualifications. if fx an
/// attendee cancels his ticket and company move from 10 to 9, then they dont qualify until they again
/// get 10 or more"</i>.
/// </summary>
/// <remarks>
/// <para>🔑 <b>Qualification is recomputed, never accumulated.</b> Each run asks the live attendee
/// data from scratch — so a cancellation removes a person the same way a new ticket adds one, with no
/// running total to drift.</para>
///
/// <para>🔴 <b>STICKY APPROVAL (operator agreed).</b> Dropping below ten flips
/// <see cref="VolumePackageCompany.QualifiedNow"/> to false and shows up as a DROPPED OUT count for
/// the organizer — it never clears <see cref="VolumePackageCompany.BenefitsApprovedAt"/>. ⚠️ Silently
/// revoking an approved benefit is how a company vanishes from a keynote slide that is already being
/// designed, on the strength of one cancellation.</para>
///
/// <para>🔒 <b>Sends nothing.</b> Stage 1 computes and records; the approval mail is stage 2. That
/// split is deliberate — this can run for weeks in DEV and PROD while nobody is contacted.</para>
/// </remarks>
public sealed class VolumePackageSweep
{
    private readonly CommunityHubDbContext _db;
    private readonly VolumePackageQualificationService _qualify;
    private readonly TimeProvider _clock;
    private readonly ILogger<VolumePackageSweep>? _log;

    public VolumePackageSweep(
        CommunityHubDbContext db, VolumePackageQualificationService qualify,
        TimeProvider? clock = null, ILogger<VolumePackageSweep>? log = null)
    {
        _db = db;
        _qualify = qualify;
        _clock = clock ?? TimeProvider.System;
        _log = log;
    }

    public async Task<VolumePackageSweepResult> RunAsync(int eventId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var results = await _qualify.ComputeAllAsync(eventId, ct);
        if (results.Count == 0) return new VolumePackageSweepResult(0, 0, 0, 0);

        var companies = await _db.VolumePackageCompanies
            .Where(c => c.EventId == eventId)
            .ToDictionaryAsync(c => c.Id, ct);

        // Today's snapshots, so a re-run (a retry, a manual trigger) UPDATES rather than duplicating.
        var existing = await _db.VolumePackageQualificationSnapshots
            .Where(s => s.EventId == eventId && s.OnDate == today)
            .ToDictionaryAsync(s => s.VolumePackageCompanyId, ct);

        int qualified = 0, newly = 0, dropped = 0;

        foreach (var r in results)
        {
            if (!companies.TryGetValue(r.CompanyId, out var company)) continue;

            var was = company.QualifiedNow;
            if (r.Qualifies) qualified++;
            if (r.Qualifies && !was) newly++;
            if (!r.Qualifies && was) dropped++;

            company.QualifiedNow = r.Qualifies;
            company.LastQualifiedCount = r.Count;
            company.LastCheckedAt = now;

            // 🔒 FirstQualifiedAt is a milestone, not a status: set once, never cleared by a later
            // drop. "They have qualified at some point" is a different question from "they qualify
            // today", and the organizer page asks both.
            if (r.Qualifies) company.FirstQualifiedAt ??= now;

            if (existing.TryGetValue(r.CompanyId, out var snap))
            {
                snap.AttendeeCount = r.Count;
                snap.Qualified = r.Qualifies;
                snap.FromOrders = r.FromOrders;
                snap.FromCoupons = r.FromCoupons;
                snap.FromAttendeeDomains = r.FromAttendeeDomains;
                snap.ComputedAt = now;
            }
            else
            {
                _db.VolumePackageQualificationSnapshots.Add(new VolumePackageQualificationSnapshot
                {
                    EventId = eventId,
                    VolumePackageCompanyId = r.CompanyId,
                    OnDate = today,
                    AttendeeCount = r.Count,
                    Qualified = r.Qualifies,
                    FromOrders = r.FromOrders,
                    FromCoupons = r.FromCoupons,
                    FromAttendeeDomains = r.FromAttendeeDomains,
                    ComputedAt = now,
                });
            }
        }

        await _db.SaveChangesAsync(ct);

        var outcome = new VolumePackageSweepResult(results.Count, qualified, newly, dropped);

        // ⚠️ A company DROPPING OUT is reported at warning level: it is the case the operator has to
        // act on, and it is invisible in a count of how many qualify.
        if (dropped > 0)
            _log?.LogWarning("Volume package sweep: {Result} — check the dropped-out companies.", outcome);
        else
            _log?.LogInformation("Volume package sweep: {Result}.", outcome);

        return outcome;
    }

    /// <summary>
    /// §1077 — the suggested approver: the BUYER who brought the most of this entity's attendees.
    /// </summary>
    /// <remarks>
    /// <para>Operator: <i>"Suggest the purchaser, who bought the most tickets in the 3 checks above"</i>.</para>
    /// <para>⚠️ Returns null rather than guessing when the entity has no orders at all — a company
    /// that qualified purely on check 3 (ten freelancers on a shared domain) has no purchaser, and
    /// inventing one would put a stranger's name in front of an organizer as a recommendation.</para>
    /// </remarks>
    public async Task<(string Email, string? Name, int Tickets)?> SuggestApproverAsync(
        int companyId, CancellationToken ct = default)
    {
        var company = await _db.VolumePackageCompanies.FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null) return null;

        var domains = company.DomainList
            .Select(VolumePackageCompany.NormaliseDomain)
            .Where(d => d.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (domains.Count == 0) return null;

        var rows = await _db.Orders
            .AsNoTracking()
            .Where(o => o.EventId == company.EventId && o.MirrorState == MirrorState.Active
                        && o.BuyerEmail != null && o.BuyerEmail != "")
            .Select(o => new { o.BackstageOrderId, o.BuyerEmail, o.BuyerName })
            .ToListAsync(ct);

        var ours = rows
            .Where(o => domains.Contains(VolumePackageQualificationService.DomainOf(o.BuyerEmail)))
            .ToList();
        if (ours.Count == 0) return null;

        var orderIds = ours.Select(o => o.BackstageOrderId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var perOrder = (await _db.Attendees
                .AsNoTracking()
                .Where(a => a.EventId == company.EventId && a.MirrorState == MirrorState.Active
                            && a.OrderId != null && orderIds.Contains(a.OrderId))
                .Select(a => a.OrderId!)
                .ToListAsync(ct))
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var best = ours
            .GroupBy(o => o.BuyerEmail!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Email = g.Key,
                Name = g.Select(x => x.BuyerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                Tickets = g.Sum(x => perOrder.TryGetValue(x.BackstageOrderId, out var n) ? n : 0),
            })
            .OrderByDescending(x => x.Tickets)
            .ThenBy(x => x.Email, StringComparer.OrdinalIgnoreCase)   // stable, so the suggestion does not wobble
            .FirstOrDefault();

        return best is null || best.Tickets == 0 ? null : (best.Email, best.Name, best.Tickets);
    }
}
