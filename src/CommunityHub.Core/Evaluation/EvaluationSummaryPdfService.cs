using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §6.6 — the COMBINED evaluation summary across every session of an edition, as one PDF.
/// </summary>
/// <remarks>
/// <para>Work order §6.6: <i>"Generates a combined summary PDF across all sessions into the same
/// folder."</i> The per-session reports already exist and stay where they are; this is the one
/// document that lets somebody see the whole event at once.</para>
///
/// <para>🔒 <b>It renders what <see cref="EvaluationScoreService"/> computed</b> — the same figures
/// the per-session reports and the organizer screens use. A second scoring pass over the same
/// responses is how a summary comes to disagree with the reports it summarises, and this is the
/// document most likely to be forwarded to somebody who will never open the others.</para>
///
/// <para>⚠️ <b>Sessions with NO responses are listed, not omitted.</b> A summary that silently drops
/// them reads as though every session was rated — and "nobody rated this" is exactly the fact an
/// organizer needs to see next year when deciding what to run again.</para>
/// </remarks>
public sealed class EvaluationSummaryPdfService
{
    /// <summary>One row of the combined summary.</summary>
    public sealed record Row(
        string Title, string? Room, DateTimeOffset? ScheduledStart,
        int Responses, double? Score);

    /// <param name="Pooled">The whole event's figure, so a session reads against its own edition.</param>
    public sealed record SummaryData(
        string EventName,
        DateTimeOffset GeneratedAt,
        IReadOnlyList<Row> Rows,
        double? Pooled,
        int TotalResponses);

    /// <summary>The file name the consolidation writes. Deterministic — a re-run REPLACES it.</summary>
    public static string FileNameFor(string eventCode) =>
        $"{Slug(eventCode)}-all-sessions-evaluation-summary.pdf";

    private static string Slug(string value)
    {
        var chars = (value ?? string.Empty).Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var joined = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return joined.Length == 0 ? "event" : joined;
    }

    public byte[] Render(SummaryData d)
    {
        ArgumentNullException.ThrowIfNull(d);

        // 🔴 §750.7 — MUST come before any XFont is constructed: an Azure Linux container has no
        // OS fonts, and PdfSharpCore otherwise throws "No Fonts installed on this device!".
        EmbeddedFontResolver.EnsureInstalled();

        var doc = new PdfDocument();
        doc.Info.Title = $"All-session evaluation summary — {d.EventName}";
        doc.Info.Subject = d.EventName;

        var F = EmbeddedFontResolver.FamilyName;
        var h1 = new XFont(F, 17, XFontStyleEx.Bold);
        var h2 = new XFont(F, 10.5, XFontStyleEx.Bold);
        var body = new XFont(F, 9.5, XFontStyleEx.Regular);
        var small = new XFont(F, 8, XFontStyleEx.Regular);

        var inkB = new XSolidBrush(EvaluationReportLayout.Ink);
        var mutedB = new XSolidBrush(EvaluationReportLayout.Muted);
        var brandB = new XSolidBrush(EvaluationReportLayout.BrandDark);
        var hairline = new XPen(EvaluationReportLayout.Hairline, 0.6);

        // 🔒 Invariant + whole numbers, for the §750.8 reasons: the render host is Danish, so the
        // current culture would print "75,0" into an English document and make the output depend on
        // which machine produced it.
        static string Num(double v) =>
            Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

        const double left = 46;
        PdfPage page = null!;
        XGraphics gfx = null!;
        double y = 0;
        double width = 0;

        void NewPage()
        {
            page = doc.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            gfx = XGraphics.FromPdfPage(page);
            width = page.Width - (left * 2);
            y = 40;
        }

        void Header()
        {
            gfx.DrawString("Session evaluations — all sessions", h1, brandB, new XPoint(left, y));
            y += 22;
            gfx.DrawString(d.EventName, h2, inkB, new XPoint(left, y));
            y += 16;
            gfx.DrawString(
                $"Generated {d.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC · "
                + $"{d.Rows.Count} session(s) · {d.TotalResponses} response(s)"
                + (d.Pooled is { } p ? $" · event score {Num(p)}" : " · no event score yet"),
                small, mutedB, new XPoint(left, y));
            y += 22;

            gfx.DrawString("Session", h2, inkB, new XPoint(left, y));
            gfx.DrawString("Responses", h2, inkB, new XPoint(left + width - 150, y));
            gfx.DrawString("Score", h2, inkB, new XPoint(left + width - 60, y));
            y += 6;
            gfx.DrawLine(hairline, left, y, left + width, y);
            y += 12;
        }

        NewPage();
        Header();

        foreach (var r in d.Rows)
        {
            if (y > page.Height - 70)
            {
                NewPage();
                Header();
            }

            var title = r.Title.Length > 62 ? r.Title[..61] + "…" : r.Title;
            gfx.DrawString(title, body, inkB, new XPoint(left, y));

            gfx.DrawString(
                r.Responses.ToString(CultureInfo.InvariantCulture),
                body, inkB, new XPoint(left + width - 150, y));

            // ⚠️ A session nobody rated shows "—", never 0. A zero SCORE is a real and terrible
            // result; "nobody pressed a button" is a different fact entirely, and printing 0 for it
            // would libel a session that was simply never rated.
            gfx.DrawString(
                r.Score is { } s ? Num(s) : "—",
                body, r.Score is null ? mutedB : inkB, new XPoint(left + width - 60, y));

            y += 13;

            var meta = string.Join(" · ", new[]
                {
                    r.Room,
                    r.ScheduledStart?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                }
                .Where(x => !string.IsNullOrWhiteSpace(x)));

            if (meta.Length > 0)
            {
                gfx.DrawString(meta, small, mutedB, new XPoint(left, y));
                y += 11;
            }

            y += 4;
        }

        if (d.Rows.Count == 0)
        {
            gfx.DrawString(
                "No sessions carry evaluation data for this edition.",
                body, mutedB, new XPoint(left, y));
        }

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }
}
