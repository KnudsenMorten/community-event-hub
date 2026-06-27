using CommunityHub.Core.Volunteers;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// §150 route-by-Responsible-Team: ELDK-Volunteers → Volunteer queue; ELDK +
/// ELDK-MOK → Organizer queue; every other named team and blank/unknown →
/// TrackedOnly (imported, never auto-assigned/queued).
/// </summary>
public sealed class ResponsibleTeamRouterTests
{
    [Theory]
    [InlineData("ELDK-Volunteers")]
    [InlineData("eldk-volunteers")]    // case-insensitive
    [InlineData("  ELDK-Volunteers  ")] // trimmed
    public void Volunteer_team_routes_to_volunteer_queue(string team) =>
        Assert.Equal(AllocationQueueKind.Volunteer, ResponsibleTeamRouter.Route(team));

    [Theory]
    [InlineData("ELDK")]
    [InlineData("ELDK-MOK")]
    [InlineData("eldk")]
    [InlineData("eldk-mok")]
    public void Organizer_bands_route_to_organizer_queue(string team) =>
        Assert.Equal(AllocationQueueKind.Organizer, ResponsibleTeamRouter.Route(team));

    [Theory]
    [InlineData("BeFree")]
    [InlineData("BC-F&B")]
    [InlineData("BC-Build")]
    [InlineData("Photo")]
    [InlineData("Video")]
    [InlineData("Speaker-team")]
    [InlineData("Community Reporter")]
    [InlineData("Transporter")]
    [InlineData("Expo-Sponsor")]
    [InlineData("Sponsor")]
    [InlineData("All")]
    public void Every_other_named_team_is_tracked_only(string team) =>
        Assert.Equal(AllocationQueueKind.TrackedOnly, ResponsibleTeamRouter.Route(team));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Something-We-Have-Never-Seen")]
    public void Blank_or_unknown_is_tracked_only(string? team) =>
        Assert.Equal(AllocationQueueKind.TrackedOnly, ResponsibleTeamRouter.Route(team));
}
