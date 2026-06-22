namespace CommunityHub.Pages.Shared;

/// <summary>
/// One tile in an organizer hub's button-grid (<c>_HubGrid</c>): a link to a
/// feature page with a title and a one-line description. Pure view data.
/// </summary>
/// <param name="Href">The feature-page route (e.g. <c>/Organizer/Participants</c>).</param>
/// <param name="Title">The tile heading.</param>
/// <param name="Description">Optional one-line explanation shown under the title.</param>
/// <param name="FeatureKey">
/// Optional <see cref="CommunityHub.Core.Settings.FeatureCatalog"/> key. When set AND
/// the feature is USER-IMPACT, <c>_HubGrid</c> badges the tile with its released ring
/// (a yellow "Ring N" pill while it is not yet Broad/GA) and HIDES it from users whose
/// effective ring is above the released ring — the same staged-rollout gate the nav
/// uses (<c>_Layout</c>). Engine features are never badged/gated here.
/// </param>
public sealed record HubTile(string Href, string Title, string? Description = null, string? FeatureKey = null);
