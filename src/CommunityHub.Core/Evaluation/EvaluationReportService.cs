using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §743 C7 — the per-session PDF report.
/// </summary>
/// <remarks>
/// <para>Rendered with <b>PdfSharpCore</b> (MIT), chosen over QuestPDF for its licence. PdfPig was
/// already referenced but is a READER — it extracts text from uploaded documents and cannot produce
/// one; the two now pair usefully, since the tests generate with PdfSharpCore and read back with
/// PdfPig to assert what a reader actually sees.</para>
///
/// <para>🔒 <b>Presentation follows Part 2 §11, which is specification and not decoration.</b> The
/// score is labelled an INDEX and never carries a <c>%</c> sign — an all-light-green session scores
/// 66.7, which reads as a failing grade as a percentage. The band always accompanies the number.
/// The response count is never shown without the score. A below-threshold session prints
/// "not enough responses yet" rather than a figure volatile enough to mislead.</para>
///
/// <para>🔒 <b>The report is generated, never stored as authoritative.</b> Every figure is derived
/// from the raw responses at render time, so a recomputation after late data simply produces a new
/// version (§8).</para>
/// </remarks>
public sealed class EvaluationReportService
{
    /// <summary>
    /// §750.2 — one slice of the response timeline: how many presses arrived in this interval.
    /// </summary>
    /// <param name="From">The interval's start (inclusive).</param>
    /// <param name="Count">Responses whose COLLECTION time fell in it.</param>
    public sealed record TimelineBucket(DateTimeOffset From, int Count);

    /// <summary>Everything one report needs. Assembled by the caller so this stays a pure renderer.</summary>
    /// <param name="Timeline">
    /// §750.2 — the brief's <i>"response timeline across the collection window"</i>. Optional so the
    /// renderer degrades to the pre-§750.2 layout rather than throwing if a caller has not supplied
    /// one; an empty or absent timeline simply omits the chart.
    /// </param>
    public sealed record ReportData(
        string EventName,
        string SessionTitle,
        string? Room,
        string? TrackName,
        DateTimeOffset ScheduledStart,
        DateTimeOffset ScheduledEnd,
        SatisfactionScore.Result Score,
        SatisfactionScore.Result EventPooled,
        IReadOnlyList<string> Comments,
        DateTimeOffset GeneratedAt,
        IReadOnlyList<TimelineBucket>? Timeline = null,
        DateTimeOffset? WindowOpensAt = null,
        DateTimeOffset? WindowClosesAt = null,
        int QrResponses = 0,
        int DeviceResponses = 0);

    /// <summary>
    /// §752.4 — why the wordmark was omitted on the last render, or null when it drew fine.
    /// </summary>
    /// <remarks>
    /// 🔑 Exists because the failure is otherwise undetectable: the report still renders, still
    /// passes its tests, and merely loses its branding. A test asserts this is null.
    /// </remarks>
    public string? LastLogoError { get; private set; }

    // -------------------------------------------------------------------------------------------
    // §752.6 — THE VERTICAL RHYTHM (operator 2026-08-01: *"more spacing in pdf"* · *"looks cramped"*
    // · *"extra line after each paragraph"*).
    //
    // 🔑 Named, not scattered. The gaps were previously two dozen literals (16, 17, 22, 24, 26, 28…)
    // with no relationship to each other, so "add a bit more space" meant editing every one and
    // guessing — which is exactly how a layout ends up looking cramped in some places and loose in
    // others. Three constants now carry the whole document, so the rhythm can be tuned in one place
    // and stays consistent everywhere.
    // -------------------------------------------------------------------------------------------

    /// <summary>Between two sections — a full blank line, which is what "extra line after each
    /// paragraph" asks for.</summary>
    private const double Section = 30;

    /// <summary>Between a section heading and its first row of content.</summary>
    private const double Heading = 21;

    /// <summary>Between rows inside a block (table rows, wrapped comment lines).</summary>
    private const double Row = 14;

