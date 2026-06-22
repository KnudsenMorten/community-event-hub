using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// <see cref="WelcomeVariants.TemplateKeyFor"/> maps each primary role to its
/// per-role welcome template key — the router both the welcome email and the
/// first-login portal welcome use.
/// </summary>
public class WelcomeVariantsTests
{
    [Theory]
    [InlineData(ParticipantRole.Speaker, "welcome-speaker")]
    [InlineData(ParticipantRole.Volunteer, "welcome-volunteer")]
    [InlineData(ParticipantRole.Sponsor, "welcome-sponsor")]
    [InlineData(ParticipantRole.Media, "welcome-media")]
    [InlineData(ParticipantRole.EventPartner, "welcome-eventpartner")]
    public void TemplateKeyFor_maps_each_welcomed_role_to_its_variant(ParticipantRole role, string key)
    {
        Assert.Equal(key, WelcomeVariants.TemplateKeyFor(role));
    }

    [Theory]
    [InlineData(ParticipantRole.Organizer)]
    [InlineData(ParticipantRole.Attendee)]
    public void TemplateKeyFor_returns_null_for_roles_with_no_welcome(ParticipantRole role)
    {
        // Operator 2026-06-22: organizers get no welcome; attendees are covered by
        // the Master Class confirmed-seat mail, not a platform welcome.
        Assert.Null(WelcomeVariants.TemplateKeyFor(role));
    }

    [Fact]
    public void Every_welcomed_role_maps_to_a_listed_template_key()
    {
        foreach (var role in System.Enum.GetValues<ParticipantRole>())
        {
            var key = WelcomeVariants.TemplateKeyFor(role);
            if (key is null) continue;   // Organizer / Attendee get no welcome
            Assert.Contains(key, WelcomeVariants.AllTemplateKeys);
        }
    }

    [Fact]
    public void Every_variant_key_is_in_the_email_template_catalog()
    {
        foreach (var key in WelcomeVariants.AllTemplateKeys)
        {
            Assert.True(EmailTemplateCatalog.Map.ContainsKey(key), $"catalog missing {key}");
            Assert.Equal("welcome-email", EmailTemplateCatalog.FeatureKeyFor(key));
        }
        // The MC selection invite is also catalogued under masterclass-invites.
        Assert.Equal("masterclass-invites",
            EmailTemplateCatalog.FeatureKeyFor("masterclass-selection-invite"));
    }
}
