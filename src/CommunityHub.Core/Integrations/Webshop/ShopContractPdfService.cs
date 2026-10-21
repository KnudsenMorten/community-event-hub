using System.Globalization;
using CommunityHub.Core.Config;
using CommunityHub.Core.Evaluation;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>Everything one contract prints. Built by <see cref="ShopSigningService"/>.</summary>
public sealed record ShopContractData(
    ShopConfig Config,
    string OrderNumber,
    DateTimeOffset GeneratedAt,
    string SponsorLegalName,
    string? SponsorPublicName,
    string? SponsorCvr,
    IReadOnlyList<string> SponsorAddress,
    (string Name, string Email, string Phone) Signer,
    (string Name, string Email, string Phone) Coordinator,
    string? InvoiceEmail,
    string? PurchaseOrderRef,
    string InvoiceCurrency,
    string? Website,
    string? LinkedIn,
    string? Twitter,
    IReadOnlyList<(string Name, string? Description, int Quantity, decimal LineEur, decimal SurchargePercent)> Lines,
    ShopTotals Totals,
    string? SpecialTerms,
    // Signatures: null ⇒ the contract is printed unsigned (the "Build Contract (offline PDF)" button).
    byte[]? SponsorSignaturePng,
    DateTimeOffset? SponsorSignedAt,
    string? SponsorSignedByName,
    DateTimeOffset? OrganizerSignedAt,
    // Billing terms the organizer agreed on this order (§1305 item 6) — printed with the special terms.
    DateOnly? InvoiceNotBefore = null,
    int? PaymentDays = null,
    // The event logo for the cover (from SharePoint); null ⇒ the built-in logo.
    byte[]? EventLogo = null,
    // The contract owner's own signature picture (from SharePoint); null ⇒ their name in a handwriting face.
    byte[]? OrganizerSignature = null)
{
    /// <summary>
    /// The "Agreed Terms" section: the organizer's special conditions plus any special billing terms, or
    /// null when nothing special was agreed — the section is then left out of the contract altogether
    /// (operator 2026-09-26: <i>"only show if any special terms were agreed upon"</i>).
    /// </summary>
    public string? AgreedTerms
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(SpecialTerms)) parts.Add(SpecialTerms.Trim());
            if (InvoiceNotBefore is DateOnly d)
                parts.Add($"Invoicing: the invoice is dated {d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}.");
            if (PaymentDays is int days)
                parts.Add($"Payment terms: {days.ToString(CultureInfo.InvariantCulture)} days net.");
            return parts.Count == 0 ? null : string.Join("\n\n", parts);
        }
    }
}

/// <summary>
/// §1305 item 7 — renders the Event Sponsor Contract as a PDF.
/// </summary>
/// <remarks>
/// <para>Same content and order as the contract the retired WordPress signing plugin produced (sample of
/// 2026-09-24) — cover, event, organizer, sponsor company, signer, event coordinator, invoice, web &amp;
/// SoMe, products, agreed terms, payment, terms &amp; conditions, agreement + signatures — WITHOUT its
/// audit-trail pages and per-page audit footer, as the operator asked (<i>"avoid all the audit
/// details"</i>).</para>
/// <para>🔒 All numbers are printed with the INVARIANT culture: the server runs Danish, which would print
/// "1.234,50" into an English contract.</para>
/// <para>📄 <b>Layout gates</b> (operator 2026-09-26: <i>"the pages must separate nicely in the pdf … build
/// gates into the pdf build so it looks nice"</i>): headings keep with what follows, labelled fields and
/// short paragraphs never split, the totals stay together, long boxes and descriptions continue on the
/// next page instead of running off it, and every page says "Page N of M". <see cref="RenderChecked"/>
/// measures the result — content past a margin, a heading stranded at a page foot, a page holding only a
/// stray line — and the tests fail the build on any of them.</para>
/// </remarks>
public sealed class ShopContractPdfService
{
    private const double Left = 50;
    private const double Top = 50;
    private const double Bottom = 60;

    public byte[] Render(ShopContractData d) => RenderChecked(d).Pdf;

