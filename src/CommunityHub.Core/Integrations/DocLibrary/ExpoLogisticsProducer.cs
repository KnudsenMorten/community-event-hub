using ClosedXML.Excel;
using CommunityHub.Core.Integrations;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// §6.4 / §3.5 — the Bella Center EXPO files: TV rental and booth furniture, from the webshop.
/// </summary>
/// <remarks>
/// <para>§3.5: <i>"Built from webshop orders: sponsor + TV count; sponsor + furniture types +
/// counts."</i></para>
///
/// <para>🔒 <b>It asks <see cref="SponsorPurchaseSummaryService"/>, which is already "THE one place
/// that answers 'which sponsors bought this, and how many?'" (§687/§666).</b> That service is
/// deliberately shared between the sponsor's own task ("you have booked 2 TVs") and the organizer's
/// logistics panel ("order 37 TVs") so those two numbers cannot drift. A third query over the same
/// orders — which is what this file would have been — is exactly how they would.</para>
///
/// <para>🔒 <b>The CATEGORIES are the specific ones, never the umbrella.</b> §687.7, operator
/// 2026-07-29: <i>"they are all part of booth options, so dont use that"</i> — "Booth Option" also
/// covers attendee-bag packaging and extra staff tickets, so matching it would put a sponsor who
/// bought neither a TV nor furniture onto a venue rental order.</para>
///
/// <para>🔴 <b>A FAILED lookup must never be published as a zero.</b> The summary service returns
/// three states, and its own remarks say why that matters more here than anywhere else: the operator
/// orders TV screens from the venue off this total, so <i>"a failure rendering as a confident 0
/// means he under-orders and finds out at check-in"</i>. When the webshop cannot be reached, this
/// producer emits NO FILE — leaving yesterday's correct file in place — and says so.</para>
/// </remarks>
public sealed class ExpoLogisticsProducer
{
    /// <summary>The webshop category for the booth TV (§687.7 — id 99).</summary>
    public const string TvCategory = "TV Rental";

    /// <summary>The webshop category for booth furniture (§687.7 — id 102).</summary>
    public const string FurnitureCategory = "Booth Furniture";

    private readonly ISponsorPurchaseSummary _purchases;

    public ExpoLogisticsProducer(ISponsorPurchaseSummary purchases) => _purchases = purchases;

    /// <summary>What one expo build produced, and what it deliberately did not.</summary>
    /// <param name="Skipped">
    /// Files NOT produced because the webshop could not be read, with the reason. 🔒 Never silently
    /// empty: an unpublished file is a fact the run has to report.
    /// </param>
    public sealed record ExpoResult(
        IReadOnlyList<GeneratedFile> Files,
        IReadOnlyList<string> Skipped);

    public async Task<ExpoResult> BuildAllAsync(
        string eventShortName, CancellationToken ct = default)
    {
        var files = new List<GeneratedFile>();
        var skipped = new List<string>();

        var tv = await _purchases.ByCategoryAsync(TvCategory, ct);
        if (tv.CouldNotCheck)
        {
            skipped.Add(
                $"{LogisticsFileNames.ExpoTvRental(eventShortName)}: the webshop could not be read "
                + $"({tv.Reason ?? "no reason given"}). The previous file is left in place — "
                + "publishing a zero would under-order the venue.");
        }
        else
        {
            files.Add(BuildTv(LogisticsFileNames.ExpoTvRental(eventShortName), tv));
        }

        var furniture = await _purchases.ByCategoryAsync(FurnitureCategory, ct);
        if (furniture.CouldNotCheck)
        {
            skipped.Add(
                $"{LogisticsFileNames.ExpoFurnitureRental(eventShortName)}: the webshop could not "
                + $"be read ({furniture.Reason ?? "no reason given"}). The previous file is left in "
                + "place.");
        }
        else
        {
            files.Add(BuildFurniture(
                LogisticsFileNames.ExpoFurnitureRental(eventShortName), furniture));
        }

        return new ExpoResult(files, skipped);
    }

