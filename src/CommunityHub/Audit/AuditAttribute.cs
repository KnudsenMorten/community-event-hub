using CommunityHub.Core.Domain;

namespace CommunityHub.Audit;

/// <summary>
/// Opt-in richer audit metadata for a Razor page handler (REQUIREMENTS §24). When a
/// handler method carries <c>[Audit("Assigned a volunteer to a task")]</c>, the global
/// <see cref="AuditPageFilter"/> uses this Summary + Category instead of the generic
/// auto-generated "Verb on /path". Purely additive — untagged handlers still get the
/// generic capture. Never put PII/secret values in the summary (it is a static label).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AuditAttribute : Attribute
{
    public AuditAttribute(string summary)
    {
        Summary = summary;
    }

    /// <summary>Human one-line summary shown in the trail (a static label, no payload).</summary>
    public string Summary { get; }

    /// <summary>Category for this action (defaults to UserAction). A nullable enum is
    /// not a legal attribute-arg type, so this is non-nullable; the filter uses it
    /// whenever an [Audit] attribute is present.</summary>
    public AuditCategory Category { get; set; } = AuditCategory.UserAction;

    /// <summary>Optional subject type (e.g. "Participant", "Hotel").</summary>
    public string? TargetType { get; set; }
}
