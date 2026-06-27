using System.Text;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Assistant;

/// <summary>
/// One already-authorized block of grounding for Otto — a short heading plus the
/// body text the model may quote from. Every section in an <see cref="OttoContext"/>
/// has already passed the authorization-at-retrieval gate (role-scoped content, or
/// the signed-in participant's OWN rows), so the assistant never sees anything the
/// caller is not allowed to see.
/// </summary>
public sealed record OttoGroundingSection(string Heading, string Body);

/// <summary>
/// The per-request grounding handed to <see cref="IOttoAssistant"/>. Built
/// SERVER-SIDE by <see cref="IOttoGroundingBuilder"/> from the signed-in
/// participant's role + id (never a client-supplied id). Carries ONLY the content
/// this role/person is authorized to see; the model is given no raw DB/table access.
/// </summary>
public sealed record OttoContext(
    ParticipantRole Role,
    int ParticipantId,
    IReadOnlyList<OttoGroundingSection> Sections)
{
    /// <summary>True when at least one section has usable body text.</summary>
    public bool HasContent => Sections.Any(s => !string.IsNullOrWhiteSpace(s.Body));

    /// <summary>An empty context (no grounding) for the given identity.</summary>
    public static OttoContext Empty(ParticipantRole role, int participantId) =>
        new(role, participantId, Array.Empty<OttoGroundingSection>());

    /// <summary>
    /// Flatten the authorized sections into the system-content the model is grounded
    /// on. Markdown headings keep the blocks separable for the model.
    /// </summary>
    public string ToGroundingText()
    {
        var sb = new StringBuilder();
        foreach (var s in Sections)
        {
            if (string.IsNullOrWhiteSpace(s.Body)) continue;
            sb.Append("## ").Append(s.Heading).Append('\n');
            sb.Append(s.Body.Trim()).Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Otto's reply. <see cref="Available"/> is false when Otto is disabled/unconfigured
/// or hit an error — the UI shows <see cref="Text"/> as a friendly message and never
/// surfaces an exception.
/// </summary>
public sealed record OttoAnswer(bool Available, string Text)
{
    public static OttoAnswer Unavailable(string message) => new(false, message);
}
