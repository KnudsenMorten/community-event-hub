using CommunityHub.Core.Tasks.Data;
using CommunityHub.Core.Tasks.Definitions;
using CommunityHub.Core.Tasks.Model;
using CommunityHub.Core.Tasks.Rendering;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §686.2 step 3 — <b>prove the ONE migrated task renders correctly in all three renderers before
/// migrating anything else.</b>
/// </summary>
/// <remarks>
/// <para>TV Rental is the proving case (§684.16 / §686.3): it is the only sponsor task that needs
/// the <c>:::data</c> provider, so if the model can express it, it can express the rest. These are
/// the assertions behind what the operator is shown at the phase-1 gate (§686.4).</para>
/// </remarks>
public class TvRentalGoldenRenderTests
{
    private const string Key = "sponsor.tv-rental";

    private static readonly TaskBodyStore Bodies = new();
    private static readonly TaskBodyHtmlRenderer Html = new();
    private static readonly TaskBodyPlainTextRenderer Plain = new();
    private static readonly TaskBodyEmailHtmlRenderer Email = new();

    private static TaskBody Body =>
        Bodies.Load(TaskDefinitionRegistry.Shipped.ByKey(Key)!.BodyRef);

    private static TaskRenderContext Context(TaskDataResult tv) =>
        new(
            new Dictionary<string, string>
            {
                ["tvRentalPriceEur"] = "450",
                ["configuratorUrl"] = "https://shop.example/sponsor",
                ["editionCode"] = "ELDK27",
            },
            new Dictionary<string, TaskDataResult> { ["tvPurchase"] = tv },
            taskPageUrl: "https://hub.example/Sponsor/Tasks",
            taskId: 1);

    private static TaskDataResult Booked(int n) =>
        TaskDataResult.Found(
            ":::callout info\n"
            + $"We checked your webshop orders: you have already booked **{n} TV screens** for your "
            + "booth. You only need to order again if you want another one.\n:::");

    private static readonly TaskDataResult NotBooked = TaskDataResult.NotFound(
        ":::callout info\nWe checked your webshop orders: you have **not booked a TV screen** yet."
        + " If you want one, order it below.\n:::");

    private static readonly TaskDataResult CouldNotCheck = TaskDataResult.Unavailable(
        "We could not check your webshop orders right now, so we cannot tell you whether you have "
        + "already booked a TV screen.");

    [Fact]
    public void The_definition_and_its_body_exist_and_are_clean()
    {
        var definition = TaskDefinitionRegistry.Shipped.ByKey(Key);

        Assert.NotNull(definition);
        Assert.Equal("TV Rental for booth-presentations", definition!.Title);
        Assert.False(definition.IsMandatory);              // an optional paid add-on
        Assert.True(Body.IsValid);
    }

    [Fact]
    public void Only_a_booth_sponsor_sees_it()
    {
        // §684.15/§456 — an exhibitor-speaker once saw every SPONSOR task. Audience is explicit and
        // testable now, rather than a side effect of which seeder ran.
        var boothSponsor = TaskAudienceFacts.For(
            Domain.ParticipantRole.Sponsor, TaskAudiencePredicate.HasBooth);
        var sponsorWithoutBooth = TaskAudienceFacts.For(Domain.ParticipantRole.Sponsor);
        var speaker = TaskAudienceFacts.For(
            Domain.ParticipantRole.Speaker, TaskAudiencePredicate.HasBooth);

        Assert.Contains(TaskDefinitionRegistry.Shipped.For(boothSponsor), d => d.Key == Key);
        Assert.DoesNotContain(TaskDefinitionRegistry.Shipped.For(sponsorWithoutBooth), d => d.Key == Key);
        Assert.DoesNotContain(TaskDefinitionRegistry.Shipped.For(speaker), d => d.Key == Key);
    }

