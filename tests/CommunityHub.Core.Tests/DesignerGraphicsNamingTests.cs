using ClosedXML.Excel;
using CommunityHub.Core.Integrations.Graphics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Pure naming + .xlsx tests for the external-designer pipeline (REQUIREMENTS §165): name-by-
/// speaker file names, deterministic de-dupe, folder-segment sanitisation, the security-critical
/// TRAVERSAL rejection, the folder-URL derivation, and that the brief renders to a valid workbook.
/// </summary>
public sealed class DesignerGraphicsNamingTests
{
    // ---- speaker photo file name --------------------------------------------

    [Fact]
    public void Photo_file_name_is_first_last_with_the_source_extension()
    {
        var baseName = DesignerGraphicsNaming.SanitizeNameBase("Ada Lovelace", 1);
        Assert.Equal("Ada-Lovelace", baseName);

        Assert.Equal("Ada-Lovelace.png",
            DesignerGraphicsNaming.BuildPhotoFileName(baseName, "https://x.test/pic.png?v=2", 1, appendIdSuffix: false));
        // Unknown / missing extension ⇒ .jpg default.
        Assert.Equal("Ada-Lovelace.jpg",
            DesignerGraphicsNaming.BuildPhotoFileName(baseName, "https://x.test/pic", 1, appendIdSuffix: false));
    }

    [Fact]
    public void Collision_appends_a_stable_participant_id_suffix()
    {
        var baseName = DesignerGraphicsNaming.SanitizeNameBase("Sam Speaker", 42);
        Assert.Equal("Sam-Speaker-42.jpg",
            DesignerGraphicsNaming.BuildPhotoFileName(baseName, "https://x.test/a.jpg", 42, appendIdSuffix: true));
    }

    [Fact]
    public void Blank_or_punctuation_only_name_falls_back_to_speaker_id()
    {
        Assert.Equal("speaker-9", DesignerGraphicsNaming.SanitizeNameBase("   ", 9));
        Assert.Equal("speaker-9", DesignerGraphicsNaming.SanitizeNameBase("!!! @@@", 9));
    }

    // ---- TRAVERSAL rejection (security-critical) -----------------------------

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows")]
    [InlineData("name/with/slashes")]
    public void File_name_can_never_contain_a_separator_or_traversal(string hostileName)
    {
        var baseName = DesignerGraphicsNaming.SanitizeNameBase(hostileName, 3);
        var file = DesignerGraphicsNaming.BuildPhotoFileName(baseName, null, 3, appendIdSuffix: false);

        Assert.DoesNotContain("/", file);
        Assert.DoesNotContain("\\", file);
        Assert.DoesNotContain("..", file);
    }

    [Theory]
    [InlineData("../evil", "evil")]
    [InlineData("Securing Your Cloud", "Securing Your Cloud")]
    [InlineData("A/B:C*?\"<>|D", "ABCD")]
    [InlineData("   ", "Untitled")]
    [InlineData("....", "Untitled")]
    public void Folder_segment_is_sanitised_and_traversal_safe(string title, string expected)
    {
        var seg = DesignerGraphicsNaming.SanitizeFolderSegment(title);
        Assert.Equal(expected, seg);
        Assert.DoesNotContain("..", seg);
        Assert.DoesNotContain("/", seg);
        Assert.DoesNotContain("\\", seg);
    }

    // ---- folder URL derivation ----------------------------------------------

    [Fact]
    public void Folder_url_drops_the_file_leaf_and_any_query()
    {
        Assert.Equal("https://t.test/sites/x/Photos",
            DesignerGraphicsNaming.FolderUrlFromFileUrl("https://t.test/sites/x/Photos/Ada.jpg"));
        Assert.Equal("https://t.test/sites/x/Photos",
            DesignerGraphicsNaming.FolderUrlFromFileUrl("https://t.test/sites/x/Photos/Ada.jpg?web=1"));
        Assert.Equal(string.Empty, DesignerGraphicsNaming.FolderUrlFromFileUrl(null));
    }

    // ---- workbook ------------------------------------------------------------

    [Fact]
    public void Brief_workbook_renders_a_valid_xlsx_with_the_header_and_a_row()
    {
        var rows = new[]
        {
            new DesignerBriefRow(
                1, "Cloud Native Talk", "Technical Session", "Cloud", "Intermediate", "60 min",
                "Mon 1 Jun 10:00–11:00", "Room A",
                new[] { "Ada Lovelace" }, new[] { "Ada-Lovelace.png" },
                "EventHub/Build/Sessions/Cloud Native Talk", "https://t.test/sites/x/Cloud Native Talk"),
        };

        var bytes = DesignerBriefWorkbook.Build(rows);
        Assert.NotEmpty(bytes);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet(1);
        Assert.Equal("Session", ws.Cell(1, 1).GetString());
        Assert.Equal("Folder link", ws.Cell(1, DesignerBriefWorkbook.Columns.Length).GetString());
        Assert.Equal("Cloud Native Talk", ws.Cell(2, 1).GetString());
        Assert.Equal("Ada-Lovelace.png", ws.Cell(2, 9).GetString());
    }
}
