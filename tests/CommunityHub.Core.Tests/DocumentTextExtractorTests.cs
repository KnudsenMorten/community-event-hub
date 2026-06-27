using System.Text;
using ClosedXML.Excel;
using CommunityHub.Core.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The best-effort plain-text extractor for §152 grounding documents. Proves a per-format
/// round-trip (.txt/.md incl. BOM, .docx, .xlsx, .pdf), that unsupported extensions and
/// corrupt / empty payloads return null, and that it NEVER throws. No external calls.
/// </summary>
public class DocumentTextExtractorTests
{
    // ---- IsSupported -------------------------------------------------------

    [Theory]
    [InlineData("a.md", true)]
    [InlineData("a.txt", true)]
    [InlineData("a.docx", true)]
    [InlineData("a.pdf", true)]
    [InlineData("a.xlsx", true)]
    [InlineData("A.MD", true)]      // case-insensitive
    [InlineData("a.png", false)]
    [InlineData("a.zip", false)]
    [InlineData("noext", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupported_matches_only_text_doc_formats(string? name, bool expected)
    {
        Assert.Equal(expected, DocumentTextExtractor.IsSupported(name));
    }

    // ---- plain text / markdown --------------------------------------------

    [Fact]
    public void Extract_txt_decodes_utf8()
    {
        var bytes = Encoding.UTF8.GetBytes("Hello grounding world\nsecond line");
        var text = DocumentTextExtractor.Extract("notes.txt", bytes);
        Assert.Contains("Hello grounding world", text);
        Assert.Contains("second line", text);
    }

    [Fact]
    public void Extract_md_strips_leading_utf8_bom()
    {
        // UTF-8 BOM (EF BB BF) prepended to the markdown (GetBytes alone does not emit it).
        var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var withBom = enc.GetPreamble().Concat(enc.GetBytes("# Title")).ToArray();
        Assert.Equal(0xEF, withBom[0]); // sanity: the BOM is present in the bytes
        var text = DocumentTextExtractor.Extract("readme.md", withBom);
        Assert.NotNull(text);
        Assert.StartsWith("# Title", text);
        Assert.DoesNotContain('﻿', text!); // BOM stripped
    }

    // ---- docx --------------------------------------------------------------

    [Fact]
    public void Extract_docx_joins_paragraphs_with_newlines()
    {
        var bytes = BuildDocx("First heading", "Second paragraph");
        var text = DocumentTextExtractor.Extract("doc.docx", bytes);
        Assert.NotNull(text);
        Assert.Contains("First heading", text);
        Assert.Contains("Second paragraph", text);
        // Per-paragraph newline join keeps the two blocks separable (not run together).
        Assert.Contains("First heading\nSecond paragraph", text);
    }

    // ---- xlsx --------------------------------------------------------------

    [Fact]
    public void Extract_xlsx_flattens_cells_with_sheet_name()
    {
        var bytes = BuildXlsx();
        var text = DocumentTextExtractor.Extract("grid.xlsx", bytes);
        Assert.NotNull(text);
        Assert.Contains("Schedule", text);  // sheet name prefix
        Assert.Contains("Lunch", text);
        Assert.Contains("12:00", text);
    }

    // ---- pdf ---------------------------------------------------------------

    [Fact]
    public void Extract_pdf_reads_page_text()
    {
        var bytes = BuildPdf("GroundingPdfMarker");
        var text = DocumentTextExtractor.Extract("doc.pdf", bytes);
        Assert.NotNull(text);
        Assert.Contains("GroundingPdfMarker", text);
    }

    // ---- unsupported / corrupt / empty (never throws) ----------------------

    [Theory]
    [InlineData("image.png")]
    [InlineData("archive.zip")]
    public void Extract_unsupported_extension_returns_null(string name)
    {
        Assert.Null(DocumentTextExtractor.Extract(name, new byte[] { 1, 2, 3, 4 }));
    }

    [Theory]
    [InlineData("bad.docx")]
    [InlineData("bad.pdf")]
    [InlineData("bad.xlsx")]
    public void Extract_corrupt_bytes_returns_null_never_throws(string name)
    {
        var garbage = Encoding.UTF8.GetBytes("this is not a real office/pdf document at all");
        var ex = Record.Exception(() => DocumentTextExtractor.Extract(name, garbage));
        Assert.Null(ex);
        Assert.Null(DocumentTextExtractor.Extract(name, garbage));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.md")]
    [InlineData("a.docx")]
    [InlineData("a.pdf")]
    [InlineData("a.xlsx")]
    public void Extract_null_or_empty_bytes_returns_null(string name)
    {
        Assert.Null(DocumentTextExtractor.Extract(name, null));
        Assert.Null(DocumentTextExtractor.Extract(name, Array.Empty<byte>()));
    }

    // ---- builders (in-memory documents; no files on disk) ------------------

    private static byte[] BuildDocx(params string[] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            foreach (var p in paragraphs)
            {
                body.AppendChild(new Paragraph(new Run(new Text(p))));
            }
            main.Document = new Document(body);
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static byte[] BuildXlsx()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Schedule");
        ws.Cell("A1").Value = "Item";
        ws.Cell("B1").Value = "Time";
        ws.Cell("A2").Value = "Lunch";
        ws.Cell("B2").Value = "12:00";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static byte[] BuildPdf(string marker)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4, isPortrait: true);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(marker, 12, new PdfPoint(25, 700), font);
        return builder.Build();
    }
}