    /// <summary>Render and report layout problems (empty when the contract lays out cleanly).</summary>
    public (byte[] Pdf, int Pages, IReadOnlyList<string> LayoutIssues) RenderChecked(ShopContractData d)
    {
        EmbeddedFontResolver.EnsureInstalled();
        var doc = new PdfDocument();
        doc.Info.Title = $"Event Sponsor Contract - {d.SponsorLegalName}";
        doc.Info.Subject = d.Config.Event.Name;

        var w = new Writer(doc, $"Event Sponsor Contract - {d.Config.Event.Name} - {d.SponsorLegalName} ({d.OrderNumber})");
        var inv = CultureInfo.InvariantCulture;
        static string Eur(decimal v) => ShopPricing.Amount(v) + "€";

        // ---- Cover -------------------------------------------------------------------------
        w.NewPage(cover: true);
        w.Space(180);
        w.Text("Event Sponsor Contract", w.Title);
        w.Space(10);
        w.Text(d.Config.Event.Name, w.H1);
        w.Space(10);
        w.Text(d.SponsorLegalName, w.H1Bold);
        var logo = d.EventLogo ?? EmbeddedFontResolver.LogoBytes();
        if (logo is not null) { w.Space(24); w.Image(logo, d.EventLogo is null ? 150 : 260, maxHeight: 260); }
        w.Space(20);
        w.Text($"Order {d.OrderNumber} · generated {d.GeneratedAt.UtcDateTime.ToString("dd/MM/yyyy", inv)}", w.Small);

        // ---- Event + organizer -------------------------------------------------------------
        w.NewPage();
        w.Heading("Event Information");
        w.Field("Event Name (Event Short Name)", d.Config.Event.Name);
        w.Field("Event Website", d.Config.Event.Website);
        w.Field("Event Dates", string.Join("\n", d.Config.Event.DatesText));
        w.Field("Event Venue", string.Join("\n", d.Config.Event.Venue));
        w.Space(8);
        w.Heading("Event Organizer Information");
        w.Field("Event Organizer (Address, Company CVR, Mail)",
            string.Join("\n", new[] { d.Config.Organizer.Name }.Concat(d.Config.Organizer.Address)
                .Concat(new[] { string.Empty, d.Config.Organizer.Cvr, d.Config.Organizer.Email })));
        var owner = d.Config.Organizer.ContractOwner;
        w.Field($"{d.Config.Organizer.Name} Contract Owner", string.Join("\n", new[] { owner.Name, owner.Email, owner.Phone }.Where(s => !string.IsNullOrWhiteSpace(s))));

        // ---- Sponsor -----------------------------------------------------------------------
        w.NewPage();
        w.Heading("Sponsor Company Information");
        w.Field("Company CVR/Reg/TAX Number", d.SponsorCvr ?? "");
        w.Field("Company Name (legal name)", d.SponsorLegalName);
        w.Field("Company Address, Zip Code, City, Country", string.Join("\n", d.SponsorAddress));
        w.Space(8);
        w.Heading("Sponsor Contract Contact Details");
        w.Field("Full Name / E-Mail / Phone (Signer)", $"{d.Signer.Name}\n{d.Signer.Email}\n{d.Signer.Phone}".TrimEnd());
        w.Heading("Sponsor Event Coordination Contact Details");
        w.Field("Full Name / E-Mail / Phone (Event Coordinator)", $"{d.Coordinator.Name}\n{d.Coordinator.Email}\n{d.Coordinator.Phone}".TrimEnd());
        w.Heading("Sponsor Invoice Information");
        w.Field("Sponsor Invoice Mail Address", d.InvoiceEmail ?? "");
        w.Field("Sponsor PO / Reference Number / Name (Invoice)", d.PurchaseOrderRef ?? "");
        w.Field("Invoice Currency", d.InvoiceCurrency);
        w.Space(8);
        w.Heading("Sponsor Web & Social Media Information");
        w.Field("Company Name that should be used in announcement", d.SponsorPublicName ?? d.SponsorLegalName);
        w.Field("Web Address URL", d.Website ?? "");
        w.Field("LinkedIn Name / URL", d.LinkedIn ?? "");
        w.Field("X/Twitter Name", d.Twitter ?? "");

        // ---- Products ----------------------------------------------------------------------
        w.NewPage();
        w.Heading("Event Sponsor Packages, Add-on, Options");
        w.TableRow("Product", "Price (EUR)", bold: true, keepWithNext: 60);
        foreach (var l in d.Lines)
        {
            var name = l.Quantity > 1 ? $"{l.Name} x {l.Quantity}" : l.Name;
            if (l.SurchargePercent > 0) name += $" (incl. {l.SurchargePercent.ToString("0", inv)}% late-order surcharge)";
            w.TableRow(name, Eur(l.LineEur), bold: false, description: l.Description);
        }
        // The totals block never splits.
        var totals = new List<(string, string)> { ("Subtotal", Eur(d.Totals.SubtotalEur)) };
        if (d.Totals.IncludedFurnitureEur > 0)
            totals.Add((d.Totals.IncludedFurnitureLabel ?? "Included booth furniture", "- " + Eur(d.Totals.IncludedFurnitureEur)));
        if (d.Totals.SwagCreditEur > 0)
            totals.Add(("Swag credit", "- " + Eur(d.Totals.SwagCreditEur)));
        totals.Add((d.Totals.VatRatePercent > 0 ? $"Tax ({d.Totals.VatRatePercent.ToString("0", inv)}% Danish VAT)" : "Tax", Eur(d.Totals.VatEur)));
        totals.Add(("Total", Eur(d.Totals.TotalEur)));
        w.EnsureRoom(totals.Count * 22 + 30);
        foreach (var (label, value) in totals) w.TableRow(label, value, bold: true);
        w.Space(6);
        w.Paragraph($"NOTE: {d.Config.Payment.CurrencyNote}", w.Small);

        // ---- Agreed terms (only when something special was agreed) + payment ---------------
        if (d.AgreedTerms is { } agreed)
        {
            w.Space(10);
            w.Heading("Agreed Terms (Contract)", keepWith: w.MeasureBox(agreed));
            w.Box(agreed);
        }
        w.Space(10);
        var payment = "Details for Payment of Contract using Bank Transfer\n\n" + string.Join("\n", d.Config.Payment.Lines());
        w.Heading("Event Sponsorship Payment Details", keepWith: w.MeasureBox(payment));
        w.Box(payment);

        // ---- Terms & conditions ------------------------------------------------------------
        w.NewPage();
        w.Text("Event Sponsorship Terms and Conditions", w.H1Bold, center: true);
        w.Space(10);
        foreach (var a in d.Config.ContractTerms)
        {
            w.Heading(a.Heading);
            foreach (var p in a.Paragraphs)
            {
                if (p == "{{paymentBlock}}")
                {
                    var block = string.Join("\n", d.Config.Payment.Lines()) + "\n"
                        + string.Join(", ", new[] { d.Config.Organizer.Name }.Concat(d.Config.Organizer.Address)) + $" · {d.Config.Organizer.Cvr}";
                    w.Paragraph(block, w.Body);
                    continue;
                }
                w.Paragraph(p.Replace("{{sponsorLegalName}}", d.SponsorLegalName), w.Body);
            }
        }

        // ---- Agreement + signatures: always together ---------------------------------------
        w.EnsureRoom(w.MeasureParagraph(d.Config.AgreementText, w.Body) + 60 + Writer.SignatureHeight);
        w.Heading("Agreement");
        w.Paragraph(d.Config.AgreementText, w.Body);
        w.Space(10);
        w.Signatures(
            d.SponsorSignedByName ?? d.Signer.Name, d.SponsorSignedAt, d.SponsorSignaturePng,
            // §1305 — the organizer signs EVERY contract by default (operator 2026-09-26: "add a signture of mine in
            // the contract by default"): a draft or offer already carries the contract owner's handwritten signature,
            // dated when it was generated; a signed contract is dated with the sponsor's signing.
            owner.Name, d.OrganizerSignedAt ?? d.GeneratedAt, d.Config.Organizer.Name, d.OrganizerSignature);

        w.Finish();
        var pages = doc.PageCount;          // read before Save: a saved document refuses every access
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return (ms.ToArray(), pages, w.Issues);
    }

