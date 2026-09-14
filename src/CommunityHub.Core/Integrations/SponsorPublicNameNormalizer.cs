using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1158 — the ONE-TIME sweep that takes the legal form off every sponsor's public name.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-31: <i>"i would like a one-time run through all sponsors in the webshop
/// and adjust the public name and remove these legal company types acronyms"</i> — asked for
/// immediately after <i>"i am worried to automate this, except if the field is empty"</i> and
/// <i>"as a sponsor can change the field at any time"</i>.</para>
///
/// <para>🔑 <b>Those two are not in conflict; they are the whole design.</b> A sweep he starts, once,
/// on names that are wrong today is a decision he is making. The same edit on a ten-minute timer
/// would be this job overwriting a sponsor's own wording again and again, with no way for them to
/// win. So this exists as a <b>separate, hand-started sweep</b> and NOT as another rule inside the
/// recurring reconcile — where the standing rules stay: fill only an EMPTY field, otherwise report.</para>
///
/// <para>🔒 <b>Preview first.</b> <see cref="RunAsync"/> defaults to <c>apply: false</c> and changes
/// nothing, so the list of edits can be read before any of it is real. It rewrites names in a live
/// webshop that sponsors see; a dry run costs one pass and removes the entire class of "it renamed
/// something I did not expect".</para>
///
/// <para>⚠️ The BILLING/legal name is never touched — only <c>company_name_public</c>. Operator:
/// <i>"billing name includes fx A/S, Aps etc"</i> · <i>"public name is for linkedin"</i>.</para>
/// </remarks>
public sealed class SponsorPublicNameNormalizer
{
    private readonly CommunityHubDbContext _db;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _options;
    private readonly ILogger<SponsorPublicNameNormalizer> _log;

    public SponsorPublicNameNormalizer(
        CommunityHubDbContext db,
        CompanyManagerClient cm,
        CompanyManagerOptions options,
        ILogger<SponsorPublicNameNormalizer> log)
    {
        _db = db;
        _cm = cm;
        _options = options;
        _log = log;
    }

    /// <summary>What the sweep would do, or did, to one company.</summary>
    /// <param name="Source">
    /// Where the current public-facing name came from: <c>public</c> when the company has its own
    /// public name, <c>legal</c> when the field is empty and the legal name is being published in
    /// its place. The distinction matters — the second is a company nobody has ever set a public
    /// name for, not a name someone chose.
    /// </param>
    public sealed record NameChange(
        string CompanyId, string CurrentName, string SuggestedName,
        string LegalForm, string Source, bool Applied, string? Error = null);

    /// <param name="Unchanged">
    /// The public-facing name of every company that was READ and left alone. ⚠️ This is the
    /// diagnostic that matters: a name we do not RECOGNISE looks exactly like a name that is
    /// already CORRECT, and without this list the two are indistinguishable in the result. The
    /// first production run reported "10 of 17 changed" and there was no way to tell whether the
    /// other 7 were fine or unmatched — two of them turned out to be unmatched.
    /// </param>
    public sealed record NormalizeResult(
        bool Enabled, bool Applied, int CompaniesChecked, int Unreadable,
        IReadOnlyList<NameChange> Changes,
        IReadOnlyList<string> Unchanged);

