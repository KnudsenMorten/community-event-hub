using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>What an announcement run did.</summary>
/// <param name="Skipped">
/// Companies deliberately not written to, with the reason — a company that already has an item, or
/// one with nobody to write to.
/// </param>
public sealed record AnnouncementResult(
    int Sent, int Failed, IReadOnlyList<string> Skipped, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// §1165g — tell sponsors the catalogue exists, with a link to it.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-01: <i>"i want to be able to send info to sponsor with link to
/// catalog"</i>.</para>
///
/// <para>🔑 <b>A catalogue nobody is told about sells nothing</b> — this is the step that turns the
/// feature into revenue. It is deliberately <b>hand-started</b> and not a schedule: a recurring nag
/// about a shop is how a sponsor mutes the sender that later carries their booth deadline.</para>
///
/// <para>🔒 <b>The link points at the CEH page, never at the webshop.</b> The CEH page knows who they
/// are — it can offer <i>reuse the logo we already hold</i>, show a credit if they have one, and mark
/// what is reserved for them. A link straight to the shop would drop exactly the part that makes
/// this easy for them.</para>
///
/// <para>⚠️ <b>Skips a company that already has an item.</b> Announcing a catalogue to somebody who
/// has already bought from it reads as not paying attention, and it is the single most common way a
/// broadcast damages the relationship it was meant to build.</para>
/// </remarks>
public sealed class SwagCatalogAnnouncementService
{
    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _email;
    private readonly ILogger<SwagCatalogAnnouncementService> _log;

    public SwagCatalogAnnouncementService(
        CommunityHubDbContext db,
        IEmailSender email,
        ILogger<SwagCatalogAnnouncementService> log)
    {
        _db = db;
        _email = email;
        _log = log;
    }

    /// <summary>
    /// Write to every sponsor company that has not taken a catalogue item yet.
    /// </summary>
    /// <param name="catalogUrl">Absolute URL of the CEH catalogue page.</param>
    /// <param name="alreadyHoldingCompanyIds">
    /// Companies that already hold or bought an item, from the caller who has the catalogue state.
    /// </param>
    /// <param name="dryRun">
    /// 🔒 True by default at the call site: a broadcast is the one action whose blast radius is every
    /// sponsor at once, so the list of who would be written to is worth reading first.
    /// </param>
    public async Task<AnnouncementResult> AnnounceAsync(
        int eventId,
        string catalogUrl,
        IReadOnlyCollection<string> alreadyHoldingCompanyIds,
        bool dryRun,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(catalogUrl))
            return new(0, 0, Array.Empty<string>(), "No catalogue URL was given.");

        var companies = await _db.SponsorInfos
            .Where(s => s.EventId == eventId && !s.IsTestData && s.Status == SponsorStatus.Active)
            .Select(s => new { s.SponsorCompanyId, s.CompanyName })
            .ToListAsync(ct);

        if (companies.Count == 0)
            return new(0, 0, Array.Empty<string>(), "There are no active sponsor companies to write to.");

        var holding = alreadyHoldingCompanyIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        var sent = 0;
        var failed = 0;

        foreach (var company in companies.OrderBy(c => c.CompanyName))
        {
            var name = company.CompanyName ?? company.SponsorCompanyId;

            if (holding.Contains(company.SponsorCompanyId))
            {
                skipped.Add($"{name} — already has a catalogue item");
                continue;
            }

            // 🔑 Coordinators, never signers. That split is the standing rule for sponsor mail in
            // this codebase: a signer approves a contract and is not the person who picks swag, and
            // mailing them anyway is how an organizer's mail starts being filtered.
            var recipients = await _db.Participants
                .Where(p => p.EventId == eventId
                            && p.SponsorCompanyId == company.SponsorCompanyId
                            && p.Role == ParticipantRole.Sponsor
                            && p.LifecycleState != ParticipantLifecycleState.Inactive
                            && p.Email != null && p.Email != string.Empty)
                .Select(p => new { p.Email, p.FullName })
                .ToListAsync(ct);

            if (recipients.Count == 0)
            {
                skipped.Add($"{name} — no active contact to write to");
                continue;
            }

            var to = recipients[0].Email!;
            var cc = recipients.Skip(1).Select(r => r.Email!).ToList();

            if (dryRun)
            {
                sent++;
                continue;
            }

            var html =
                $"<p>Hi {System.Net.WebUtility.HtmlEncode(recipients[0].FullName ?? name)},</p>"
                + "<p>We have opened a <b>swag catalogue</b> for sponsors: a small set of useful, "
                + "brandable items that go into every attendee bag. You pick one, we put your logo "
                + "on it, and we handle the artwork, the order, the delivery and the packing.</p>"
                + $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(catalogUrl)}\">Open the catalogue</a></p>"
                + "<p>Each item shows the price per piece and the total for the full print run, and "
                + "how many are left — some items are exclusive, so once one is taken it is gone. If "
                + "we already hold your logo you will see it there; if not, we will ask for artwork "
                + "after you choose.</p>"
                + "<p>Reply to this mail if you would like something held while you decide.</p>";

            try
            {
                await _email.SendAsync(to, "Sponsor swag catalogue — pick an item for the attendee bags", html, cc, ct);
                sent++;
            }
            catch (Exception ex)
            {
                failed++;
                _log.LogWarning(ex, "§1165g: announcement failed for {Company}.", name);
            }
        }

        _log.LogInformation(
            "§1165g announcement ({Mode}): {Sent} sent, {Failed} failed, {Skipped} skipped.",
            dryRun ? "preview" : "send", sent, failed, skipped.Count);

        return new(sent, failed, skipped, null);
    }
}
