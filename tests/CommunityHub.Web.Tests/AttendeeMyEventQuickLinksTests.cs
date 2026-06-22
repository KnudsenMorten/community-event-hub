using CommunityHub.Core.Domain;
using CommunityHub.Pages.Attendee;
using CommunityHub.Pages.Forms;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Bug-fix §20 Attendee My-event — crew-only quick-link leak. The My-event page
/// used to render the Hotel / Swag / Lunch <c>/Forms/*</c> quick-links to EVERY
/// role, but those forms are role-gated to crew / speaker / volunteer / organizer
/// (an attendee is not eligible and lands on AccessDenied). These tests pin the
/// pure eligibility helpers the page model now branches on, so the rendered links
/// can never point a role at a form it would be denied on.
///
/// The eligibility is sourced from the forms' own source-of-truth role sets
/// (<see cref="LunchModel.EligibleRoles"/> / <see cref="SwagModel.EligibleRoles"/>)
/// and the nav's Hotel role-set — verified here to stay in lock-step.
/// </summary>
public sealed class AttendeeMyEventQuickLinksTests
{
    // --- (a) An attendee sees NONE of the crew-only form quick-links ----------

    [Fact]
    public void Attendee_is_not_eligible_for_hotel_swag_or_lunch_quick_links()
    {
        Assert.False(MyEventModel.IsEligibleForHotel(ParticipantRole.Attendee));
        Assert.False(MyEventModel.IsEligibleForSwag(ParticipantRole.Attendee));
        Assert.False(MyEventModel.IsEligibleForLunch(ParticipantRole.Attendee));
    }

    [Fact]
    public void Sponsor_is_not_eligible_for_hotel_swag_or_lunch_quick_links()
    {
        // Sponsors are likewise excluded from these crew/speaker forms.
        Assert.False(MyEventModel.IsEligibleForHotel(ParticipantRole.Sponsor));
        Assert.False(MyEventModel.IsEligibleForSwag(ParticipantRole.Sponsor));
        Assert.False(MyEventModel.IsEligibleForLunch(ParticipantRole.Sponsor));
    }

    // --- (b) Eligible roles DO see the links they are entitled to -------------

    [Theory]
    [InlineData(ParticipantRole.Organizer)]
    [InlineData(ParticipantRole.Speaker)]
    [InlineData(ParticipantRole.Volunteer)]
    public void Lunch_and_swag_eligible_roles_see_those_links(ParticipantRole role)
    {
        Assert.True(MyEventModel.IsEligibleForLunch(role));
        Assert.True(MyEventModel.IsEligibleForSwag(role));
    }

    [Theory]
    [InlineData(ParticipantRole.Organizer)]
    [InlineData(ParticipantRole.Speaker)]
    [InlineData(ParticipantRole.Volunteer)]
    [InlineData(ParticipantRole.Media)]
    [InlineData(ParticipantRole.EventPartner)]
    public void Hotel_eligible_roles_see_the_hotel_link(ParticipantRole role)
    {
        Assert.True(MyEventModel.IsEligibleForHotel(role));
    }

    // --- Lock-step with the forms' own source-of-truth eligibility ------------

    [Fact]
    public void Quick_link_eligibility_matches_the_forms_own_eligible_roles()
    {
        foreach (ParticipantRole role in Enum.GetValues<ParticipantRole>())
        {
            Assert.Equal(LunchModel.EligibleRoles.Contains(role), MyEventModel.IsEligibleForLunch(role));
            Assert.Equal(SwagModel.EligibleRoles.Contains(role), MyEventModel.IsEligibleForSwag(role));
        }
    }
}
