using System.Globalization;
using CommunityHub.Core.Evaluation;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace CommunityHub.Core.Visa;

/// <summary>
/// §1374 — the visa form as a PDF: one applicant per page group, the five sections as label/value rows
/// (from <see cref="VisaApplicationService.Fields"/>), the application ID on top. One PDF can hold one
/// applicant or all of them.
/// </summary>
public static class VisaApplicationPdf
{
    private const double Left = 50;
    private const double Top = 50;
    private const double Bottom = 60;
    private const double LabelWidth = 190;

    public static byte[] Render(IEnumerable<VisaRow> rows, string eventName, DateTimeOffset generatedAt)
    {
        EmbeddedFontResolver.EnsureInstalled();
        var doc = new PdfDocument();
        doc.Info.Title = $"Visa applications - {eventName}";

        var f = EmbeddedFontResolver.FamilyName;
        var title = new XFont(f, 16, XFontStyleEx.Bold);
        var h2 = new XFont(f, 11, XFontStyleEx.Bold);
        var body = new XFont(f, 9.5, XFontStyleEx.Regular);
        var bodyBold = new XFont(f, 9.5, XFontStyleEx.Bold);
        var small = new XFont(f, 8, XFontStyleEx.Regular);
        var ink = new XSolidBrush(XColor.FromArgb(26, 26, 46));
        var muted = new XSolidBrush(XColor.FromArgb(107, 114, 128));
        var hair = new XPen(XColor.FromArgb(210, 210, 210), 0.5);

        PdfPage page = null!;
        XGraphics g = null!;
        double y = 0, width = 0;
        var header = $"Visa applications - {eventName} - generated {generatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC";

        void NewPage()
        {
            g?.Dispose();
            page = doc.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            g = XGraphics.FromPdfPage(page);
            width = page.Width.Point - Left * 2;
            y = Top;
            g.DrawString(header, small, muted, new XPoint(Left, 30));
        }
        double PageBottom() => page.Height.Point - Bottom;
        void EnsureRoom(double h) { if (y + h > PageBottom()) NewPage(); }

        List<string> Wrap(string text, XFont font, double maxWidth)
        {
            var lines = new List<string>();
            foreach (var raw in (string.IsNullOrEmpty(text) ? " " : text).Replace("\r\n", "\n").Split('\n'))
            {
                var line = string.Empty;
                foreach (var word in raw.Split(' '))
                {
                    var candidate = line.Length == 0 ? word : line + " " + word;
                    if (line.Length == 0 || g.MeasureString(candidate, font).Width <= maxWidth) line = candidate;
                    else { lines.Add(line); line = word; }
                }
                lines.Add(line);
            }
            return lines;
        }

        var any = false;
        foreach (var r in rows)
        {
            any = true;
            NewPage();
            var a = r.Application;
            g.DrawString(r.FullName, title, ink, new XPoint(Left, y + title.Size)); y += title.Size * 1.6;
            var status = a?.SubmittedAt is DateTimeOffset s
                ? $"{r.Role} · {r.Email} · submitted {s.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
                  + (a.UpdatedAt is DateTimeOffset u && u > s ? $", last changed {u.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : string.Empty)
                : $"{r.Role} · {r.Email} · NOT SUBMITTED YET";
            g.DrawString(status, body, muted, new XPoint(Left, y + body.Size)); y += body.Size * 1.8;
            g.DrawString($"Application ID: {(string.IsNullOrWhiteSpace(a?.ApplicationId) ? "(not set)" : a!.ApplicationId)}",
                bodyBold, ink, new XPoint(Left, y + bodyBold.Size));
            y += bodyBold.Size * 2.2;

            if (a?.SubmittedAt is null) continue;

            string? section = null;
            foreach (var field in VisaApplicationService.Fields)
            {
                var lh = body.Size * 1.45;
                var labelLines = Wrap(field.Label, body, LabelWidth - 8);
                var valueLines = Wrap(field.Value(a), body, width - LabelWidth);
                var h = Math.Max(labelLines.Count, valueLines.Count) * lh + 6;
                if (field.Section != section)
                {
                    EnsureRoom(h2.Size * 2.4 + h);      // a heading keeps its first row
                    y += 6;
                    g.DrawString(field.Section, h2, ink, new XPoint(Left, y + h2.Size));
                    y += h2.Size * 1.8;
                    section = field.Section;
                }
                EnsureRoom(h);
                for (var i = 0; i < labelLines.Count; i++)
                    g.DrawString(labelLines[i], body, muted, new XPoint(Left, y + body.Size + i * lh));
                for (var i = 0; i < valueLines.Count; i++)
                    g.DrawString(valueLines[i], body, ink, new XPoint(Left + LabelWidth, y + body.Size + i * lh));
                y += h;
                g.DrawLine(hair, Left, y - 3, Left + width, y - 3);
            }
        }

        if (!any)
        {
            NewPage();
            g.DrawString("No visa applications.", body, ink, new XPoint(Left, y + body.Size));
        }
        g.Dispose();

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }
}
