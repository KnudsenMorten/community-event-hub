using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>What the public coupon form posts. Everything is nullable: the service decides what is missing.</summary>
public sealed record CouponSignupInput(
    CouponSignupOrderType? OrderType,
    CouponSignupTicketType? TicketType,
    int? TicketCount,
    string? VatNumber,
    string? CompanyName,
    string? Address,
    string? PostalCode,
    string? City,
    string? Country,
    string? RequesterName,
    string? RequesterEmail,
    string? RequesterMobile,
    string? InvoiceEmail,
    string? EanNumber,
    int? SplitPercent,
    string? PurchaseOrder);

/// <summary>The result of a submit: OK + the request id and the proposed promo code, or the problems to show.</summary>
public sealed record CouponSignupOutcome(
    bool Ok,
    IReadOnlyList<string> Problems,
    int? RequestId = null,
    string? CouponCode = null)
{
    public static CouponSignupOutcome Fail(params string[] problems) => new(false, problems);
}

/// <summary>
/// §1382 — the PUBLIC coupon sign-up: validate the request, store it, create the coupon in CEH, find or create the
/// customer in e-conomic (customer group 2), and mail the request to the event mailbox.
/// </summary>
/// <remarks>
/// <para>Operator 2026-11-07: <i>"a public coupon onboarding form with a captcha where a customer signs up for a ticket
/// coupon"</i> — <i>"create the coupon in CEH, create the customer in the ERP if not found (kundegruppe = 2), send a mail
/// with the request to info@expertslive.dk"</i>.</para>
///
/// <para>🔑 <b>What "the coupon in CEH" is.</b> A <see cref="CouponInvoicingSetting"/> row with a PROPOSED promo code,
/// the billing type (prepaid / ad-hoc), the company's share of the ticket and the e-conomic customer. The code itself must
/// still be created in Zoho Backstage by hand — Backstage has no coupon API (§787.14) — and confirmed on
/// /Organizer/CouponInvoicing (§1095). A prepaid request does NOT get a pool here: a pool needs Backstage's ticket-class id
/// and adding tickets to it raises the e-conomic invoice (§990), both of which are the organizer's act.</para>
///
/// <para>🔒 <b>e-conomic: look up first, never a duplicate.</b> The customer is matched by VAT number ONLY
/// (<see cref="IEconomicErpClient.FindCustomerNumberByCvrAsync"/>, §1309 — never by name). Only when none exists is one
/// created, and that create must pass TWO ceilings: <see cref="ExternalSystems.ErpCouponCustomerCreate"/> here (unset ⇒
/// blocked on DEV, allowed on PROD) and <see cref="ExternalSystems.Erp"/> inside the client. A refusal or failure never
/// loses the request — it is stored, the coupon is created without a customer (it then "needs attention" on the
/// organizer page), and the mail says in red why the customer is missing.</para>
/// </remarks>
public sealed class CouponSignupService
{
    /// <summary>The e-conomic customer group coupon customers are created in (operator 2026-11-07: "kundegruppe = 2").</summary>
    public const int ErpCustomerGroup = 2;

    /// <summary>The mailbox the request goes to (the event-actions mailbox every coupon ops notice uses, §1118).</summary>
    public const string RequestMailbox = CouponMailAudience.CopyTo;

    /// <summary>The registered mail key (EmailTemplateCatalog) and its template file.</summary>
    public const string MailKey = "coupon-signup-request";

    /// <summary>The anonymous form's rate limits (per hour).</summary>
    public const int MaxPerIpPerHour = 5;
    public const int MaxPerEmailPerHour = 3;

    public const int MaxTickets = 500;

    private readonly CommunityHubDbContext _db;
    private readonly IEconomicErpClient? _erp;
    private readonly IExternalWriteGuard _writes;
    private readonly IEmailSender _email;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailContextAccessor? _context;
    private readonly TimeProvider _clock;
    private readonly ILogger<CouponSignupService>? _log;

    public CouponSignupService(
        CommunityHubDbContext db,
        IExternalWriteGuard writes,
        IEmailSender email,
        EmailTemplateProvider templates,
        TimeProvider clock,
        IEconomicErpClient? erp = null,
        IEmailContextAccessor? context = null,
        ILogger<CouponSignupService>? log = null)
    {
        _db = db;
        _writes = writes;
        _email = email;
        _templates = templates;
        _clock = clock;
        _erp = erp;
        _context = context;
        _log = log;
    }