    /// <summary>A tiny flowing-text writer over PDFsharp: word wrap, page breaks, a header line, layout checks.</summary>
    private sealed class Writer
    {
        public const double SignatureHeight = 180;

        private readonly PdfDocument _doc;
        private readonly string _header;
        private PdfPage _page = null!;
        private XGraphics _g = null!;
        private double _y;
        private double _width;
        private bool _cover;
        private string? _lastHeading;      // a heading with nothing under it yet
        private double _pageContentTop;

        public readonly XFont Title, H1, H1Bold, H2, Body, BodyBold, Label, Small;
        private readonly XBrush _ink = new XSolidBrush(XColor.FromArgb(26, 26, 46));
        private readonly XBrush _muted = new XSolidBrush(XColor.FromArgb(107, 114, 128));
        private readonly XPen _line = new(XColor.FromArgb(60, 60, 60), 0.7);
        private readonly XPen _hair = new(XColor.FromArgb(200, 200, 200), 0.5);

        public List<string> Issues { get; } = new();

        public Writer(PdfDocument doc, string header)
        {
            _doc = doc;
            _header = header;
            var f = EmbeddedFontResolver.FamilyName;
            Title = new XFont(f, 30, XFontStyleEx.Bold);
            H1 = new XFont(f, 18, XFontStyleEx.Regular);
            H1Bold = new XFont(f, 18, XFontStyleEx.Bold);
            H2 = new XFont(f, 11.5, XFontStyleEx.Bold);
            Body = new XFont(f, 9.5, XFontStyleEx.Regular);
            BodyBold = new XFont(f, 9.5, XFontStyleEx.Bold);
            Label = new XFont(f, 7.5, XFontStyleEx.Regular);
            Small = new XFont(f, 8, XFontStyleEx.Regular);
        }

