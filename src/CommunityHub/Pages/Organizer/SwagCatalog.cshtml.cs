using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1165c — the organizer side of the swag catalogue: who has what, and everything that has to
/// happen before a branded item is in 1 500 bags.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-01: <i>"we as org must be able to handle logistics"</i>.</para>
///
/// <para>🔑 <b>The sponsor page is the shop window; this is the product.</b> The promise — <i>"we
/// handle buy, logo, delivery, packing"</i> — is kept here, and it is the first place in CEH that
/// rolls up <b>per ITEM across companies</b> rather than per company. That is the genuinely new
/// shape: everything else in the hub answers "how is this sponsor doing?", and a supplier order
/// needs "how many of this thing, for whom, and is the artwork in?".</para>
/// </remarks>
[Authorize]
public class SwagCatalogModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SponsorSwagCatalogService _catalog;
    private readonly SwagCatalogHoldService _holds;
    private readonly SwagCatalogCreditService _credits;
    private readonly SwagCatalogAnnouncementService _announce;
    private readonly TimeProvider _clock;

    public SwagCatalogModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SponsorSwagCatalogService catalog,
        SwagCatalogHoldService holds,
        SwagCatalogCreditService credits,
        SwagCatalogAnnouncementService announce,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _catalog = catalog;
        _holds = holds;
        _credits = credits;
        _announce = announce;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }

    [TempData] public string? ActionMessage { get; set; }

    /// <summary>One catalogue item, with everything an organizer has to know about it.</summary>
    /// <param name="ArtworkOnFile">
    /// Whether the company that has it has already given us a logo. ⚠️ Only meaningful when the item
    /// is held or bought — for a free item it is simply not a question yet.
    /// </param>
    public sealed record ItemRow(
        long ProductId,
        string Name,
        string? PriceText,
        decimal? TotalPrice,
        bool InStock,
        int? RemainingCount,
        SwagCatalogHold? Hold,
        bool? ArtworkOnFile);

    public IReadOnlyList<ItemRow> Rows { get; private set; } = Array.Empty<ItemRow>();
    public string? Diagnostic { get; private set; }
    public int BagQuantity { get; private set; }
    public string? CurrencySymbol { get; private set; }

    /// <summary>Every live credit across all companies — the money promised but not yet spent.</summary>
    public IReadOnlyList<SwagCatalogCredit> LiveCredits { get; private set; } =
        Array.Empty<SwagCatalogCredit>();

    /// <summary>Sponsor companies, for the reserve / grant pickers.</summary>
    public IReadOnlyList<(string Id, string Name)> Companies { get; private set; } =
        Array.Empty<(string, string)>();

    /// <summary>
    /// How many bags are actually being made, counted from CEH — the sanity check against
    /// <see cref="BagQuantity"/>.
    /// </summary>
    /// <remarks>
    /// 🔑 Operator 2026-09-02: <i>"bag content includes also speakers, volunteers, as example"</i>.
    /// So this counts every role that receives a bag, not attendees alone. An item ordered against
    /// the wrong number is discovered at packing, which is far too late to fix.
    /// </remarks>
    public int ReceivingHeadcount { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return;
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return; }

        await LoadAsync(me.EventId, ct);
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var catalog = await _catalog.GetAsync(ct);
        Diagnostic = catalog.Diagnostic;
        BagQuantity = catalog.BagQuantity;
        CurrencySymbol = catalog.CurrencySymbol;

        var live = await _holds.LiveHoldsAsync(eventId, now, ct);
        var holdByProduct = live.GroupBy(h => h.ProductId).ToDictionary(g => g.Key, g => g.First());

        // Which companies have a logo on file — the artwork half of the promise.
        var logos = await _db.SponsorInfos
            .Where(s => s.EventId == eventId)
            .Select(s => new { s.SponsorCompanyId, s.LogoVectorFileName, s.LogoRasterFileName })
            .ToListAsync(ct);
        var hasLogo = logos
            .GroupBy(s => s.SponsorCompanyId)
            .ToDictionary(
                g => g.Key,
                g => g.Any(x => !string.IsNullOrWhiteSpace(x.LogoVectorFileName)
                                || !string.IsNullOrWhiteSpace(x.LogoRasterFileName)),
                StringComparer.Ordinal);

        Rows = catalog.Items.Select(i =>
        {
            holdByProduct.TryGetValue(i.ProductId, out var hold);
            bool? artwork = hold is null
                ? null
                : hasLogo.TryGetValue(hold.SponsorCompanyId, out var v) && v;

            return new ItemRow(
                i.ProductId, i.Name, i.PriceText, i.TotalPrice,
                i.Available, i.RemainingCount, hold, artwork);
        }).ToList();

        var credits = await _db.SwagCatalogCredits
            .Where(c => c.EventId == eventId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        LiveCredits = credits.Where(c => c.IsLiveOn(today)).ToList();

        Companies = await _db.SponsorInfos
            .Where(s => s.EventId == eventId && !s.IsTestData)
            .OrderBy(s => s.CompanyName)
            .Select(s => new ValueTuple<string, string>(
                s.SponsorCompanyId, s.CompanyName ?? s.SponsorCompanyId))
            .ToListAsync(ct);

        // §1165l — every role that receives a bag, not attendees alone.
        ReceivingHeadcount = await _db.Participants
            .CountAsync(p => p.EventId == eventId
                             && p.LifecycleState != ParticipantLifecycleState.Inactive, ct);
    }

    public async Task<IActionResult> OnPostHoldAsync(
        long productId, string productName, string companyId, int days, string? note, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var now = _clock.GetUtcNow();
        var company = await _db.SponsorInfos
            .Where(s => s.EventId == me.EventId && s.SponsorCompanyId == companyId)
            .Select(s => s.CompanyName)
            .FirstOrDefaultAsync(ct);

        // 🔒 A hold with no end is indistinguishable from a sale; the form's default is 14 days and
        // anything non-positive is corrected rather than accepted.
        var span = days > 0 ? days : 14;

        var r = await _holds.HoldAsync(
            me.EventId, productId, productName, companyId, company ?? companyId,
            now.AddDays(span), me.Email, note, now, ct);

        ActionMessage = r.Ok
            ? $"'{productName}' is reserved for {r.Hold!.CompanyName} until {r.Hold.ExpiresAt:yyyy-MM-dd}."
            : r.Error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReleaseAsync(int holdId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var ok = await _holds.ReleaseAsync(
            me.EventId, holdId, me.Email, $"Released by {me.Email}.", _clock.GetUtcNow(), ct);

        ActionMessage = ok
            ? "The reservation was released — the item is free again."
            : "That reservation was already gone.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostGrantCreditAsync(
        string companyId, decimal amount, int? validDays, string? note, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var company = await _db.SponsorInfos
            .Where(s => s.EventId == me.EventId && s.SponsorCompanyId == companyId)
            .Select(s => s.CompanyName)
            .FirstOrDefaultAsync(ct);

        // 🔒 The credit is restricted to this company's OWN contacts, so a forwarded code cannot
        // spend our money. Gathered here because this is where we know who they are.
        var emails = await _db.Participants
            .Where(p => p.EventId == me.EventId
                        && p.SponsorCompanyId == companyId
                        && p.Email != null && p.Email != string.Empty)
            .Select(p => p.Email!)
            .Distinct()
            .ToListAsync(ct);

        var edition = await _db.Events
            .Where(e => e.Id == me.EventId).Select(e => e.Code).FirstOrDefaultAsync(ct);

        var now = _clock.GetUtcNow();
        DateOnly? expires = validDays is int d && d > 0
            ? DateOnly.FromDateTime(now.UtcDateTime).AddDays(d)
            : null;

        var r = await _credits.GrantAsync(
            me.EventId, companyId, company ?? companyId, amount, expires,
            me.Email, note, emails, edition ?? string.Empty, ct);

        ActionMessage = r.Ok
            ? $"{company ?? companyId} now has a credit of {amount:N0} — code {r.Credit!.Code}"
              + (expires is null ? " (no expiry)." : $", valid until {expires:yyyy-MM-dd}.")
              + (emails.Count == 0
                    ? " ⚠️ They have no contacts on file, so the code is NOT locked to their e-mail addresses — anyone it is forwarded to can spend it."
                    : $" Locked to their {emails.Count} contact e-mail(s).")
            : r.Error;
        return RedirectToPage();
    }

    /// <summary>
    /// §1165g — write to sponsors with a link to the catalogue. Preview by default.
    /// </summary>
    /// <remarks>
    /// 🔒 Two presses on purpose. This is the one action here whose blast radius is EVERY sponsor at
    /// once; the preview says exactly who would be written to and who would be skipped, and reading
    /// that costs a few seconds against a mail that cannot be recalled.
    /// </remarks>
    public async Task<IActionResult> OnPostAnnounceAsync(bool send, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var now = _clock.GetUtcNow();

        // Companies that already hold an item are skipped — announcing a catalogue to someone who
        // has already bought from it reads as not paying attention.
        var holding = (await _holds.LiveHoldsAsync(me.EventId, now, ct))
            .Select(h => h.SponsorCompanyId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var url = $"{Request.Scheme}://{Request.Host}{Url.Page("/Sponsor/SwagCatalog")}";

        var r = await _announce.AnnounceAsync(me.EventId, url, holding, dryRun: !send, ct);

        if (!r.Ok)
        {
            ActionMessage = r.Error;
            return RedirectToPage();
        }

        var skippedText = r.Skipped.Count == 0
            ? string.Empty
            : $" Skipped {r.Skipped.Count}: " + string.Join("; ", r.Skipped.Take(20))
              + (r.Skipped.Count > 20 ? " …" : string.Empty);

        ActionMessage = (send
                ? $"Announcement SENT to {r.Sent} company(ies)"
                  + (r.Failed > 0 ? $", {r.Failed} FAILED" : string.Empty)
                : $"PREVIEW — {r.Sent} company(ies) would be written to, nothing sent")
            + "." + skippedText;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeCreditAsync(int creditId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var credit = await _db.SwagCatalogCredits
            .FirstOrDefaultAsync(c => c.EventId == me.EventId && c.Id == creditId && c.RevokedAt == null, ct);

        if (credit is null)
        {
            ActionMessage = "That credit was already withdrawn.";
            return RedirectToPage();
        }

        credit.RevokedAt = _clock.GetUtcNow();
        credit.RevokedByEmail = me.Email;
        credit.RevokedReason = $"Withdrawn by {me.Email}.";
        await _db.SaveChangesAsync(ct);

        // ⚠️ CEH stops SHOWING it; the coupon itself still exists in the webshop. Deleting a coupon
        // there would orphan any order that already used it, so withdrawing is deliberately a CEH
        // act — and the code must also be disabled in the shop if it was never spent.
        ActionMessage = $"Credit {credit.Code} withdrawn in CEH. ⚠️ If it was never spent, disable "
            + "the coupon in the webshop too — CEH does not delete it, because deleting a used "
            + "coupon would orphan the order that used it.";
        return RedirectToPage();
    }
}
