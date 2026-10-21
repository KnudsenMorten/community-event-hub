using CommunityHub.Core.Tasks.Data;
using CommunityHub.Core.Tasks.Definitions;
using CommunityHub.Core.Tasks.Parsing;
using CommunityHub.Core.Tasks.Rendering;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1305 — every sponsor task that sells something has a button straight to its place in the CEH shop.
/// </summary>
/// <remarks>
/// Operator 2026-09-26: <i>"read the tasks, as there could be missing a button to order it … read all tasks and verify
/// it links to the relevant category in the webshop so it is easy to order"</i>. Placeholders are substituted BEFORE
/// parsing, so the shop's in-hub link reached the parser under a file still declaring <c>interstitial: webshop</c> —
/// which the parser refused, dropping the button from all five tasks while the shop was on.
/// </remarks>
public class SponsorTaskShopButtonTests
{
    // The in-hub values SponsorTaskPlaceholderBuilder sets while the CEH shop is on.
    private static readonly Dictionary<string, string> ShopOn = new()
    {
        ["configuratorUrl"] = "/Sponsor/Shop",
        ["furnitureShopUrl"] = "/Sponsor/Shop?Group=Furniture",
        ["bagShopUrl"] = "/Sponsor/Shop?Group=BoothOptions&Find=bag",
        ["tvShopUrl"] = "/Sponsor/Shop?Group=BoothOptions&Find=TV",
        ["staffShopUrl"] = "/Sponsor/Shop?Group=BoothOptions&Find=staff",
        ["shipmentShopUrl"] = "/Sponsor/Shop?Group=PackageHandling",
        ["shopName"] = "Sponsor Shop",
    };

    [Theory]
    [InlineData("sponsor.attendee-bag-content", "/Sponsor/Shop?Group=BoothOptions&amp;Find=bag")]
    [InlineData("sponsor.tv-rental", "/Sponsor/Shop?Group=BoothOptions&amp;Find=TV")]
    [InlineData("sponsor.register-booth-members", "/Sponsor/Shop?Group=BoothOptions&amp;Find=staff")]
    [InlineData("sponsor.pre-event-shipment", "/Sponsor/Shop?Group=PackageHandling")]
    [InlineData("sponsor.booth-layout", "/Sponsor/Shop?Group=Furniture")]
    public void The_order_button_opens_the_shop_on_the_product(string key, string href)
    {
        var raw = new TaskBodyStore().LoadRaw(TaskDefinitionRegistry.Shipped.ByKey(key)!.BodyRef);
        foreach (var (k, v) in ShopOn) raw = raw.Replace("{{" + k + "}}", v);

        var body = TaskBodyParser.Parse(raw);
        var html = new TaskBodyHtmlRenderer().Render(body, new TaskRenderContext(
            new Dictionary<string, string>(), new Dictionary<string, TaskDataResult>(), taskPageUrl: "x", taskId: 1));

        Assert.Contains($"class=\"task-link-btn\" href=\"{href}\"", html);
        // In-hub: no "you are leaving for a different system" notice.
        Assert.DoesNotContain("data-webshop-interstitial", html);
    }
}