        private double PageBottom => _page.Height.Point - Bottom;
        private double Usable => _page.Height.Point - Bottom - Top;

        public void NewPage(bool cover = false)
        {
            if (_page is not null) { ClosePage(); _g.Dispose(); }
            _page = _doc.AddPage();
            _page.Size = PdfSharp.PageSize.A4;
            _g = XGraphics.FromPdfPage(_page);
            _width = _page.Width.Point - Left * 2;
            _y = Top;
            _pageContentTop = Top;
            _cover = cover;
            if (!cover) _g.DrawString(_header, Small, _muted, new XPoint(Left, 30));
        }

        private void ClosePage()
        {
            // Gate: a heading must never be the last thing on a page.
            if (_lastHeading is not null)
                Issues.Add($"Page {_doc.PageCount}: heading '{_lastHeading}' is stranded at the foot of the page.");
            _lastHeading = null;
        }

        /// <summary>Called after drawing: records content, and flags anything past the bottom margin.</summary>
        private void Drawn(bool isHeading = false, string? heading = null)
        {
            if (_y > PageBottom + 0.5)
                Issues.Add($"Page {_doc.PageCount}: content runs {(_y - PageBottom):0} pt past the bottom margin.");
            _lastHeading = isHeading ? heading : null;
        }

        /// <summary>Page numbers ("Page N of M") and the final page checks.</summary>
        public void Finish()
        {
            ClosePage();
            var total = _doc.PageCount;
            for (var i = 0; i < total; i++)
            {
                var page = _doc.Pages[i];
                if (i == 0) continue;   // the cover carries no header or number
                var g = ReferenceEquals(page, _page) ? _g : XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var s = $"Page {i + 1} of {total}";
                var sw = g.MeasureString(s, Small).Width;
                g.DrawString(s, Small, _muted, new XPoint(Left + _width - sw, page.Height.Point - 30));
            }
            // A last page holding only a stray line or two looks broken.
            if (!_cover && _y - _pageContentTop < 30)
                Issues.Add($"Page {total}: holds only {(_y - _pageContentTop):0} pt of content.");
        }

        public void EnsureRoom(double h)
        {
            // A block taller than a page cannot be kept together; it flows instead.
            if (h > Usable) h = Usable;
            if (_y + h > PageBottom) NewPage();
        }

        public void Space(double h) => _y = Math.Min(_y + h, PageBottom);

        public void Text(string s, XFont f, bool center = false)
        {
            EnsureRoom(f.Size * 1.4);
            var size = _g.MeasureString(s, f);
            var x = center ? Left + (_width - size.Width) / 2 : Left;
            _g.DrawString(s, f, _ink, new XPoint(x, _y + f.Size));
            _y += f.Size * 1.4;
            Drawn();
        }

        /// <summary>A section heading, kept with at least the first few lines of what follows.</summary>
        public void Heading(string s, double keepWith = 60)
        {
            EnsureRoom(6 + H2.Size * 1.8 + Math.Min(keepWith, Usable - 80));
            _y += 6;
            _g.DrawString(s, H2, _ink, new XPoint(Left, _y + H2.Size));
            _y += H2.Size * 1.8;
            Drawn(isHeading: true, heading: s);
        }

        public double MeasureParagraph(string text, XFont f) => Wrap(text, f, _width).Count * f.Size * 1.45 + 6;

