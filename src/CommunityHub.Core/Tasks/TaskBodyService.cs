using CommunityHub.Core.Domain;
using CommunityHub.Core.Tasks.Data;
using CommunityHub.Core.Tasks.Definitions;
using CommunityHub.Core.Tasks.Model;
using CommunityHub.Core.Tasks.Parsing;
using CommunityHub.Core.Tasks.Rendering;

namespace CommunityHub.Core.Tasks;

/// <summary>Which rendering of a task body a caller wants.</summary>
public enum TaskBodyFlavour
{
    /// <summary>The hub page.</summary>
    Html = 0,

    /// <summary>ICS <c>DESCRIPTION</c> and plain-text mail.</summary>
    PlainText = 1,

    /// <summary>A reminder e-mail.</summary>
    EmailHtml = 2,
}

/// <summary>One rendered task body, plus what the render could not resolve.</summary>
/// <param name="Missing">
/// Placeholder keys the body referenced and the caller did not supply. 🔒 They rendered as EMPTY —
/// never as literal <c>{{braces}}</c> — which is right for the reader and invisible to everyone
/// else, so the caller is expected to LOG this. A live task silently missing its price or its
/// shipping address looks entirely normal.
/// </param>
public sealed record TaskBodyRender(
    TaskDefinition Definition,
    string Html,
    IReadOnlyCollection<string> Missing);

/// <summary>
/// THE entry point for rendering a task's body. Registry-backed tasks render from their authored
/// body; everything else falls back to the stored <c>Description</c>.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Why a single service rather than switching each surface.</b> §684.16 migrates ONE task
/// at a time, so for a while the two models coexist — and the failure mode to avoid is a task that
/// renders from the registry on its page but from a stale <c>Description</c> in the chase mail.
/// §684.12 is explicit: <i>a task must not say one thing on the page and another in the chase
/// mail</i>. Routing all three surfaces through one method means the registry-vs-legacy decision is
/// made ONCE, from the row itself, and cannot be made differently in three places.</para>
///
/// <para>🔒 <b>The legacy fallback is load-bearing and must stay until the last task is
/// migrated.</b> §684.21 is a REGRESSION GATE, not a feature: the redesign must change nothing about
/// who gets chased, when, or how. An unmigrated task keeps rendering exactly as it did — the old
/// markup path, byte for byte.</para>
///
/// <para>§684.14 — a registry-backed row stores NO rendered prose. That removes the per-company
/// duplication (one row per company × task, each holding a full copy), the 20 000-character column
/// question, and the staleness of any live value, all at once. <c>Tasks.Description</c> survives
/// only as the unmigrated tasks' storage and is dropped LAST (§684.16 step 7).</para>
/// </remarks>
public sealed class TaskBodyService
{
    private readonly TaskDefinitionRegistry _registry;
    private readonly TaskBodyStore _bodies;
    private readonly TaskDataResolver _data;

    private static readonly TaskBodyHtmlRenderer HtmlRenderer = new();
    private static readonly TaskBodyPlainTextRenderer PlainRenderer = new();
    private static readonly TaskBodyEmailHtmlRenderer EmailRenderer = new();

    public TaskBodyService(
        TaskDefinitionRegistry registry, TaskBodyStore bodies, TaskDataResolver data)
    {
        _registry = registry;
        _bodies = bodies;
        _data = data;
    }

