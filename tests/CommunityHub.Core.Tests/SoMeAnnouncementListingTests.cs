using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1287 — the social-media announcement notice is listed under BOTH roles it reaches.
/// </summary>
/// <remarks>
/// Operator 2026-09-21: *"but i dont see it under sponsor"*. It mails a session's speakers and a
/// sponsor's signers/coordinators (<c>SoMeAnnouncementNotifier.AudienceForAsync</c>), but was filed
/// only under Speaker, so the Sponsors section neither showed it nor offered a sponsor ring.
/// </remarks>
public sealed class SoMeAnnouncementListingTests
{
    [Fact]
    public void It_reaches_speakers_and_sponsors()
    {
        var roles = EmailTemplateCatalog.RecipientRolesFor("some-announcement");
        Assert.Contains(ParticipantRole.Speaker, roles);
        Assert.Contains(ParticipantRole.Sponsor, roles);
    }

    [Fact]
    public void It_is_listed_under_Speakers_first_and_also_under_Sponsors()
    {
        var listed = EmailTemplateCatalog.ListingAudiencesFor("some-announcement");
        Assert.Equal(EmailAudience.Speaker, listed[0]);   // its home is unchanged
        Assert.Contains(EmailAudience.Sponsor, listed);
    }
}