        /// <summary>
        /// Flowing text. A paragraph of up to six lines is kept whole; a longer one may break, but never
        /// leaves a single line behind at a page foot or alone at the top of the next page.
        /// </summary>
        public void Paragraph(string text, XFont f, double gapAfter = 6, double indent = 0)
        {
            var lines = Wrap(text, f, _width - indent);
            var lh = f.Size * 1.45;
            if (lines.Count <= 6) EnsureRoom(lines.Count * lh);
            else EnsureRoom(2 * lh);
            for (var i = 0; i < lines.Count; i++)
            {
                var left = lines.Count - i;
                if (_y + lh > PageBottom || (left == 2 && _y + 2 * lh > PageBottom && i > 0)) NewPage();
                _g.DrawString(lines[i], f, _ink, new XPoint(Left + indent, _y + f.Size));
                _y += lh;
                Drawn();
            }
            _y += gapAfter;
        }

        /// <summary>A labelled box, like the sample's form fields. Never split.</summary>
        public void Field(string label, string value)
        {
            var lines = Wrap(string.IsNullOrWhiteSpace(value) ? " " : value, Body, _width - 16);
            var h = 8 + Label.Size * 1.4 + lines.Count * Body.Size * 1.45 + 6;
            EnsureRoom(h);
            _g.DrawRectangle(_line, Left, _y, _width, h);
            var y = _y + 6 + Label.Size;
            _g.DrawString(label, Label, _ink, new XPoint(Left + 8, y));
            y += Label.Size * 0.6;
            foreach (var l in lines)
            {
                y += Body.Size * 1.45;
                _g.DrawString(l, Body, _ink, new XPoint(Left + 8, y));
            }
            _y += h;
            Drawn();
        }

        /// <summary>How much of a box must fit before it starts: all of it, or — if it can never fit on one
        /// page — its first three lines (it then flows).</summary>
        private double BoxKeep(int lines)
        {
            var whole = 10 + lines * Body.Size * 1.45;
            return whole > Usable - 80 ? 10 + 3 * Body.Size * 1.45 : whole;
        }

        public double MeasureBox(string text) => BoxKeep(Wrap(text, Body, _width - 16).Count);

        /// <summary>A framed text box. Kept whole when it fits on a page; otherwise it continues framed on the next.</summary>
        public void Box(string text)
        {
            var lines = Wrap(text, Body, _width - 16);
            var lh = Body.Size * 1.45;
            EnsureRoom(BoxKeep(lines.Count));
            var top = _y;
            _y += 4;
            foreach (var l in lines)
            {
                if (_y + lh + 6 > PageBottom)
                {
                    _g.DrawRectangle(_line, Left, top, _width, _y + 4 - top);
                    NewPage();
                    top = _y;
                    _y += 4;
                }
                _g.DrawString(l, Body, _ink, new XPoint(Left + 8, _y + Body.Size)); _lastHeading = null;
                _y += lh;
            }
            _y += 6;
            _g.DrawRectangle(_line, Left, top, _width, _y - top);
            Drawn();
        }

        public void TableRow(string left, string right, bool bold, string? description = null, double keepWithNext = 0)
        {
            var f = bold ? BodyBold : Body;
            var nameLines = Wrap(left, f, _width - 110);
            var descLines = string.IsNullOrWhiteSpace(description) ? new List<string>() : Wrap(description!, Small, _width - 110);
            // The name + price part stays together; the FULL product description (a contract is a legal
            // document — operator 2026-09-26) flows line by line and may continue on the next page, but a
            // short description (≤ 8 lines) stays with its product.
            var head = 8 + nameLines.Count * f.Size * 1.45;
            var descH = descLines.Count * Small.Size * 1.4 + (descLines.Count > 0 ? 4 : 0);
            EnsureRoom(head + keepWithNext + (descLines.Count <= 8 ? descH : 3 * Small.Size * 1.4));
            var top = _y;
            var y = _y + 2;
            foreach (var l in nameLines)
            {
                y += f.Size * 1.45;
                _g.DrawString(l, f, _ink, new XPoint(Left + 8, y));
            }
            var rw = _g.MeasureString(right, f).Width;
            _g.DrawString(right, f, _ink, new XPoint(Left + _width - 8 - rw, _y + 2 + f.Size * 1.45));
            _y += head;
            _lastHeading = null;
            foreach (var l in descLines)
            {
                var lh = Small.Size * 1.4;
                if (_y + lh > PageBottom)
                {
                    Frame(top, _y);
                    NewPage();
                    top = _y;
                }
                _g.DrawString(l, Small, _muted, new XPoint(Left + 8, _y + Small.Size)); _lastHeading = null;
                _y += lh;
            }
            if (descLines.Count > 0) _y += 4;
            Frame(top, _y);
            Drawn();
        }

