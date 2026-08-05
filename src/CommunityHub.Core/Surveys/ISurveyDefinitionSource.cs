namespace CommunityHub.Core.Surveys;

/// <summary>
/// §6.5 — how Core asks for a survey definition without knowing where they are stored.
/// </summary>
/// <remarks>
/// <para>The definitions are JSON files under <c>App_Data/Surveys/</c> and the loader
/// (<c>SurveyDefinitionProvider</c>) lives in the web project, so Core cannot reference it. Core
/// needs them for exactly one thing: the <b>derived close date</b>. This seam gives it that without
/// dragging file loading into Core or teaching every caller to pass the definition in.</para>
///
/// <para>🔒 <b>Why not just pass the months to <c>IsOpenAsync</c>?</b> Three call sites read the
/// open state (the public page's GET, its POST, and the organizer list). A parameter is a thing each
/// of them can forget, and forgetting it fails in the worst direction — a post-event survey that
/// should have closed silently keeps accepting responses. The seam makes it impossible to forget.
/// </para>
/// </remarks>
public interface ISurveyDefinitionSource
{
    /// <summary>The definition for a slug, or null when there is none.</summary>
    SurveyDefinition? TryGet(string slug);
}