    // ------------------------------------------------------------------ validation

    /// <summary>The cleaned values of a valid request.</summary>
    public sealed record Normalized(
        CouponSignupOrderType OrderType, CouponSignupTicketType TicketType, int TicketCount,
        string VatNumber, string CompanyName, string Address, string PostalCode, string City, string CountryIso2,
        string RequesterName, string RequesterEmail, string RequesterMobile,
        string? InvoiceEmail, string? EanNumber, int SplitPercent, string? PurchaseOrder);

    /// <summary>
    /// Validate every field server-side. Returns the problems (empty when valid) and, when valid, the normalised values.
    /// </summary>
    public static (IReadOnlyList<string> Problems, Normalized? Value) Validate(CouponSignupInput i)
    {
        var p = new List<string>();

        if (i.OrderType is not (CouponSignupOrderType.Prepaid or CouponSignupOrderType.AdHoc))
            p.Add("Choose the order type: prepaid or ad-hoc billing.");
        if (i.TicketType is not (CouponSignupTicketType.TwoDay or CouponSignupTicketType.OneDay))
            p.Add("Choose the ticket type: 2-day or 1-day.");
        if (i.TicketCount is not int count || count < 1)
            p.Add("Enter the number of tickets (at least 1).");
        else if (count > MaxTickets)
            p.Add($"The number of tickets can be at most {MaxTickets}. Please contact us for a larger order.");

        var country = (i.Country ?? string.Empty).Trim().ToUpperInvariant();
        if (country.Length != 2 || !Countries.All.Any(c => c.Code == country))
            p.Add("Choose the country.");

        var vat = NormalizeVat(i.VatNumber, country);
        if (string.IsNullOrEmpty(vat))
            p.Add("Enter the VAT number (e.g. DK12345678).");
        else if (!IsValidVat(vat))
            p.Add("The VAT number does not look right. Use the country code and the number, e.g. DK12345678 (a Danish CVR is 8 digits).");

        var company = Clean(i.CompanyName, 200);
        if (company is null) p.Add("Enter the company name.");
        var address = Clean(i.Address, 200);
        if (address is null) p.Add("Enter the address.");
        var postal = Clean(i.PostalCode, 20);
        if (postal is null) p.Add("Enter the postal code.");
        var city = Clean(i.City, 100);
        if (city is null) p.Add("Enter the city.");

        var name = Clean(i.RequesterName, 200);
        if (name is null) p.Add("Enter your full name.");
        var email = Clean(i.RequesterEmail, 320)?.ToLowerInvariant();
        if (email is null) p.Add("Enter your e-mail address.");
        else if (!IsPlausibleEmail(email)) p.Add("Your e-mail address does not look right.");
        var mobile = Clean(i.RequesterMobile, 40);
        if (mobile is null) p.Add("Enter your mobile number.");
        else if (!IsPlausiblePhone(mobile)) p.Add("Your mobile number does not look right (8–15 digits, a leading + is fine).");

        // Invoice details: EXACTLY ONE of invoice e-mail and EAN.
        var invoiceEmail = Clean(i.InvoiceEmail, 320)?.ToLowerInvariant();
        var ean = (i.EanNumber ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).Trim();
        var eanOrNull = ean.Length == 0 ? null : ean;
        if (invoiceEmail is null && eanOrNull is null)
            p.Add("Give the invoice details: an invoice e-mail OR an EAN number.");
        else if (invoiceEmail is not null && eanOrNull is not null)
            p.Add("Give an invoice e-mail OR an EAN number — not both.");
        else if (invoiceEmail is not null && !IsPlausibleEmail(invoiceEmail))
            p.Add("The invoice e-mail address does not look right.");
        else if (eanOrNull is not null && !Regex.IsMatch(eanOrNull, @"^\d{13}$"))
            p.Add("The EAN number must be exactly 13 digits.");

        // Split: the ATTENDEE's share, 0–100 accepted as a number; 100 is refused because the company would then pay
        // nothing and there is nothing for a coupon to do.
        var split = i.SplitPercent ?? 0;
        if (split is < 0 or > 100)
            p.Add("The split payment must be between 0 and 100 %.");
        else if (split == 100)
            p.Add("A split of 100 % means the attendee pays the whole ticket, so no coupon is needed. Use 0–99 %.");

        var po = Clean(i.PurchaseOrder, 200);

        if (p.Count > 0) return (p, null);
        return (p, new Normalized(
            i.OrderType!.Value, i.TicketType!.Value, i.TicketCount!.Value,
            vat!, company!, address!, postal!, city!, country,
            name!, email!, mobile!, invoiceEmail, eanOrNull, split, po));
    }

