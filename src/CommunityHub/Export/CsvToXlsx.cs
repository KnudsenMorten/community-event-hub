using ClosedXML.Excel;

namespace CommunityHub.Export;

/// <summary>
/// Converts an already-built CSV export into an .xlsx workbook, so every CSV
/// download can be offered as Excel too without re-deriving the data. Each export
/// handler keeps its single source of truth (the CSV builder) and calls
/// <see cref="Build"/> for the Excel variant. Header row is bolded; columns
/// auto-fit. Values are written as text to preserve ids / leading zeros / phone
/// numbers exactly as the CSV had them.
/// </summary>
public static class CsvToXlsx
{
    /// <summary>MIME type for an .xlsx (OpenXML spreadsheet).</summary>
    public const string ContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Build an .xlsx byte array from a CSV string (RFC-4180: quoted
    /// fields, escaped quotes, embedded commas/newlines).</summary>
    public static byte[] Build(string csv, string sheetName = "Export")
    {
        var rows = ParseCsv(csv ?? string.Empty);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SafeSheetName(sheetName));
        for (var r = 0; r < rows.Count; r++)
        {
            var cols = rows[r];
            for (var c = 0; c < cols.Count; c++)
                ws.Cell(r + 1, c + 1).SetValue(cols[c]);   // text, no type coercion
        }
        if (rows.Count > 0) ws.Row(1).Style.Font.Bold = true;
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Excel limits sheet names to 31 chars and forbids []*?/\: — sanitise.</summary>
    private static string SafeSheetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Export";
        var cleaned = new string(name.Select(ch => "[]*?/\\:".Contains(ch) ? ' ' : ch).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "Export";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    /// <summary>Minimal RFC-4180 CSV parser into rows of fields.</summary>
    private static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var field = new System.Text.StringBuilder();
        var row = new List<string>();
        var inQuotes = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else
            {
                switch (ch)
                {
                    case '"': inQuotes = true; break;
                    case ',': row.Add(field.ToString()); field.Clear(); break;
                    case '\r': break; // handled with \n
                    case '\n':
                        row.Add(field.ToString()); field.Clear();
                        rows.Add(row); row = new List<string>();
                        break;
                    default: field.Append(ch); break;
                }
            }
        }
        // trailing field / row (no final newline)
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