    /// <summary>§3.5: "sponsor + TV count" — one product, so a quantity is the right answer.</summary>
    private static GeneratedFile BuildTv(string fileName, SponsorPurchaseSummary summary)
    {
        var rows = Ordered(summary);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("TV rental");
        Header(ws, "Sponsor", "TVs");

        var row = 2;
        foreach (var r in rows)
        {
            ws.Cell(row, 1).Value = r.CompanyName;
            ws.Cell(row, 2).Value = r.Quantity;
            row++;
        }
        if (rows.Count > 0)
        {
            ws.Cell(row, 1).Value = "Total TVs to order";
            ws.Cell(row, 2).Value = rows.Sum(r => r.Quantity);
            ws.Range(row, 1, row, 2).Style.Font.Bold = true;
        }
        Finish(ws);

        var key = GeneratedFile.KeyOf(rows.Select(r => $"{r.CompanyId}|{r.CompanyName}|{r.Quantity}"));

        // The venue orders TVs, not sponsors — the headline is the number that gets rented.
        return new GeneratedFile(fileName, Save(wb), GeneratedFile.XlsxContentType, key,
            LogisticsHeadline.Of(rows.Sum(r => r.Quantity), "TV", "TVs"));
    }

    /// <summary>
    /// §3.5: "sponsor + furniture TYPES + counts".
    /// </summary>
    /// <remarks>
    /// 🔑 <b>The types are the point, and a total would destroy them.</b> The summary service says
    /// so itself: a category quantity ("3 storage + 2 handling = 5") reads like a number and tells
    /// you nothing. The venue delivers a chair and a bar table, not five furnitures — so this file
    /// is one row per sponsor PER ITEM.
    /// </remarks>
    private static GeneratedFile BuildFurniture(string fileName, SponsorPurchaseSummary summary)
    {
        var rows = Ordered(summary);

        using var wb = new XLWorkbook();

        var ws = wb.Worksheets.Add("Furniture per sponsor");
        Header(ws, "Sponsor", "Item", "Count");
        var row = 2;
        foreach (var r in rows)
        {
            foreach (var line in r.Lines.OrderBy(l => l.ProductName, StringComparer.Ordinal))
            {
                ws.Cell(row, 1).Value = r.CompanyName;
                ws.Cell(row, 2).Value = line.ProductName;
                ws.Cell(row, 3).Value = line.Quantity;
                row++;
            }
        }
        Finish(ws);

        // What the venue actually loads onto the truck: the total per ITEM across all sponsors.
        var totals = wb.Worksheets.Add("Totals per item");
        Header(totals, "Item", "Total");
        var trow = 2;
        foreach (var g in rows
                     .SelectMany(r => r.Lines)
                     .GroupBy(l => l.ProductName, StringComparer.Ordinal)
                     .Select(g => new { Item = g.Key, Total = g.Sum(l => l.Quantity) })
                     .OrderByDescending(x => x.Total)
                     .ThenBy(x => x.Item, StringComparer.Ordinal))
        {
            totals.Cell(trow, 1).Value = g.Item;
            totals.Cell(trow, 2).Value = g.Total;
            trow++;
        }
        Finish(totals);

        var key = GeneratedFile.KeyOf(rows.SelectMany(r =>
            r.Lines
                .OrderBy(l => l.ProductName, StringComparer.Ordinal)
                .Select(l => $"{r.CompanyId}|{l.ProductName}|{l.Quantity}")));

        // Items to rent across every sponsor — the figure the furniture order is placed on.
        return new GeneratedFile(fileName, Save(wb), GeneratedFile.XlsxContentType, key,
            LogisticsHeadline.Of(rows.SelectMany(r => r.Lines).Sum(l => l.Quantity), "item", "items"));
    }

    /// <summary>
    /// Stable order by company — the service sorts biggest-first for a screen, which would make the
    /// file "change" every time two sponsors swapped places without anybody buying anything.
    /// </summary>
    private static List<SponsorPurchaseRow> Ordered(SponsorPurchaseSummary summary) =>
        summary.Rows
            .OrderBy(r => r.CompanyName, StringComparer.Ordinal)
            .ThenBy(r => r.CompanyId, StringComparer.Ordinal)
            .ToList();

    private static void Header(IXLWorksheet ws, params string[] headers)
    {
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Range(1, 1, 1, headers.Length).Style.Font.Bold = true;
    }

    private static void Finish(IXLWorksheet ws)
    {
        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
    }

    private static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
