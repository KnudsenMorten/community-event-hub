using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Sponsors;

/// <summary>One company that has not finished Get started, and who to chase about it.</summary>
/// <param name="SponsorCompanyId">The join key — shown only when the name cannot be resolved.</param>
/// <param name="CompanyName">Company Manager's public name, via the canonical chain.</param>
/// <param name="Coordinators">The event coordinators for the company: who answers for it (§7c).</param>
/// <param name="DoneSteps">Steps finished.</param>
/// <param name="EvaluableSteps">Steps that CAN be finished — the honest denominator (§250).</param>
/// <param name="OpenSteps">The step keys still open, in wizard order.</param>
public sealed record SponsorGetStartedRow(
    string SponsorCompanyId,
    string CompanyName,
    IReadOnlyList<SponsorCoordinator> Coordinators,
    int DoneSteps,
    int EvaluableSteps,
    IReadOnlyList<string> OpenSteps)
{
    /// <summary>Whole percent, for the column. 0 evaluable steps reads as 0 rather than dividing.</summary>
    public int PercentDone =>
        EvaluableSteps <= 0 ? 0 : (int)Math.Round(100.0 * DoneSteps / EvaluableSteps);

    /// <summary>
    /// 🔴 True when there is nobody to chase. Worse than an unfinished wizard, and invisible until
    /// now: no coordinator means the chase emails have no recipient either (§7c).
    /// </summary>
    public bool HasNobodyToChase => Coordinators.Count == 0;
}

/// <summary>A person who answers for a sponsor company.</summary>
public sealed record SponsorCoordinator(string Name, string Email);

/// <summary>
/// 🔴 §1210 — WHICH SPONSORS HAVE NOT FINISHED "GET STARTED", AND WHO TO ASK.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-12: <i>"can you give me an overview of all sponsors that have not completed
/// the get started process. i need event sponsor name + coordinator name + email"</i>.</para>
///
/// <para>🔑 <b>The data existed; the view did not.</b> §250's digest already computes exactly this to
/// decide who to nag — but it computes it PER PARTICIPANT, mails them, and keeps no list. The
/// organizer Sponsors grid counts TASKS, which is a different and larger set than the wizard's steps.
/// So the one question an organizer actually asks — <i>who still owes me their onboarding, and whose
/// inbox do I chase</i> — had no answer on any page.</para>
///
/// <para>🔒 <b>Through <see cref="SponsorWizardService"/>, never a re-implementation.</b> The wizard
/// service IS the definition of "completed": the same object the sponsor's own page renders and the
/// same one the digest chases with. A second rule here would drift, and the drift would show up as
/// chasing a sponsor who is finished — which costs goodwill rather than time.
/// <c>[[ceh-count-the-shared-things]]</c></para>
///
/// <para>⚠️ <b>Test and withdrawn companies are out</b>, via <see cref="SponsorZohoScope"/>'s stored
/// flag — the same rule that decides who reaches Backstage. §905's lesson is why it is the STORED flag
/// and not "all contacts look like test users": a real Gold sponsor carries six test contacts because
/// it is the operator's own company.</para>
///
/// <para>⚠️ One wizard build per COMPANY, not per contact — the wizard is company-scoped, so building
/// it for each of a company's four coordinators would be four identical answers.</para>
/// </remarks>
public sealed class SponsorGetStartedReport
{
    private readonly CommunityHubDbContext _db;
    private readonly SponsorWizardService _wizard;

    public SponsorGetStartedReport(CommunityHubDbContext db, SponsorWizardService wizard)
    {
        _db = db;
        _wizard = wizard;
    }

    /// <summary>Every in-scope company that has NOT finished, worst first.</summary>
    public async Task<IReadOnlyList<SponsorGetStartedRow>> NotCompletedAsync(
        int eventId, CancellationToken ct = default)
    {
        var infos = await _db.SponsorInfos.AsNoTracking()
            .Where(s => s.EventId == eventId && s.SponsorCompanyId != null)
            .ToListAsync(ct);

        var inScope = infos.Where(SponsorZohoScope.MayPushToZoho).ToList();
        if (inScope.Count == 0) return Array.Empty<SponsorGetStartedRow>();

        var companyIds = inScope.Select(s => s.SponsorCompanyId!).ToList();

        // §443 — every name in ONE query, never one lookup per row.
        var names = await SponsorCompanyNameService.ResolveFromLocalAsync(_db, eventId, companyIds, ct);

        // The sponsor contacts for all of these companies, in one query. The COORDINATORS are who
        // answers for a company (§7c); a signer-only contact is deliberately not chased.
        var contacts = await _db.Participants.AsNoTracking()
            .Where(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Sponsor
                        && p.IsActive
                        && p.SponsorCompanyId != null
                        && companyIds.Contains(p.SponsorCompanyId))
            .Select(p => new
            {
                p.Id, p.SponsorCompanyId, p.FullName, p.Email, p.IsEventCoordinator,
            })
            .ToListAsync(ct);

        var byCompany = contacts
            .GroupBy(c => c.SponsorCompanyId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<SponsorGetStartedRow>();

        foreach (var info in inScope)
        {
            var companyId = info.SponsorCompanyId!;
            var people = byCompany.TryGetValue(companyId, out var list) ? list : [];

            // 🔑 The wizard is COMPANY-scoped but takes a participant, so any contact of the company
            // produces the same answer. Prefer a coordinator so the view is built from the person the
            // chase would actually go to.
            var subject = people.Find(p => p.IsEventCoordinator) ?? people.FirstOrDefault();

            // 🛑 NO CONTACT AT ALL ⇒ the wizard cannot even be built. That is not "finished", it is a
            // company nobody can start — and silently dropping it would hide the worst case in the
            // report (§854). Reported with an empty coordinator list and 0 of 0.
            if (subject is null)
            {
                rows.Add(new SponsorGetStartedRow(
                    companyId, NameOf(names, companyId), Array.Empty<SponsorCoordinator>(),
                    DoneSteps: 0, EvaluableSteps: 0, OpenSteps: Array.Empty<string>()));
                continue;
            }

            var view = await _wizard.BuildAsync(eventId, subject.Id, ct);

            // A null view means the participant has no company link — already handled above for the
            // no-contact case, so this is a data oddity rather than an expected state. Reported the
            // same way: named, never dropped.
            if (view is null || view.AllDone) continue;

            var coordinators = people
                .Where(p => p.IsEventCoordinator)
                .Select(p => new SponsorCoordinator(
                    string.IsNullOrWhiteSpace(p.FullName) ? p.Email : p.FullName, p.Email))
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            rows.Add(new SponsorGetStartedRow(
                companyId,
                NameOf(names, companyId),
                coordinators,
                view.DoneCount,
                view.EvaluableSteps,
                view.Steps.Where(s => s.Done == false).Select(s => s.Key).ToList()));
        }

        // Worst first: the least complete is the one to chase today. Companies with nobody to chase
        // sort to the very top — they cannot progress at all without an organizer acting.
        return rows
            .OrderByDescending(r => r.HasNobodyToChase)
            .ThenBy(r => r.PercentDone)
            .ThenBy(r => r.CompanyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string NameOf(IReadOnlyDictionary<string, string> names, string companyId) =>
        names.TryGetValue(companyId, out var n) && !string.IsNullOrWhiteSpace(n)
            ? n
            : SponsorCompanyName.UnresolvedName(companyId);
}
