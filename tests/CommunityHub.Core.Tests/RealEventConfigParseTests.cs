using CommunityHub.Core.Config;
using Xunit;
namespace CommunityHub.Core.Tests;
public class RealEventConfigParseTests
{
    [Fact]
    public void Real_event_config_parses_volunteer_section()
    {
        var cfg = new EventEditionConfigLoader().Load("config/event.eldk27.json");
        Assert.NotNull(cfg.Volunteer);
        Assert.Contains(cfg.Volunteer!.ExtraAvailabilityDays, d => d.Date == "2027-02-07");
        Assert.NotNull(cfg.SharePoint);
        Assert.False(string.IsNullOrEmpty(cfg.SharePoint!.VolunteerPhotoFolderPath));
    }
}
