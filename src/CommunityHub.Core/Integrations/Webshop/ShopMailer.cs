using System.Net;
using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>
/// §1305 — every mail the CEH sponsor shop sends. Templates live in <c>templates/emails/shop-*.html</c>
/// and are registered in <see cref="EmailTemplateCatalog"/>.
/// </summary>
public sealed class ShopMailer
{
    public const string RegistrationCodeTemplate = "shop-registration-code";
    public const string SignRequestTemplate = "shop-sign-request";
    public const string SignedTemplate = "shop-order-signed";
    public const string OrganizerNoticeTemplate = "shop-organizer-notice";
    public const string ReservationReminderTemplate = "shop-reservation-reminder";
    public const string CartReminderTemplate = "shop-cart-reminder";
    public const string FeatureKey = "sponsor-webshop";

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailSender _email;
    private readonly ShopSettings _settings;
    private readonly IEmailContextAccessor? _context;
    private readonly ILogger<ShopMailer>? _log;
    private readonly IEmailDeliveryOutcome? _outcome;
    private readonly ShopBoothMapPicture? _boothMap;

    public ShopMailer(
        CommunityHubDbContext db, EmailTemplateProvider templates, IEmailSender email,
        ShopSettings settings,
        IEmailContextAccessor? context = null, ILogger<ShopMailer>? log = null, IEmailDeliveryOutcome? outcome = null,
        ShopBoothMapPicture? boothMap = null)
    {
        _boothMap = boothMap;
        _db = db;
        _templates = templates;
        _email = email;
        _settings = settings;
        _context = context;
        _log = log;
        _outcome = outcome;
    }

    /// <summary>
    /// §1312 — the organizer address(es) CC'd on every shop mail to a sponsor / requester (<c>organizer.copyEmails</c>).
    /// Operator 2026-09-27: "add the account … to any webshop emails like signer emails, offer emails, reminders
    /// reservations, cart reminder emails" · "just add to CC". Not on the registration code (a sign-in secret) or on the
    /// organizer notices (they already go to the organizers).
    /// </summary>
    internal IReadOnlyCollection<string>? Copy()
    {
        var list = _settings.Shop().Organizer.CopyEmails
            .Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? null : list;
    }

    /// <summary>
    /// §1312 — the organizers' OWN e-mail domains (organizer, contract owner, notify and copy addresses in the shop config).
    /// Operator 2026-09-27, registering with a test address on the organizers' domain: <i>"this is wrong as it should not
    /// default to 2linkit. why"</i> — test accounts of a sponsor used that domain, so it pointed at that sponsor.
    /// </summary>
    public IReadOnlySet<string> OrganizerDomains()
    {
        if (!_settings.IsConfigured) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var o = _settings.Shop().Organizer;
        return new[] { o.Email, o.ContractOwner.Email }.Concat(o.NotifyEmails).Concat(o.CopyEmails)
            .Select(e => e?.Trim() ?? string.Empty).Where(e => e.Contains('@'))
            .Select(e => e[(e.LastIndexOf('@') + 1)..].ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The hub's public origin (from the mail templates' config).</summary>
    public string HubOrigin => (_templates.NewTokenSet().GetValueOrDefault("hubUrl") ?? string.Empty).TrimEnd('/');

    /// <summary>The one-time code that proves a registrant owns their address.</summary>
    /// <remarks>
    /// 🔒 Ring-exempt, like <c>pin-signin</c>: the registrant is not a participant yet and has no ring, so a
    /// ring gate would drop the one mail that lets them in. The kill switch and the DEV redirect still apply.
    /// </remarks>
    public async Task SendRegistrationCodeAsync(int eventId, string email, string code, int validMinutes, CancellationToken ct)
    {
        using (_context?.Set(new EmailContext(RegistrationCodeTemplate, eventId, null, null,
                   TemplateName: RegistrationCodeTemplate, RingExempt: true)))
        {
            var t = _templates.NewTokenSet();
            // §1312 — greet by name when the address is already known (operator: "verify the webshop mails have a hi with name").
            var known = await KnownNameAsync(eventId, email, ct);
            t["greeting"] = known is null ? "Hi," : $"Hi {FirstName(known)},";
            t["code"] = code;
            t["expiryMinutes"] = validMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var r = _templates.Render(RegistrationCodeTemplate, t);
            await _email.SendAsync(email, r.Subject, r.HtmlBody, ct);
        }
    }

    public const string OfferTemplate = "shop-offer";

    /// <summary>
    /// An OFFER to a potential sponsor: the cart's products and prices, and the draft contract attached
    /// (operator 2026-09-26: <i>"send email with products and prices … with contract with all products per
    /// mail to potential sponsor"</i>). Nothing is reserved. Sent to the address the organizer types, who
    /// is usually not a participant yet — so ring-exempt, like the registration code.
    /// </summary>
    public async Task SendOfferAsync(ShopOrder order, ShopTotals totals, string toEmail, string? note, byte[]? contractPdf, CancellationToken ct, string? toName = null)
    {
        // §1312 — greet by name (operator 2026-09-27: "why is name gone"): the name typed with the offer, else the person
        // this address belongs to in the hub or the company's known people; "Hello," only when nobody is known.
        var name = string.IsNullOrWhiteSpace(toName) ? await KnownNameAsync(order.EventId, toEmail, ct) : toName.Trim();
        static string E(string s) => WebUtility.HtmlEncode(s);
        using (_context?.Set(new EmailContext(OfferTemplate, order.EventId, null, null,
                   TemplateName: OfferTemplate, FeatureKey: FeatureKey, RingExempt: true)))
        {
            var t = _templates.NewTokenSet();
            t["greeting"] = string.IsNullOrWhiteSpace(name) ? "Hello," : $"Hi {FirstName(name)},";
            t["companyName"] = await CompanyNameAsync(order, ct);
            t["itemsHtml"] = ItemsHtml(order, totals);
            // The event's own name — the config fallback is blank, so the subject read "Your sponsor offer from — …".
            var offerEvent = await _db.Events.AsNoTracking().Where(e => e.Id == order.EventId).Select(e => e.DisplayName).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(offerEvent)) t["eventDisplayName"] = offerEvent;
            t["noteHtml"] = string.IsNullOrWhiteSpace(note) ? string.Empty
                : "<p style=\"margin:0 0 16px;white-space:pre-line;\">" + E(note.Trim()) + "</p>";
            // §1312 — two ways forward, both self-service through registration (operator 2026-09-27: "in the offer can you
            // make 2 buttons: reservation request (which i can approve) and sign/order").
            t["actionHtml"] = WithHubUrl(
                ButtonHtml("{{hubUrl}}/sponsor-registration?next=order", "Order and sign")
                + ButtonHtml("{{hubUrl}}/sponsor-registration?next=request", "Request a reservation", primary: false), t);
            // §1312 — the sponsor presentation (operator: "link to this powerpoint with more details about the event and
            // products"); nothing when the edition has none configured.
            var deck = _settings.IsConfigured ? _settings.Shop().Event.PresentationUrl : null;
            t["presentationHtml"] = Uri.TryCreate(deck?.Trim(), UriKind.Absolute, out var deckUri) && deckUri.Scheme is "https" or "http"
                ? $"<p style=\"margin:0 0 16px;\">Want to know more about the event, the audience and every sponsor package? "
                  + $"<a href=\"{E(deckUri.AbsoluteUri)}\">See the sponsor presentation</a>.</p>"
                : string.Empty;
            var pdf = Pdf(contractPdf, $"Event-Sponsor-Contract-{order.Number ?? "offer-" + order.Id}-offer.pdf");
            // §1312 — the offered booth marked on the expo map (operator: "goal is to point out which booth is offered").
            var map = await BoothMapAsync(order, ct);
            if (map is not null) t["itemsHtml"] += map.Value.Html;
            // Operator 2026-09-27: "there is NO pdf file attached" — the mail may only say so when it IS attached.
            t["attachmentHtml"] = pdf is null ? string.Empty
                : "<p style=\"margin:16px 0 16px;\">The draft <strong>Event Sponsor Contract</strong> with every product, price and term is attached as a PDF, so you can read it in full.</p>";
            var r = _templates.Render(OfferTemplate, t);
            var files = (pdf ?? Array.Empty<EmailAttachment>()).Concat(map is null ? Array.Empty<EmailAttachment>() : new[] { map.Value.File }).ToList();
            if (files.Count > 0) await _email.SendWithAttachmentsAsync(toEmail.Trim(), r.Subject, r.HtmlBody, files, Copy(), ct);
            else await _email.SendAsync(toEmail.Trim(), r.Subject, r.HtmlBody, Copy(), ct);
        }
    }

