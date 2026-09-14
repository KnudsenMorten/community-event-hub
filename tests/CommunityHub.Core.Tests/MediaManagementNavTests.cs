using CommunityHub.Core.Domain;
using CommunityHub.Core.Navigation;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1078 — the "Media Management" section: two items, for the media crew and organizers only.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"Add 'Media Management' in main menu with 2 menu-items: Picture
/// upload management … Video upload management"</i>.</para>
///
/// <para>🔑 <b>The routes are HUB pages, and that is the point being pinned.</b> He supplied two
/// SharePoint URLs; a menu item pointing at one would open SharePoint in the visitor's own session —
/// their user context, their tenant account — which is exactly what he ruled out in the same
/// sentence. The nav test asserts the hub route, so a future "simplification" back to the raw link
/// fails here rather than in front of a photographer who has no SharePoint access.</para>
/// </remarks>
public sealed class MediaManagementNavTests
{
    private const string Pictures = "/Media/Pictures";
    private const string Videos = "/Media/Videos";

    private static IReadOnlyList<NavItem> ItemsFor(ParticipantRole role) =>
        NavBuilder.Build(role).Groups.SelectMany(g => g.Items).ToList();

    [Theory]
    [InlineData(ParticipantRole.Media)]
    [InlineData(ParticipantRole.Organizer)]
    public void The_media_crew_and_organizers_get_both_items(ParticipantRole role)
    {
        var items = ItemsFor(role);

        var picture = Assert.Single(items, i => i.Href == Pictures);
        var video = Assert.Single(items, i => i.Href == Videos);

        Assert.Equal("Nav.SectionMedia", picture.SectionKey);
        Assert.Equal("Nav.SectionMedia", video.SectionKey);
    }

    /// <summary>
    /// 🔒 Nobody else. These folders hold the event's raw footage; a sponsor or an attendee has no
    /// business in them, and the gate is asserted per role rather than assumed from the branch.
    /// </summary>
    [Theory]
    [InlineData(ParticipantRole.Speaker)]
    [InlineData(ParticipantRole.Sponsor)]
    [InlineData(ParticipantRole.Volunteer)]
    [InlineData(ParticipantRole.Attendee)]
    [InlineData(ParticipantRole.EventPartner)]
    public void No_other_role_sees_the_media_section(ParticipantRole role)
    {
        var items = ItemsFor(role);

        Assert.DoesNotContain(items, i => i.Href == Pictures || i.Href == Videos);
        Assert.DoesNotContain(items, i => i.SectionKey == "Nav.SectionMedia");
    }

    /// <summary>
    /// ⚠️ The items point INTO the hub. A SharePoint URL here would run in the visitor's own user
    /// context and need a tenant account per media person — the thing the whole feature avoids.
    /// </summary>
    [Fact]
    public void The_items_are_hub_pages_not_sharepoint_links()
    {
        var items = ItemsFor(ParticipantRole.Media)
            .Where(i => i.SectionKey == "Nav.SectionMedia")
            .ToList();

        Assert.Equal(2, items.Count);
        Assert.All(items, i =>
        {
            Assert.StartsWith("/Media/", i.Href);
            Assert.False(i.External);
            Assert.DoesNotContain("sharepoint", i.Href, StringComparison.OrdinalIgnoreCase);
        });
    }
}