        private void Frame(double top, double bottom)
        {
            _g.DrawRectangle(_hair, Left, top, _width, bottom - top);
            _g.DrawLine(_hair, Left + _width - 100, top, Left + _width - 100, bottom);
        }

        public void Image(byte[] png, double width, double maxHeight = double.MaxValue)
        {
            using var ms = new MemoryStream(png);
            var img = XImage.FromStream(ms);
            var h = width * img.PixelHeight / Math.Max(1, img.PixelWidth);
            if (h > maxHeight) { width *= maxHeight / h; h = maxHeight; }
            EnsureRoom(h);
            _g.DrawImage(img, Left, _y, width, h);
            _y += h;
            Drawn();
        }

        public void Signatures(
            string sponsorName, DateTimeOffset? sponsorAt, byte[]? sponsorPng,
            string organizerName, DateTimeOffset? organizerAt, string organizerCompany, byte[]? organizerPng = null)
        {
            var half = (_width - 20) / 2;
            EnsureRoom(SignatureHeight - 10);
            var inv = CultureInfo.InvariantCulture;
            void Column(double x, string name, DateTimeOffset? at, byte[]? png, string? company, bool handwritten = false)
            {
                _g.DrawRectangle(_hair, x, _y, half, 22);
                _g.DrawString(name, Body, _ink, new XPoint(x + 8, _y + 15));
                _g.DrawRectangle(_hair, x, _y + 22, half, 22);
                _g.DrawString(at?.UtcDateTime.ToString("dd/MM/yyyy", inv) ?? " ", Body, _ink, new XPoint(x + 8, _y + 37));
                var sigTop = _y + 60;
                if (png is not null)
                {
                    using var ms = new MemoryStream(png);
                    var img = XImage.FromStream(ms);
                    var sw = Math.Min(half - 20, 200.0);
                    var sh = Math.Min(60.0, sw * img.PixelHeight / Math.Max(1, img.PixelWidth));
                    _g.DrawImage(img, x + 10, sigTop, sw, sh);
                }
                else if (handwritten && at is not null)
                {
                    // §1305 — the FALLBACK when the contract owner's signature picture cannot be read: their name in
                    // a handwriting face, so the organizer column is never left unsigned.
                    var size = 34.0;
                    var font = new XFont(EmbeddedFontResolver.SignatureFamilyName, size, XFontStyleEx.Regular);
                    while (_g.MeasureString(name, font).Width > half - 20 && size > 16)
                        font = new XFont(EmbeddedFontResolver.SignatureFamilyName, size -= 2, XFontStyleEx.Regular);
                    _g.DrawString(name, font, _ink, new XPoint(x + 10, sigTop + 48));
                }
                _g.DrawLine(_hair, x, sigTop + 64, x + half, sigTop + 64);
                var by = at is null ? "Not signed" : $"Signed By {name}" + (company is null ? "" : $" [{company}]");
                _g.DrawString(by, Small, _ink, new XPoint(x, sigTop + 78));
                if (at is not null)
                    _g.DrawString($"Signed On: {at.Value.UtcDateTime.ToString("dd/MM/yyyy HH:mm 'UTC'", inv)}", Small, _ink, new XPoint(x, sigTop + 90));
            }
            Column(Left, sponsorName, sponsorAt, sponsorPng, null);
            Column(Left + half + 20, organizerName, organizerAt, organizerPng, organizerCompany, handwritten: true);
            _y += SignatureHeight - 10;
            Drawn();
        }

        private List<string> Wrap(string text, XFont f, double maxWidth)
        {
            var result = new List<string>();
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var words = raw.Split(' ');
                var line = string.Empty;
                foreach (var word in words)
                {
                    var candidate = line.Length == 0 ? word : line + " " + word;
                    if (_g.MeasureString(candidate, f).Width <= maxWidth || line.Length == 0)
                    {
                        line = candidate;
                    }
                    else
                    {
                        result.Add(line);
                        line = word;
                    }
                }
                result.Add(line);
            }
            return result;
        }
    }
}
