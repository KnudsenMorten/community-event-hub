using CommunityHub.Audit;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Webshop;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1305 item 10b — edit the public sponsor catalogue (the replacement of the sponsor PowerPoint). Operator:
/// <i>"I must have an organizer page to modify this."</i>
/// </summary>
[Authorize]
public class ShopCatalogPagesModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ShopSettings _settings;
    private readonly TimeProvider _clock;

    private readonly ShopCatalogDeckImportRunner? _importRunner;

    public ShopCatalogPagesModel(CommunityHubDbContext db, ICurrentParticipantAccessor participant, ShopSettings settings, TimeProvider clock,
        ShopCatalogDeckImportRunner? importRunner = null)
    {
        _importRunner = importRunner;
        _db = db;
        _participant = participant;
        _settings = settings;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }
    [BindProperty(SupportsGet = true)] public int? Edit { get; set; }
    [TempData] public string? Message { get; set; }
    public IReadOnlyList<ShopCatalogPage> Pages { get; private set; } = Array.Empty<ShopCatalogPage>();
    public ShopCatalogPage? Editing { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return;
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return; }
        Pages = await _db.ShopCatalogPages.AsNoTracking().Where(p => p.EventId == me.EventId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Title).ToListAsync(ct);
        Editing = Edit is int id ? Pages.FirstOrDefault(p => p.Id == id) : null;
    }

    [Audit("Saved a sponsor catalogue page", Category = AuditCategory.Admin, Action = "webshop.catalog-page-save", TargetType = "ShopCatalogPage")]
    public async Task<IActionResult> OnPostSaveAsync(
        int? id, string title, string? slug, string? chapter, int sortOrder, string? bodyMarkdown, string? mediaUrl,
        ShopGroup? productGroup, bool showBoothMap, bool isPublished, CancellationToken ct,
        string? productMatch = null, ShopCatalogAvailabilityView availabilityView = ShopCatalogAvailabilityView.None)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        if (string.IsNullOrWhiteSpace(title)) { Message = "A page needs a title."; return RedirectToPage(new { Edit = id }); }
        if (!string.IsNullOrWhiteSpace(mediaUrl) && !mediaUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        { Message = "The picture / video link must start with https://"; return RedirectToPage(new { Edit = id }); }

        var cleanSlug = Slugify(string.IsNullOrWhiteSpace(slug) ? title : slug);
        var page = id is int pid ? await _db.ShopCatalogPages.FirstOrDefaultAsync(p => p.Id == pid && p.EventId == me.EventId, ct) : null;
        if (await _db.ShopCatalogPages.AnyAsync(p => p.EventId == me.EventId && p.Slug == cleanSlug && p.Id != (page == null ? 0 : page.Id), ct))
        { Message = $"Another page already uses the address '{cleanSlug}'."; return RedirectToPage(new { Edit = id }); }

        if (page is null)
        {
            page = new ShopCatalogPage { EventId = me.EventId };
            _db.ShopCatalogPages.Add(page);
        }
        page.Title = title.Trim();
        page.Slug = cleanSlug;
        page.Chapter = string.IsNullOrWhiteSpace(chapter) ? null : chapter.Trim();
        page.SortOrder = sortOrder;
        page.BodyMarkdown = bodyMarkdown;
        page.MediaUrl = string.IsNullOrWhiteSpace(mediaUrl) ? null : mediaUrl.Trim();
        page.ProductGroup = productGroup;
        page.ShowBoothMap = showBoothMap;
        page.ProductMatch = string.IsNullOrWhiteSpace(productMatch) ? null : productMatch.Trim()[..Math.Min(productMatch.Trim().Length, 200)];
        page.AvailabilityView = availabilityView;
        page.IsPublished = isPublished;
        page.UpdatedAt = _clock.GetUtcNow();
        page.UpdatedByEmail = me.Email;
        await _db.SaveChangesAsync(ct);
        Message = $"Saved '{page.Title}'.";
        return RedirectToPage(new { Edit = page.Id });
    }

    [Audit("Deleted a sponsor catalogue page", Category = AuditCategory.Admin, Action = "webshop.catalog-page-delete", TargetType = "ShopCatalogPage")]
    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var page = await _db.ShopCatalogPages.FirstOrDefaultAsync(p => p.Id == id && p.EventId == me.EventId, ct);
        if (page is not null) { _db.ShopCatalogPages.Remove(page); await _db.SaveChangesAsync(ct); Message = $"Deleted '{page.Title}'."; }
        return RedirectToPage();
    }

    public bool DeckConfigured => _settings.IsConfigured && !string.IsNullOrWhiteSpace(_settings.Shop().CatalogDeck.FileName);
    public string DeckFileName => _settings.IsConfigured ? _settings.Shop().CatalogDeck.FileName : string.Empty;
    public ShopCatalogDeckImportRunner.Status? ImportStatus => _importRunner?.Current;

    /// <summary>§1312 — preview (writes nothing) or import the sponsor deck, in the background.</summary>
    [Audit("Imported the sponsor deck into the catalogue", Category = AuditCategory.Admin, Action = "webshop.catalog-deck-import", TargetType = "ShopCatalogPage")]
    public IActionResult OnPostImportDeck(bool dryRun)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        if (_importRunner is null) { Message = "The import is not available on this host."; return RedirectToPage(); }
        Message = _importRunner.Start(me.EventId, me.Email, dryRun)
            ? (dryRun ? "Preview started — the result appears below in a minute or two." : "Import started — the pages appear in a few minutes.")
            : "An import is already running.";
        return RedirectToPage();
    }

    /// <summary>§1312 — publish every imported (deck-…) page at once, after review.</summary>
    [Audit("Published the imported sponsor catalogue pages", Category = AuditCategory.Admin, Action = "webshop.catalog-deck-publish", TargetType = "ShopCatalogPage")]
    public async Task<IActionResult> OnPostPublishImportedAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var pages = await _db.ShopCatalogPages
            .Where(p => p.EventId == me.EventId && p.Slug.StartsWith(ShopCatalogDeckImporter.SlugPrefix) && !p.IsPublished).ToListAsync(ct);
        foreach (var p in pages) { p.IsPublished = true; p.UpdatedAt = _clock.GetUtcNow(); p.UpdatedByEmail = me.Email; }
        await _db.SaveChangesAsync(ct);
        Message = $"{pages.Count} imported page(s) published.";
        return RedirectToPage();
    }

    /// <summary>
    /// Starter pages following the ELDK27 sponsor deck's chapters — all unpublished, text to be edited. Only pages
    /// whose address does not exist yet are created, so it is safe to press twice.
    /// </summary>
    [Audit("Created the starter sponsor catalogue pages", Category = AuditCategory.Admin, Action = "webshop.catalog-seed", TargetType = "ShopCatalogPage")]
    public async Task<IActionResult> OnPostSeedAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var cfg = _settings.Shop();
        var ev = cfg.Event;
        var existing = await _db.ShopCatalogPages.Where(p => p.EventId == me.EventId).Select(p => p.Slug).ToListAsync(ct);
        var now = _clock.GetUtcNow();
        var created = 0;
        foreach (var (order, chapter, title, body, group, map) in StarterPages(ev))
        {
            var s = Slugify(title);
            if (existing.Contains(s)) continue;
            _db.ShopCatalogPages.Add(new ShopCatalogPage
            {
                EventId = me.EventId, Slug = s, Title = title, Chapter = chapter, SortOrder = order, BodyMarkdown = body,
                ProductGroup = group, ShowBoothMap = map, IsPublished = false, UpdatedAt = now, UpdatedByEmail = me.Email,
            });
            created++;
        }
        await _db.SaveChangesAsync(ct);
        Message = $"{created} starter page(s) created, unpublished. Edit the text, then publish.";
        return RedirectToPage();
    }

    private static IEnumerable<(int, string, string, string, ShopGroup?, bool)> StarterPages(Core.Config.ShopEventInfo ev)
    {
        var dates = string.Join("\n", ev.DatesText.Select(d => "- " + d));
        var venue = string.Join(", ", ev.Venue);
        yield return (10, "About", $"{ev.Name} — sponsor information",
            $"The largest in-person Microsoft multi-day conference in Denmark, delivered by the Microsoft community.\n\n**Dates**\n\n{dates}\n\n**Venue:** {venue}\n\n**Website:** {ev.Website}", null, false);
        yield return (20, "About", "Numbers", "Attendees, no-show rate, exhibitor leads from the last edition — *edit these numbers*.", null, false);
        yield return (30, "About", "Videos", "- Highlights (1 min)\n- Sponsor interview (3 min)\n- Sponsor thank-you\n\n*Add the video links.*", null, false);
        yield return (100, "Sponsor opportunities", "Booths", "Tier packages with an exhibitor booth — Platinum, Diamond and Gold, each with its own booth size, wall and included furniture.", ShopGroup.Booths, false);
        yield return (110, "Sponsor opportunities", "Booth map", "Where every booth is, and which are still free.", null, true);
        yield return (120, "Sponsor opportunities", "Digital only sponsor (no booth)", "Get your name and logo shown throughout the event without a booth.", ShopGroup.Packages, false);
        yield return (130, "Sponsor opportunities", "Sponsor sessions", "Morning, long and expo-stage sessions, and pre-day master-class track sponsorships. You get the leads of attendees and of people who bookmarked your session.", ShopGroup.Sessions, false);
        yield return (140, "Sponsor opportunities", "Branded feature packages", "Coffee, lunch, party, lounge, name badges, key hangers and more — visible to every participant.", ShopGroup.Branding, false);
        yield return (150, "Sponsor opportunities", "Furniture", "Chairs, tables and counters for your booth. Your booth tier's included furniture is taken off the cart automatically.", ShopGroup.Furniture, false);
        yield return (155, "Sponsor opportunities", "Booth options", "TV screens, extra staff tickets and attendee-bag packaging.", ShopGroup.BoothOptions, false);
        yield return (160, "Sponsor opportunities", "Package handling", "Getting your goods to and from the venue.", ShopGroup.PackageHandling, false);
        yield return (170, "Sponsor opportunities", "Swag for the attendee bags", "Branded items we buy, brand and pack into every bag.", ShopGroup.Swag, false);
        yield return (200, "How to participate", "How to participate",
            "**Reservation (handshake) vs. order (commitment).** Reserve any package or option — first come, first served — and sign the contract when you are ready.\n\n**Payment:** invoice, payable 30 days after the contract is signed.\n\nNeed special arrangements? Talk to us and we will find a solution.\n\n[Register your company](/sponsor-registration)", null, false);
    }

    public static string Slugify(string s)
    {
        var chars = s.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var slug = System.Text.RegularExpressions.Regex.Replace(new string(chars), "-{2,}", "-").Trim('-');
        return slug.Length > 90 ? slug[..90] : (slug.Length == 0 ? "page" : slug);
    }
}
