using System.Text.Json;
using CommunityHub.Core.Config;
using CommunityHub.Core.Tasks.Data;
using CommunityHub.Core.Tasks.Definitions;
using CommunityHub.Core.Tasks.Model;
using CommunityHub.Core.Tasks.Rendering;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §684.18 — the build-failing gate over the REAL shipped task catalog.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This is where an authoring mistake is supposed to die.</b> §682 is the standing scar:
/// one over-long paragraph in <c>sponsor.eldk27.json</c> threw during the WooCommerce pull and took
/// the entire sponsor order sync down with it. The runtime response was to make authored content
/// harmless — an unknown directive renders as nothing, a missing file is an empty body, a provider
/// that throws degrades to "could not check". That is correct, and it is also exactly how a mistake
/// becomes INVISIBLE. These tests are the other half of the bargain: loud at build time, harmless at
/// runtime.</para>
/// </remarks>
public class TaskBodyCatalogTests
{
    private static readonly TaskBodyStore Bodies = new();

    private static readonly TaskBodyRenderer[] Renderers =
    {
        new TaskBodyHtmlRenderer(),
        new TaskBodyPlainTextRenderer(),
        new TaskBodyEmailHtmlRenderer(),
    };

    /// <summary>
    /// Every data provider the shipped bodies are allowed to name. A body naming anything else would
    /// render "we could not check" forever — honest, but never what the author meant.
    /// </summary>
    private static readonly string[] RegisteredProviders =
    {
        "tvPurchase", "shipmentPurchases", "boothFurniturePurchases",
        "attendeeBagPackaging", "extraStaffTickets",
    };

    public static TheoryData<string> Definitions()
    {
        var data = new TheoryData<string>();
        foreach (var definition in TaskDefinitionRegistry.Shipped.All) data.Add(definition.Key);
        return data;
    }

    private static TaskDefinition Definition(string key) =>
        TaskDefinitionRegistry.Shipped.ByKey(key)
        ?? throw new InvalidOperationException($"No definition '{key}'.");

    [Fact]
    public void The_registry_is_not_empty_and_keys_are_unique()
    {
        var all = TaskDefinitionRegistry.Shipped.All;

        Assert.NotEmpty(all);
        Assert.Equal(all.Count, all.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(Definitions))]
    public void Every_definitions_body_file_exists(string key)
    {
        var definition = Definition(key);

        Assert.True(
            Bodies.Exists(definition.BodyRef),
            $"'{key}' points at config/tasks/eldk27/{definition.BodyRef}.md, which is not on disk. "
            + "A missing body renders as NOTHING at runtime — deliberately, so it cannot break the "
            + "reminder job — which is precisely why it has to fail here instead.");
    }

    [Theory]
    [MemberData(nameof(Definitions))]
    public void Every_body_parses_with_no_diagnostics(string key)
    {
        var body = Bodies.Load(Definition(key).BodyRef);

        Assert.True(
            body.IsValid,
            $"'{key}' has authoring errors:\n  "
            + string.Join("\n  ", body.Diagnostics.Select(d => d.ToString())));
        Assert.NotEmpty(body.Blocks);
    }

    [Theory]
    [MemberData(nameof(Definitions))]
    public void Every_body_renders_in_all_three_flavours_without_throwing(string key)
    {
        // §682's lesson, generalised: authored content must never be able to break a runtime path,
        // and the three paths here are a sponsor's page, a calendar invite and a chase e-mail.
        var body = Bodies.Load(Definition(key).BodyRef);
        var context = FullyPopulatedContext(body);

        foreach (var renderer in Renderers)
        {
            var output = renderer.Render(body, context);
            Assert.False(
                string.IsNullOrWhiteSpace(output),
                $"'{key}' rendered empty in {renderer.GetType().Name}.");
        }
    }

    [Theory]
    [MemberData(nameof(Definitions))]
    public void Every_placeholder_a_body_uses_is_one_the_hub_can_resolve(string key)
    {
        // A placeholder nobody supplies renders as NOTHING — never as literal {{braces}} — so a typo
        // would silently delete the price, the address or the coupon code from a live task and look
        // entirely normal doing it.
        var body = Bodies.Load(Definition(key).BodyRef);
        var context = FullyPopulatedContext(body);

        foreach (var renderer in Renderers) renderer.Render(body, context);

        Assert.True(
            context.MissingPlaceholders.Count == 0,
            $"'{key}' uses placeholders the hub does not supply: "
            + string.Join(", ", context.MissingPlaceholders));
    }

    [Theory]
    [MemberData(nameof(Definitions))]
    public void Every_data_directive_names_a_registered_provider(string key)
    {
        var body = Bodies.Load(Definition(key).BodyRef);

        foreach (var source in DataSources(body.Blocks))
        {
            Assert.True(
                RegisteredProviders.Contains(source, StringComparer.OrdinalIgnoreCase),
                $"'{key}' names ':::data {source}', which no provider answers. At runtime that "
                + "renders 'we could not check' forever.");
        }
    }

