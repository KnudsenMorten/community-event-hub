using CommunityHub.Core.Domain;
using CommunityHub.Core.Tasks;
using CommunityHub.Core.Tasks.Data;
using CommunityHub.Core.Tasks.Definitions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §688 — placeholder VALUES are substituted before parsing, so what they contain actually works.
/// </summary>
/// <remarks>
/// <para>🔴 These are regression tests for three live defects the operator found on PROD task pages,
/// all with one root cause: values were injected as TEXT after the body was parsed.</para>
///
/// <para>🔒 <b>Why the existing catalogue tests could not see them.</b>
/// <c>TaskBodyCatalogTests</c> renders with a SYNTHETIC map — <c>furnitureSpec</c> is the literal
/// string <c>"[furnitureSpec]"</c>. A stand-in with no nested placeholder, no markup and no e-mail
/// address cannot fail in any of these ways. **Fixtures that are simpler than production data hide
/// exactly the bugs production data causes**, which is why these use realistic values.</para>
/// </remarks>
public class TaskPlaceholderSubstitutionTests
{
    private const string BodyRef = "sponsor/booth-layout";

    private static TaskBodyService Service() =>
        new(TaskDefinitionRegistry.Shipped,
            new TaskBodyStore(),
            new TaskDataResolver(
                Array.Empty<ITaskDataProvider>(),
                new MemoryCache(new MemoryCacheOptions()),
                NullLogger<TaskDataResolver>.Instance));

    private static ParticipantTask BoothLayoutTask() => new()
    {
        Id = 1,
        EventId = 1,
        Title = "Choose your booth layout (table, chairs)",
        SourceKey = "sponsor:12:choose-your-booth-layout-table-chairs",
    };

    /// <summary>The shape the REAL edition config has: a value that contains another placeholder.</summary>
    private static Dictionary<string, string> RealisticPlaceholders() => new(StringComparer.OrdinalIgnoreCase)
    {
        // furnitureSpec genuinely contains {{couponCode}} in sponsor.eldk27.json.
        ["furnitureSpec"] =
            "Coupon value: **99 EURO**\n\n__Step 1 — choose your chairs__\n"
            + "* Booth: Chair, Tolix Stool Outdoor, black – EURO 35\n"
            + "Use code **{{couponCode}}** at checkout.",
        ["couponCode"] = "eldk27-gold-booth-included",
        ["configuratorUrl"] = "https://shop.example/sponsor",
        ["editionCode"] = "ELDK27",
        ["wallContactName"] = "The design lead",
        ["wallContactEmail"] = "design@example.dk",
    };

    private static async Task<string> RenderAsync(IReadOnlyDictionary<string, string> map)
    {
        var result = await Service().RenderAsync(
            BoothLayoutTask(), TaskBodyFlavour.Html, map);
        Assert.NotNull(result);
        return result!.Html;
    }

    [Fact]
    public async Task A_placeholder_inside_a_placeholder_value_resolves()
    {
        // 🔴 THE LIVE BUG: sponsors saw a literal "**{{couponCode}}**" on the booth-layout task,
        // because furnitureSpec was substituted and then nothing looked at it again. The old
        // seed-time pull only got this right by accident of pass ordering.
        var html = await RenderAsync(RealisticPlaceholders());

        Assert.Contains("eldk27-gold-booth-included", html);
        Assert.DoesNotContain("{{couponCode}}", html);
        Assert.DoesNotContain("{{", html);
    }

    [Fact]
    public async Task Markup_inside_a_placeholder_value_is_parsed_not_shown_raw()
    {
        // 🔴 THE LIVE BUG: furnitureSpec's __headings__, **bold** and "* bullets" all rendered as
        // literal marker characters in one run-on paragraph.
        var html = await RenderAsync(RealisticPlaceholders());

        Assert.Contains("<strong>99 EURO</strong>", html);
        Assert.Contains("<li>", html);                 // the bullet became a real list item
        Assert.DoesNotContain("__Step 1", html);       // the underline markers are gone
        Assert.DoesNotContain("**", html);
    }

    [Fact]
    public async Task An_email_inside_a_placeholder_value_becomes_a_mailto_link()
    {
        // 🔴 THE LIVE BUG (operator: "email to sabine is not mailto format"): link detection runs in
        // the PARSER over literal text, so an address arriving after parsing stayed plain text.
        var map = RealisticPlaceholders();
        map["furnitureSpec"] = "Questions? Write to design@example.dk first.";

        var html = await RenderAsync(map);

        Assert.Contains("mailto:design@example.dk", html);
    }

    [Fact]
    public async Task An_unknown_placeholder_renders_as_nothing_and_is_reported()
    {
        var map = RealisticPlaceholders();
        map.Remove("couponCode");

        var result = await Service().RenderAsync(BoothLayoutTask(), TaskBodyFlavour.Html, map);

        Assert.NotNull(result);
        Assert.DoesNotContain("{{", result!.Html);     // never literal braces on a sponsor's page
        Assert.Contains("couponCode", result.Missing);
    }

    [Fact]
    public async Task An_EMPTY_placeholder_value_is_reported_too()
    {
        // 🔴 §688.4 — the wall-spec button rendered with no destination because its key EXISTED with
        // a blank value. Nothing was "missing", so nothing was logged, and the control looked broken
        // to the sponsor. Blank is a defect here, not a value.
        var map = RealisticPlaceholders();
        map["couponCode"] = "";

        var result = await Service().RenderAsync(BoothLayoutTask(), TaskBodyFlavour.Html, map);

        Assert.NotNull(result);
        Assert.Contains("couponCode", result!.Missing);
    }

    [Fact]
    public async Task A_self_referencing_placeholder_cannot_hang_the_render()
    {
        // A config typo (a -> {{b}} -> {{a}}) must degrade, not spin. The depth cap is what stops a
        // task page from hanging on authored content (§682's rule).
        var map = RealisticPlaceholders();
        map["furnitureSpec"] = "{{couponCode}}";
        map["couponCode"] = "{{furnitureSpec}}";

        var result = await Service().RenderAsync(BoothLayoutTask(), TaskBodyFlavour.Html, map);

        Assert.NotNull(result);
    }
}
