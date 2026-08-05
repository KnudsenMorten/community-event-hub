using CommunityHub.Core.Evaluation;
using UglyToad.PdfPig;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §743 C7 — the session report PDF.
/// </summary>
/// <remarks>
/// 🔑 These tests <b>generate with PdfSharpCore and read back with PdfPig</b>, asserting the text a
/// reader actually sees rather than the arguments we passed in. That matters for a document: a
/// renderer can be called correctly and still put nothing on the page, and Part 2 §11's rules are
/// about what the READER is shown.
/// </remarks>
public sealed class EvaluationReportServiceTests
{
    private static readonly DateTimeOffset Start = new(2027, 2, 9, 9, 0, 0, TimeSpan.Zero);

    private static string TextOf(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return string.Join(" ", doc.GetPages().Select(p => p.Text));
    }

    private static EvaluationReportService.ReportData Data(
        SatisfactionScore.Distribution dist,
        SatisfactionScore.Distribution? eventDist = null,
        IReadOnlyList<string>? comments = null,
        int qrResponses = 0,
        int deviceResponses = 0) =>
        new(
            "Experts Live Denmark 2027",
            "Engineering Autonomous AI Agents",
            "Room 1", "Cloud",
            Start, Start.AddHours(1),
            SatisfactionScore.Compute(dist),
            SatisfactionScore.Compute(eventDist ?? new SatisfactionScore.Distribution(20, 20, 5, 5)),
            comments ?? Array.Empty<string>(),
            new DateTimeOffset(2027, 2, 9, 10, 35, 0, TimeSpan.Zero),
            QrResponses: qrResponses,
            DeviceResponses: deviceResponses);

    /// <summary>
    /// §752.6 — a valid PDF, and every page carries content.
    /// </summary>
    /// <remarks>
    /// 🔑 This asserted <b>exactly one page</b>. The operator then asked for the layout to breathe
    /// (<i>"more spacing in pdf"</i>, <i>"looks cramped"</i>, <i>"extra line after each paragraph"</i>)
    /// and a no-comment report grew to two. A page count is a CONSEQUENCE of the spacing and the
    /// content, not a requirement — pinning it here would have meant re-tightening the layout he had
    /// just asked to loosen. What actually matters is that no page is left blank or orphaned, which
    /// is what a page-break bug would produce, so that is what is pinned now.
    /// </remarks>
    [Fact]
    public void The_report_renders_a_valid_pdf_with_no_empty_pages()
    {
        var pdf = new EvaluationReportService().Render(Data(new(9, 8, 2, 1)));

        Assert.NotEmpty(pdf);
        // A real PDF, not bytes that merely got written: PdfPig will refuse anything malformed.
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        Assert.InRange(doc.NumberOfPages, 1, 2);   // no comments — it must not sprawl
        for (var p = 1; p <= doc.NumberOfPages; p++)
        {
            // Not merely non-empty: a page holding only the footer is an orphan, and the footer alone
            // is ~120 characters. Real content is what has to be there.
            var text = doc.GetPage(p).Text;
            Assert.True(text.Length > 200, $"page {p} carries no real content — orphaned by a page break");
        }
    }

    [Fact]
    public void The_headline_carries_the_score_the_band_and_the_sample_size()
    {
        // The brief's worked example: 20 responses → 75.0, Strong, 85% positive.
        var text = TextOf(new EvaluationReportService().Render(Data(new(9, 8, 2, 1))));

        Assert.Contains("75", text);      // §750.8 — whole numbers now ("ignore comma. round up")
        // 🔒 §11 — the band always accompanies the number, and (§752.7) its RANGE always accompanies
        // the band. The range is not decoration here: now that the band words ARE the button words,
        // it is the only thing separating the band "Happy" from the count of Happy presses.
        Assert.Contains("Happy (60–79 of 100)", text);
        // 🔒 §11 — never a score without its sample size. §752.6 changed how that is WORDED (real
        // people, not "responses"), so this pins the requirement rather than the old sentence.
        Assert.Contains("of the 20 people who answered", text);
        Assert.Contains("Engineering Autonomous AI Agents", text);
    }

