using CommunityHub.Core.Evaluation;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1274 — the grey "How this score works" panel in the session report must reach past its last
/// line of text.
/// </summary>
/// <remarks>
/// Operator 2026-09-21, with a screenshot of the PDF: <i>"error in the evaluation report … move space
/// needed + how the score works color issue"</i>. The panel's height was a separate sum that left out
/// the bands and weighting lines, so they printed below the grey on white, and the next heading sat
/// against them. The height is now derived from the same blocks the text is placed at; this pins
/// that the last line — the weighting note, placed 20pt into the bands block — ends inside it.
/// </remarks>
public sealed class EvaluationReportHowPanelTests
{
    private const double Row = 14;          // the report's line pitch
    private const double SmallTextHeight = 12;

    [Theory]
    [InlineData(6, true)]
    [InlineData(6, false)]
    [InlineData(9, true)]
    public void The_panel_reaches_below_its_last_line(int explanationLines, bool withFormula)
    {
        var height = EvaluationReportService.HowPanelHeight(explanationLines, Row, withFormula);

        var weightingLineTop = EvaluationReportService.HowPanelTitleBlock
            + (explanationLines * Row)
            + (withFormula ? EvaluationReportService.HowPanelFormulaBlock : 0)
            + 20;   // "Weighting used" is drawn at ly + 20

        Assert.True(height >= weightingLineTop + SmallTextHeight + 8,
            $"Panel {height}pt ends before its last line ({weightingLineTop + SmallTextHeight}pt).");
    }
}