    [Fact]
    public void The_old_go_and_check_your_contract_note_is_gone()
    {
        // §666 — THE POINT OF THE WHOLE EXERCISE. The task used to say "NOTE: Check your contract to
        // see whether you have already booked a TV", asking the sponsor to look up something the hub
        // already knows.
        foreach (var renderer in new TaskBodyRenderer[] { Html, Plain, Email })
        {
            var output = renderer.Render(Body, Context(NotBooked));
            Assert.DoesNotContain("Check your contract", output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void It_says_how_many_screens_are_already_booked(int screens)
    {
        // He asked for the AMOUNT, not just yes/no — "did he buy TV (amount) or did he not" —
        // because a sponsor may have bought two, and someone reading a bare "you have a TV" with
        // two booths would order a third.
        foreach (var renderer in new TaskBodyRenderer[] { Html, Plain, Email })
        {
            var output = renderer.Render(Body, Context(Booked(screens)));
            Assert.Contains($"{screens} TV screens", output);
        }
    }

    [Fact]
    public void A_failed_lookup_never_renders_as_you_have_not_booked_a_TV()
    {
        // 🔒 THE §555 RULE, and the single most important assertion in this file. §666: rendering
        // "you have not booked a TV" from a FAILED lookup pushes a sponsor into buying a second one.
        // The old model could not honour this at all — a seed-time failure PERSISTED the wrong
        // answer, and by render time nothing remembered that the lookup had failed.
        foreach (var renderer in new TaskBodyRenderer[] { Html, Plain, Email })
        {
            var output = renderer.Render(Body, Context(CouldNotCheck));

            Assert.Contains("could not check", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("not booked a TV", output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void All_three_surfaces_agree_on_the_facts()
    {
        // §684.12 — a task must not say one thing on the page and another in the chase mail. The
        // three renderings differ in MARKUP, never in content.
        var html = Html.Render(Body, Context(Booked(2)));
        var plain = Plain.Render(Body, Context(Booked(2)));
        var email = Email.Render(Body, Context(Booked(2)));

        foreach (var fact in new[] { "450", "2 TV screens", "TV Screen Exhibitor Booth ELDK27" })
        {
            Assert.Contains(fact, html);
            Assert.Contains(fact, plain);
            Assert.Contains(fact, email);
        }

        // The webshop link survives in all three — as a real button on the page and in mail, and as
        // "label: url" in the calendar entry, where there is nothing to click.
        Assert.Contains("https://shop.example/sponsor", html);
        Assert.Contains("Open the Sponsor Webshop: https://shop.example/sponsor", plain);
        Assert.Contains("https://shop.example/sponsor", email);
    }

    [Fact]
    public void No_markup_marker_leaks_into_the_calendar_entry()
    {
        // §685 — the defect this design removes: the ICS renderer never learned *italic* or
        // ==highlight==, so the raw markers rode into every calendar entry for two features running.
        var plain = Plain.Render(Body, Context(Booked(1)));

        Assert.DoesNotContain("**", plain);
        Assert.DoesNotContain("==", plain);
        Assert.DoesNotContain("__", plain);
        Assert.DoesNotContain(":::", plain);
        Assert.DoesNotContain("{{", plain);
    }

    [Fact]
    public void The_webshop_button_carries_its_hand_off_notice_and_the_external_indicator()
    {
        // §672 — chosen by declared KIND, not by sniffing the URL. §653 — an external button says it
        // leaves the hub. The real confusion is not the new tab: it is landing in a DIFFERENT system
        // with its own sign-in and assuming the hub logged you out (§487).
        var html = Html.Render(Body, Context(NotBooked));

        Assert.Contains("data-webshop-interstitial=\"1\"", html);
        Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("&#8599;", html);
    }

    [Fact]
    public void The_email_flavour_degrades_for_desktop_outlook()
    {
        // 🔒 Outlook renders with the WORD engine — only VML gives rounded corners and white text
        // ([ceh-email-button-vml]).
        var email = Email.Render(Body, Context(NotBooked));

        Assert.Contains("v:roundrect", email);
        Assert.Contains("<!--[if mso]>", email);
    }
}
