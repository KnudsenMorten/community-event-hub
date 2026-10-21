using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>What one sponsor-company sweep did.</summary>
public sealed record SponsorOrganizationSweepResult(int Considered, int Resolved, int NotFound, int Failed, int Skipped)
{
    public override string ToString() =>
        $"sponsor companies considered {Considered}: resolved {Resolved}, not found {NotFound}, "
      + $"failed {Failed}, skipped {Skipped}";
}

/// <summary>
/// §1300 — fills <see cref="Domain.SponsorInfo.LinkedInOrganizationId"/> from the sponsor's LinkedIn
/// company URL, so the sponsor announcement TAGS the company (operator 2026-09-23: *"it should tag the
/// company in the tag area, so the companys linkedin is automatically tagged"*).
/// </summary>
/// <remarks>
/// <para>🔒 Runs inside the scheduled mention-resolution job — the only place that calls LinkedIn for
/// mentions. Rendering only READS the cached id, so a post never waits on LinkedIn.</para>
/// <list type="bullet">
///   <item><b>Resolved for the current slug ⇒ skip.</b> An organization id never changes.</item>
///   <item><b>The URL now carries a different slug ⇒ ask again</b> and replace the id.</item>
///   <item><b>An id with no recorded slug was set by hand ⇒ kept</b> (a stored id wins — LinkedInUrlParser).</item>
///   <item><b>Negative / failed answer ⇒ re-asked after a day</b>, like person mentions (§884.4).</item>
///   <item>🔒 <b>A failed lookup never erases an id</b> — "could not ask" is not "no such company".</item>
/// </list>
/// </remarks>
public sealed class SponsorOrganizationResolutionService
{
    public static readonly TimeSpan NegativeAnswerTtl = TimeSpan.FromHours(24);

    private readonly CommunityHubDbContext _db;
    private readonly LinkedInOrganizationLookupClient _lookup;
    private readonly TimeProvider _clock;
    private readonly ILogger<SponsorOrganizationResolutionService>? _log;

    public SponsorOrganizationResolutionService(
        CommunityHubDbContext db, LinkedInOrganizationLookupClient lookup, TimeProvider clock,
        ILogger<SponsorOrganizationResolutionService>? log = null)
    {
        _db = db;
        _lookup = lookup;
        _clock = clock;
        _log = log;
    }

    public async Task<SponsorOrganizationSweepResult> ResolveAsync(int eventId, CancellationToken ct = default)
    {
        // `SponsorInfos` means SPONSORS (the §1034 query filter) — exactly the companies we post about.
        // §1360 — the LinkedIn URL is ONE field, on the company page; only sponsors that have one are asked about.
        var links = await Webshop.CompanyLinks.AllAsync(_db, eventId, ct);
        var sponsors = (await _db.SponsorInfos
                .Where(s => s.EventId == eventId)
                .ToListAsync(ct))
            .Where(s => Webshop.CompanyLinks.Of(links, s.SponsorCompanyId).LinkedIn is not null)
            .ToList();

        int resolved = 0, notFound = 0, failed = 0, skipped = 0;
        var now = _clock.GetUtcNow();

        foreach (var s in sponsors)
        {
            var linkedIn = Webshop.CompanyLinks.Of(links, s.SponsorCompanyId).LinkedIn!;
            var slug = LinkedInUrlParser.TryReadVanityName(linkedIn);
            if (!ShouldAsk(slug, s.LinkedInOrganizationId, s.LinkedInOrganizationLookedUpFrom,
                    s.LinkedInOrganizationCheckedAt, now))
            {
                skipped++;
                continue;
            }

            var outcome = await _lookup.ResolveAsync(linkedIn, ct);
            s.LinkedInOrganizationCheckedAt = _clock.GetUtcNow();
            switch (outcome.Status)
            {
                case OrganizationLookupStatus.Resolved:
                    s.LinkedInOrganizationId = outcome.OrganizationId;
                    s.LinkedInOrganizationLookedUpFrom = slug;
                    resolved++;
                    break;
                case OrganizationLookupStatus.NotFound:
                    // The slug is not a company LinkedIn knows. An id looked up from an OLD slug no
                    // longer describes this URL, so it goes; a hand-set id (no slug recorded) stays.
                    if (s.LinkedInOrganizationLookedUpFrom is not null)
                    {
                        s.LinkedInOrganizationId = null;
                        s.LinkedInOrganizationLookedUpFrom = null;
                    }
                    notFound++;
                    _log?.LogWarning("Sponsor company not found on LinkedIn: {Reason}", outcome.Reason);
                    break;
                default:
                    failed++;
                    _log?.LogWarning("Sponsor company lookup FAILED: {Reason}", outcome.Reason);
                    break;
            }
        }

        await _db.SaveChangesAsync(ct);
        var result = new SponsorOrganizationSweepResult(sponsors.Count, resolved, notFound, failed, skipped);
        _log?.LogInformation("Sponsor organization resolution — {Result}", result);
        return result;
    }

    /// <summary>Whether this sponsor's company should be looked up now.</summary>
    internal static bool ShouldAsk(
        string? slug, string? organizationId, string? lookedUpFrom, DateTimeOffset? checkedAt, DateTimeOffset now)
    {
        if (slug is null) return false;                                   // numeric URL or no company URL
        var hasId = !string.IsNullOrWhiteSpace(organizationId);
        if (hasId && lookedUpFrom is null) return false;                  // set by hand — it wins
        if (hasId && string.Equals(lookedUpFrom, slug, StringComparison.OrdinalIgnoreCase)) return false;
        if (hasId) return true;                                           // the URL's slug changed
        return checkedAt is null || now - checkedAt.Value >= NegativeAnswerTtl;
    }
}
