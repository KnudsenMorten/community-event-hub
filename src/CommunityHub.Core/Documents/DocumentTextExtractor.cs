using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace CommunityHub.Core.Documents;

/// <summary>
/// Best-effort PLAIN-TEXT extractor for the operator-dropped grounding documents
/// (REQUIREMENTS §152). Turns the bytes of a supported document into readable text the
/// AI Community Helper can quote from. Supports markdown / plain text, Word (.docx),
/// PDF (.pdf) and Excel (.xlsx).
///
/// <para>ROBUSTNESS CONTRACT: this NEVER throws. A null/empty payload, an unsupported
/// extension, or a corrupt / password-protected / locked file all return
/// <c>null</c> — the whole body is wrapped in try/catch — so one bad file can never
/// sink the grounding batch. Callers filter with <see cref="IsSupported"/> before
/// downloading bytes.</para>
/// </summary>
public static class DocumentTextExtractor
{
    private static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".txt", ".docx", ".pdf", ".xlsx",
        };

    /// <summary>
    /// True when <paramref name="fileName"/> has an extension this extractor can read
    /// (.md / .txt / .docx / .pdf / .xlsx). Lets the provider skip unsupported files
    /// BEFORE spending a download.
    /// </summary>
    public static bool IsSupported(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        SupportedExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>
    /// Extract readable text from <paramref name="bytes"/> using the document type implied
    /// by <paramref name="fileName"/>'s extension. Returns null for empty input, an
    /// unsupported type, or any failure (corrupt / locked / encrypted) — never throws.
    /// </summary>
    public static string? Extract(string? fileName, byte[]? bytes)
    {
        if (string.IsNullOrWhiteSpace(fileName) || bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            var text = ext switch
            {
                ".md" or ".txt" => ExtractPlainText(bytes),
                ".docx" => ExtractDocx(bytes),
                ".pdf" => ExtractPdf(bytes),
                ".xlsx" => ExtractXlsx(bytes),
                _ => null,
            };

            return CollapseBlankLines(text);
        }
        catch
        {
            // Corrupt / locked / password-protected / unexpected payload — stay inert.
            return null;
        }
    }

    /// <summary>Decode UTF-8 text, stripping a leading byte-order mark if present.</summary>
    private static string ExtractPlainText(byte[] bytes)
    {
        // new UTF8Encoding(false) does not EMIT a BOM, but GetString still keeps a leading
        // U+FEFF from the bytes, so trim it explicitly.
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes);
        return text.TrimStart('﻿');
    }

    /// <summary>
    /// Read a Word document body. Joins each paragraph's InnerText with a newline —
    /// <c>Body.InnerText</c> alone concatenates every run with NO separator, so headings
    /// and paragraphs would run together; per-paragraph joining keeps it readable.
    /// </summary>
    private static string? ExtractDocx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return null;

        var paragraphs = body.Descendants<Paragraph>()
            .Select(p => p.InnerText)
            .Where(t => !string.IsNullOrWhiteSpace(t));

        return string.Join("\n", paragraphs);
    }

    /// <summary>Extract the text of every PDF page, joined by newlines.</summary>
    private static string ExtractPdf(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        var pages = pdf.GetPages()
            .Select(p => p.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t));
        return string.Join("\n", pages);
    }

    /// <summary>
    /// Flatten a workbook: each used worksheet is prefixed with its sheet name, cells are
    /// joined by a tab and rows by a newline. Only the used range is read.
    /// </summary>
    private static string ExtractXlsx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);

        var sb = new StringBuilder();
        foreach (var ws in wb.Worksheets)
        {
            var used = ws.RangeUsed();
            if (used is null) continue;

            sb.Append("Sheet: ").Append(ws.Name).Append('\n');
            foreach (var row in used.Rows())
            {
                // CellText renders time-only cells as a clean clock time ("12:30") and
                // flattens rich-text/HTML cells (the agenda's "<p><span>…</span></p>"
                // description cells) to plain text.
                var cells = row.Cells().Select(CellText);
                sb.Append(string.Join("\t", cells)).Append('\n');
            }
            sb.Append('\n');
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Render a cell as readable text. Excel time-only values are stored as a fraction on an
    /// EPOCH date, which ClosedXML surfaces as a datetime like <c>1/1/1970 12:30</c>; for those
    /// we emit just the clock time (<c>12:30</c>) so the agenda reads cleanly. Pure dates and
    /// real datetimes keep an ISO form; everything else is the formatted string, HTML-stripped.
    /// </summary>
    private static string CellText(IXLCell c)
    {
        if (c.DataType == XLDataType.DateTime)
        {
            try
            {
                var dt = c.GetDateTime();
                if (dt.Year <= 1970)
                {
                    return dt.TimeOfDay == TimeSpan.Zero ? string.Empty : dt.ToString("HH:mm");
                }
                return dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm");
            }
            catch { /* fall through to the formatted string */ }
        }
        return StripHtml(c.GetFormattedString());
    }

    /// <summary>
    /// Flatten an HTML / rich-text cell value to plain text: drop tags, decode entities,
    /// collapse whitespace. Fast-paths the common no-markup case so plain values
    /// (e.g. "12:30") are returned untouched.
    /// </summary>
    private static string StripHtml(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        if (s.IndexOf('<') < 0) return s;
        var noTags = Regex.Replace(s, "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        return Regex.Replace(decoded, "\\s+", " ").Trim();
    }

    /// <summary>Collapse runs of 3+ blank lines down to a single blank line; trim ends.</summary>
    private static string? CollapseBlankLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        var blankRun = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                blankRun++;
                if (blankRun <= 1) sb.Append('\n');
            }
            else
            {
                blankRun = 0;
                sb.Append(line).Append('\n');
            }
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
