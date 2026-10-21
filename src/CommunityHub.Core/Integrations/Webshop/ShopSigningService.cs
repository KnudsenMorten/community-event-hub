using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>
/// §1305 item 7 — the contract: build it (offline PDF), send it for signature (magic link to the signer),
/// sign it, and turn the company into a sponsor.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-26: <i>"2 buttons 'Sign Contract' and 'Build Contract (offline PDF)' … people can
/// sign digitally or upload their signature as graphic … generate a digital signature with the signers
/// name … automatically send link for signing to signer role, where they get a magic link … read the whole
/// contract, download it and also sign it."</i></para>
///
/// <para>🔑 <b>The PDF is always re-renderable</b> from the stored order + signature picture, so the
/// SharePoint copy is an archive, not the only copy: a SharePoint outage never loses a signed contract.</para>
///
/// <para>🔑 <b>Signing makes the company a sponsor</b> (operator: <i>"flipped from sponsor (candidate …) to
/// sponsor (order)"</i>) and the next order-pull run onboards it through the normal pipeline
/// (<see cref="ShopOrderSource"/>) — §1305 item 7b.</para>
/// </remarks>
public sealed class ShopSigningService
{
    /// <summary>A signature picture larger than this is refused before it is decoded.</summary>
    public const int MaxSignatureBytes = 2 * 1024 * 1024;

    private readonly CommunityHubDbContext _db;
    private readonly ShopOrderService _orders;
    private readonly ShopSettings _settings;
    private readonly ShopContractPdfService _pdf;
    private readonly ShopMailer _mail;
    private readonly ISharePointFileStore _store;
    private readonly IDocLibraryPathResolver _paths;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShopSigningService>? _log;

    public ShopSigningService(
        CommunityHubDbContext db, ShopOrderService orders, ShopSettings settings, ShopContractPdfService pdf,
        ShopMailer mail, ISharePointFileStore store, IDocLibraryPathResolver paths, TimeProvider clock,
        ILogger<ShopSigningService>? log = null)
    {
        _db = db;
        _orders = orders;
        _settings = settings;
        _pdf = pdf;
        _mail = mail;
        _store = store;
        _paths = paths;
        _clock = clock;
        _log = log;
    }

    /// <summary>Render the order's contract — signed if it is signed, otherwise blank for signing offline.</summary>
    public async Task<byte[]> RenderAsync(ShopOrder order, CancellationToken ct = default)
    {
        var (pdf, _, issues) = _pdf.RenderChecked(await BuildAsync(order, ct));
        // The layout gate is enforced by the tests; a live contract that still trips it is logged, not refused.
        if (issues.Count > 0)
            _log?.LogWarning("§1305 contract {Number} layout: {Issues}", order.Number, string.Join(" | ", issues));
        return pdf;
    }