    /// <summary>Render the report. Returns the PDF bytes.</summary>
    /// <remarks>
    /// <para>§750.8 — laid out to be SCREENSHOTTED (operator: <i>"people brag on linkedin and cut
    /// paste result so it must look fantastic"</i>). The wordmark, the score and its band sit in the
    /// top third, so a crop still carries the event's identity and the figure's meaning together.</para>
    ///
    /// <para>🔑 <b>Multi-page when the feedback needs it</b> (operator 2026-08-01: <i>"report can be
    /// multi page depending on open feedback comments"</i>). Comments are never truncated to protect
    /// the layout — the layout gives way instead. A busy session simply produces a longer document,
    /// and every page carries the footer so a loose sheet is still attributable.</para>
    /// </remarks>
    public byte[] Render(ReportData d)
    {
        ArgumentNullException.ThrowIfNull(d);

        // 🔴 §750.7 — MUST come before any XFont is constructed. PdfSharpCore otherwise asks the OS
        // for a font family, and an Azure Linux container has none: this threw
        // "No Fonts installed on this device!" on PROD for every session.
        EmbeddedFontResolver.EnsureInstalled();

        var doc = new PdfDocument();
        doc.Info.Title = $"Session evaluation — {d.SessionTitle}";
        doc.Info.Subject = d.EventName;

        var page = doc.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        var gfx = XGraphics.FromPdfPage(page);

        var F = EmbeddedFontResolver.FamilyName;
        var hero  = new XFont(F, 54, XFontStyleEx.Bold);
        var h1    = new XFont(F, 17, XFontStyleEx.Bold);
        var h2    = new XFont(F, 10.5, XFontStyleEx.Bold);
        var body  = new XFont(F, 9.5, XFontStyleEx.Regular);
        var bodyB = new XFont(F, 9.5, XFontStyleEx.Bold);
        var small = new XFont(F, 8, XFontStyleEx.Regular);

        const double left = 46;
        var width = page.Width - (left * 2);
        var y = 40.0;

        var inkB   = new XSolidBrush(EvaluationReportLayout.Ink);
        var mutedB = new XSolidBrush(EvaluationReportLayout.Muted);
        var brandB = new XSolidBrush(EvaluationReportLayout.BrandDark);

        // 🔒 §750.8 — ALL numbers formatted invariantly. The render host is Danish, so the current
        // culture printed "75,0" and "83,3" into a document written in English and meant to be
        // shared internationally. Worse, it made the output depend on WHICH machine rendered it.
        // 🔑 §750.8 (operator 2026-08-01: "ignore comma. round up") — WHOLE numbers on the page.
        // A score of 75 reads instantly; 75,0 carries a decimal nobody acts on and, on a Danish
        // host, a comma that looks like a typo in an English document. Rounded away from zero so
        // 74.5 shows as 75 rather than the banker's-rounding 74 that .NET would otherwise pick.
        static string Num(double v) =>
            Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

        static string Whole(double v) =>
            Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

        void Text(string s, XFont f, XBrush b, double x, double yy, double w, XStringFormat? fmt = null)
            => gfx.DrawString(s, f, b, new XRect(x, yy, w, f.Height + 4), fmt ?? XStringFormats.TopLeft);

        void Footer()
        {
            gfx.DrawLine(new XPen(EvaluationReportLayout.Hairline, 1),
                left, page.Height - 52, left + width, page.Height - 52);
            // 🔑 §752.7 (operator: *"move footer info about linear into how scoring was made"*) — the
            // weight profile and the not-a-percentage note moved into "How this score works", where a
            // reader who wants them is already looking. They were in the footer because the brief
            // requires the profile to travel with any published score (§4.4: without it, 75 is
            // ambiguous between the two models and cannot be recomputed) — that duty is met by having
            // it in the DOCUMENT, not by having it in the footer. The footer keeps only the stamp.
            gfx.DrawString(
                $"Generated {d.GeneratedAt:dd MMM yyyy HH:mm} UTC",
                small, mutedB, new XRect(left, page.Height - 44, width, 14), XStringFormats.TopLeft);
        }

        // 🔴 §752.6 — START A NEW PAGE if <paramref name="needed"/> points will not fit.
        //
        // This did not exist: the ONLY page break in the document was inside the comments loop, so
        // every other block simply drew past the bottom of the sheet if it happened not to fit.
        // Nothing caught it because the report still rendered, still passed, and still opened —
        // the content was just *outside the page*, which no assertion on extracted text can see.
        // Widening the spacing is what pushed the "How this score works" panel over the edge and
        // exposed it; the layout was one section away from this bug the whole time.
        //
        // 🔑 Returns true when it broke, so a caller can reprint a "(continued)" heading.
        bool EnsureRoom(double needed)
        {
            if (y + needed <= page.Height - 70) return false;
            Footer();
            gfx.Dispose();
            page = doc.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            gfx = XGraphics.FromPdfPage(page);
            y = 46;
            return true;
        }

        // ---- masthead: the real logo, so a screenshot carries the event with it ------------------
        //
        // 🔴 §752.5 — the XImage is held until AFTER doc.Save(), NOT disposed here. It was a
        // `using var` and the logo silently vanished from every report: PDFsharp reads the image
        // bytes lazily when the document is written, so disposing at the end of this block left it
        // with nothing to embed. The only outward sign was the PDF dropping ~14 KB — the exact size
        // of the PNG — because the swallowing catch below hid the failure completely.
        XImage? logoImage = null;
        try
        {
            var logo = EmbeddedFontResolver.LogoBytes();
            if (logo is not null)
            {
                logoImage = XImage.FromStream(new MemoryStream(logo));
                const double lw = 150.0;
                gfx.DrawImage(logoImage, left, y, lw, lw * logoImage.PixelHeight / logoImage.PixelWidth);
            }
        }
        catch (Exception ex)
        {
            // 🔒 A missing logo must never cost a speaker their report — but it must not be INVISIBLE
            // either. This catch was empty and cost an hour of "why is the logo gone".
            LastLogoError = ex.Message;
            logoImage = null;
        }

        if (logoImage is null)
        {
            // 🔑 §752.5 — LAST-RESORT fallback only. Since the move to official PDFsharp the PNG
            // decodes, so this should never run; it stays because a speaker's report must not be lost
            // over a decorative asset, and `LastLogoError` above records why it was needed.
            var markA = new XFont(F, 19, XFontStyleEx.Bold);
            var markB = new XFont(F, 19, XFontStyleEx.Regular);
            const string one = "Experts";
            const string two = " Live ";
            const string three = "Denmark";
            var w1 = gfx.MeasureString(one, markA).Width;
            var w2 = gfx.MeasureString(two, markB).Width;
            gfx.DrawString(one, markA, new XSolidBrush(EvaluationReportLayout.BrandDark),
                new XRect(left, y, 300, 26), XStringFormats.TopLeft);
            gfx.DrawString(two, markB, new XSolidBrush(EvaluationReportLayout.BrandBlue),
                new XRect(left + w1, y, 300, 26), XStringFormats.TopLeft);
            gfx.DrawString(three, markA, new XSolidBrush(EvaluationReportLayout.BrandBlue),
                new XRect(left + w1 + w2, y, 300, 26), XStringFormats.TopLeft);
        }

        // 🔑 §752.4 (operator: "logo and event name") — BOTH, always. The wordmark is left-aligned
        // and this is right-aligned, so they sit at opposite ends of the same line.
        //
        // ⚠️ I briefly suppressed this because extracted text read "Experts Live DenmarkExperts Live
        // Denmark 2027" and I took it for a layout collision. It was not: PdfPig concatenates runs in
        // draw order with no notion of horizontal position, so two items at opposite margins come out
        // adjacent. **Text extraction is evidence about CONTENT, never about layout.**
        Text(d.EventName, small, mutedB, left, y + 10, width, XStringFormats.TopRight);
        y += 42;
        gfx.DrawLine(new XPen(EvaluationReportLayout.BrandDark, 2), left, y, left + width, y);
        y += Section;

        // ---- session ----------------------------------------------------------------------------
        Text(d.SessionTitle, h1, brandB, left, y, width);
        y += 25;
        var when  = $"{d.ScheduledStart:ddd dd MMM yyyy HH:mm}–{d.ScheduledEnd:HH:mm}";
        var where = string.IsNullOrWhiteSpace(d.Room) ? "" : $"   ·   {d.Room}";
        var track = string.IsNullOrWhiteSpace(d.TrackName) ? "" : $"   ·   {d.TrackName}";
        Text(when + where + track, small, mutedB, left, y, width);
        y += 17;

        // 🔑 §750.8 (operator 2026-08-01: "make intro. show session name, capture date and time.
        // show how many came from qr and devices and total", and "as people use summary data to
        // brag"). It sits ABOVE the score deliberately: this is the block that gets screenshotted,
        // so it has to answer "which session, collected when, from how many people, how" before the
        // number is read — a figure posted without its sample size invites the wrong argument.
        //
        // 🔒 The capture window is NOT the session's own time: it runs 30 minutes past the end, and
        // saying so stops "but I finished at 10:00" being a challenge to the data.
        var capFrom = d.WindowOpensAt ?? d.ScheduledStart;
        var capTo = d.WindowClosesAt ?? d.ScheduledEnd.AddMinutes(EvaluationAttribution.GraceMinutes);
        // 🔑 §752.3 (operator 2026-08-01: "header simplify. QR: 5, Session feedback devices: 18
        // highest number first" · "include % in parantes") — labelled counts with their share,
        // ORDERED BY COUNT descending so the channel that actually carried the session leads. A
        // fixed order buries the interesting number: if the QR outperformed the box, that is the
        // fact worth seeing first, and which one wins flips from session to session.
        //
        // 🔒 §752.6 (operator: *"if no open feedback or no qr code results, still show but write 0"*).
        // BOTH channels are always printed, including a zero. A channel that vanishes when it scores
        // nothing is the worst of both worlds: the reader cannot tell "the QR code got no scans" from
        // "the QR code was never offered", and a speaker deciding whether to keep putting the QR slide
        // up needs exactly that difference. An explicit `QR code: 0 (0%)` answers it.
        var channelTotal = d.DeviceResponses + d.QrResponses;
        var channels = string.Join(", ",
            new[]
            {
                ("feedback devices", d.DeviceResponses),
                ("QR code", d.QrResponses),
            }
            .OrderByDescending(x => x.Item2)
            // Guard the divide: with no feedback at all every share is 0%, not NaN.
            .Select(x => $"{x.Item1}: {x.Item2} "
                         + $"({(channelTotal == 0 ? "0" : Num(100d * x.Item2 / channelTotal))}%)"));

        Text($"{capFrom:ddd dd MMM HH:mm}–{capTo:HH:mm}"
             + $"     ·     Feedbacks: {d.Score.Responses} - {channels}",
            small, inkB, left, y, width);
        y += Section;

        // ---- the hero figure ---------------------------------------------------------------------
        var band = EvaluationReportLayout.BandColour(d.Score.Score);
        const double heroH = 100;
        EvaluationReportLayout.Panelled(gfx, left, y, width, heroH, EvaluationReportLayout.Panel);
        gfx.DrawRoundedRectangle(new XSolidBrush(band), left, y, 7, heroH, 8, 8);

        if (d.Score.Score is double score)
        {
            gfx.DrawString(Num(score), hero,
                new XSolidBrush(band), new XRect(left + 24, y + 17, 220, 62), XStringFormats.TopLeft);
            // 🔒 §11 — the band ALWAYS travels with the number, and a label that stops the figure
            // being read as a percentage travels with it too, so a cropped screenshot cannot mislead.
            //
            // 🔑 §752.6 (operator: *"dont like index strong from 12 responses - 83 in every 100 pressed
            // green. not understandable and user friendly"*). Two separate faults, both mine:
            //   • "index" is jargon. It was doing the §11 job of blocking a percentage reading, but a
            //     word the reader does not know cannot correct a misreading. "Satisfaction score"
            //     says what the number IS, which blocks it better and needs no glossary.
            //   • "83 in every 100 pressed green" was stated to a reader who could see there were only
            //     12 responses. Rescaling a small sample to an imaginary 100 people reads as spin.
            //     Real counts of real people, always — 10 of 12, never 83 in every 100.
            var positive = d.Score.Distribution.Four + d.Score.Distribution.Three;
            // §752.7 — the RANGE beside the band, so "Strong" explains itself where it is read.
            Text($"Satisfaction score · {d.Score.Band} ({SatisfactionScore.BandRange(d.Score.Score)} of 100)",
                h2, inkB, left + 176, y + 32, 300);
            Text($"{positive} of the {d.Score.Responses} people who answered were happy or very happy",
                small, mutedB, left + 176, y + 54, 340);
        }
        else
        {
            Text("Not enough responses yet", h1, inkB, left + 24, y + 26, width - 48);
            Text($"{d.Score.Responses} of {SatisfactionScore.MinimumResponses} responses needed before a score "
                 + "is published — below that a single press moves the result by more than ten points.",
                small, mutedB, left + 24, y + 58, width - 60);
        }
        y += heroH + Section;

        // ---- distribution, in the device's own colours ---------------------------------------------
        Text("How people answered", h2, inkB, left, y, width);
        y += Heading;
        var dist = d.Score.Distribution;
        var rows = new (string Label, int Count, XColor Colour)[]
        {
            (EvaluationRatingLabels.Label(4), dist.Four,  EvaluationReportLayout.Rating4),
            (EvaluationRatingLabels.Label(3), dist.Three, EvaluationReportLayout.Rating3),
            (EvaluationRatingLabels.Label(2), dist.Two,   EvaluationReportLayout.Rating2),
            (EvaluationRatingLabels.Label(1), dist.One,   EvaluationReportLayout.Rating1),
        };
        var peak = Math.Max(1, rows.Max(r => r.Count));
        foreach (var (label, count, colour) in rows)
        {
            gfx.DrawRoundedRectangle(new XSolidBrush(colour), left, y + 2, 9, 9, 4, 4);
            Text(label, body, inkB, left + 16, y - 1, 155);
            const double barX = 176, barW = 250;
            gfx.DrawRoundedRectangle(new XSolidBrush(EvaluationReportLayout.Panel), barX, y + 1, barW, 11, 6, 6);
            if (count > 0)
                gfx.DrawRoundedRectangle(new XSolidBrush(colour), barX, y + 1,
                    Math.Max(11, barW * count / peak), 11, 6, 6);
            Text(count.ToString(CultureInfo.InvariantCulture), bodyB, inkB, barX + barW + 10, y - 1, 40);
            y += 21;
        }
        y += Section - 21;

        // ---- open feedback, BELOW the numbers, and never truncated -------------------------------------
        Text("What people wrote", h2, inkB, left, y, width);
        y += Heading;
        if (d.Comments.Count == 0)
        {
            // 🔑 §752.6 — the section is ALWAYS rendered, empty or not, and the empty line names the
            // channel: written comments can only arrive through the QR survey, because the room device
            // has four buttons and no keyboard. "No written feedback" invited the wrong conclusion —
            // that nobody had anything to say — when the real reading is usually that few people
            // scanned. Naming the route turns a silence into information.
            Text("No open feedback via QR code — the room device collects button presses only.",
                body, mutedB, left, y, width);
            y += Row;
        }
        else
        {
            // 🔒 Verbatim — never summarised, never filtered, and (§750.8) never cut short to keep the
            // page count down. The score says whether a talk landed; these say why.
            foreach (var comment in d.Comments)
            {
                var wrapped = Wrap(gfx, comment, body, width - 16);
                var blockH = (wrapped.Count * Row) + 8;

                if (EnsureRoom(blockH))
                {
                    Text("What people wrote (continued)", h2, inkB, left, y, width);
                    y += Heading;
                }

                gfx.DrawRoundedRectangle(new XSolidBrush(EvaluationReportLayout.BrandBlue),
                    left, y + 2, 3, Math.Max(11, (wrapped.Count * Row) - 4), 2, 2);
                var ty = y;
                for (var i = 0; i < wrapped.Count; i++)
                {
                    var lineText = wrapped.Count == 1 ? $"“{wrapped[i]}”"
                        : i == 0 ? $"“{wrapped[i]}"
                        : i == wrapped.Count - 1 ? $"{wrapped[i]}”"
                        : wrapped[i];
                    Text(lineText, body, inkB, left + 12, ty - 1, width - 12);
                    ty += Row;
                }
                // 🔑 §752.6 — a clear blank line BETWEEN comments, so two quotes never read as one.
                y += blockH + 10;
            }
        }
        y += Section - 10;

        // ---- (§752.8) the event comparison is GONE -----------------------------------------------
        //
        // Operator 2026-08-01: *"remove part with every session. not relavant"*. It printed
        // "Every session at the event so far scores 75 together… Yours is 0 above that."
        //
        // 🔑 It is a leaderboard in one sentence, on a document written to be screenshotted. The
        // brief is deliberate about not ranking speakers against each other — C11 signage shows no
        // leaderboard "so no individual speaker is singled out" — and this quietly did on paper what
        // the signage refuses to do on screen. `EventPooled` is still carried on ReportData and still
        // computed: the event figure has real uses (C9, C11), it just does not belong on a speaker's
        // personal record of their own session.

        // 🔑 §750.8 (operator 2026-08-01: "count and % and impact pr button") — the arithmetic laid
        // out so the headline can be CHECKED BY HAND. The four "adds" columns sum to the score
        // exactly, which is the whole point: nobody has to take the number on trust, and a speaker
        // who wants to argue with it can see precisely where it came from.
        var total = dist.Four + dist.Three + dist.Two + dist.One;
        if (total > 0)
        {
            // heading + column labels + rule + four rows + totals line
            EnsureRoom(Heading + 15 + 9 + (4 * 26) + 10 + 20);
            Text("Where the score comes from", h2, inkB, left, y, width);
            y += Heading;
            Text("BUTTON", small, mutedB, left + 16, y, 160);
            Text("PRESSES", small, mutedB, left + 176, y, 60);
            Text("SHARE", small, mutedB, left + 246, y, 60);
            Text("ADDS TO SCORE", small, mutedB, left + 316, y, 110);
            y += 15;
            gfx.DrawLine(new XPen(EvaluationReportLayout.Hairline, 1), left, y, left + width, y);
            y += 9;

            foreach (var (label, count, colour) in rows)
            {
                // 🔒 Resolved from the SAME source that produced the label, never a switch on the
                // label text. A literal switch is what broke this once already: the wordings moved to
                // the brief's "Very happy" set and every row silently fell through to rating 1, so
                // every impact printed +0.0 while the shares beside them stayed correct — wrong in a
                // way that looks deliberate.
                var rating = EvaluationRatingLabels.Order
                    .First(r => EvaluationRatingLabels.Label(r) == label);
                // Weight comes from SatisfactionScore, never a literal, so a profile change moves
                // this table with the headline instead of leaving them disagreeing.
                var adds = count * SatisfactionScore.WeightOf(rating, d.Score.WeightProfile) / total;

                gfx.DrawRoundedRectangle(new XSolidBrush(colour), left, y + 2, 8, 8, 4, 4);
                Text(label, small, inkB, left + 16, y - 1, 160);
                // 🔑 The colour NAMED under the label: an attendee remembers pressing "the yellow
                // one", not "somewhat unhappy" — and it survives a greyscale print, which the
                // swatch beside it does not.
                Text(EvaluationReportLayout.ColourName(rating), small, mutedB, left + 16, y + 8, 160);
                Text(count.ToString(CultureInfo.InvariantCulture), small, inkB, left + 176, y - 1, 60);
                Text(Num(100d * count / total) + "%", small, inkB, left + 246, y - 1, 60);
                Text("+ " + Num(adds), small, inkB, left + 316, y - 1, 110);
                y += 26;
            }
            gfx.DrawLine(new XPen(EvaluationReportLayout.Hairline, 1), left, y + 1, left + width, y + 1);
            y += 10;
            Text($"{total} presses in total", small, inkB, left + 176, y, 120);
            Text(d.Score.Score is double s2 ? $"= {Num(s2)} out of 100" : "= no score yet",
                small, inkB, left + 316, y, 140);
            y += Section;
        }
        else
        {
            y += 10;
        }

        // ---- how the score works, in words ----------------------------------------------------------
        var howH = 20 + (EvaluationReportLayout.HowItWorks.Length * Row) + (total > 0 ? 36 : 0) + 24;
        // 🔴 The panel is drawn as ONE rectangle, so it cannot be split — without this it ran off the
        // bottom of the sheet, taking the worked formula with it.
        EnsureRoom(howH);
        EvaluationReportLayout.Panelled(gfx, left, y, width, howH, EvaluationReportLayout.Panel);
        Text("How this score works", h2, inkB, left + 16, y + 12, width - 32);
        var ly = y + 34;
        foreach (var line in EvaluationReportLayout.HowItWorks) { Text(line, small, inkB, left + 16, ly, width - 32); ly += Row; }
        // 🔑 §750.8 (operator: "explain formula ... with real data") — the same sum worked through
        // with THIS session's counts. A formula in the abstract gets skipped; the identical formula
        // carrying your own numbers gets checked, and that is the difference between a figure people
        // trust and one they argue about.
        if (total > 0)
        {
            var parts = EvaluationRatingLabels.Order
                .Select(r => (Count: r switch { 4 => dist.Four, 3 => dist.Three, 2 => dist.Two, _ => dist.One },
                              Weight: SatisfactionScore.WeightOf(r, d.Score.WeightProfile)))
                .ToList();
            var sum = parts.Sum(p => p.Count * p.Weight);
            var expression = string.Join("  +  ", parts.Select(p => $"({p.Count} × {Whole(p.Weight)})"));
            Text($"This session:   {expression}   =   {Whole(sum)}", small, inkB, left + 16, ly + 6, width - 32);
            Text($"{Whole(sum)}  ÷  {total} presses   =   {Num(sum / total)} out of 100",
                small, inkB, left + 16, ly + 20, width - 32);
            ly += 36;
        }
        // 🔑 §752.7 — the bands, with the word the reader actually met at the top of the page marked
        // out, so "Strong" is anchored to a range rather than left as vocabulary to be learned.
        Text("What the words mean:   80–100 very happy   ·   60–79 happy   ·   40–59 somewhat unhappy"
             + "   ·   0–39 unhappy",
            small, inkB, left + 16, ly + 6, width - 32);
        // 🔒 §752.7 — moved here from the footer. The brief (§4.4) requires the weight profile to
        // travel with any published score: without it a historical 75 is ambiguous between the two
        // weighting models and cannot be recomputed or compared. It belongs in the explanation, not
        // in a footnote nobody reads — but it does have to stay SOMEWHERE, and this is that place.
        Text($"Weighting used: {d.Score.WeightProfile} (100 / 67 / 33 / 0). Recorded so this score can "
             + "still be checked and compared later.",
            small, mutedB, left + 16, ly + 20, width - 32);
        y += howH + Section;

        // ---- when they answered — LAST -----------------------------------------------------------
        //
        // 🔑 §752.6 (operator: *"move when people scored to bottom as less relevant"*). It used to sit
        // between the distribution and the comments, which put a minor curiosity — the shape of the
        // arrival times — ahead of what people actually wrote. Nothing here changes a speaker's
        // decisions; it is background, so it reads last.
        if (d.Timeline is { Count: > 0 } timeline && timeline.Any(b => b.Count > 0))
        {
            const double chartW = 436, chartH = 30;
            // 🔒 The chart must not be split across a page break — half a bar chart with its axis on
            // the next page is worse than a clean break.
            EnsureRoom(Heading + chartH + 9 + 14);
            Text("When people answered", h2, inkB, left, y, width);
            y += Heading;
            var slot = chartW / timeline.Count;
            var top = Math.Max(1, timeline.Max(b => b.Count));
            gfx.DrawLine(new XPen(EvaluationReportLayout.Hairline, 1), left, y + chartH, left + chartW, y + chartH);
            for (var i = 0; i < timeline.Count; i++)
            {
                if (timeline[i].Count <= 0) continue;
                var h = Math.Max(3.0, chartH * timeline[i].Count / top);
                gfx.DrawRoundedRectangle(new XSolidBrush(EvaluationReportLayout.BrandBlue),
                    left + (slot * i) + 1.5, y + chartH - h, slot - 3, h, 3, 3);
            }
            y += chartH + 9;
            Text($"{timeline[0].From:HH:mm}", small, mutedB, left, y, 60);
            Text($"{d.ScheduledEnd.AddMinutes(EvaluationAttribution.GraceMinutes):HH:mm}", small, mutedB,
                left + chartW - 60, y, 60, XStringFormats.TopRight);
            y += Section;
        }

        Footer();
        gfx.Dispose();

        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>
    /// Break a comment into lines that fit <paramref name="maxWidth"/>, measuring the REAL font.
    /// </summary>
    /// <remarks>
    /// 🔑 Measured, not estimated by character count: a character-count guess overflows on wide text
    /// and wastes half the line on narrow text, and this is the block that decides the page count.
    /// </remarks>
    private static List<string> Wrap(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Split(' ',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (gfx.MeasureString(candidate, font).Width <= maxWidth) { current = candidate; continue; }
            if (current.Length > 0) lines.Add(current);
            current = word;
        }
        if (current.Length > 0) lines.Add(current);
        return lines.Count == 0 ? new List<string> { string.Empty } : lines;
    }
}