    /// <summary>
    /// Upper-case, no spaces / dots / dashes. A bare number gets the chosen country's prefix (so "12345678" in Denmark is
    /// "DK12345678"); Greece's VAT prefix is EL. Null when blank.
    /// </summary>
    public static string? NormalizeVat(string? raw, string? countryIso2)
    {
        var s = new string((raw ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (s.Length == 0) return null;
        var hasPrefix = s.Length > 2 && char.IsLetter(s[0]) && char.IsLetter(s[1]);
        if (!hasPrefix && countryIso2 is { Length: 2 } c)
            s = (c.Equals("GR", StringComparison.OrdinalIgnoreCase) ? "EL" : c.ToUpperInvariant()) + s;
        return s;
    }

    /// <summary>A two-letter prefix and 2–13 letters/digits; Danish numbers must be DK + exactly 8 digits.</summary>
    public static bool IsValidVat(string normalized)
    {
        if (!Regex.IsMatch(normalized, @"^[A-Z]{2}[A-Z0-9]{2,13}$")) return false;
        return !normalized.StartsWith("DK", StringComparison.Ordinal) || Regex.IsMatch(normalized, @"^DK\d{8}$");
    }

    public static bool IsPlausibleEmail(string e) =>
        e.Length is > 3 and < 321 && !e.Contains(' ') && e.IndexOf('@') > 0
        && e.IndexOf('@') == e.LastIndexOf('@') && e.LastIndexOf('.') > e.IndexOf('@') + 1 && !e.EndsWith('.');

    public static bool IsPlausiblePhone(string phone)
    {
        if (!Regex.IsMatch(phone, @"^\+?[0-9 ()\-./]+$")) return false;
        var digits = phone.Count(char.IsDigit);
        return digits is >= 8 and <= 15;
    }

    private static string? Clean(string? s, int max)
    {
        var t = (s ?? string.Empty).Trim();
        if (t.Length == 0) return null;
        return t.Length > max ? t[..max] : t;
    }

    // ------------------------------------------------------------------ submit

    /// <summary>
    /// Validate, rate-limit, store, create the coupon, resolve the e-conomic customer and mail the mailbox.
    /// </summary>
    public async Task<CouponSignupOutcome> SubmitAsync(int eventId, CouponSignupInput input, string? ip, CancellationToken ct = default)
    {
        var (problems, v) = Validate(input);
        if (v is null) return new CouponSignupOutcome(false, problems);

        var now = _clock.GetUtcNow();
        var hourAgo = now.AddHours(-1);
        var byIp = string.IsNullOrEmpty(ip) ? 0
            : await _db.CouponSignupRequests.CountAsync(r => r.RequestIp == ip && r.CreatedAt > hourAgo, ct);
        var byEmail = await _db.CouponSignupRequests.CountAsync(r => r.RequesterEmail == v.RequesterEmail && r.CreatedAt > hourAgo, ct);
        if (byIp >= MaxPerIpPerHour || byEmail >= MaxPerEmailPerHour)
            return CouponSignupOutcome.Fail("Too many requests were sent. Please wait an hour and try again, or write to us.");

        var request = new CouponSignupRequest
        {
            EventId = eventId,
            OrderType = v.OrderType,
            TicketType = v.TicketType,
            TicketCount = v.TicketCount,
            VatNumber = v.VatNumber,
            CompanyName = v.CompanyName,
            Address = v.Address,
            PostalCode = v.PostalCode,
            City = v.City,
            CountryIso2 = v.CountryIso2,
            RequesterName = v.RequesterName,
            RequesterEmail = v.RequesterEmail,
            RequesterMobile = v.RequesterMobile,
            InvoiceEmail = v.InvoiceEmail,
            EanNumber = v.EanNumber,
            SplitPercent = v.SplitPercent,
            PurchaseOrder = v.PurchaseOrder,
            RequestIp = ip is { Length: > 64 } ? ip[..64] : ip,
            CreatedAt = now,
        };
        // 🔒 SAVED FIRST: the request is the record. Whatever e-conomic or the mail does next cannot lose it.
        _db.CouponSignupRequests.Add(request);
        await _db.SaveChangesAsync(ct);

        await ResolveErpCustomerAsync(request, ct);

        var coupon = await CreateCouponAsync(request, ct);
        request.CouponInvoicingSettingId = coupon.Id;
        await _db.SaveChangesAsync(ct);

        await SendRequestMailAsync(request, coupon, ct);

        _log?.LogInformation("§1382 coupon sign-up #{Id}: {Company}, {Count} × {Ticket}, {Order}; e-conomic {Customer}{Created}; coupon {Code}.",
            request.Id, request.CompanyName, request.TicketCount, request.TicketType, request.OrderType,
            request.ErpCustomerNumber?.ToString(CultureInfo.InvariantCulture) ?? "none",
            request.ErpCustomerCreated ? " (created)" : string.Empty, coupon.CouponName);
        return new CouponSignupOutcome(true, Array.Empty<string>(), request.Id, coupon.CouponName);
    }

    /// <summary>Find the customer by VAT number; create it in group 2 only when none exists and the guard allows it.</summary>
    private async Task ResolveErpCustomerAsync(CouponSignupRequest r, CancellationToken ct)
    {
        if (_erp is null || !_erp.CanWrite)
        {
            r.ErpError = "e-conomic is not connected on this server — the customer was neither looked up nor created. "
                       + "Find or create it by hand and set it on the coupon.";
            await _db.SaveChangesAsync(ct);
            return;
        }
        try
        {
            // Reads are never gated (§340-H): looking up an existing customer changes nothing outside CEH.
            var existing = await _erp.FindCustomerNumberByCvrAsync(r.VatNumber, ct);
            if (existing is not null)
            {
                r.ErpCustomerNumber = int.TryParse(existing, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
                r.ErpCustomerCreated = false;
                if (r.ErpCustomerNumber is null) r.ErpError = $"e-conomic answered customer \"{existing}\", which is not a number.";
                await _db.SaveChangesAsync(ct);
                return;
            }

            if (!await _writes.AllowAsync(ExternalSystems.ErpCouponCustomerCreate, "CreateCouponCustomer", ct))
            {
                r.ErpError = "No e-conomic customer has this VAT number, and this server may not create one "
                           + "(Integrations:ExternalWrites:ErpCouponCustomerCreate is off). Create it by hand in customer group "
                           + $"{ErpCustomerGroup} and set it on the coupon.";
                await _db.SaveChangesAsync(ct);
                return;
            }

            var zone = VatZoneMapper.EconomicZoneNumber(VatZoneMapper.Resolve(r.CountryIso2));
            var customer = new ErpCustomer(
                CompanyId: $"coupon-signup-{r.Id}",
                Name: r.CompanyName,
                CorporateIdentificationNumber: r.VatNumber,
                Currency: null,
                VatZone: zone?.ToString(CultureInfo.InvariantCulture),
                Email: r.InvoiceEmail,
                ErpCustomerNumber: null,
                Address: r.Address,
                Zip: r.PostalCode,
                City: r.City,
                Country: CountryName(r.CountryIso2),
                CustomerGroupNumber: ErpCustomerGroup,
                Ean: r.EanNumber);
            var number = await _erp.CreateCustomerAsync(customer, ct);
            r.ErpCustomerNumber = int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var created) ? created : null;
            r.ErpCustomerCreated = r.ErpCustomerNumber is not null;
            if (r.ErpCustomerNumber is null) r.ErpError = $"e-conomic created customer \"{number}\", which is not a number.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            r.ErpError = ex.Message.Length > 480 ? ex.Message[..480] : ex.Message;
            _log?.LogWarning(ex, "§1382 e-conomic customer for coupon request #{Id} ({Company}) could not be resolved.", r.Id, r.CompanyName);
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The CEH coupon row: proposed code, billing type, company share, cap (ad-hoc), customer.</summary>
    private async Task<CouponInvoicingSetting> CreateCouponAsync(CouponSignupRequest r, CancellationToken ct)
    {
        var code = ProposedCode(r.CompanyName, r.TicketType, r.Id);
        var name = code;
        for (var n = 2; await _db.CouponInvoicingSettings.AnyAsync(c => c.EventId == r.EventId && c.CouponName == name, ct); n++)
            name = $"{code}-{n}";

        var now = _clock.GetUtcNow();
        var coupon = new CouponInvoicingSetting
        {
            EventId = r.EventId,
            CouponName = name,
            BillingType = r.OrderType == CouponSignupOrderType.Prepaid
                ? CouponBillingType.AllocatedPrepaymentByCustomer
                : CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = r.ErpCustomerNumber,
            RequesterName = r.RequesterName,
            // The coupon stores the COMPANY's share; null means 100 % (every existing coupon's meaning).
            InvoicedSharePercent = r.SplitPercent == 0 ? null : 100 - r.SplitPercent,
            // Ad-hoc: the requested number is the agreed ceiling (in tickets, never kroner — §1094). Prepaid: the number is the
            // pool an organizer creates (it needs the Backstage ticket class and raises the invoice), so no cap here.
            ClaimCapTickets = r.OrderType == CouponSignupOrderType.AdHoc ? r.TicketCount : null,
            Notes = CouponNotes(r),
            CreatedAt = now,
            UpdatedAt = now,
            LastUpdatedByEmail = r.RequesterEmail,
        };
        _db.CouponInvoicingSettings.Add(coupon);
        await _db.SaveChangesAsync(ct);
        return coupon;
    }

    /// <summary>
    /// A promo-code PROPOSAL: up to 10 letters/digits of the company name, the ticket type and the request number —
    /// e.g. CONTOSOAPS-2D-17. Unique by the request number; an organizer may rename it on /Organizer/CouponInvoicing.
    /// </summary>
    public static string ProposedCode(string companyName, CouponSignupTicketType ticket, int requestId)
    {
        var letters = new string(RemoveDiacritics(companyName).Where(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9').ToArray())
            .ToUpperInvariant();
        if (letters.Length == 0) letters = "COUPON";
        if (letters.Length > 10) letters = letters[..10];
        return $"{letters}-{(ticket == CouponSignupTicketType.TwoDay ? "2D" : "1D")}-{requestId.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string RemoveDiacritics(string s)
    {
        var map = s.Replace("Æ", "AE").Replace("æ", "ae").Replace("Ø", "OE").Replace("ø", "oe").Replace("Å", "AA").Replace("å", "aa");
        var formD = map.Normalize(NormalizationForm.FormD);
        return new string(formD.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    private static string CouponNotes(CouponSignupRequest r)
    {
        var text = $"§1382 public sign-up request #{r.Id} ({r.CreatedAt:yyyy-MM-dd}): {r.TicketCount} × {TicketLabel(r.TicketType)}, "
                 + $"{OrderLabel(r.OrderType)}, attendee pays {r.SplitPercent} %. {r.CompanyName} ({r.VatNumber}). "
                 + $"Requester {r.RequesterName} <{r.RequesterEmail}>, {r.RequesterMobile}. "
                 + (r.EanNumber is not null ? $"Invoice by EAN {r.EanNumber}." : $"Invoice to {r.InvoiceEmail}.")
                 + (r.PurchaseOrder is not null ? $" PO: {r.PurchaseOrder}." : string.Empty)
                 + " Create the promo code in Zoho Backstage, then confirm it here.";
        return text.Length > 1000 ? text[..1000] : text;
    }

    public static string TicketLabel(CouponSignupTicketType t) => t == CouponSignupTicketType.TwoDay ? "2-day ticket" : "1-day ticket";
    public static string OrderLabel(CouponSignupOrderType o) => o == CouponSignupOrderType.Prepaid ? "prepaid" : "ad-hoc billing";

    private static string CountryName(string iso2) =>
        Countries.All.FirstOrDefault(c => string.Equals(c.Code, iso2, StringComparison.OrdinalIgnoreCase)).Name ?? iso2;

    // ------------------------------------------------------------------ mail

    /// <summary>
    /// The request to the event mailbox. Ring-exempt (a mailbox, never a participant — the feedback-intake / engine-alert
    /// rule), Reply-To the requester so "Reply" reaches the customer. Best-effort: a failed send leaves the request stored.
    /// </summary>
    private async Task SendRequestMailAsync(CouponSignupRequest r, CouponInvoicingSetting coupon, CancellationToken ct)
    {
        try
        {
            using (_context?.Set(new EmailContext(MailKey, r.EventId, null, null,
                       TemplateName: MailKey, FeatureKey: "outbound-email", RingExempt: true)))
            {
                var t = _templates.NewTokenSet();
                var ev = await _db.Events.AsNoTracking().Where(e => e.Id == r.EventId).Select(e => e.DisplayName).FirstOrDefaultAsync(ct);
                if (!string.IsNullOrWhiteSpace(ev)) t["eventDisplayName"] = ev;
                t["companyName"] = r.CompanyName;
                t["ticketSummary"] = $"{r.TicketCount} × {TicketLabel(r.TicketType)}";
                t["orderType"] = OrderLabel(r.OrderType);
                t["couponCode"] = coupon.CouponName;
                t["requestDetailHtml"] = DetailHtml(r);
                t["erpStatusHtml"] = ErpStatusHtml(r);
                var rendered = _templates.Render(MailKey, t);
                await _email.SendAsync(RequestMailbox, rendered.Subject, rendered.HtmlBody,
                    new EmailReplyTo(r.RequesterEmail, r.RequesterName), ct);
            }
            r.MailSentAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "§1382 coupon sign-up mail for request #{Id} failed (the request is stored).", r.Id);
        }
    }

    private static string DetailHtml(CouponSignupRequest r)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var sb = new StringBuilder("<table style=\"border-collapse:collapse;font-size:14px;\">");
        void Row(string label, string valueHtml) => sb.Append(
            "<tr><td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;color:#6b7280;vertical-align:top;\">")
            .Append(E(label)).Append("</td><td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">")
            .Append(valueHtml).Append("</td></tr>");
        Row("Order type", E(OrderLabel(r.OrderType)));
        Row("Ticket type", E(TicketLabel(r.TicketType)));
        Row("Number of tickets", $"<strong>{r.TicketCount.ToString(CultureInfo.InvariantCulture)}</strong>");
        Row("Split payment", r.SplitPercent == 0
            ? "The company pays 100 % of the ticket"
            : $"The company pays {100 - r.SplitPercent} %, the attendee pays {r.SplitPercent} %");
        Row("Company", E(r.CompanyName));
        Row("VAT number", E(r.VatNumber));
        Row("Address", $"{E(r.Address)}<br>{E(r.PostalCode)} {E(r.City)}<br>{E(CountryName(r.CountryIso2))}");
        Row("Requester", $"{E(r.RequesterName)}<br>{E(r.RequesterEmail)}<br>{E(r.RequesterMobile)}");
        Row("Invoice", r.EanNumber is not null ? $"EAN {E(r.EanNumber)}" : $"By e-mail to {E(r.InvoiceEmail)}");
        Row("Purchase order", r.PurchaseOrder is null ? "<em>none</em>" : E(r.PurchaseOrder));
        Row("Request no.", r.Id.ToString(CultureInfo.InvariantCulture));
        return sb.Append("</table>").ToString();
    }

    private static string ErpStatusHtml(CouponSignupRequest r)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        if (r.ErpCustomerNumber is int n)
        {
            var what = r.ErpCustomerCreated
                ? $"created as customer <strong>{n.ToString(CultureInfo.InvariantCulture)}</strong> in customer group {ErpCustomerGroup}"
                : $"already existed as customer <strong>{n.ToString(CultureInfo.InvariantCulture)}</strong> (matched by VAT number) — its "
                  + "details in e-conomic were NOT changed; check the invoice e-mail / EAN against the request";
            return $"<p style=\"margin:0 0 12px;\">e-conomic: {what}.</p>";
        }
        return "<p style=\"margin:0 0 12px;color:#b91c1c;font-weight:700;\">e-conomic customer NOT resolved: "
               + E(r.ErpError ?? "unknown reason") + "</p>";
    }
}
