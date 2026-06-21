namespace CommunityHub.Pages.Shared;

/// <summary>
/// One tile in an organizer hub's button-grid (<c>_HubGrid</c>): a link to a
/// feature page with a title and a one-line description. Pure view data.
/// </summary>
/// <param name="Href">The feature-page route (e.g. <c>/Organizer/Participants</c>).</param>
/// <param name="Title">The tile heading.</param>
/// <param name="Description">Optional one-line explanation shown under the title.</param>
public sealed record HubTile(string Href, string Title, string? Description = null);