    [Fact]
    public void The_score_cannot_be_read_as_a_percentage_and_says_so_without_jargon()
    {
        // 🔒 §11, and not cosmetic: an all-light-green session scores 66.7, which reads as a failing
        // grade if presented as a percentage. The weighting was deliberately NOT adjusted to make
        // the number look friendlier, so the labelling has to carry that load.
        //
        // 🔑 §752.6 — the label used to be the word "index". It carried the §11 duty and failed the
        // reader: *"not understandable and user friendly"*. A word someone has to look up cannot
        // correct a misreading, so the guard is now plain English. Both halves are pinned here —
        // the protection must survive, and it must survive WITHOUT the jargon coming back.
        var text = TextOf(new EvaluationReportService().Render(Data(new(0, 20, 0, 0))));

        Assert.Contains("67", text);      // 66.7 rounds to 67 on the page
        Assert.Contains("Satisfaction score", text);
        Assert.Contains("It is not a percentage", text);
        // §752.7 — "Happy", not "Somewhat unhappy": the calibration still puts an all-light-green
        // session (nobody dissatisfied at all) in the second band, which is the whole reason the
        // 60 floor sits below 66.7. Relabelling must not move that.
        Assert.Contains("Happy (60–79 of 100)", text);
        Assert.DoesNotContain("66.7%", text);
        Assert.DoesNotContain("index", text);
    }

    /// <summary>
    /// §752.6 — 🔒 a small sample is never rescaled to an imaginary 100 people.
    /// </summary>
    /// <remarks>
    /// The report said <i>"83 in every 100 pressed green"</i> to a reader who could see, on the same
    /// line, that only 12 people had answered. Inventing 100 respondents out of 12 reads as spin and
    /// is exactly the kind of claim a speaker would be embarrassed to have screenshotted. Real counts
    /// of real people — <b>10 of 12</b> — say the same thing and are defensible.
    /// </remarks>
    [Fact]
    public void A_small_sample_is_reported_as_real_people_never_scaled_to_a_hundred()
    {
        var text = TextOf(new EvaluationReportService().Render(Data(new(6, 4, 1, 1))));   // 12 answers

        Assert.Contains("10 of the 12 people who answered were happy or very happy", text);
        Assert.DoesNotContain("in every 100", text);
    }

    [Fact]
    public void Below_the_threshold_the_report_explains_itself_instead_of_printing_a_number()
    {
        var text = TextOf(new EvaluationReportService().Render(Data(new(2, 1, 0, 1))));   // 4 responses

        Assert.Contains("Not enough responses yet", text);
        Assert.Contains("4 of 10", text);
        // 🔒 No score is printed at all — not a zero, which a reader would take as a real result.
        Assert.DoesNotContain("index ·", text);
    }

    [Fact]
    public void The_distribution_is_printed_with_all_four_counts()
    {
        var text = TextOf(new EvaluationReportService().Render(Data(new(9, 8, 2, 1))));

        Assert.Contains("How people answered", text);
        Assert.Contains("Very happy", text);            // §750.8 — the BRIEF's button wording
        Assert.Contains("Unhappy", text);
        // Rating 2's label must read negatively — §2.1, the forced choice has no neutral option.
        Assert.Contains("Somewhat unhappy", text);      // 🔒 still negative: there is no midpoint
    }

    [Fact]
    public void Comments_are_reproduced_VERBATIM()
    {
        // 🔒 Never summarised or filtered: the score says whether a talk landed, the comments say why.
        var comment = "Loved the demo but the room was far too warm";
        var text = TextOf(new EvaluationReportService().Render(
            Data(new(9, 8, 2, 1), comments: new[] { comment })));

        Assert.Contains(comment, text);
    }

