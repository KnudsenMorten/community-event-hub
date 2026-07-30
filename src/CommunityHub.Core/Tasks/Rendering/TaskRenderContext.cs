using CommunityHub.Core.Domain;
using CommunityHub.Core.Tasks.Data;

namespace CommunityHub.Core.Tasks.Rendering;

/// <summary>
/// Everything a renderer needs that is NOT in the body: the per-edition and per-company values a
/// <c>{{placeholder}}</c> resolves to, the already-resolved <c>:::data</c> answers, and the state of
/// any decision.
/// </summary>
/// <remarks>
/// 🔒 <b>Placeholders resolve HERE, at render.</b> §684.1: today they resolve during the WooCommerce
/// pull and bake into <c>Tasks.Description</c>, so every value is frozen at the moment the pull ran.
/// Passing the map to the renderer instead is what makes the body a template again.
/// </remarks>
public sealed class TaskRenderContext
{
    private readonly IReadOnlyDictionary<string, string> _placeholders;
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

    public TaskRenderContext(
        IReadOnlyDictionary<string, string>? placeholders = null,
        IReadOnlyDictionary<string, TaskDataResult>? data = null,
        TaskDecisionAnswer? decision = null,
        string? taskPageUrl = null,
        int? taskId = null)
    {
        _placeholders = placeholders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Data = data ?? new Dictionary<string, TaskDataResult>(StringComparer.OrdinalIgnoreCase);
        Decision = decision;
        TaskPageUrl = taskPageUrl;
        TaskId = taskId;
    }

    /// <summary>Resolved <c>:::data</c> answers, keyed by source name.</summary>
    public IReadOnlyDictionary<string, TaskDataResult> Data { get; }

    /// <summary>
    /// How this task's decision has been answered — null when unanswered.
    /// </summary>
    /// <remarks>
    /// 🔒 One enum, <see cref="TaskDecisionAnswer"/>, shared with the stored
    /// <c>ParticipantTask.DecisionAnswer</c> column. A second "what did they answer" type living
    /// only in the renderer is how a display state drifts from the state the organizer roll-up
    /// reports.
    /// </remarks>
    public TaskDecisionAnswer? Decision { get; }

    /// <summary>
    /// Absolute URL of the hub page carrying this task. Required by the e-mail and plain-text
    /// flavours: you cannot press a decision button inside an inbox, so those flavours link to the
    /// page instead of pretending the buttons work.
    /// </summary>
    public string? TaskPageUrl { get; }

    /// <summary>The <c>ParticipantTask.Id</c>, for the HTML flavour's decision form posts.</summary>
    public int? TaskId { get; }

    /// <summary>
    /// Placeholder keys that were referenced but not supplied. A key that is missing renders as
    /// NOTHING — never as literal <c>{{braces}}</c> on a sponsor's page — and shows up here so the
    /// golden-render tests can assert the set is empty.
    /// </summary>
    public IReadOnlyCollection<string> MissingPlaceholders => _missing;

    /// <summary>Resolve one placeholder key. Unknown ⇒ empty string, recorded in <see cref="MissingPlaceholders"/>.</summary>
    public string Resolve(string key)
    {
        if (_placeholders.TryGetValue(key, out var value)) return value;
        _missing.Add(key);
        return string.Empty;
    }

    /// <summary>
    /// Resolve every <c>{{placeholder}}</c> in a directive's scalar field (an <c>href:</c>, an
    /// address <c>value:</c>). Prose goes through the inline parser instead, which produces
    /// placeholder NODES.
    /// </summary>
    public string ResolveText(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.Contains("{{", StringComparison.Ordinal)) return raw ?? string.Empty;

        var sb = new System.Text.StringBuilder(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == '{' && i + 1 < raw.Length && raw[i + 1] == '{')
            {
                var close = raw.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    sb.Append(Resolve(raw[(i + 2)..close].Trim()));
                    i = close + 2;
                    continue;
                }
            }
            sb.Append(raw[i]);
            i++;
        }
        return sb.ToString();
    }
}
