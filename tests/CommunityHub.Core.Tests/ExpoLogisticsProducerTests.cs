using ClosedXML.Excel;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.DocLibrary;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.4 / §3.5 — the expo rental files: TV and booth furniture, from the webshop.
/// </summary>
/// <remarks>
/// 🔴 <b>The expensive failure here is a confident zero.</b> The operator orders TV screens from the
/// venue off this total, so a webshop lookup that FAILED must never be published as "0 TVs" — he
/// would under-order and find out at check-in. That case gets the first test.
/// </remarks>
public sealed class ExpoLogisticsProducerTests
{
    private sealed class FakeSummary : ISponsorPurchaseSummary
    {
        public Dictionary<string, SponsorPurchaseSummary> ByCategory { get; } = new();

        public Task<SponsorPurchaseSummary> ByCategoryAsync(string category, CancellationToken ct = default) =>
            Task.FromResult(ByCategory.TryGetValue(category, out var s)
                ? s
                : new SponsorPurchaseSummary([], false));

        public Task<SponsorPurchaseSummary> ByProductAsync(long productId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static SponsorPurchaseRow Row(string id, string name, params (string Item, int Qty)[] lines) =>
        new(id, name, lines.Sum(l => l.Qty),
            lines.Select(l => new SponsorPurchaseLine(l.Item, l.Qty)).ToList());

    private static IXLWorksheet Sheet(GeneratedFile f, string name) =>
        new XLWorkbook(new MemoryStream(f.Content)).Worksheet(name);

    /// <summary>
    /// 🔴 THE ONE THAT MATTERS. A failed lookup leaves the previous file in place and is REPORTED —
    /// it never becomes a zero the operator would order against.
    /// </summary>
    [Fact]
    public async Task A_webshop_failure_publishes_NO_FILE_rather_than_a_zero()
    {
        var fake = new FakeSummary();
        fake.ByCategory[ExpoLogisticsProducer.TvCategory] =
            SponsorPurchaseSummary.Unavailable("WooCommerce returned 503");

        var result = await new ExpoLogisticsProducer(fake).BuildAllAsync("ELDK27");

        Assert.DoesNotContain(result.Files, f => f.FileName.Contains("tv"));
        Assert.Contains(result.Skipped, s => s.Contains("503") && s.Contains("tv"));
    }

    [Fact]
    public async Task A_genuinely_empty_webshop_result_DOES_publish_a_file()
    {
        var fake = new FakeSummary();   // no rows, CouldNotCheck false

        var result = await new ExpoLogisticsProducer(fake).BuildAllAsync("ELDK27");

        // "Nobody bought a TV" is an answer; "we could not ask" is not. Only the first is publishable.
        Assert.Contains(result.Files, f => f.FileName == "eldk27-expo-rental-tv.xlsx");
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task The_TV_file_is_sponsor_plus_count_with_a_total_to_order()
    {
        var fake = new FakeSummary();
        fake.ByCategory[ExpoLogisticsProducer.TvCategory] = new SponsorPurchaseSummary(
            [Row("c1", "Contoso", ("TV screen 55\"", 2)), Row("c2", "Fabrikam", ("TV screen 55\"", 1))],
            false);

        var file = (await new ExpoLogisticsProducer(fake).BuildAllAsync("ELDK27")).Files
            .Single(f => f.FileName == "eldk27-expo-rental-tv.xlsx");
        var ws = Sheet(file, "TV rental");

        Assert.Equal("Contoso", ws.Cell(2, 1).GetString());
        Assert.Equal(2, ws.Cell(2, 2).GetValue<int>());
        Assert.Equal("Total TVs to order", ws.Cell(4, 1).GetString());
        Assert.Equal(3, ws.Cell(4, 2).GetValue<int>());
    }

    /// <summary>
    /// 🔑 §3.5 asks for furniture TYPES, not a total. The venue delivers a chair and a bar table,
    /// not "five furnitures" — a summed quantity would be a number nobody can load onto a truck.
    /// </summary>
    [Fact]
    public async Task The_furniture_file_lists_ITEMS_per_sponsor_and_totals_per_item()
    {
        var fake = new FakeSummary();
        fake.ByCategory[ExpoLogisticsProducer.FurnitureCategory] = new SponsorPurchaseSummary(
            [
                Row("c1", "Contoso", ("Bar stool", 2), ("Bar table", 1)),
                Row("c2", "Fabrikam", ("Bar stool", 3)),
            ],
            false);

        var file = (await new ExpoLogisticsProducer(fake).BuildAllAsync("ELDK27")).Files
            .Single(f => f.FileName == "eldk27-expo-rental-furniture.xlsx");

        var perSponsor = Sheet(file, "Furniture per sponsor");
        Assert.Equal("Contoso", perSponsor.Cell(2, 1).GetString());
        Assert.Equal("Bar stool", perSponsor.Cell(2, 2).GetString());
        Assert.Equal(2, perSponsor.Cell(2, 3).GetValue<int>());

        var totals = Sheet(file, "Totals per item");
        Assert.Equal("Bar stool", totals.Cell(2, 1).GetString());
        Assert.Equal(5, totals.Cell(2, 2).GetValue<int>());     // 2 + 3 across sponsors
    }

    /// <summary>
    /// ⚠️ The summary service orders biggest-first for a SCREEN. Publishing in that order would make
    /// the file "change" whenever two sponsors swapped places without anybody buying anything — and
    /// §6.4 mails on change.
    /// </summary>
    [Fact]
    public async Task The_content_key_does_not_move_when_only_the_service_ORDER_changes()
    {
        var a = new FakeSummary();
        a.ByCategory[ExpoLogisticsProducer.TvCategory] = new SponsorPurchaseSummary(
            [Row("c1", "Contoso", ("TV", 2)), Row("c2", "Fabrikam", ("TV", 2))], false);

        var b = new FakeSummary();
        b.ByCategory[ExpoLogisticsProducer.TvCategory] = new SponsorPurchaseSummary(
            [Row("c2", "Fabrikam", ("TV", 2)), Row("c1", "Contoso", ("TV", 2))], false);

        var first = (await new ExpoLogisticsProducer(a).BuildAllAsync("ELDK27")).Files
            .Single(f => f.FileName.Contains("tv"));
        var second = (await new ExpoLogisticsProducer(b).BuildAllAsync("ELDK27")).Files
            .Single(f => f.FileName.Contains("tv"));

        Assert.Equal(first.ContentKey, second.ContentKey);
    }

    [Fact]
    public async Task A_real_change_in_what_was_bought_DOES_move_the_key()
    {
        var a = new FakeSummary();
        a.ByCategory[ExpoLogisticsProducer.TvCategory] = new SponsorPurchaseSummary(
            [Row("c1", "Contoso", ("TV", 2))], false);

        var b = new FakeSummary();
        b.ByCategory[ExpoLogisticsProducer.TvCategory] = new SponsorPurchaseSummary(
            [Row("c1", "Contoso", ("TV", 3))], false);

        var first = (await new ExpoLogisticsProducer(a).BuildAllAsync("ELDK27")).Files
            .Single(f => f.FileName.Contains("tv"));
        var second = (await new ExpoLogisticsProducer(b).BuildAllAsync("ELDK27")).Files
            .Single(f => f.FileName.Contains("tv"));

        Assert.NotEqual(first.ContentKey, second.ContentKey);
    }
}
