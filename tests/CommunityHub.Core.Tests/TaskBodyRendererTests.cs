using System.Reflection;
using CommunityHub.Core.Tasks.Data;
using CommunityHub.Core.Tasks.Model;
using CommunityHub.Core.Tasks.Parsing;
using CommunityHub.Core.Tasks.Rendering;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §684.10 / §684.18 — ONE body, three renderers, and the golden-render tests that keep them in
/// step.
/// </summary>
public class TaskBodyRendererTests
{
    private static readonly TaskBodyHtmlRenderer Html = new();
    private static readonly TaskBodyPlainTextRenderer Plain = new();
    private static readonly TaskBodyEmailHtmlRenderer Email = new();

    private static TaskBodyRenderer[] AllRenderers => new TaskBodyRenderer[] { Html, Plain, Email };

    private static string Render(TaskBodyRenderer renderer, string source, TaskRenderContext? ctx = null) =>
        renderer.Render(TaskBodyParser.Parse(source), ctx ?? new TaskRenderContext());

    // ---------------------------------------------------------------- §685 --

    [Theory]
    [InlineData("**bold**", "bold")]
    [InlineData("*italic*", "italic")]
    [InlineData("__underline__", "underline")]
    [InlineData("==highlight==", "highlight")]
    public void No_emphasis_marker_ever_leaks_into_plain_text(string source, string inner)
    {
        // ❗ THIS IS §685, made structural. TaskMarkup.ToPlainText implemented bold / underline /
        // link / bullet while the web renderer implemented sixteen passes; *italic* (§600.5) and
        // ==highlight== (§675) were added to the web only, so BOTH markers leaked verbatim into
        // every calendar entry built from a task body — for two features running, with nothing able
        // to catch it. Emphasis is a NODE now, shared by all three flavours.
        var text = Render(Plain, source);

        Assert.Equal(inner, text);
        Assert.DoesNotContain("*", text);
        Assert.DoesNotContain("=", text);
        Assert.DoesNotContain("_", text);
    }

    [Fact]
    public void Every_emphasis_kind_is_handled_by_every_renderer()
    {
        // Stronger than four record types would be: adding a member to the enum turns THIS red.
        foreach (TaskEmphasisKind kind in Enum.GetValues<TaskEmphasisKind>())
        {
            var body = new TaskBody(
                new TaskBlock[]
                {
                    new TaskParagraph(new TaskInline[]
                    {
                        new TaskEmphasis(kind, new TaskInline[] { new TaskText("marked") }),
                    }),
                },
                Array.Empty<TaskBodyDiagnostic>());

            foreach (var renderer in AllRenderers)
            {
                var output = renderer.Render(body, new TaskRenderContext());
                Assert.Contains("marked", output);
            }
        }
    }