    /// <summary>
    /// The definition this row was seeded from, or null when the row predates the migration.
    /// </summary>
    /// <remarks>
    /// Matched on the task's TITLE via the same slug the <c>SourceKey</c> uses, so a row seeded by
    /// the old JSON path and a row seeded by the registry resolve to the SAME definition — which is
    /// what lets a company's existing rows start rendering the new body without being re-created.
    /// See <see cref="SponsorTaskKeys"/>.
    /// </remarks>
    public TaskDefinition? DefinitionFor(ParticipantTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Title)) return null;

        var slug = SponsorTaskKeys.Slug(task.Title);
        var exact = _registry.All.FirstOrDefault(
            d => string.Equals(SponsorTaskKeys.Slug(d.Title), slug, StringComparison.Ordinal));
        if (exact is not null) return exact;

        // 🔒 §707.43 — A DEFINITION WHOSE TITLE CARRIES A {{token}} CAN NEVER SLUG-MATCH ITS OWN ROWS.
        //
        // The row stores the RENDERED title ("… for 1250 attendee bags"); the definition holds the
        // TEMPLATE ("… for {{expectedAttendees}} attendee bags"). Slugged, those are
        // "…-for-1250-attendee-bags" vs "…-for-expectedattendees-attendee-bags" — never equal. So
        // `DefinitionFor` returned null, `RenderAsync` bailed to the legacy path, and the page fell
        // back to the stored `Description`, which for every registry-seeded sponsor task is EMPTY.
        // Result: a task rendering its title, its buttons and NOTHING ELSE — no body, and no decision
        // block either, since that also comes from the definition. Operator 2026-07-30: *"i am missing
        // the entire task text here (sponsor)"*.
        //
        // Matched on the literal PREFIX before the first token, which is long and distinctive
        // ("brochures-competition-flyer-or-swags-for"), and required to be UNAMBIGUOUS: two
        // definitions sharing a prefix resolve to neither, because rendering the wrong body is worse
        // than rendering none. The prefix also sidesteps `Slug`'s 60-char truncation, which cuts the
        // tail off exactly the long titles that carry tokens.
        var candidates = _registry.All
            .Where(d => d.Title.Contains("{{", StringComparison.Ordinal))
            .Where(d => TokenPrefixMatches(d.Title, slug))
            .Take(2)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// §707.43 — does <paramref name="taskSlug"/> begin with the slug of the definition title's
    /// literal text before its first <c>{{token}}</c>? Empty/short prefixes never match, so a title
    /// that STARTS with a token can never claim every row.
    /// </summary>
    private static bool TokenPrefixMatches(string definitionTitle, string taskSlug)
    {
        var cut = definitionTitle.IndexOf("{{", StringComparison.Ordinal);
        if (cut <= 0) return false;

        var prefix = SponsorTaskKeys.Slug(definitionTitle[..cut]).TrimEnd('-');
        // Guard against a trivially short prefix matching half the registry.
        return prefix.Length >= 12 && taskSlug.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>True when this row renders from the registry rather than from its stored prose.</summary>
    public bool IsMigrated(ParticipantTask task) => DefinitionFor(task) is not null;

    /// <summary>
    /// Render one task's body. Resolves <c>:::data</c> providers first, so the renderers stay
    /// synchronous.
    /// </summary>
    /// <param name="task">The row being rendered.</param>
    /// <param name="flavour">Which surface is asking.</param>
    /// <param name="placeholders">Per-company / per-edition placeholder values.</param>
    /// <param name="taskPageUrl">Absolute URL of the hub page carrying this task (mail flavours).</param>
    /// <param name="decision">How a decision task has been answered.</param>
    public async Task<TaskBodyRender?> RenderAsync(
        ParticipantTask task,
        TaskBodyFlavour flavour,
        IReadOnlyDictionary<string, string>? placeholders = null,
        string? taskPageUrl = null,
        TaskDecisionAnswer? decision = null,
        CancellationToken ct = default)
    {
        var definition = DefinitionFor(task);
        if (definition is null)
        {
            // Not migrated: the caller keeps its existing legacy rendering of Description. Returning
            // null rather than the raw string is deliberate — it forces the call site to be explicit
            // about which path it is on instead of silently emitting unrendered markup.
            return null;
        }

        // 🔒 §688 — SUBSTITUTE, THEN PARSE. See TaskBodyStore.LoadRaw for why the order is the fix:
        // a placeholder value can itself contain another placeholder, markup, or an e-mail address,
        // and none of those worked when the value was injected after parsing.
        var missing = new List<string>();
        var source = SubstitutePlaceholders(
            _bodies.LoadRaw(definition.BodyRef), placeholders, missing);

        var body = TaskBodyParser.Parse(source);

        var context = new TaskRenderContext(
            placeholders,
            await _data.ResolveAsync(
                new List<TaskBody> { body },
                new TaskDataContext(task.EventId, task.SponsorCompanyId, task.AssignedParticipantId),
                ct),
            decision,
            taskPageUrl,
            task.Id);

        var output = Renderer(flavour).Render(body, context);

        // Anything the parser still saw as a placeholder node (a directive field, say) plus
        // everything substitution could not resolve.
        foreach (var key in context.MissingPlaceholders) missing.Add(key);

        // MissingPlaceholders is only populated BY rendering, so it has to be read after — and it
        // has to be handed back, or the one signal that a live task quietly lost its price or its
        // shipping address dies inside this method.
        return new TaskBodyRender(
            definition, output, missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// §688 — resolve every <c>{{placeholder}}</c> in the RAW body before it is parsed.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>RECURSIVE, and bounded.</b> A value may itself contain a placeholder —
    /// <c>{{furnitureSpec}}</c> holds <c>{{couponCode}}</c> — which the previous one-level pass left
    /// on a sponsor's screen as a literal <c>{{couponCode}}</c>. The old seed-time pull only got this
    /// right by accident of pass ordering. The depth cap stops a config typo
    /// (<c>a → {{b}}</c>, <c>b → {{a}}</c>) from hanging a page render.</para>
    ///
    /// <para>🔒 <b>An EMPTY value is reported too, not just an absent key.</b> §688.4: the
    /// spec-button href resolved to a key that EXISTED with a blank value, so nothing was "missing"
    /// and nothing was logged — while the button rendered with no destination. Blank is a defect
    /// here, not a value.</para>
    /// </remarks>
    private static string SubstitutePlaceholders(
        string source, IReadOnlyDictionary<string, string>? values, List<string> missing)
    {
        if (string.IsNullOrEmpty(source) || !source.Contains("{{", StringComparison.Ordinal))
        {
            return source;
        }

        const int maxDepth = 5;
        var current = source;

        for (var depth = 0; depth < maxDepth; depth++)
        {
            var sb = new System.Text.StringBuilder(current.Length);
            var replaced = false;
            var i = 0;

            while (i < current.Length)
            {
                if (current[i] == '{' && i + 1 < current.Length && current[i + 1] == '{')
                {
                    var close = current.IndexOf("}}", i + 2, StringComparison.Ordinal);
                    // A key never spans a line; a stray "{{" is literal text, not a broken token.
                    if (close > i + 2
                        && current.IndexOf('\n', i, close - i) < 0)
                    {
                        var key = current[(i + 2)..close].Trim();
                        if (values is not null && values.TryGetValue(key, out var value))
                        {
                            if (string.IsNullOrWhiteSpace(value)) missing.Add(key);
                            sb.Append(value);
                        }
                        else
                        {
                            // Unknown ⇒ renders as NOTHING, never as literal braces on a
                            // participant-facing page.
                            missing.Add(key);
                        }
                        i = close + 2;
                        replaced = true;
                        continue;
                    }
                }

                sb.Append(current[i]);
                i++;
            }

            current = sb.ToString();
            if (!replaced || !current.Contains("{{", StringComparison.Ordinal)) break;
        }

        return current;
    }

    private static TaskBodyRenderer Renderer(TaskBodyFlavour flavour) => flavour switch
    {
        TaskBodyFlavour.Html => HtmlRenderer,
        TaskBodyFlavour.PlainText => PlainRenderer,
        TaskBodyFlavour.EmailHtml => EmailRenderer,
        _ => throw new NotSupportedException($"Unhandled flavour {flavour}."),
    };
}