    /// <summary>
    /// §752.6 — 🔒 an empty section is still a section, and it names the CHANNEL.
    /// </summary>
    /// <remarks>
    /// Operator: <i>"if no open feedback or no qr code results, still show but write 0 and no open
    /// feedback via QR code"</i>. Written comments can only arrive through the QR survey — the room
    /// device has four buttons and no keyboard — so "no written feedback" invited the wrong reading,
    /// that nobody had anything to say. Naming the route turns a silence into information.
    /// </remarks>
    [Fact]
    public void With_no_comments_the_report_names_the_channel_rather_than_leaving_a_blank()
    {
        var text = TextOf(new EvaluationReportService().Render(Data(new(9, 8, 2, 1))));

        Assert.Contains("What people wrote", text);            // the heading survives an empty section
        Assert.Contains("No open feedback via QR code", text);
    }

    /// <summary>
    /// §752.6 — 🔒 BOTH channels are always printed, including a zero.
    /// </summary>
    /// <remarks>
    /// Operator: <i>"still show but write 0"</i>, and again <i>"same with devices, still show but 0 if
    /// none"</i>. A channel that disappears when it scores nothing leaves the reader unable to tell
    /// "the QR code got no scans" from "the QR code was never offered" — and that is exactly the
    /// difference a speaker needs when deciding whether to keep showing the QR slide.
    /// </remarks>
    [Fact]
    public void A_channel_that_collected_nothing_is_still_printed_as_zero()
    {
        var noQr = TextOf(new EvaluationReportService().Render(
            Data(new(9, 8, 2, 1), qrResponses: 0, deviceResponses: 20)));
        Assert.Contains("QR code: 0 (0%)", noQr);
        Assert.Contains("feedback devices: 20 (100%)", noQr);

        var noDevices = TextOf(new EvaluationReportService().Render(
            Data(new(9, 8, 2, 1), qrResponses: 20, deviceResponses: 0)));
        Assert.Contains("feedback devices: 0 (0%)", noDevices);
        Assert.Contains("QR code: 20 (100%)", noDevices);
    }

    /// <summary>
    /// §752.8 — 🔒 the speaker's report does NOT rank them against the rest of the event.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-01: <i>"remove part with every session. not relavant"</i>. It printed
    /// <i>"Every session at the event so far scores 75 together… Yours is 0 above that."</i>
    ///
    /// <para>🔑 Pinned as an ABSENCE, deliberately. This is a leaderboard in one sentence, on a
    /// document written to be screenshotted — and the brief already refuses to rank speakers on the
    /// C11 signage <i>"so no individual speaker is singled out"</i>. The report was quietly doing on
    /// paper what the signage is forbidden to do on screen. A removed feature with no test is a
    /// feature that comes back the next time someone reads <c>EventPooled</c> on ReportData and
    /// assumes it is unused.</para>
    ///
    /// <para>EventPooled is still CARRIED and still computed — it has real uses in C9 and C11. What
    /// is asserted here is only that it does not reach the speaker's personal report.</para>
    /// </remarks>
    [Fact]
    public void The_report_does_not_compare_the_speaker_against_the_rest_of_the_event()
    {
        var text = TextOf(new EvaluationReportService().Render(
            Data(new(9, 8, 2, 1), eventDist: new SatisfactionScore.Distribution(20, 20, 5, 5))));

        Assert.DoesNotContain("Every session at the event", text);
        Assert.DoesNotContain("pooled", text);
        Assert.DoesNotContain("above that", text);
        Assert.DoesNotContain("below that", text);
    }

    [Fact]
    public void The_footer_records_the_weight_profile_so_a_historical_figure_stays_interpretable()
    {
        // §4.1/§8 — without it a 75.0 is ambiguous between the two models and cannot be recomputed.
        var text = TextOf(new EvaluationReportService().Render(Data(new(9, 8, 2, 1))));

        Assert.Contains("linear", text);
        Assert.Contains("Generated", text);
    }
}