    [Fact]
    public void Every_block_and_inline_type_renders_in_all_three_flavours()
    {
        // §684.10's promise, verified by reflection: a new block type that a renderer forgot would
        // throw NotSupportedException here rather than on a sponsor's page.
        var blocks = new TaskBlock[]
        {
            new TaskParagraph(new TaskInline[]
            {
                new TaskText("text "),
                new TaskEmphasis(TaskEmphasisKind.Bold, new TaskInline[] { new TaskText("bold") }),
                new TaskLink(new TaskInline[] { new TaskText("link") }, "https://x.example/a"),
                new TaskMailLink("info@example.dk"),
                new TaskPlaceholder("companyName"),
                new TaskLineBreak(),
            }),
            new TaskSection("Title", new TaskBlock[] { new TaskParagraph(Array.Empty<TaskInline>()) }),
            new TaskList(false, new[] { new TaskListItem(new TaskInline[] { new TaskText("item") }) }),
            new TaskButton("Go", "https://x.example/b", TaskButtonStyle.Primary,
                TaskLinkTarget.External, TaskInterstitial.Webshop),
            new TaskCallout(TaskCalloutTone.Warning,
                new TaskBlock[] { new TaskParagraph(Array.Empty<TaskInline>()) }),
            new TaskAddress("Shipment to", "Somewhere 1", "MARK"),
            new TaskDecision("k", "Yes", "No", null),
            new TaskEmbed("boothMembers"),
            new TaskData("tvPurchase"),
        };

        var covered = blocks.Select(b => b.GetType()).ToHashSet();
        var declared = typeof(TaskBlock).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(TaskBlock)) && !t.IsAbstract)
            .ToList();
        Assert.Empty(declared.Except(covered));

        var body = new TaskBody(blocks, Array.Empty<TaskBodyDiagnostic>());
        var context = new TaskRenderContext(
            placeholders: new Dictionary<string, string> { ["companyName"] = "Contoso" },
            data: new Dictionary<string, TaskDataResult>
            {
                ["tvPurchase"] = TaskDataResult.Found("Answer."),
            },
            taskPageUrl: "https://hub.example/Sponsor/Tasks",
            taskId: 7);

        foreach (var renderer in AllRenderers)
        {
            var output = renderer.Render(body, context);
            Assert.False(string.IsNullOrWhiteSpace(output));
        }
    }

    // ---------------------------------------------------- §555 / §666 / §684.9 --

    [Fact]
    public void Nothing_found_and_could_not_check_render_differently()
    {
        // 🔒 THE §555 RULE, and the reason §666 was blocked on this redesign. Rendering "you have
        // not booked a TV" from a FAILED lookup pushes a sponsor into buying a second one.
        var body = TaskBodyParser.Parse(":::data tvPurchase\n:::");

        var nothing = Html.Render(body, new TaskRenderContext(
            data: new Dictionary<string, TaskDataResult>
            {
                ["tvPurchase"] = TaskDataResult.NotFound("You have **not booked** a TV screen."),
            }));

        var couldNot = Html.Render(body, new TaskRenderContext(
            data: new Dictionary<string, TaskDataResult>
            {
                ["tvPurchase"] = TaskDataResult.Unavailable("We could not check your orders."),
            }));

        Assert.Contains("not booked", nothing);
        Assert.DoesNotContain("could not check", nothing, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("could not check", couldNot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not booked", couldNot);
        // "Could not check" is a WARNING in every flavour, never ordinary prose.
        Assert.Contains("task-body__callout--warning", couldNot);
    }

    [Fact]
    public void An_unresolved_data_source_says_we_could_not_check_rather_than_nothing()
    {
        // A missing entry means the resolver never ran for this source — which IS not knowing.
        // Rendering nothing would quietly drop the answer the task exists to give.
        var body = TaskBodyParser.Parse(":::data tvPurchase\n:::");

        foreach (var renderer in AllRenderers)
        {
            var output = renderer.Render(body, new TaskRenderContext());
            Assert.Contains("could not check", output, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------- security --

    [Fact]
    public void Raw_html_in_authored_copy_cannot_execute()
    {
        // §684.17 — copy is organizer-authored and lands on participant-facing pages. Text is
        // HTML-encoded first; the only tags in the output are the ones the renderer writes.
        var html = Render(Html, "Hello <script>alert('x')</script> world");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Email_buttons_emit_a_VML_roundrect_for_desktop_outlook()
    {
        // 🔒 Desktop Outlook renders with the WORD engine: it ignores border-radius and will not
        // infer readable text on a dark fill. Only VML gives rounded corners and white text
        // ([ceh-email-button-vml]) — the operator-verified standard already used by the welcome
        // templates. Block types make this a per-type decision instead of a regex that "mostly"
        // survives.
        var email = Render(
            Email, ":::button\nlabel: Open the Sponsor Webshop\nhref: https://x.example/s\n:::");

        Assert.Contains("<!--[if mso]>", email);
        Assert.Contains("v:roundrect", email);
        Assert.Contains("color:#ffffff", email);
        Assert.Contains("<!--[if !mso]>", email);
    }

    [Fact]
    public void Plain_text_keeps_a_buttons_url_because_there_is_nothing_to_click()
    {
        var text = Render(
            Plain, ":::button\nlabel: Open the Sponsor Webshop\nhref: https://x.example/s\n:::");

        Assert.Equal("Open the Sponsor Webshop: https://x.example/s", text);
    }

    [Fact]
    public void An_embedded_component_degrades_to_a_link_outside_the_hub()
    {
        // 🔒 §688.12 — an editable booth-members grid cannot exist in a calendar entry or an inbox.
        // Rendering NOTHING there would silently drop the task's whole point from the chase mail:
        // the reader would see the surrounding prose and no way to act on it.
        var source = ":::embed boothMembers\n:::";
        var context = new TaskRenderContext(taskPageUrl: "https://hub.example/Sponsor/Tasks");

        var html = Render(Html, source, context);
        var text = Render(Plain, source, context);
        var email = Render(Email, source, context);

        // The hub gets a placement MARKER — the Razor row swaps the real component in there.
        Assert.Contains("data-task-embed=\"boothMembers\"", html);

        // The other two get a way to reach it.
        Assert.Contains("https://hub.example/Sponsor/Tasks", text);
        Assert.Contains("https://hub.example/Sponsor/Tasks", email);
    }

    [Fact]
    public void An_unknown_embed_component_is_refused_at_parse_time()
    {
        // 🔒 The set is CLOSED. An embed the row does not know how to render would leave a silent
        // HOLE where the sponsor expects to do the work — worse than a link, because nothing on the
        // page indicates anything is missing.
        var body = TaskBodyParser.Parse(":::embed somethingElse\n:::");

        Assert.Empty(body.Blocks);
        Assert.Contains(body.Diagnostics, d => d.Message.Contains("unknown embed component"));
    }

    [Fact]
    public void A_decision_in_a_mail_links_to_the_hub_instead_of_drawing_dead_buttons()
    {
        // 🔒 A mail cannot POST. Two clickable-looking options would either do nothing or need a
        // state-changing GET — and a link that records an answer when a mail scanner follows it is
        // how a decision gets logged that nobody made.
        var source = ":::decision appGame\naccept: We participate\ndecline: No interest\n:::";
        var context = new TaskRenderContext(taskPageUrl: "https://hub.example/Sponsor/Tasks");

        var email = Render(Email, source, context);
        var text = Render(Plain, source, context);

        Assert.Contains("https://hub.example/Sponsor/Tasks", email);
        Assert.Contains("https://hub.example/Sponsor/Tasks", text);
    }

    // ---------------------------------------------------------- placeholders --

    [Fact]
    public void An_unknown_placeholder_renders_as_nothing_and_is_reported()
    {
        // Never literal {{braces}} on a sponsor's page; always visible to the catalog test.
        var context = new TaskRenderContext();
        var html = Render(Html, "Price: {{tvRentalPriceEur}}", context);

        Assert.DoesNotContain("{{", html);
        Assert.Contains("tvRentalPriceEur", context.MissingPlaceholders);
    }

    [Fact]
    public void Placeholders_resolve_at_render_time_including_inside_a_buttons_href()
    {
        // §684.1 — the whole point: today they resolve at SEED time and bake into
        // Tasks.Description, so every value is frozen at the moment the WooCommerce pull ran.
        var context = new TaskRenderContext(new Dictionary<string, string>
        {
            ["configuratorUrl"] = "https://shop.example/sponsor",
        });

        var html = Render(Html, ":::button\nlabel: Shop\nhref: {{configuratorUrl}}\n:::", context);

        Assert.Contains("https://shop.example/sponsor", html);
        Assert.Empty(context.MissingPlaceholders);
    }
}