    /// <summary>
    /// The order's items, prices and totals as a mail table — shared by the offer and the signing request (operator
    /// 2026-09-26: "you forgot to include the cart options in the mail").
    /// </summary>
    internal static string ItemsHtml(ShopOrder order, ShopTotals totals)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        static string E(string s) => WebUtility.HtmlEncode(s);
        static string Eur(decimal v) => ShopPricing.Amount(v) + " €";
        var rows = new System.Text.StringBuilder();
        rows.Append("<table role=\"presentation\" cellpadding=\"6\" cellspacing=\"0\" width=\"100%\" style=\"border-collapse:collapse;margin:0 0 12px;font-size:14px;\">");
        foreach (var l in totals.Lines)
            rows.Append("<tr><td style=\"border-bottom:1px solid #e5e7eb;\">").Append(E(l.Name)).Append(l.Quantity > 1 ? $" × {l.Quantity}" : "")
                .Append(l.NormalUnitEur is decimal n ? $" <span style=\"color:#b91c1c;font-weight:700;\">(discount — normal <s>{Eur(n * l.Quantity)}</s>)</span>" : "")
                .Append("</td><td align=\"right\" style=\"border-bottom:1px solid #e5e7eb;white-space:nowrap;\">").Append(Eur(l.LineEur)).Append("</td></tr>");
        void Total(string label, string value, bool bold = false) =>
            rows.Append("<tr><td").Append(bold ? " style=\"font-weight:700;\"" : "").Append('>').Append(E(label))
                .Append("</td><td align=\"right\" style=\"white-space:nowrap;").Append(bold ? "font-weight:700;" : "").Append("\">").Append(value).Append("</td></tr>");
        Total("Subtotal", Eur(totals.SubtotalEur));
        if (totals.IncludedFurnitureEur > 0) Total(totals.IncludedFurnitureLabel ?? "Included booth furniture", "- " + Eur(totals.IncludedFurnitureEur));
        if (totals.SwagCreditEur > 0) Total("Swag credit", "- " + Eur(totals.SwagCreditEur));
        Total(totals.VatRatePercent > 0 ? $"VAT ({totals.VatRatePercent.ToString("0", inv)}%)" : "VAT", Eur(totals.VatEur));
        Total("Total", Eur(totals.TotalEur), bold: true);
        rows.Append("</table>").Append(Gap);
        if (!string.IsNullOrWhiteSpace(order.SpecialTerms) || order.InvoiceNotBefore is not null || order.PaymentDays is not null)
            rows.Append("<p style=\"margin:0 0 12px;\">Special conditions agreed for you are included in the contract under <strong>Agreed Terms</strong>.</p>");
        return rows.ToString();
    }

    /// <summary>
    /// §1312 — one line of air after a table, box or button. Outlook ignores the margins of tables and boxes, so the next
    /// paragraph stuck to them (operator 2026-09-27: "looks cramped - make more room (1 extra line) … chk all webshop mails").
    /// </summary>
    internal const string Gap = "<p style=\"margin:0;font-size:16px;line-height:16px;mso-line-height-rule:exactly;\">&nbsp;</p>";

    /// <summary>
    /// §1312 — a real mail button (Outlook VML + every other client), followed by a <see cref="Gap"/>. A bare link rendered
    /// as a highlighted word in Outlook (operator: "button looks wrong also here"). <paramref name="href"/> may start with
    /// <c>{{hubUrl}}</c>: the mailer fills in the recipient's own sign-in link. The width is fitted to the label at send time
    /// (<see cref="OutlookButtonFit"/>). <paramref name="primary"/> false = the outlined second button.
    /// </summary>
    internal static string ButtonHtml(string href, string label, bool primary = true)
    {
        var h = WebUtility.HtmlEncode(href);
        var t = WebUtility.HtmlEncode(label);
        var (fill, text, border) = primary ? ("#1565c0", "#ffffff", "#1565c0") : ("#ffffff", "#1565c0", "#1565c0");
        return "<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin:0;\"><tr><td align=\"center\">"
            + $"<!--[if mso]><v:roundrect xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:w=\"urn:schemas-microsoft-com:office:word\" href=\"{h}\" "
            + $"style=\"height:52px;v-text-anchor:middle;width:296px;\" arcsize=\"50%\" strokecolor=\"{border}\" fillcolor=\"{fill}\"><w:anchorlock/>"
            + $"<center style=\"color:{text};font-family:Aptos,'Segoe UI',Arial,sans-serif;font-size:16px;font-weight:bold;\">{t}</center></v:roundrect><![endif]-->"
            + $"<!--[if !mso]><!-- --><a href=\"{h}\" style=\"background-color:{fill};border:2px solid {border};border-radius:999px;color:{text};display:inline-block;"
            + "font-family:Aptos,'Segoe UI',Arial,sans-serif;font-size:16px;font-weight:700;line-height:48px;text-align:center;text-decoration:none;"
            + $"padding:0 32px;white-space:nowrap;-webkit-text-size-adjust:none;\">{t}</a><!--<![endif]-->"
            + "</td></tr></table>" + Gap;
    }

    /// <summary>§1312 — "Ask for more time", only when the reservation has a deadline.</summary>
    internal static string ExtensionButtonHtml(ShopOrder order) =>
        order.ReservationDeadline is null ? string.Empty
            : ButtonHtml(ReservationPage(order, "more-time"), "Ask for more time", primary: false);

    /// <summary>
    /// §1312 — "Cancel reservation" (operator 2026-09-27: "also give sponsor option to cancel their reservation (button) from
    /// email"). It OPENS the reservation page, where the cancel is a button press — mail scanners open every link.
    /// </summary>
    internal static string CancelButtonHtml(ShopOrder order) => ButtonHtml(ReservationPage(order, "cancel"), "Cancel reservation", primary: false);

    private static string ReservationPage(ShopOrder order, string anchor) =>
        "{{hubUrl}}/Sponsor/Shop/Reservation/" + order.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "#" + anchor;

    /// <summary>Put the recipient's own (sign-in) hub link into the <c>{{hubUrl}}</c> of code-built *Html tokens.</summary>
    private static string WithHubUrl(string html, IReadOnlyDictionary<string, string> tokens) =>
        html.Contains("{{hubUrl}}", StringComparison.Ordinal)
            ? html.Replace("{{hubUrl}}", WebUtility.HtmlEncode((tokens.GetValueOrDefault("hubUrl") ?? string.Empty).TrimEnd('/')), StringComparison.Ordinal)
            : html;

    public const string OrganizerReservationTemplate = "shop-organizer-reservation";

    /// <summary>
    /// §1308 — the reminder to a reservation's REQUESTER: what is reserved, and the deadline. Operator 2026-09-27: "weekly
    /// email where they will have a deadline date about the reservation. It must show what they reserved."
    /// </summary>
    /// <remarks>
    /// 🔒 Ring-exempt like the offer: the organizer typed this address and chose the cadence for this one reservation,
    /// and the requester is often not a participant (no ring exists for them). The kill switch and the DEV redirect
    /// still apply. Returns false when the transport did not deliver, so the caller does not record it.
    /// </remarks>
    public async Task<bool> SendOrganizerReservationReminderAsync(ShopOrder order, string companyName, ShopTotals? totals, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(order.ReservationRequesterEmail)) return false;
        var eventName = await _db.Events.AsNoTracking().Where(e => e.Id == order.EventId).Select(e => e.DisplayName).FirstOrDefaultAsync(ct);
        // A requester with sign-in access (a contact of this company) gets a button straight to the order — and, being a
        // participant, their hub link signs them in.
        var requesterEmail = order.ReservationRequesterEmail.Trim();
        // A contact of THIS company through any of their company links (a group's shared coordinator's primary company can
        // be another one).
        var access = await _db.SponsorCompanyContacts.AsNoTracking()
            .Where(l => l.EventId == order.EventId && l.SponsorCompanyId == order.SponsorCompanyId
                        && l.Participant.Email == requesterEmail && l.Participant.IsActive && l.Participant.Role == ParticipantRole.Sponsor)
            .Select(l => (int?)l.ParticipantId).FirstOrDefaultAsync(ct);
        using (_context?.Set(new EmailContext(OrganizerReservationTemplate, order.EventId, access, order.ReservationRequesterName,
                   TemplateName: OrganizerReservationTemplate, FeatureKey: FeatureKey, RingExempt: true)))
        {
            var t = _templates.NewTokenSet();
            t["firstName"] = FirstName(order.ReservationRequesterName ?? string.Empty);
            t["companyName"] = companyName;
            t["orderNumber"] = order.Number ?? string.Empty;
            t["itemsHtml"] = totals is null ? string.Empty : ItemsHtml(order, totals);
            // §1312 — the reserved booth marked on the expo map.
            var map = await BoothMapAsync(order, ct);
            if (map is not null) t["itemsHtml"] += map.Value.Html;
            t["deadlineHtml"] = DeadlineHtml(order);
            t["deadlineSubject"] = DeadlineSubject(order);
            // Self-service (operator 2026-09-27: "we dont support them replying back to me so i manually have to do something.
            // it is selfservice we are building. they can reply back if they have issues"): one real button into the portal —
            // the requester's own sign-in link when they are a contact, else the sign-in page — and "Ask for more time".
            t["actionHtml"] = WithHubUrl(
                ButtonHtml(access is null
                    ? $"{HubOrigin}/Login?email={Uri.EscapeDataString(requesterEmail)}&ReturnUrl=%2FSponsor%2FShop%2FCart"
                    : "{{hubUrl}}/Sponsor/Shop/Cart", "Review and sign")
                + ExtensionButtonHtml(order) + CancelButtonHtml(order), t);
            if (!string.IsNullOrWhiteSpace(eventName)) t["eventDisplayName"] = eventName;
            var r = _templates.Render(OrganizerReservationTemplate, t);
            if (map is not null) await _email.SendWithAttachmentsAsync(requesterEmail, r.Subject, r.HtmlBody, new[] { map.Value.File }, Copy(), ct);
            else await _email.SendAsync(requesterEmail, r.Subject, r.HtmlBody, Copy(), ct);
        }
        return _outcome is null || _outcome.LastSendDelivered;
    }

    /// <summary>§1312 — the order's booth(s) on the expo map, with the line that says so; null when there is no booth or map.</summary>
    private async Task<(EmailAttachment File, string Html)?> BoothMapAsync(ShopOrder order, CancellationToken ct)
    {
        if (_boothMap is null) return null;
        var file = await _boothMap.ForOrderAsync(order.EventId, order.Id, ct);
        if (file is null) return null;
        var booths = await _boothMap.BoothIdsAsync(order.Id, ct);
        var label = booths.Count == 1 ? $"Booth {booths[0]} is" : $"Booths {string.Join(", ", booths)} are";
        return (file, $"<p style=\"margin:0 0 16px;\">{WebUtility.HtmlEncode(label)} marked on the expo map attached to this e-mail.</p>");
    }

    public const string CancelledTemplate = "shop-order-cancelled";

    /// <summary>
    /// §1312 — a reservation or order was CANCELLED (by the sponsor or the organizers): its signer, coordinator and
    /// reservation requester are told, and the organizers through the CC (operator 2026-09-27: "sponsor + me must get an
    /// email when an reservation is cancelled. sponsor + me must get an email when order is cancelled"). Ring-exempt: it is
    /// about their own order. Best-effort per person.
    /// </summary>
    public async Task SendCancelledAsync(ShopOrder order, bool byOrganizers, string? reason, ShopTotals? totals, CancellationToken ct)
    {
        var company = await CompanyNameAsync(order, ct);
        var eventName = await _db.Events.AsNoTracking().Where(e => e.Id == order.EventId).Select(e => e.DisplayName).FirstOrDefaultAsync(ct);
        var ids = new[] { order.SignerParticipantId, order.CoordinatorParticipantId, order.ReservationRequestedByParticipantId }
            .Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        var people = await _db.Participants.AsNoTracking().Where(p => ids.Contains(p.Id) && p.IsActive)
            .Select(p => new { p.Id, p.Email, p.FullName }).ToListAsync(ct);
        // A requester who is not (yet) a hub contact is still told.
        var requester = order.ReservationRequesterEmail?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(requester) && !people.Any(p => string.Equals(p.Email, requester, StringComparison.OrdinalIgnoreCase)))
            people.Add(new { Id = 0, Email = requester, FullName = order.ReservationRequesterName ?? string.Empty });

        var what = order.IsOrganizerReservation && order.SignedAt is null ? "reservation" : "order";
        foreach (var p in people.DistinctBy(p => p.Email.ToLowerInvariant()))
        {
            try
            {
                using (_context?.Set(new EmailContext(CancelledTemplate, order.EventId, p.Id == 0 ? null : p.Id, p.FullName,
                           TemplateName: CancelledTemplate, FeatureKey: FeatureKey, RingExempt: true)))
                {
                    var t = _templates.NewTokenSet(p.Id == 0 ? null : p.Id);
                    t["firstName"] = FirstName(p.FullName);
                    t["companyName"] = company;
                    t["orderNumber"] = order.Number ?? string.Empty;
                    t["whatLabel"] = what;
                    t["byLabel"] = byOrganizers ? "by the organizers" : "as you asked";
                    t["itemsHtml"] = totals is null
                        ? "<ul style=\"margin:0 0 16px;\">" + string.Concat(order.Lines.Select(l => $"<li>{WebUtility.HtmlEncode(l.ProductName)}{(l.Quantity > 1 ? $" × {l.Quantity}" : "")}</li>")) + "</ul>"
                        : ItemsHtml(order, totals);
                    t["reasonHtml"] = string.IsNullOrWhiteSpace(reason) || !byOrganizers ? string.Empty
                        : $"<p style=\"margin:0 0 16px;\"><strong>From the organizers:</strong> {WebUtility.HtmlEncode(reason.Trim())}</p>";
                    t["actionHtml"] = WithHubUrl(ButtonHtml("{{hubUrl}}/Sponsor/Shop", "Open the sponsor shop"), t);
                    if (!string.IsNullOrWhiteSpace(eventName)) t["eventDisplayName"] = eventName;
                    var r = _templates.Render(CancelledTemplate, t);
                    await _email.SendAsync(p.Email, r.Subject, r.HtmlBody, Copy(), ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.LogWarning(ex, "§1312 cancellation mail for {Number} to a contact failed.", order.Number);
            }
        }
    }

    public const string ReservationAnswerTemplate = "shop-reservation-answer";

    /// <summary>
    /// §1312 — the organizers' answer when they DECLINE a reservation request or a request for more time, to the person who
    /// asked. Ring-exempt: it answers the one person who just asked. Returns false when the transport did not deliver.
    /// </summary>
    public async Task<bool> SendReservationAnswerAsync(
        ShopOrder order, Participant to, string headline, string messageHtml, string? note, CancellationToken ct)
    {
        var company = await CompanyNameAsync(order, ct);
        using (_context?.Set(new EmailContext(ReservationAnswerTemplate, order.EventId, to.Id, to.FullName,
                   TemplateName: ReservationAnswerTemplate, FeatureKey: FeatureKey, RingExempt: true)))
        {
            var t = _templates.NewTokenSet(to.Id);
            t["firstName"] = FirstName(to.FullName);
            t["companyName"] = company;
            t["headline"] = headline;
            t["messageHtml"] = messageHtml;
            t["noteHtml"] = string.IsNullOrWhiteSpace(note) ? string.Empty
                : "<p style=\"margin:0 0 16px;white-space:pre-line;\"><strong>From the organizers:</strong> " + WebUtility.HtmlEncode(note.Trim()) + "</p>";
            t["actionHtml"] = WithHubUrl(ButtonHtml("{{hubUrl}}/Sponsor/Shop/Cart", "Open your order"), t);
            var r = _templates.Render(ReservationAnswerTemplate, t);
            await _email.SendAsync(to.Email, r.Subject, r.HtmlBody, Copy(), ct);
        }
        return _outcome is null || _outcome.LastSendDelivered;
    }

    /// <summary>
    /// §1312 — the signing DEADLINE, marked clearly, or nothing. Operator 2026-09-27: "it is important that any deadlines
    /// for this is marked clearly so they dont forget to sign before the date and understand that products will be
    /// released. they can contact me at mok@expertslive.dk if they need extension. only mention if deadline is defined on
    /// the reservation". More time is asked for with the "Ask for more time" button below the box (operator: "add a extension
    /// request button in the reminder mail where they select a date and give reason - and it is sent to me for approval").
    /// </summary>
    internal static string DeadlineHtml(ShopOrder order)
    {
        if (order.ReservationDeadline is not DateOnly deadline) return string.Empty;
        var day = deadline.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        return "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:0;\"><tr>"
               + "<td style=\"background-color:#fef2f2;border-left:4px solid #b91c1c;padding:14px 18px;border-radius:4px;\">"
               + $"<p style=\"margin:0 0 6px;color:#b91c1c;font-weight:700;\">Deadline: please sign by {day}.</p>"
               + $"<p style=\"margin:0;\">The items are reserved for you until then. If the contract is not signed by {day}, the reservation "
               + "ends and the products are released to other sponsors. Need more time? Use <strong>Ask for more time</strong> below "
               + "to request a later date.</p>"
               + "</td></tr></table>" + Gap;
    }

    /// <summary>" — sign by 15 October 2026" for a subject line, or empty when there is no deadline.</summary>
    internal static string DeadlineSubject(ShopOrder order) =>
        order.ReservationDeadline is DateOnly d
            ? " — sign by " + d.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>The file name of an order's contract PDF.</summary>
    public static string ContractFileName(ShopOrder order, bool signed) =>
        $"Event-Sponsor-Contract-{order.Number ?? "draft-" + order.Id}-{(signed ? "signed" : "for-signature")}.pdf";

    /// <summary>
    /// The signer's magic link to read, download and sign the contract — WITH the contract attached, because it is a
    /// legal document the signer must be able to read and keep whatever happens to the link.
    /// </summary>
    public async Task<bool> SendSignRequestAsync(ShopOrder order, ShopTotals? totals, byte[]? contractPdf, CancellationToken ct)
    {
        if (order.SignerParticipantId is not int signerId) return false;
        var signer = await _db.Participants.AsNoTracking().FirstOrDefaultAsync(p => p.Id == signerId, ct);
        if (signer is null) return false;
        try
        {
            var company = await CompanyNameAsync(order, ct);
            // The event's own name — the config fallback is blank on DEV, so the mail read "for is ready".
            var eventName = await _db.Events.AsNoTracking().Where(e => e.Id == order.EventId)
                .Select(e => e.DisplayName).FirstOrDefaultAsync(ct);
            var tokens = new Dictionary<string, string>
            {
                ["firstName"] = FirstName(signer.FullName),
                ["companyName"] = company,
                ["orderNumber"] = order.Number ?? string.Empty,
                ["orderId"] = order.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["itemsHtml"] = totals is null ? string.Empty : ItemsHtml(order, totals),
                // Operator 2026-09-27: "where is the pdf file" — the mail says it is attached only when it is.
                ["attachmentHtml"] = contractPdf is { Length: > 0 }
                    ? "<p style=\"margin:0 0 16px;\">The contract is attached as a PDF, so you can read it and keep a copy.</p>"
                    : string.Empty,
                ["deadlineHtml"] = DeadlineHtml(order),
                ["deadlineSubject"] = DeadlineSubject(order),
                ["extensionButtonHtml"] = ExtensionButtonHtml(order),
            };
            if (!string.IsNullOrWhiteSpace(eventName)) tokens["eventDisplayName"] = eventName;
            await SendAsync(signer, SignRequestTemplate, tokens, order.EventId, ct, Pdf(contractPdf, ContractFileName(order, signed: false)));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "§1305 sign request for order {Number} could not be sent.", order.Number);
            return false;
        }
    }

    /// <summary>After signing: the signer and coordinator get a link to the signed contract; organizers get a notice.</summary>
    public async Task SendSignedAsync(ShopOrder order, ShopTotals? totals, byte[]? pdf, CancellationToken ct)
    {
        var company = await CompanyNameAsync(order, ct);
        // Any signed order — the first sponsorship or a later furniture order — so the mail names the order and its items
        // and never says "welcome on board" (operator 2026-09-26).
        var eventName = await _db.Events.AsNoTracking().Where(e => e.Id == order.EventId)
            .Select(e => e.DisplayName).FirstOrDefaultAsync(ct);
        var itemsHtml = totals is null ? string.Empty : ItemsHtml(order, totals);
        var ids = new[] { order.SignerParticipantId, order.CoordinatorParticipantId, order.SignedByParticipantId }
            .Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        var people = await _db.Participants.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        foreach (var p in people)
        {
            try
            {
                var tokens = new Dictionary<string, string>
                {
                    ["firstName"] = FirstName(p.FullName),
                    ["companyName"] = company,
                    ["orderNumber"] = order.Number ?? string.Empty,
                    ["signedBy"] = order.SignedByName ?? string.Empty,
                    ["orderId"] = order.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["itemsHtml"] = itemsHtml,
                    // §1312 — "attached" only when it is (the signed PDF could not be rendered ⇒ point to the hub instead).
                    ["attachmentHtml"] = pdf is { Length: > 0 }
                        ? "<p style=\"margin:0 0 16px;\">The signed contract is attached to this e-mail — please keep it with your records.</p>"
                        : "<p style=\"margin:0 0 16px;\">You can open and download the signed contract in the sponsor portal with the button below.</p>",
                };
                if (!string.IsNullOrWhiteSpace(eventName)) tokens["eventDisplayName"] = eventName;
                await SendAsync(p, SignedTemplate, tokens, order.EventId, ct, Pdf(pdf, ContractFileName(order, signed: true)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.LogWarning(ex, "§1305 signed-order mail to {Pid} failed.", p.Id);
            }
        }

        await NotifyOrganizersAsync(order.EventId,
            $"Contract signed: {company} ({order.Number})",
            $"<p><b>{WebUtility.HtmlEncode(company)}</b> signed order <b>{WebUtility.HtmlEncode(order.Number ?? "")}</b>"
            + $" ({WebUtility.HtmlEncode(order.SignedByName ?? "")}). The signed contract is attached.</p>"
            + $"<p><a href=\"{HubOrigin}/Organizer/ShopOrders?order={order.Id}\">Open the order</a></p>", ct,
            Pdf(pdf, ContractFileName(order, signed: true)),
            extraTo: _settings.Shop().Organizer.ContractEmails);   // §1312 — signed contracts also to e.g. the info mailbox
    }

    /// <summary>
    /// Tell the organizers that products just sold out — whether the last unit went through this shop or
    /// through the webshop (operator 2026-09-26: <i>"i must also get email when a product stock has run
    /// out"</i>). Called once per transition in-stock → sold-out, so a product that stays at zero is not
    /// reported again every sync.
    /// </summary>
    /// <remarks>
    /// §1312 — <paramref name="reservation"/>: the last unit went to a RESERVATION, not a sale (operator 2026-09-28: he made a
    /// reservation and got "Sold out … by order ELDK27-0007"). It then reads "Reserved — no longer available", says for whom,
    /// and that it comes back if the reservation is cancelled.
    /// </remarks>
    public async Task NotifySoldOutAsync(int eventId, IReadOnlyCollection<ShopProduct> products, string where, CancellationToken ct,
        bool reservation = false)
    {
        if (products.Count == 0) return;
        var items = string.Concat(products.OrderBy(p => p.Name).Select(p =>
            $"<li><b>{WebUtility.HtmlEncode(p.Name)}</b>{(p.WooProductId is long w ? $" (webshop product no. {w})" : "")}"
            + (p.PromotedSlot is int slot ? $" — <b>it was in the commercial corner (place {slot})</b>" : "") + "</li>"));
        var promoted = products.Any(p => p.PromotedSlot is not null);
        var headline = reservation
            ? (products.Count == 1 ? $"Reserved — no longer available: {products.First().Name}" : $"{products.Count} products reserved — no longer available")
            : (products.Count == 1 ? $"Sold out: {products.First().Name}" : $"{products.Count} products sold out");
        await NotifyOrganizersAsync(eventId, headline,
            (reservation
                ? $"<p>Held {WebUtility.HtmlEncode(where)} — <b>not signed yet</b>. It is available again if the reservation is "
                  + "cancelled or expires unsigned.</p>"
                : $"<p>Sold out {WebUtility.HtmlEncode(where)}:</p>")
            + $"<ul>{items}</ul>"
            // Operator 2026-09-26: "info when one is sold so i can add a new one".
            + (promoted ? $"<p>A sold-out product leaves the commercial corner by itself. "
                          + $"<a href=\"{HubOrigin}/Organizer/ShopPromotions\">Choose a new product for the corner</a>.</p>" : "")
            + $"<p><a href=\"{HubOrigin}/Organizer/WebshopCatalog\">Open the shop catalogue</a></p>", ct);
    }

    private static IReadOnlyCollection<EmailAttachment>? Pdf(byte[]? pdf, string name) =>
        pdf is { Length: > 0 } ? new[] { new EmailAttachment(name, pdf, "application/pdf") } : null;

    /// <summary>A short notice to the organizers listed in <c>shop.&lt;edition&gt;.json → organizer.notifyEmails</c>.</summary>
    public async Task NotifyOrganizersAsync(
        int eventId, string headline, string htmlDetail, CancellationToken ct, IReadOnlyCollection<EmailAttachment>? attachments = null,
        IEnumerable<string>? extraTo = null)
    {
        foreach (var to in _settings.Shop().Organizer.NotifyEmails.Concat(extraTo ?? Array.Empty<string>())
                     .Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using (_context?.Set(new EmailContext(OrganizerNoticeTemplate, eventId, null, null,
                           TemplateName: OrganizerNoticeTemplate, FeatureKey: FeatureKey, RingExempt: true)))
                {
                    var t = _templates.NewTokenSet();
                    // Plain text: the renderer encodes it. `detailHtml` is RAW by its name (the renderer's
                    // *Html convention) — without the suffix the markup arrived as visible tags.
                    t["headline"] = headline;
                    t["detailHtml"] = htmlDetail;
                    var r = _templates.Render(OrganizerNoticeTemplate, t);
                    if (attachments is { Count: > 0 })
                        await _email.SendWithAttachmentsAsync(to.Trim(), r.Subject, r.HtmlBody, attachments, ct);
                    else
                        await _email.SendAsync(to.Trim(), r.Subject, r.HtmlBody, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.LogWarning(ex, "§1305 organizer notice to {To} failed.", to);
            }
        }
    }

    /// <summary>
    /// §1305 item 10 — the weekly reservation reminders, as reminder-engine messages (the engine sends
    /// each at most once per ISO week per person).
    /// </summary>
    public async Task<IReadOnlyList<ReminderMessage>> BuildReservationRemindersAsync(
        int eventId, DateOnly today, CancellationToken ct)
    {
        var window = _settings.Shop().Reservation;
        if (!window.IsOpen(today)) return Array.Empty<ReminderMessage>();

        var open = await _db.ShopEntitlements.AsNoTracking()
            .Where(e => e.EventId == eventId && e.ClaimedOrderLineId == null && e.DeclinedAt == null)
            .ToListAsync(ct);
        if (open.Count == 0) return Array.Empty<ReminderMessage>();

        var week = System.Globalization.ISOWeek.GetWeekOfYear(today.ToDateTime(TimeOnly.MinValue));
        var year = System.Globalization.ISOWeek.GetYear(today.ToDateTime(TimeOnly.MinValue));
        var result = new List<ReminderMessage>();

        foreach (var byCompany in open.GroupBy(e => e.SponsorCompanyId))
        {
            // Operator: "Both signer and default event coordinator must receive a weekly reminder".
            var people = await DefaultSignerAndCoordinatorAsync(eventId, byCompany.Key, ct);

            var items = string.Join(string.Empty, byCompany.Select(e =>
                $"<li>{WebUtility.HtmlEncode(e.ProductName)}{(e.MatchedProductId is null ? " <i>(the organizers will match it)</i>" : string.Empty)}</li>"));

            foreach (var p in people)
            {
                var t = _templates.NewTokenSet(p.Id);
                t["firstName"] = FirstName(p.FullName);
                t["companyName"] = byCompany.First().CompanyName;
                t["itemsHtml"] = items;
                t["closesOn"] = window.ClosesOnDate?.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                var r = _templates.Render(ReservationReminderTemplate, t);
                result.Add(new ReminderMessage(
                    RecipientEmail: p.Email,
                    ReminderType: "shop-reservation",
                    OccasionKey: $"shop-reservation:{byCompany.Key}:{p.Id}:{year}w{week}",
                    Subject: r.Subject,
                    HtmlBody: r.HtmlBody,
                    ParticipantId: p.Id,
                    RecipientName: p.FullName,
                    Cc: Copy(),
                    FeatureKey: FeatureKey,
                    MailKey: ReservationReminderTemplate));
            }
        }
        return result;
    }

    /// <summary>
    /// §1305 item 7c — the DAILY reminder about an unsigned order that still has items. Operator: "send a
    /// daily reminder to the default signer + default event coordinator until it is emptied … or
    /// confirmed/signed. an empty cart stops the flow". The engine sends each at most once per day per person.
    /// </summary>
    public async Task<IReadOnlyList<ReminderMessage>> BuildCartRemindersAsync(
        int eventId, DateOnly today, CancellationToken ct)
    {
        var pending = await _db.ShopOrders.AsNoTracking()
            .Where(o => o.EventId == eventId
                        // §1312 — a contract waiting for its signer is reminded with the SIGNING mail itself
                        // (ShopSigningService.SendSignRemindersAsync), not with this generic one.
                        && (o.Status == ShopOrderStatus.Cart || o.Status == ShopOrderStatus.Reserved)
                        && o.Lines.Any()
                        // §1308 — an organizer reservation has its OWN reminder (requester, cadence, deadline); the daily
                        // cart reminder must not ALSO chase it.
                        && o.ReservationRequesterEmail == null
                        // §1312 — a cart whose reservation request waits for the organizers is waiting on US: no chasing.
                        && o.ReservationRequestedAt == null)
            .Include(o => o.Lines)
            .ToListAsync(ct);
        if (pending.Count == 0) return Array.Empty<ReminderMessage>();

        var result = new List<ReminderMessage>();
        foreach (var byCompany in pending.GroupBy(o => o.SponsorCompanyId))
        {
            var company = await _db.ShopCompanies.AsNoTracking()
                .Where(c => c.EventId == eventId && c.SponsorCompanyId == byCompany.Key)
                .Select(c => c.LegalName).FirstOrDefaultAsync(ct) ?? byCompany.Key;
            var items = string.Join(string.Empty, byCompany.SelectMany(o => o.Lines).Select(l =>
                $"<li>{WebUtility.HtmlEncode(l.ProductName)}{(l.Quantity > 1 ? $" × {l.Quantity}" : string.Empty)}</li>"));

            foreach (var p in await DefaultSignerAndCoordinatorAsync(eventId, byCompany.Key, ct))
            {
                var t = _templates.NewTokenSet(p.Id);
                t["firstName"] = FirstName(p.FullName);
                t["companyName"] = company;
                t["itemsHtml"] = items;
                var r = _templates.Render(CartReminderTemplate, t);
                result.Add(new ReminderMessage(
                    RecipientEmail: p.Email,
                    ReminderType: "shop-cart",
                    OccasionKey: $"shop-cart:{byCompany.Key}:{p.Id}:{today:yyyy-MM-dd}",
                    Subject: r.Subject,
                    HtmlBody: r.HtmlBody,
                    ParticipantId: p.Id,
                    RecipientName: p.FullName,
                    Cc: Copy(),
                    FeatureKey: FeatureKey,
                    MailKey: CartReminderTemplate));
            }
        }
        return result;
    }

    /// <summary>The company's default signer(s) and default event coordinator(s), active only.</summary>
    private async Task<IReadOnlyList<Participant>> DefaultSignerAndCoordinatorAsync(int eventId, string companyId, CancellationToken ct)
    {
        var links = await _db.SponsorCompanyContacts.AsNoTracking()
            .Where(l => l.EventId == eventId && l.SponsorCompanyId == companyId && (l.IsSigner || l.IsEventCoordinator))
            .Select(l => l.ParticipantId).ToListAsync(ct);
        var people = await _db.Participants.AsNoTracking()
            .Where(p => p.EventId == eventId && p.IsActive
                        && (links.Contains(p.Id) || (p.SponsorCompanyId == companyId && (p.IsSigner || p.IsEventCoordinator))))
            .ToListAsync(ct);
        return people.DistinctBy(p => p.Id).ToList();
    }

    private async Task SendAsync(
        Participant to, string template, Dictionary<string, string> extra, int eventId, CancellationToken ct,
        IReadOnlyCollection<EmailAttachment>? attachments = null)
    {
        using (_context?.Set(new EmailContext(template, eventId, to.Id, to.FullName, TemplateName: template, FeatureKey: FeatureKey)))
        {
            var t = _templates.NewTokenSet(to.Id);
            // Plain values: the renderer HTML-encodes every non-*Html token itself. Encoding here as well turned
            // "Café" into "Caf&amp;eacute;" — double-encoded.
            foreach (var kv in extra) t[kv.Key] = kv.Key.EndsWith("Html", StringComparison.Ordinal) ? WithHubUrl(kv.Value, t) : kv.Value;
            var r = _templates.Render(template, t);
            if (attachments is { Count: > 0 })
                await _email.SendWithAttachmentsAsync(to.Email, r.Subject, r.HtmlBody, attachments, Copy(), ct);
            else
                await _email.SendAsync(to.Email, r.Subject, r.HtmlBody, Copy(), ct);
        }
    }

    /// <summary>The full name behind an address: a hub participant, else a known person of a company (never the bare address).</summary>
    private async Task<string?> KnownNameAsync(int eventId, string email, CancellationToken ct)
    {
        var e = email.Trim().ToLowerInvariant();
        var name = await _db.Participants.AsNoTracking().Where(p => p.EventId == eventId && p.Email == e)
                       .Select(p => p.FullName).FirstOrDefaultAsync(ct)
                   ?? await _db.ShopKnownContacts.AsNoTracking().Where(k => k.EventId == eventId && k.Email == e)
                       .Select(k => k.FullName).FirstOrDefaultAsync(ct);
        // A known contact saved from an offer carries only the address's local part as its name — not a real name.
        return string.IsNullOrWhiteSpace(name) || string.Equals(name, e, StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, e.Split('@')[0], StringComparison.OrdinalIgnoreCase)
            ? null : name.Trim();
    }

    private async Task<string> CompanyNameAsync(ShopOrder order, CancellationToken ct) =>
        await _db.ShopCompanies.AsNoTracking()
            .Where(c => c.EventId == order.EventId && c.SponsorCompanyId == order.SponsorCompanyId)
            .Select(c => c.LegalName)
            .FirstOrDefaultAsync(ct) ?? order.SponsorCompanyId;

    /// <summary>"Jimmy" from "Jimmy Wind"; "there" when no real name is known (blank, or the e-mail address stored as the name).</summary>
    private static string FirstName(string fullName)
    {
        var first = (fullName ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) || first.Contains('@') ? "there" : first;
    }
}
