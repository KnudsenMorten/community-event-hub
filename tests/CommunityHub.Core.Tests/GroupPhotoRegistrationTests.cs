using CommunityHub.Core.Domain;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The group photo is a perk for the volume packages: a company with
/// <see cref="GroupPhotoRegistration.QualifyingTicketThreshold"/> tickets <b>OR MORE</b> qualifies.
/// </summary>
/// <remarks>
/// 🔴 <b>This file used to assert the opposite at the boundary.</b> §25f (June) made the threshold
/// EXCLUSIVE — <i>"10 does NOT qualify; 11 does"</i> — while §1077 (August) defines the volume
/// package as ≥10 and names the group photo as one of its three benefits. Linking the two models in
/// stage 4 is what surfaced it: a company with exactly ten qualified for the package and was refused
/// the photo invite, with a message asking them to raise the ticket count.
///
/// <para>✅ The operator settled it — <i>"10 or more is correct"</i> (2026-08-11) — so the boundary
/// case below now asserts a DECISION rather than an accident, and cannot drift back unnoticed.</para>
/// </remarks>
public sealed class GroupPhotoRegistrationTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]    // 🔴 the boundary he settled: exactly ten DOES qualify
    [InlineData(11, true)]
    [InlineData(50, true)]
    public void Qualifies_is_true_at_ten_or_more(int tickets, bool expected)
    {
        var reg = new GroupPhotoRegistration { TicketCount = tickets };
        Assert.Equal(expected, reg.Qualifies);
    }

    [Fact]
    public void Threshold_is_ten()
    {
        Assert.Equal(10, GroupPhotoRegistration.QualifyingTicketThreshold);
    }

    /// <summary>
    /// 🔑 The photo rule and the volume-package rule are now the SAME number, from the same
    /// direction. Asserted together so a change to one is a visible change to both.
    /// </summary>
    [Fact]
    public void The_photo_threshold_matches_the_volume_package_threshold()
    {
        Assert.Equal(
            CommunityHub.Core.Integrations.VolumePackageQualificationService.Threshold,
            GroupPhotoRegistration.QualifyingTicketThreshold);

        var atTheBoundary = new GroupPhotoRegistration
        {
            TicketCount = CommunityHub.Core.Integrations.VolumePackageQualificationService.Threshold,
        };
        Assert.True(atTheBoundary.Qualifies);
    }
}