    [Theory]
    [MemberData(nameof(Definitions))]
    public void A_body_never_renders_a_control_that_is_not_wired_up(string key)
    {
        // 🔒 A ':::decision' draws two buttons that POST an answer. Until the answer has somewhere to
        // be stored and a handler to store it, those buttons do nothing — and a dead button is worse
        // than the e-mail hand-off it replaces, because it LOOKS like it worked. So the directive
        // and the completion kind must agree: if the body asks the participant to choose, the
        // definition must say the task completes by choosing.
        //
        // [§670]/[§676] are the live case. Their bodies deliberately keep the old contact copy until
        // the persistence lands, and this test is what stops the directive being added early.
        var definition = Definition(key);
        var body = Bodies.Load(definition.BodyRef);

        var hasDecisionBlock = HasDecision(body.Blocks);
        // §687.5 — `DecisionAndPurchase` ALSO completes by choosing; it just adds a SECOND gate (the
        // sponsor must have bought the packaging too). The buttons still post to a real handler,
        // which is the only thing this test guards. Written as an explicit list rather than a
        // loosened check, so a NEW completion kind that draws decision buttons has to be added here
        // deliberately.
        var completesByDecision =
            definition.Completion is TaskCompletion.Decision
                                  or TaskCompletion.DecisionAndPurchase;

        Assert.True(
            hasDecisionBlock == completesByDecision,
            hasDecisionBlock
                ? $"'{key}' renders a ':::decision' but its Completion is "
                  + $"{definition.Completion.GetType().Name} — the buttons would post to nothing."
                : $"'{key}' declares Completion.Decision but its body has no ':::decision', so there "
                  + "is no way for the participant to answer and the task can never complete.");
    }

    private static bool HasDecision(IReadOnlyList<TaskBlock> blocks) =>
        blocks.Any(b => b switch
        {
            TaskDecision => true,
            TaskSection section => HasDecision(section.Children),
            TaskCallout callout => HasDecision(callout.Children),
            _ => false,
        });

    [Fact]
    public void No_migrated_task_is_left_behind_in_the_sponsor_json()
    {
        // 🔒 §684.16: "no step may leave two sources of truth for one task". That is how the §683
        // override layer ended up write-only — built, plausible, and connected to nothing. Until
        // taskSets is deleted entirely (§686.2 step 5) the two sets must at least be DISJOINT.
        var json = ConfigPaths.Resolve("config/sponsor.eldk27.json");
        if (!File.Exists(json)) return;   // already deleted — the end state, nothing to check

        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        if (!doc.RootElement.TryGetProperty("taskSets", out var taskSets)) return;

        var jsonTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in taskSets.EnumerateObject())
        {
            if (set.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var task in set.Value.EnumerateArray())
            {
                if (task.TryGetProperty("title", out var title) && title.GetString() is { } t)
                {
                    jsonTitles.Add(t);
                }
            }
        }

        foreach (var definition in TaskDefinitionRegistry.Shipped.All)
        {
            Assert.False(
                jsonTitles.Contains(definition.Title),
                $"'{definition.Title}' is defined BOTH in code ({definition.Key}) and in "
                + "config/sponsor.eldk27.json taskSets. Two sources of truth for one task: delete "
                + "the JSON entry as part of migrating it.");
        }
    }

    // ------------------------------------------------------------------------

    /// <summary>
    /// A context supplying every placeholder the hub can actually resolve, and an answer for every
    /// data source — so a failure here means the BODY is wrong, not the fixture.
    /// </summary>
    private static TaskRenderContext FullyPopulatedContext(TaskBody body)
    {
        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. The edition's own placeholder block — read from the REAL shipped config, so a body
        //    referring to a key the operator has not defined fails here rather than rendering blank.
        var eventJson = ConfigPaths.Resolve("config/event.eldk27.json");
        if (File.Exists(eventJson))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(eventJson));
            if (doc.RootElement.TryGetProperty("placeholders", out var block))
            {
                foreach (var property in block.EnumerateObject())
                {
                    // "_doc" / "_xNote" entries are documentation, not placeholders.
                    if (property.Name.StartsWith('_')) continue;
                    placeholders[property.Name] = property.Value.ToString();
                }
            }
        }

        // 2. The dynamic keys SponsorOrderPullService resolves per company / tier / edition.
        foreach (var key in new[]
                 {
                     "companyName", "boothNumber", "expectedAttendees", "editionCode",
                     "editionCodeLower", "furnitureSpec", "wallSpecUrl", "couponCode", "wallSize",
                     "logoFolderUrl", "wallFolderUrl",
                 })
        {
            placeholders[key] = $"[{key}]";
        }

        // 3. §708 — the canonical FORM ROUTES SpeakerTaskPlaceholderBuilder publishes to the speaker
        //    bodies. Read from the builder's own list rather than repeated as literals here: a
        //    second copy would drift, and the whole point of this test is that a body naming a
        //    placeholder nothing supplies fails the BUILD instead of quietly rendering a button with
        //    no destination (§688.4).
        //
        //    🔒 The value is a real app-relative route, not a "[key]" stand-in, because the parser
        //    infers a button's target from its href (§671): anything not starting with "/" is
        //    treated as leaving the hub and would carry the ↗ external indicator. Substituting a
        //    placeholder-shaped value would hide that.
        foreach (var (placeholder, formKey) in
                 CommunityHub.Core.Tasks.SpeakerTaskPlaceholderBuilder.FormPlaceholders)
        {
            placeholders[placeholder] =
                CommunityHub.Core.Forms.FormRoutes.For(formKey)
                ?? throw new InvalidOperationException(
                    $"FormRoutes has no canonical route for '{formKey}', so the speaker body "
                    + $"naming {{{{{placeholder}}}}} would render a button with no destination.");
        }

        var data = DataSources(body.Blocks)
            .ToDictionary(s => s, _ => TaskDataResult.Found("Answer."), StringComparer.OrdinalIgnoreCase);

        return new TaskRenderContext(
            placeholders, data, taskPageUrl: "https://hub.example/Sponsor/Tasks", taskId: 1);
    }

    private static IEnumerable<string> DataSources(IReadOnlyList<TaskBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TaskData data:
                    yield return data.Source;
                    break;
                case TaskSection section:
                    foreach (var s in DataSources(section.Children)) yield return s;
                    break;
                case TaskCallout callout:
                    foreach (var s in DataSources(callout.Children)) yield return s;
                    break;
            }
        }
    }
}