    /// <summary>Everything the contract prints, from the order, the company and its people.</summary>
    public async Task<ShopContractData> BuildAsync(ShopOrder order, CancellationToken ct = default)
    {
        var cfg = _settings.Shop();
        var company = await _db.ShopCompanies.AsNoTracking()
            .FirstAsync(c => c.EventId == order.EventId && c.SponsorCompanyId == order.SponsorCompanyId, ct);
        var totals = await _orders.PriceAsync(order, company, ct);

        async Task<(string, string, string)> Person(int? id)
        {
            if (id is not int pid) return (string.Empty, string.Empty, string.Empty);
            var p = await _db.Participants.AsNoTracking().Where(x => x.Id == pid)
                .Select(x => new { x.FullName, x.Email, x.Phone }).FirstOrDefaultAsync(ct);
            return p is null ? (string.Empty, string.Empty, string.Empty) : (p.FullName, p.Email, p.Phone ?? string.Empty);
        }

        var address = new[] { company.Address, company.City, company.Postcode, CountryName(company.CountryIso2) }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
        var byId = order.Lines.ToDictionary(l => l.Id);

        return new ShopContractData(
            cfg,
            order.Number ?? $"draft-{order.Id}",
            _clock.GetUtcNow(),
            company.LegalName,
            company.PublicLabel,   // §1312 — "used in announcement": never the legal name with its legal form
            company.Cvr,
            address,
            await Person(order.SignerParticipantId),
            await Person(order.CoordinatorParticipantId),
            company.InvoiceEmail ?? company.CompanyEmail,
            order.PurchaseOrderRef,
            string.IsNullOrWhiteSpace(order.Currency) ? company.Currency : order.Currency,
            company.Website,
            company.LinkedInUrl,
            company.TwitterUrl,
            totals.Lines.Select(l => (l.Name, DiscountNote(l) + SwagNote(byId.GetValueOrDefault(l.LineId)?.Product)
                + Description(byId.GetValueOrDefault(l.LineId)?.Product),
                l.Quantity, l.LineEur, l.SurchargePercent)).ToList(),
            totals,
            order.SpecialTerms,
            order.SignatureImage,
            order.SignedAt,
            order.SignedByName,
            order.SignedAt,
            order.InvoiceNotBefore,
            order.PaymentDays,
            await EventLogoAsync(ct),
            await OrganizerSignatureAsync(cfg.Organizer.ContractOwner.SignatureImageFileName, ct));

        // A discounted line says so first: the normal price and the discount (operator 2026-09-26: "so people can see
        // normal price and new price"). Invariant numbers — the contract is English.
        static string DiscountNote(ShopPricedLine l) =>
            l.NormalUnitEur is decimal n && n > l.ListUnitEur
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Discounted: normal price €{ShopPricing.Amount(n * l.Quantity)}, your price €{ShopPricing.Amount(l.ListUnitEur * l.Quantity)} (−{Math.Round((1 - l.ListUnitEur / n) * 100m):0}%).\n")
                : string.Empty;

        // A swag line states the split: the sponsor's share, the full price for every bag, ELDK's part (operator
        // 2026-09-26: "show the split in payment").
        string SwagNote(ShopProduct? p) =>
            p?.PrimaryCategory?.Group == ShopGroup.Swag
                ? ShopPricing.SwagSplitText(ShopPricing.SwagSplitFor(p, cfg.SwagPricing, 1)) + "\n"
                : string.Empty;

        // The full description; the short one only when the product has nothing longer.
        static string? Description(ShopProduct? p) =>
            p is null ? null : PlainText(p.DescriptionHtml) ?? PlainText(p.ShortDescriptionHtml);
    }

