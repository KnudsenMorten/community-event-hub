using System.Text;

namespace CommunityHub.Core.Export;

/// <summary>
/// Minimal RFC 4180 CSV writer for the organizer table exports. Quotes a field
/// only when it must (comma, quote, newline); doubles embedded quotes.
/// </summary>
public static class CsvWriter
{
    /// <summary>
    /// Build a CSV document from a header row and data rows.
    /// </summary>
    public static string Write(
        IReadOnlyList<string> header,
        IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', header.Select(Escape)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(',', row.Select(Escape)));
        }
        return sb.ToString();
    }

    /// <summary>Quote a field if it contains a comma, quote, CR or LF.</summary>
    private static string Escape(string? field)
    {
        var value = field ?? string.Empty;
        var mustQuote = value.Contains(',')
                        || value.Contains('"')
                        || value.Contains('\n')
                        || value.Contains('\r');
        if (!mustQuote)
        {
            return value;
        }
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
