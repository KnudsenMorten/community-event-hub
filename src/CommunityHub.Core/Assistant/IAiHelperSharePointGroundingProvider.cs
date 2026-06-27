namespace CommunityHub.Core.Assistant;

/// <summary>
/// Supplies PUBLIC, all-roles grounding from the operator-dropped documents in the
/// configured SharePoint grounding folder (REQUIREMENTS §152): the operator drops
/// md / txt / docx / pdf / xlsx files into
/// <c>General/Events/ELDK 2027/EventHub/ExtraAIGroundingInfo</c> and the AI Community
/// Helper answers from their text. Editing a file on SharePoint propagates WITHOUT a
/// deploy (the implementation caches with a short TTL — that TTL IS the refresh window).
///
/// <para>PARITY with <see cref="IAiHelperPublicInfoProvider"/> (§149): like that provider,
/// these sections are added to the grounding for ALL roles (no role gate) because the
/// folder is curated by the operator as public reference material. UNLIKE the §149
/// provider there is NO <c>eventId</c> parameter — the grounding folder is a per-edition
/// DEPLOYMENT CONFIG path (an app setting), not EF-backed per-event data, so the source
/// is the same for every event the deployment serves.</para>
///
/// <para>The implementation lives in Core (no EF / web deps) and is INERT — returns an
/// empty list, never throws — when the SharePoint read seam is not wired or the folder
/// path is blank. Optional: when not wired, the grounding builder simply omits it.</para>
/// </summary>
public interface IAiHelperSharePointGroundingProvider
{
    /// <summary>
    /// The grounding sections built from the SharePoint grounding folder's documents
    /// (may be empty when not configured or the folder is empty/unreachable). Read-only;
    /// never throws.
    /// </summary>
    Task<IReadOnlyList<AiHelperGroundingSection>> GetGroundingAsync(CancellationToken ct = default);
}