    /// <summary>
    /// Reserve (if still a cart) and send the signer a magic link to read, download and sign the contract.
    /// </summary>
    public async Task<ShopResult> SendForSignatureAsync(ShopOrder order, CancellationToken ct = default)
    {
        if (order.SignerParticipantId is null) return ShopResult.Fail("Choose who signs the contract first.");
        if (order.Status is ShopOrderStatus.Signed or ShopOrderStatus.Cancelled)
            return ShopResult.Fail("This order is already signed or cancelled.");

        // §1312 — prices frozen and a number given, but NO stock taken: nothing is ordered until it is signed.
        var reserved = await _orders.ReserveAsync(order, ct, takeStock: false);
        if (!reserved.Ok) return reserved;

        order.Status = ShopOrderStatus.AwaitingSignature;
        order.SentForSignatureAt = _clock.GetUtcNow();
        order.UpdatedAt = order.SentForSignatureAt;
        await _db.SaveChangesAsync(ct);

        // The contract travels WITH the request: a legal document the signer must be able to read and keep.
        byte[]? pdf = null;
        try { pdf = await RenderAsync(order, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "§1305 contract for {Number} could not be rendered for the signing mail.", order.Number);
        }
        // The items and prices go in the mail body too, not only in the attachment.
        ShopTotals? totals = null;
        var company = await _db.ShopCompanies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EventId == order.EventId && c.SponsorCompanyId == order.SponsorCompanyId, ct);
        if (company is not null) totals = await _orders.PriceAsync(order, company, ct);
        var sent = await _mail.SendSignRequestAsync(order, totals, pdf, ct);
        return sent
            ? ShopResult.Success($"The signer has been sent a link to sign order {order.Number}. Nothing is ordered until it is signed.")
            : ShopResult.Success($"Order {order.Number} is ready to sign. The signing e-mail could not be sent — the signer can sign from the Orders page.");
    }

    /// <summary>The <see cref="SentReminder"/> type of the daily signing reminder.</summary>
    public const string SignReminderType = "shop-sign-reminder";

    /// <summary>
    /// §1312 — the DAILY reminder for a contract waiting for its signer IS the signing mail again: the magic link, the items
    /// and the contract PDF, to the signer named on the order. Operator 2026-09-28: <i>"if a webshop order is in awaiting
    /// signer, then the daily reminder must be the mail that sends the signing mail to the signer"</i>.
    /// </summary>
    /// <remarks>
    /// Before this, an order sent for signature from a reservation got no daily reminder at all (the cart reminder skips
    /// organizer reservations; the reservation reminder chases only <see cref="ShopOrderStatus.Reserved"/>), and any other
    /// got the generic "your cart is waiting" mail without the link. Not on the day it was sent (the signer already has it
    /// that day); at most once per order per event-local day (<see cref="SentReminder"/>).
    /// </remarks>
    public async Task<int> SendSignRemindersAsync(int eventId, DateOnly today, CancellationToken ct = default)
    {
        // 🔴 §1312 — WITH the lines and their products: without them the reminder's contract PDF and totals were built from an
        // empty order — "Subtotal 0 €" and a contract without items went to Egiss (ELDK27-0003, operator 2026-10-01: "bug: values
        // are missing … also in contract"). The same includes as ShopOrderService's own order load.
        var waiting = await _db.ShopOrders
            .Include(o => o.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.PrimaryCategory)
            .Include(o => o.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.Images)
            .Where(o => o.EventId == eventId && o.Status == ShopOrderStatus.AwaitingSignature
                        && o.SignerParticipantId != null && o.Lines.Any())
            .ToListAsync(ct);
        var sent = 0;
        foreach (var order in waiting)
        {
            if (order.SentForSignatureAt is DateTimeOffset at
                && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, EventTimezone.Tz).DateTime) >= today) continue;
            var occasion = $"{order.Id}:{today:yyyy-MM-dd}";
            if (await _db.SentReminders.AnyAsync(r => r.EventId == eventId && r.ReminderType == SignReminderType && r.OccasionKey == occasion, ct))
                continue;
            try
            {
                byte[]? pdf = null;
                try { pdf = await RenderAsync(order, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log?.LogWarning(ex, "§1312 contract for {Number} could not be rendered for the signing reminder.", order.Number);
                }
                ShopTotals? totals = null;
                var company = await _db.ShopCompanies.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.EventId == order.EventId && c.SponsorCompanyId == order.SponsorCompanyId, ct);
                if (company is not null) totals = await _orders.PriceAsync(order, company, ct);
                if (!await _mail.SendSignRequestAsync(order, totals, pdf, ct)) continue;

                var signer = await _db.Participants.AsNoTracking().Where(p => p.Id == order.SignerParticipantId)
                    .Select(p => p.Email).FirstOrDefaultAsync(ct);
                _db.SentReminders.Add(new SentReminder
                {
                    EventId = eventId, RecipientEmail = signer ?? string.Empty, ReminderType = SignReminderType,
                    OccasionKey = occasion, SentAt = _clock.GetUtcNow(),
                });
                await _db.SaveChangesAsync(ct);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.LogWarning(ex, "§1312 signing reminder for order {Number} failed.", order.Number);
            }
        }
        return sent;
    }

    /// <summary>
    /// Sign the contract. <paramref name="signaturePng"/> is the picture from the browser: drawn, typed
    /// (rendered from the signer's name) or uploaded — decoded, cleaned and re-encoded here.
    /// </summary>
    public async Task<ShopResult> SignAsync(
        ShopOrder order, int participantId, ShopSignatureKind kind, byte[] signaturePng, string? ip,
        CancellationToken ct = default)
    {
        if (order.Status is ShopOrderStatus.Signed) return ShopResult.Fail("This contract is already signed.");
        if (order.Status is ShopOrderStatus.Cancelled) return ShopResult.Fail("This order was cancelled.");
        if (!_settings.IsConfigured) return ShopResult.Fail("The contract is not configured for this event yet.");

        var person = await _db.Participants.FirstOrDefaultAsync(p => p.Id == participantId, ct);
        if (person is null) return ShopResult.Fail("Unknown signer.");

        // Only the chosen signer — or, when none is chosen, a signer of that company — may sign.
        var mayAct = order.SignerParticipantId is int chosen
            ? chosen == participantId
            : await _db.SponsorCompanyContacts.AnyAsync(l => l.EventId == order.EventId
                && l.SponsorCompanyId == order.SponsorCompanyId && l.ParticipantId == participantId && l.IsSigner, ct);
        if (!mayAct) return ShopResult.Fail("Only the signer named on the order can sign this contract.");

        byte[] clean;
        try { clean = CleanSignature(signaturePng); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ShopResult.Fail("The signature picture could not be read. Please draw, type or upload it again.");
        }

        var reserved = await _orders.ReserveAsync(order, ct);
        if (!reserved.Ok) return reserved;
        // §1312 — the stock is taken HERE, at signing, for anything not already held by an accepted reservation.
        var stock = await _orders.TakeOutstandingStockAsync(order, ct);
        if (!stock.Ok) return stock;

        var now = _clock.GetUtcNow();
        order.Status = ShopOrderStatus.Signed;
        order.SignedAt = now;
        order.SignedByParticipantId = person.Id;
        order.SignedByName = person.FullName;
        order.SignedByEmail = person.Email;
        order.SignatureKind = kind;
        order.SignatureImage = clean;
        order.SignedFromIp = ip;
        order.SignerParticipantId ??= person.Id;
        order.UpdatedAt = now;

        await MakeSponsorAsync(order, now, ct);
        await _db.SaveChangesAsync(ct);

        // Archive copy in SharePoint. Failure is logged, not fatal: the PDF can always be re-rendered.
        byte[]? pdf = null;
        try
        {
            pdf = await RenderAsync(order, ct);
            if (_store.CanStore && _paths.TryResolve(DocLibraryPaths.SponsorWebshopContracts, out var folder)
                && !string.IsNullOrWhiteSpace(folder))
            {
                var company = await _db.ShopCompanies.AsNoTracking()
                    .Where(c => c.EventId == order.EventId && c.SponsorCompanyId == order.SponsorCompanyId)
                    .Select(c => c.LegalName).FirstOrDefaultAsync(ct) ?? order.SponsorCompanyId;
                var name = $"{order.Number}-{Safe(company)}-signed.pdf";
                var stored = await _store.UploadToFolderAsync(folder, name, pdf, "application/pdf", ct);
                order.ContractPath = stored.Path;
                order.ContractItemId = stored.ItemId;
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "§1305 signed contract {Number} could not be archived in SharePoint.", order.Number);
        }

        ShopTotals? signedTotals = null;
        var signedCompany = await _db.ShopCompanies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EventId == order.EventId && c.SponsorCompanyId == order.SponsorCompanyId, ct);
        if (signedCompany is not null) signedTotals = await _orders.PriceAsync(order, signedCompany, ct);
        await _mail.SendSignedAsync(order, signedTotals, pdf, ct);
        return ShopResult.Success($"Thank you — order {order.Number} is signed. A copy is on its way by e-mail.");
    }

    /// <summary>
    /// §1312 — an ORGANIZER confirms an order the sponsor signed their own way (a printed / PDF contract), operator 2026-09-27:
    /// "organizer manually complete the order if the sponsor make a manual pdf signing … i need a Manuel Confirm Order button".
    /// Exactly what a digital signature does — stock taken, the company becomes a sponsor, the signed mails go out — with the
    /// sponsor's own signed file (when uploaded) archived as the contract.
    /// </summary>
    public async Task<ShopResult> ConfirmOfflineAsync(
        ShopOrder order, string organizerName, byte[]? signedFile, string? signedFileName, CancellationToken ct = default)
    {
        if (order.Status is ShopOrderStatus.Signed) return ShopResult.Fail("This order is already signed.");
        if (order.Status is ShopOrderStatus.Cancelled) return ShopResult.Fail("This order was cancelled.");
        if (order.Lines.Count == 0) return ShopResult.Fail("The order has no items.");

        var reserved = await _orders.ReserveAsync(order, ct);
        if (!reserved.Ok) return reserved;
        var stock = await _orders.TakeOutstandingStockAsync(order, ct);
        if (!stock.Ok) return stock;

        var signer = order.SignerParticipantId is int sid
            ? await _db.Participants.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sid, ct) : null;
        var now = _clock.GetUtcNow();
        order.Status = ShopOrderStatus.Signed;
        order.SignedAt = now;
        order.SignedByParticipantId = signer?.Id;
        order.SignedByName = signer?.FullName ?? "Signed offline";
        order.SignedByEmail = signer?.Email;
        order.SignatureKind = ShopSignatureKind.Offline;
        order.SignatureImage = null;
        var by = $"offline, confirmed by {organizerName}";
        order.SignedFromIp = by.Length > 64 ? by[..64] : by;   // the column is 64 wide
        order.UpdatedAt = now;
        await MakeSponsorAsync(order, now, ct);
        await _db.SaveChangesAsync(ct);

        // Archive: the sponsor's own signed file when the organizer uploaded it, otherwise the contract as it stands.
        byte[]? pdf = signedFile is { Length: > 0 } ? signedFile : null;
        try
        {
            pdf ??= await RenderAsync(order, ct);
            if (_store.CanStore && _paths.TryResolve(DocLibraryPaths.SponsorWebshopContracts, out var folder)
                && !string.IsNullOrWhiteSpace(folder))
            {
                var company = await _db.ShopCompanies.AsNoTracking()
                    .Where(c => c.EventId == order.EventId && c.SponsorCompanyId == order.SponsorCompanyId)
                    .Select(c => c.LegalName).FirstOrDefaultAsync(ct) ?? order.SponsorCompanyId;
                var ext = Path.GetExtension(signedFileName ?? "") is { Length: > 1 } e && signedFile is { Length: > 0 } ? e : ".pdf";
                var stored = await _store.UploadToFolderAsync(folder, $"{order.Number}-{Safe(company)}-signed-offline{ext}", pdf,
                    ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "application/octet-stream", ct);
                order.ContractPath = stored.Path;
                order.ContractItemId = stored.ItemId;
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "§1312 offline-signed contract {Number} could not be archived in SharePoint.", order.Number);
        }

        ShopTotals? totals = null;
        var shopCompany = await _db.ShopCompanies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EventId == order.EventId && c.SponsorCompanyId == order.SponsorCompanyId, ct);
        if (shopCompany is not null) totals = await _orders.PriceAsync(order, shopCompany, ct);
        await _mail.SendSignedAsync(order, totals, pdf, ct);
        return ShopResult.Success($"Order {order.Number} is confirmed as signed offline — the items are ordered and the confirmation is sent.");
    }

    /// <summary>
    /// Flip the company to sponsor. Its SponsorInfo becomes visible (IsSponsor), which is what brings it
    /// into the sponsor lists, mails and the onboarding pipeline.
    /// </summary>
    private async Task MakeSponsorAsync(ShopOrder order, DateTimeOffset now, CancellationToken ct)
    {
        var company = await _db.ShopCompanies
            .FirstOrDefaultAsync(c => c.EventId == order.EventId && c.SponsorCompanyId == order.SponsorCompanyId, ct);
        if (company is not null && company.State != ShopCompanyState.Sponsor)
        {
            company.State = ShopCompanyState.Sponsor;
            company.BecameSponsorAt = now;
        }

        var info = await _db.SponsorInfos.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.EventId == order.EventId && s.SponsorCompanyId == order.SponsorCompanyId, ct);
        if (info is null)
        {
            info = new SponsorInfo
            {
                EventId = order.EventId,
                SponsorCompanyId = order.SponsorCompanyId,
                CompanyName = company?.PublicLabel,
                WebsiteUrl = company?.Website,
                LinkedInUrl = company?.LinkedInUrl,
                TwitterUrl = company?.TwitterUrl,
                CreatedAt = now,
            };
            _db.SponsorInfos.Add(info);
        }
        info.IsSponsor = true;
    }

    /// <summary>
    /// Decode, bound and re-encode a signature picture. Refuses anything that is not a real image, so a
    /// posted payload can never be stored verbatim.
    /// </summary>
    public static byte[] CleanSignature(byte[] input)
    {
        if (input.Length == 0 || input.Length > MaxSignatureBytes)
            throw new InvalidDataException("Empty or too large.");
        using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(input);
        img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(900, 300) }));
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static string Safe(string s) =>
        new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or ' ').ToArray()).Trim().Replace(' ', '-');

    /// <summary>
    /// The product's WHOLE description as plain text with its paragraphs and bullets kept — the contract
    /// is what the sponsor buys, so nothing is cut (operator 2026-09-26: <i>"i am also missing the whole
    /// descriptions of the products in the contract"</i>).
    /// </summary>
    public static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var rx = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<li[^>]*>", "\n• ", rx);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<br\s*/?>|</p>|</div>|</h\d>|</ul>|</ol>|</tr>", "\n", rx);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text).Replace(' ', ' ');
        var lines = text.Replace("\r", "").Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"[ \t]+", " ").Trim())
            .Where(l => l.Length > 0 && l != "•");
        var result = string.Join("\n", lines);
        return result.Length == 0 ? null : result;
    }

    // The contract owner's signature picture, from the contracts folder in SharePoint (the file named in
    // shop.<edition>.json), downscaled once and kept for 30 minutes. Missing ⇒ null, and the PDF writes the name in
    // a handwriting face instead — a contract is never refused for want of a picture.
    private static (string? Name, byte[]? Image, DateTimeOffset At)? s_signature;

    private async Task<byte[]?> OrganizerSignatureAsync(string? fileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var now = _clock.GetUtcNow();
        if (s_signature is { } c && c.Name == fileName && now - c.At < TimeSpan.FromMinutes(30)) return c.Image;
        byte[]? image = null;
        try
        {
            if (_store.CanRead && _paths.TryResolve(DocLibraryPaths.SponsorWebshopContracts, out var folder))
            {
                var file = (await _store.ListAsync(folder, ct))
                    .FirstOrDefault(f => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase));
                var raw = file is null ? null : await _store.DownloadAsync(file.ItemId, ct);
                if (raw is { Length: > 0 })
                {
                    using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(raw);
                    img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(900, 300) }));
                    using var ms = new MemoryStream();
                    img.SaveAsPng(ms);
                    image = ms.ToArray();
                }
                else
                {
                    _log?.LogWarning("§1305 contract: the organizer signature '{File}' was not found in {Folder}; the handwriting fallback is used.", fileName, folder);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "§1305 contract: the organizer signature could not be read; the handwriting fallback is used.");
        }
        s_signature = (fileName, image, now);
        return image;
    }

    // The event logo for the contract cover, from the logo-pack folder in SharePoint (EventLogo-*.png),
    // downscaled once and kept for 30 minutes. A SharePoint outage prints the built-in logo instead.
    private static (byte[]? Logo, DateTimeOffset At)? s_logo;

    private async Task<byte[]?> EventLogoAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (s_logo is { } c && now - c.At < TimeSpan.FromMinutes(30)) return c.Logo;
        byte[]? logo = null;
        try
        {
            if (_store.CanRead && _paths.TryResolve(DocLibraryPaths.EventLogoPack, out var folder))
            {
                var files = await _store.ListAsync(folder, ct);
                var pick = files.Where(f => f.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.Name.StartsWith("EventLogo", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(f => f.Name.Contains("white", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                var raw = pick is null ? null : await _store.DownloadAsync(pick.ItemId, ct);
                if (raw is { Length: > 0 })
                {
                    using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(raw);
                    img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(1000, 1000) }));
                    using var ms = new MemoryStream();
                    img.SaveAsPng(ms);
                    logo = ms.ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "§1305 contract: the event logo could not be read from SharePoint; using the built-in logo.");
        }
        s_logo = (logo, now);
        return logo;
    }

    private static string CountryName(string iso2) =>
        Countries.All.FirstOrDefault(c => string.Equals(c.Code, iso2, StringComparison.OrdinalIgnoreCase)).Name ?? iso2;
}
