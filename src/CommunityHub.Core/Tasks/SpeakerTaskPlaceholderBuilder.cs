using CommunityHub.Core.Config;
using CommunityHub.Core.Forms;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Tasks;

/// <summary>
/// §708 — the <c>{{placeholder}}</c> map for one SPEAKER's task bodies, at RENDER time.
/// </summary>
/// <remarks>
/// <para>Sibling of <see cref="SponsorTaskPlaceholderBuilder"/> and deliberately much smaller: a
/// speaker task has no company, no tier, no coupon and no per-task SharePoint folder. What it does
/// have — and what §708.2a is about — are the FORM ROUTES.</para>
///
/// <para>🔒 <b>Why the routes come through here instead of being written in the bodies.</b> The
/// speaker task descriptions used to carry
/// <c>[Open the Hotel form](https://eldk27.eventhub.expertslive.dk/Forms/Hotel)</c> — an ABSOLUTE,
/// edition-specific URL inside <c>config/speaker-deadlines.eldk27.json</c>. It broke the evergreen
/// rule (a new edition is a new config file, never a hostname baked into copy), it could not follow a
/// route change, and it was the third door to a form that already had two (§708.2). Resolving the
/// route HERE, from <see cref="FormRoutes"/>, is what makes the operator's acceptance test true:
/// <i>"if changing a form's route requires editing more than one place it is not smart enough
/// yet"</i>.</para>
///
/// <para>🔒 A placeholder this builder cannot supply renders as EMPTY, never as literal
/// <c>{{braces}}</c>. <see cref="ReportMissing"/> is the only thing between that and silence — §688.4
/// is the precedent, where a button resolved to a blank value and rendered with no destination while
/// nothing anywhere was "missing".</para>
/// </remarks>
public sealed class SpeakerTaskPlaceholderBuilder
{
    private readonly EventEditionConfigLoader _eventConfig;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly ILogger<SpeakerTaskPlaceholderBuilder> _log;

    public SpeakerTaskPlaceholderBuilder(
        EventEditionConfigLoader eventConfig,
        EventConfigOptions eventConfigOptions,
        ILogger<SpeakerTaskPlaceholderBuilder> log)
    {
        _eventConfig = eventConfig;
        _eventConfigOptions = eventConfigOptions;
        _log = log;
    }

    /// <summary>Build the placeholder map every speaker task body renders against.</summary>
    public IReadOnlyDictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The edition's cross-cutting placeholder map + facts, exactly as the sponsor builder reads
        // them — so a body can name {{supportEmail}} and friends without a speaker-only copy.
        // Fail-soft (§682): a missing or unreadable edition config must not stop a speaker's task
        // page rendering. The bodies then show empty values, which ReportMissing makes loud.
        try
        {
            var facts = _eventConfig.Load(_eventConfigOptions.EventConfigPath);
            if (facts.Placeholders is { Count: > 0 } placeholders)
            {
                foreach (var (key, value) in placeholders) map[key] = value ?? string.Empty;
            }
            map["editionCode"] = facts.Code ?? string.Empty;
            map["editionCodeLower"] = (facts.Code ?? string.Empty).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex, "Could not read the edition config for speaker task placeholders; "
                + "edition-level values will render empty.");
        }

        // 🔒 §708.2a — THE CANONICAL ROUTES, resolved in ONE place and reused by every surface.
        //
        // Added AFTER the edition map so a stray config key of the same name cannot quietly
        // redirect a form button: the route is code's answer, not content's.
        foreach (var (key, formKey) in FormPlaceholders)
        {
            var route = FormRoutes.For(formKey);
            // A null route would render a button with no destination (§688.4). FormRoutes.For only
            // returns null for a key it does not know, which SpeakerTaskDefinitionTests forbids —
            // so this is belt-and-braces, and it fails as an ABSENT key (loud) rather than a blank.
            if (route is not null) map[key] = route;
        }

        return map;
    }

    /// <summary>
    /// The placeholder each form's canonical route is published under, for the task bodies.
    /// </summary>
    /// <remarks>
    /// Named <c>…FormUrl</c> to match the sponsor bodies' existing convention
    /// (<c>{{wallSpecUrl}}</c>, <c>{{configuratorUrl}}</c>), so an author moving between the two
    /// sets is not learning a second vocabulary.
    /// </remarks>
    /// <remarks>
    /// 🔒 <b>PUBLIC because the catalogue test reads it.</b> <c>TaskBodyCatalogTests</c> fails the
    /// build when a body names a placeholder nothing supplies — an unresolved key renders as EMPTY,
    /// so a typo would silently delete a form button from a live task and look entirely normal doing
    /// it (§688.4). Listing the keys in the test as literals would be a second copy that drifts;
    /// reading THIS list means adding a body placeholder without wiring it here cannot pass.
    /// </remarks>
    public static readonly (string Placeholder, string FormKey)[] FormPlaceholders =
    {
        ("hotelFormUrl", "hotel"),
        ("dinnerFormUrl", "dinner"),
        ("lunchFormUrl", "lunch"),
        ("swagFormUrl", "swag"),
        ("travelFormUrl", "travel"),
    };

    /// <summary>
    /// Log placeholders a render could not resolve. 🔒 Call at EVERY render site — an unresolved
    /// placeholder is invisible on the page by design, so this log is the only signal that a task
    /// quietly lost its button.
    /// </summary>
    public void ReportMissing(string taskKey, IReadOnlyCollection<string> missing)
    {
        if (missing.Count == 0) return;

        _log.LogError(
            "Speaker task '{TaskKey}' referenced {Count} placeholder(s) the hub cannot resolve: "
            + "{Keys}. They rendered as EMPTY on a participant-facing surface. Add them to the "
            + "edition config, or correct the body.",
            taskKey, missing.Count, string.Join(", ", missing));
    }
}
