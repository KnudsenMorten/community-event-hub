using CommunityHub.Core.Domain;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// REQUIREMENTS §25f: the group photo is a perk for the larger volume packages —
/// only a company with MORE THAN <see cref="GroupPhotoRegistration.QualifyingTicketThreshold"/>
/// tickets qualifies. The threshold is exclusive (10 does NOT qualify; 11 does).
/// </summary>
public sealed class GroupPhotoRegistrationTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(10, false)]   // exactly the threshold does NOT qualify ("more than 10")
    [InlineData(11, true)]
    [InlineData(50, true)]
    public void Qualifies_is_true_only_above_the_ticket_threshold(int tickets, bool expected)
    {
        var reg = new GroupPhotoRegistration { TicketCount = tickets };
        Assert.Equal(expected, reg.Qualifies);
    }

    [Fact]
    public void Threshold_is_ten()
    {
        Assert.Equal(10, GroupPhotoRegistration.QualifyingTicketThreshold);
    }
}
