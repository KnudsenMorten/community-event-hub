using ClosedXML.Excel;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// Pure .xlsx builder for the external-designer brief (REQUIREMENTS §165 step 4). One row per
/// non-service session with everything a designer needs in one place: session facts, speaker
/// name(s), the SharePoint build-folder PATH + (organizer-only) LINK, and the photo file name(s)
/// for that session. Uses ClosedXML (already referenced by Core — no new dependency). No DB / no
/// SharePoint here: it just renders the supplied <see cref="DesignerBriefRow"/> rows, so it is
/// trivially unit-testable.
/// </summary>
public static class DesignerBriefWorkbook
{
    public const string SheetName = "Designer Brief";

    /// <summary>The column order written into the brief (header row, bold).</summary>
    public static readonly string[] Columns =
    {
        "Session",        // 1
        "Type",           // 2
        "Track",          // 3
        "Level",          // 4
        "Length",         // 5
        "Scheduled",      // 6
        "Room",           // 7
        "Speakers",       // 8
        "Photo file(s)",  // 9 — the name-keyed photo files for this session
        "Build folder",   // 10 — drive-relative folder path
        "Folder link",    // 11 — organizer-only SharePoint link (blank until resolvable)
    };

    /// <summary>Render the rows to a valid .xlsx byte array.</summary>
    public static byte[] Build(IEnumerable<DesignerBriefRow> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SheetName);

        for (var c = 0; c < Columns.Length; c++)
        {
            ws.Cell(1, c + 1).SetValue(Columns[c]);
        }
        ws.Row(1).Style.Font.Bold = true;

        var row = 2;
        foreach (var r in rows)
        {
            ws.Cell(row, 1).SetValue(r.Title ?? string.Empty);
            ws.Cell(row, 2).SetValue(r.Type ?? string.Empty);
            ws.Cell(row, 3).SetValue(r.Track ?? string.Empty);
            ws.Cell(row, 4).SetValue(r.Level ?? string.Empty);
            ws.Cell(row, 5).SetValue(r.Length ?? string.Empty);
            ws.Cell(row, 6).SetValue(r.Scheduled ?? string.Empty);
            ws.Cell(row, 7).SetValue(r.Room ?? string.Empty);
            ws.Cell(row, 8).SetValue(string.Join(", ", r.SpeakerNames));
            ws.Cell(row, 9).SetValue(string.Join(", ", r.PhotoFileNames));
            ws.Cell(row, 10).SetValue(r.FolderPath ?? string.Empty);
            ws.Cell(row, 11).SetValue(r.FolderUrl ?? string.Empty);
            row++;
        }

        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
