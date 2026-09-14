using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>What the carry-forward moved, or why it could not.</summary>
public sealed record CarryForwardResult(
    IReadOnlyList<string> Copied, IReadOnlyList<string> Skipped, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// §1173 — carry a sponsor's PROFILE to the company that succeeds it.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-03, on a supplier that re-registered under a new VAT number: <i>"i need to
/// keep the old sylvester in erp do to history … cm must create the new and link it up"</i> ·
/// <i>"as i need new contract with correct vat number"</i> · <i>"i have done this before"</i>.</para>
///
/// <para>🔑 <b>The contract belongs to the legal entity; the brand does not.</b> Invoices, orders and
/// the ERP customer must stay with the entity that placed them — that is the whole reason for the new
/// company. But the logo, the description, the website and the LinkedIn page belong to the
/// ORGANISATION, which has not changed. Without this the sponsor is asked for all of it a second
/// time, and the files they already sent sit under the old company's folder.</para>
///
/// <para>🔴 <b>WHAT IS NEVER COPIED, and why each one would be a defect:</b></para>
/// <list type="bullet">
/// <item><b>Zoho ids</b> — two CEH companies pointing at one Zoho record is §1159 exactly: each
/// reconcile would overwrite the other's fields, and the symptom reads as "the sync keeps
/// reverting".</item>
/// <item><b>ERP customer number / links</b> — the new entity has its own, and that separation is
/// the point of the exercise.</item>
/// <item><b>Order-derived state</b> (tier, package, booth, categories, entitlements) — those are
/// facts about what was BOUGHT. The new company's order decides them, and copying would assert a
/// purchase that has not happened.</item>
/// <item><b>Status</b> — whether the old company is withdrawn is a decision about the old company.</item>
/// </list>
///
/// <para>🔒 <b>FILL-BLANK ONLY.</b> Nothing already set on the target is overwritten, so running it
/// twice is safe and a value someone has typed always wins. This is the rule he set himself:
/// <i>"i am worried to automate this, except if the field is empty"</i>.</para>
/// </remarks>
public sealed class SponsorProfileCarryForward
{
    private readonly CommunityHubDbContext _db;
    private readonly ILogger<SponsorProfileCarryForward> _log;

    public SponsorProfileCarryForward(
        CommunityHubDbContext db, ILogger<SponsorProfileCarryForward> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>Copy the brand-owned fields from one company to another.</summary>
    /// <param name="dryRun">
    /// 🔒 True by default at the call site: it says what it would copy before anything moves.
    /// </param>
    public async Task<CarryForwardResult> RunAsync(
        int eventId, string fromCompanyId, string toCompanyId, bool dryRun,
        CancellationToken ct = default)
    {
        var copied = new List<string>();
        var skipped = new List<string>();

        if (string.IsNullOrWhiteSpace(fromCompanyId) || string.IsNullOrWhiteSpace(toCompanyId))
            return new(copied, skipped, "Both a source and a target company are needed.");

        // ⚠️ Copying a company onto itself would report a long list of "already set" and look like it
        // worked, which is the most confusing possible outcome.
        if (string.Equals(fromCompanyId.Trim(), toCompanyId.Trim(), StringComparison.OrdinalIgnoreCase))
            return new(copied, skipped, "The source and the target are the same company.");

        var from = await _db.SponsorInfos.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == fromCompanyId, ct);
        if (from is null)
            return new(copied, skipped, $"No sponsor record for company {fromCompanyId} to copy from.");

        var to = await _db.SponsorInfos.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == toCompanyId, ct);
        if (to is null)
        {
            // 🔑 Deliberately does NOT create the row. A SponsorInfo is created by the order pull
            // when the company actually buys something; making one here would assert a sponsorship
            // that no order supports, and the pull would then treat it as established.
            return new(copied, skipped,
                $"Company {toCompanyId} has no sponsor record yet. It gets one when its first "
                + "completed order is pulled — carry the profile forward after that.");
        }

        void Move(string label, string? source, Func<string?> current, Action<string?> set)
        {
            if (string.IsNullOrWhiteSpace(source)) return;

            if (!string.IsNullOrWhiteSpace(current()))
            {
                skipped.Add($"{label} (already set on the new company)");
                return;
            }

            if (!dryRun) set(source);
            copied.Add(label);
        }

        // The BRAND: what the organisation is, not what it bought.
        Move("Company description", from.CompanyDescription,
            () => to.CompanyDescription, v => to.CompanyDescription = v);
        Move("Short description", from.CompanyDescriptionShort,
            () => to.CompanyDescriptionShort, v => to.CompanyDescriptionShort = v);
        Move("Social media intro", from.SocialMediaIntro,
            () => to.SocialMediaIntro, v => to.SocialMediaIntro = v);
        Move("Website", from.WebsiteUrl, () => to.WebsiteUrl, v => to.WebsiteUrl = v);
        Move("LinkedIn", from.LinkedInUrl, () => to.LinkedInUrl, v => to.LinkedInUrl = v);
        Move("X / Twitter", from.TwitterUrl, () => to.TwitterUrl, v => to.TwitterUrl = v);

        // The LOGO — path and file name together, or the row names a file it cannot find.
        if (!string.IsNullOrWhiteSpace(from.LogoVectorPath) && string.IsNullOrWhiteSpace(to.LogoVectorPath))
        {
            if (!dryRun)
            {
                to.LogoVectorPath = from.LogoVectorPath;
                to.LogoVectorFileName = from.LogoVectorFileName;
            }
            copied.Add($"Vector logo ({from.LogoVectorFileName})");
        }
        else if (!string.IsNullOrWhiteSpace(from.LogoVectorPath))
        {
            skipped.Add("Vector logo (already set on the new company)");
        }

        if (!string.IsNullOrWhiteSpace(from.LogoRasterPath) && string.IsNullOrWhiteSpace(to.LogoRasterPath))
        {
            if (!dryRun)
            {
                to.LogoRasterPath = from.LogoRasterPath;
                to.LogoRasterFileName = from.LogoRasterFileName;
            }
            copied.Add($"Raster logo ({from.LogoRasterFileName})");
        }
        else if (!string.IsNullOrWhiteSpace(from.LogoRasterPath))
        {
            skipped.Add("Raster logo (already set on the new company)");
        }

        // The COORDINATOR — a person, and the same person in practice. ⚠️ NOT ZohoContactEmail:
        // that records what was last pushed to Zoho for a DIFFERENT record, and copying it would
        // make the new record's first e-mail look already-sent (§41a caps those at three).
        Move("Coordinator first name", from.EventCoordinatorFirstName,
            () => to.EventCoordinatorFirstName, v => to.EventCoordinatorFirstName = v);
        Move("Coordinator last name", from.EventCoordinatorLastName,
            () => to.EventCoordinatorLastName, v => to.EventCoordinatorLastName = v);
        Move("Coordinator e-mail", from.EventCoordinatorEmail,
            () => to.EventCoordinatorEmail, v => to.EventCoordinatorEmail = v);
        Move("Coordinator phone", from.EventCoordinatorPhone,
            () => to.EventCoordinatorPhone, v => to.EventCoordinatorPhone = v);

        if (!dryRun && copied.Count > 0)
        {
            to.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation(
                "§1173: carried {Count} profile field(s) from company {From} to {To}.",
                copied.Count, fromCompanyId, toCompanyId);
        }

        return new(copied, skipped, null);
    }
}
