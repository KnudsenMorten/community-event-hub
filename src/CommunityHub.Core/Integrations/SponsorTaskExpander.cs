using CommunityHub.Core.Config;

namespace CommunityHub.Core.Integrations;

/// <summary>One concrete sponsor task to create.</summary>
public sealed record SponsorTask(
    string Title,
    DateOnly? DueDate,
    string DeadlineRuleName,
    string Description,
    BoothTier Tier,
    bool Mandatory,
    SponsorTaskUploadDefinition? Upload,
    /// <summary>§648 — the wizard step that completes this task, or null.</summary>
    string? Form = null);

/// <summary>
/// Expands a classified sponsor product into the concrete task list, using the
/// task sets and deadline rules from sponsor.&lt;edition&gt;.json (CONTEXT.md -
/// sponsor pipeline). This is the JSON-driven control the organizer edits: add
/// a task to a task set in the config and every matching future order picks
/// it up, with no code change.
///
/// Rules (from the config's own _doc): every sponsor gets the "allSponsors"
/// set. A booth product also gets the shared "booth" set plus its tier set
/// (boothPlatinum / boothDiamond / boothGold). session / brandedFeature /
/// preday products add their matching sets.
/// </summary>
public sealed class SponsorTaskExpander
{
    private readonly SponsorConfig _config;

    public SponsorTaskExpander(SponsorConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Expand one classified product into dated tasks.
    /// </summary>
    /// <param name="cls">The product classification (kind + tier).</param>
    /// <param name="eventDate">The edition's main start date.</param>
    /// <param name="firstOrderDate">
    /// The sponsor's first-order date, for contractPlus deadlines. Null =&gt;
    /// the rule's fallbackNowPlus is used instead.
    /// </param>
    /// <param name="today">Today, for the fallbackNowPlus basis.</param>
    public IReadOnlyList<SponsorTask> Expand(
        SponsorProductClass cls,
        DateOnly eventDate,
        DateOnly? firstOrderDate,
        DateOnly today)
    {
        if (!cls.GeneratesTasks)
        {
            return Array.Empty<SponsorTask>();
        }

        var setNames = new List<string> { "allSponsors" };

        switch (cls.Kind)
        {
            case SponsorProductKind.Booth:
                setNames.Add("booth");
                setNames.Add(cls.Tier switch
                {
                    BoothTier.Platinum => "boothPlatinum",
                    BoothTier.Diamond => "boothDiamond",
                    BoothTier.Feature => "boothGold", // feature uses gold set
                    _ => "boothGold",
                });
                break;
            case SponsorProductKind.Session:
                setNames.Add("session");
                break;
            case SponsorProductKind.BrandedFeature:
                setNames.Add("brandedFeature");
                break;
            case SponsorProductKind.PreDay:
                setNames.Add("preday");
                break;
        }

        var rules = _config.DeadlineRules();
        var tasks = new List<SponsorTask>();
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var setName in setNames)
        {
            foreach (var def in _config.TaskSet(setName))
            {
                // A product matching several sets must not duplicate a task.
                if (!seenTitles.Add(def.Title))
                {
                    continue;
                }

                var due = ResolveDeadline(
                    def.Deadline, rules, eventDate, firstOrderDate, today);
                tasks.Add(new SponsorTask(def.Title, due, def.Deadline, def.Description, cls.Tier, def.Mandatory, def.Upload, def.Form));
            }
        }

        return tasks;
    }

    /// <summary>
    /// Resolve one named deadline rule to a concrete date — the SAME rules the JSON task sets use.
    /// </summary>
    /// <remarks>
    /// 🔒 §684.11 — a migrated definition's <c>TaskDue.FromConfig</c> resolves through THIS method,
    /// not a reimplementation. The deadline SOURCE is being unified; the deadline RULES are not
    /// changing, and the dates stay per-edition config. A second copy of <c>eventMinus</c> /
    /// <c>contractPlus</c> arithmetic is how a migrated task would quietly acquire a due date one
    /// day off the one its reminders were already built around.
    /// </remarks>
    public DateOnly? ResolveDeadlineRule(
        string ruleName,
        DateOnly eventDate,
        DateOnly? firstOrderDate,
        DateOnly today) =>
        ResolveDeadline(ruleName, _config.DeadlineRules(), eventDate, firstOrderDate, today);

    /// <summary>Resolve a named deadline rule to a concrete date.</summary>
    private static DateOnly? ResolveDeadline(
        string ruleName,
        IReadOnlyDictionary<string, DeadlineRule> rules,
        DateOnly eventDate,
        DateOnly? firstOrderDate,
        DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(ruleName)
            || !rules.TryGetValue(ruleName, out var rule))
        {
            return null;
        }

        return rule.Basis switch
        {
            "eventMinus" => eventDate.AddDays(-rule.Days),
            "contractPlus" => firstOrderDate is not null
                ? firstOrderDate.Value.AddDays(rule.Days)
                : today.AddDays(rule.FallbackNowPlus ?? rule.Days),
            _ => null,
        };
    }
}