    /// <summary>
    /// Walk every sponsor company and take the trailing legal form off its public name.
    /// </summary>
    /// <param name="apply">
    /// false (default) = PREVIEW, nothing is written. true = perform the edits.
    /// </param>
    /// <remarks>
    /// ⚠️ A company whose record cannot be READ is counted and skipped, never guessed at. Company
    /// Manager returning null is "I could not tell you", not "this company has no public name" —
    /// treating the second as the first would push a name over one that may already be correct.
    /// </remarks>
    public async Task<NormalizeResult> RunAsync(
        int eventId, bool apply = false, CancellationToken ct = default)
    {
        var changes = new List<NameChange>();
        var unchanged = new List<string>();
        if (!_options.Enabled) return new(false, apply, 0, 0, changes, unchanged);

        // 🔑 SCOPE = EVERY WEBSHOP COMPANY, not just those with a CEH sponsor row.
        //
        // Operator 2026-08-31: *"a one-time run through all sponsors in the webshop"*. The first
        // version walked SponsorInfos and reached 17 companies; the webshop holds more, and a
        // company whose sponsor row has not been created yet still has a public name on display.
        // ⚠️ The field being fixed is a WEBSHOP field, so the webshop's own company list is the
        // right population — deriving it from CEH's side could only ever be a subset.
        //
        // IgnoreQueryFilters on the CEH side is deliberate too: the filter narrows to IsSponsor,
        // and a mis-flagged company still shows a public name.
        var cehIds = await _db.SponsorInfos
            .IgnoreQueryFilters()
            .Where(s => s.EventId == eventId && !s.IsTestData)
            .Select(s => s.SponsorCompanyId)
            .Distinct()
            .ToListAsync(ct);

        var webshopIds = new List<string>();
        try
        {
            var refs = await _cm.ListCompaniesAsync(ct);
            webshopIds.AddRange(refs.Select(r => r.Id.ToString()));
        }
        catch (Exception ex)
        {
            // ⚠️ Fall back to the CEH set rather than failing the sweep — a partial pass he can
            // see is better than none, and the counts below say how many were reached.
            _log.LogWarning(ex, "§1158 sweep: could not list webshop companies; using the CEH set only.");
        }

        var companyIds = cehIds.Concat(webshopIds).Distinct(StringComparer.Ordinal).ToList();

        var checkedCount = 0;
        var unreadable = 0;

        foreach (var companyId in companyIds.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!int.TryParse(companyId, out var cmId)) continue;

            CompanyManagerCompany? company;
            try
            {
                company = await _cm.GetCompanyAsync(cmId, ct);
            }
            catch (Exception ex)
            {
                // One company's transient failure must not stop the sweep — the same per-company
                // graceful-catch the ERP reconcile uses.
                _log.LogWarning(ex, "§1158 sweep: could not read company {Co}.", companyId);
                unreadable++;
                continue;
            }

            if (company is null) { unreadable++; continue; }
            checkedCount++;

            var publicNameEmpty = string.IsNullOrWhiteSpace(company.PublicName);
            var current = publicNameEmpty ? company.Name : company.PublicName;
            var form = CompanyLegalForm.Detect(current);
            if (form is null)
            {
                if (!string.IsNullOrWhiteSpace(current)) unchanged.Add(current);
                continue;
            }

            var suggested = CompanyLegalForm.Strip(current);
            // 🔒 Never write a name identical to the one already there, and never write an empty
            // one: an empty public name falls back to the LEGAL name, which is the state we are
            // here to remove.
            if (string.IsNullOrWhiteSpace(suggested)
                || string.Equals(suggested, company.PublicName, StringComparison.Ordinal))
            {
                unchanged.Add(current);
                continue;
            }

            var source = publicNameEmpty ? "legal" : "public";

            if (!apply)
            {
                changes.Add(new NameChange(companyId, current, suggested, form, source, Applied: false));
                continue;
            }

            try
            {
                var ok = await _cm.UpdateCompanyAsync(
                    cmId,
                    new Dictionary<string, object?> { ["company_name_public"] = suggested },
                    ct);

                changes.Add(new NameChange(
                    companyId, current, suggested, form, source,
                    Applied: ok,
                    Error: ok ? null : "Company Manager refused the update."));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "§1158 sweep: public-name update failed for company {Co}.", companyId);
                changes.Add(new NameChange(
                    companyId, current, suggested, form, source, Applied: false, Error: ex.Message));
            }
        }

        _log.LogInformation(
            "§1158 sweep ({Mode}): {Checked} companies checked, {Changes} name(s) {Verb}, {Unreadable} unreadable.",
            apply ? "apply" : "preview", checkedCount, changes.Count,
            apply ? "changed" : "to change", unreadable);

        return new(true, apply, checkedCount, unreadable, changes, unchanged);
    }
}
