using ClosedXML.Excel;
using CommunityHub.Core.Integrations.DocLibrary;

namespace CommunityHub.Core.Surveys;

/// <summary>
/// §6.5 — turns one post-event survey's responses into the summary workbook that is published daily
/// to its §3.4 folder.
/// </summary>
/// <remarks>
/// <para>🔒 <b>It summarises what <see cref="SurveyQuestionSummaryService"/> already computed.</b>
/// The organizer page and this file therefore show the same numbers by construction. A second
/// aggregation over the same answer rows is exactly how a file and a screen come to disagree, and
/// the file is the one that gets forwarded to a board.</para>
///
/// <para>🔒 <b>The workbook carries NO identity, because the survey has none to carry.</b> There is
/// no respondent column, no timestamp column and no ordering by arrival — free text is sorted by its
/// text, as it already is on screen, so that a comment cannot be lined up against a send time.</para>
///
/// <para>⚠️ <b>Deterministic, like the logistics files and for the same reason.</b> It is rebuilt
/// daily and republished only when the content key changes, so nothing inside may vary run to run —
/// no "generated at" cell. The date the file was generated is the library's own modified stamp.</para>
/// </remarks>
public sealed class SurveySummaryFileProducer
{
    /// <summary>The file name for one survey's summary, e.g. <c>eldk27-post-attendee-summary.xlsx</c>.</summary>
    public static string FileNameFor(string slug) => $"{slug}-summary.xlsx";

    public GeneratedFile Build(SurveyDefinition definition, QuestionSurveySummary summary)
    {
        using var wb = new XLWorkbook();

        // --- Sheet 1: the ratings, each WITH its distribution ---------------
        var ratings = wb.Worksheets.Add("Ratings");
        Header(ratings, "Question", "Answers", "Average", "Score", "Count");
        var row = 2;
        foreach (var r in summary.Ratings)
        {
            // 🔒 The average is written on the same rows as the distribution, never on its own. A
            // mean of 3.0 is what "everybody lukewarm" and "half loved it, half hated it" both look
            // like, and a reader who only ever sees the mean cannot tell those apart.
            var first = true;
            foreach (var (value, count) in r.Distribution)
            {
                // The prompt, the answer count and the average are written once, on the first of the
                // question's distribution rows — repeating them down every row would read as several
                // questions that happen to share a name.
                if (first)
                {
                    ratings.Cell(row, 1).Value = r.Prompt;
                    ratings.Cell(row, 2).Value = r.Answered;
                    ratings.Cell(row, 3).Value = Math.Round(r.Average, 2);
                }
                ratings.Cell(row, 4).Value = value;
                ratings.Cell(row, 5).Value = count;
                row++;
                first = false;
            }

            if (r.Distribution.Count == 0)
            {
                ratings.Cell(row, 1).Value = r.Prompt;
                ratings.Cell(row, 2).Value = 0;
                row++;
            }
        }
        Finish(ratings);

        // --- Sheet 2: the choice questions ----------------------------------
        var choices = wb.Worksheets.Add("Choices");
        Header(choices, "Question", "Option", "Count");
        row = 2;
        foreach (var c in summary.Choices)
        {
            var first = true;
            foreach (var (label, count) in c.Options)
            {
                choices.Cell(row, 1).Value = first ? c.Prompt : string.Empty;
                choices.Cell(row, 2).Value = label;
                choices.Cell(row, 3).Value = count;
                row++;
                first = false;
            }
        }
        Finish(choices);

        // --- Sheet 3: every word people wrote --------------------------------
        var text = wb.Worksheets.Add("Comments");
        Header(text, "Question", "Answer");
        row = 2;
        foreach (var f in summary.FreeText)
        {
            foreach (var answer in f.Answers)
            {
                text.Cell(row, 1).Value = f.Prompt;
                text.Cell(row, 2).Value = answer;
                row++;
            }
        }
        Finish(text);

        // The key is the ANSWERS, so the file is republished when somebody responds and not because
        // a workbook was rebuilt.
        var key = GeneratedFile.KeyOf(
            new[] { $"responses:{summary.Responses}" }
                .Concat(summary.Ratings.SelectMany(r =>
                    r.Distribution.Select(d => $"r|{r.QuestionId}|{d.Value}|{d.Count}")))
                .Concat(summary.Choices.SelectMany(c =>
                    c.Options.Select(o => $"c|{c.QuestionId}|{o.Label}|{o.Count}")))
                .Concat(summary.FreeText.SelectMany(f =>
                    f.Answers.Select(a => $"t|{f.QuestionId}|{a}"))));

        return new GeneratedFile(
            FileNameFor(definition.Slug),
            Save(wb),
            GeneratedFile.XlsxContentType,
            key,
            LogisticsHeadline.Of(summary.Responses, "response", "responses"));
    }

    private static void Header(IXLWorksheet ws, params string[] headers)
    {
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Range(1, 1, 1, headers.Length).Style.Font.Bold = true;
    }

    private static void Finish(IXLWorksheet ws) => ws.Columns().AdjustToContents();

    private static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
