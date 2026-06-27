using ClosedXML.Excel;
using CommunityHub.Core.Documents;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §152: the xlsx extractor must (a) read EVERY worksheet labeled by name, (b) render an
/// epoch-dated time-only cell as a clean clock time ("12:30", not "1/1/1970 12:30"), and
/// (c) strip HTML/rich-text from cells (the agenda's description cells are HTML). Verified
/// against the real Agenda.xlsx during build; these synthetic cases guard the behaviour.
/// </summary>
public sealed class DocumentTextExtractorTimeAndHtmlTests
{
    private static byte[] Build(System.Action<XLWorkbook> build)
    {
        using var wb = new XLWorkbook();
        build(wb);
        using var ms = new System.IO.MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Epoch_time_cell_renders_as_clock_time_not_a_1970_date()
    {
        var bytes = Build(wb =>
        {
            var ws = wb.AddWorksheet("Day - 2");
            ws.Cell(1, 1).Value = "Lunch";
            ws.Cell(1, 2).Value = new System.DateTime(1970, 1, 1, 12, 30, 0);
        });

        var txt = DocumentTextExtractor.Extract("agenda.xlsx", bytes) ?? "";

        Assert.Contains("12:30", txt);
        Assert.DoesNotContain("1970", txt);
    }

    [Fact]
    public void Html_rich_text_cell_is_stripped_to_plain_text()
    {
        var bytes = Build(wb =>
            wb.AddWorksheet("Day - 1").Cell(1, 1).Value =
                "<p><span style=\"color:red\">Meet</span> the sponsors</p>");

        var txt = DocumentTextExtractor.Extract("agenda.xlsx", bytes) ?? "";

        Assert.Contains("Meet the sponsors", txt);
        Assert.DoesNotContain("<p>", txt);
        Assert.DoesNotContain("<span", txt);
    }

    [Fact]
    public void Every_worksheet_is_read_and_labeled_by_name()
    {
        var bytes = Build(wb =>
        {
            wb.AddWorksheet("Day - 1").Cell(1, 1).Value = "first";
            wb.AddWorksheet("Day - 2").Cell(1, 1).Value = "second";
        });

        var txt = DocumentTextExtractor.Extract("agenda.xlsx", bytes) ?? "";

        Assert.Contains("Sheet: Day - 1", txt);
        Assert.Contains("Sheet: Day - 2", txt);
        Assert.Contains("first", txt);
        Assert.Contains("second", txt);
    }
}
