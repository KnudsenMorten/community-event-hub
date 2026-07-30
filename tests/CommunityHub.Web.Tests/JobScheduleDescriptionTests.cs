using CommunityHub.Pages.Organizer;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §440 — the Jobs page printed a raw NCRONTAB expression in the Schedule column, which the
/// operator summed up as "this is impossible to understand - how can i change the schedule".
/// These pin the plain-English rendering AND the deliberate fallback: an expression we do not
/// recognise must degrade to the raw string, never to a confidently wrong description.
/// </summary>
public sealed class JobScheduleDescriptionTests
{
    [Theory]
    [InlineData("0 */5 * * * *", "Every 5 minutes")]
    [InlineData("0 */1 * * * *", "Every minute")]
    [InlineData("0 0 */2 * * *", "Every 2 hours")]
    [InlineData("0 0 */1 * * *", "Every hour")]
    [InlineData("0 15 * * * *", "Every hour at :15")]
    [InlineData("0 30 6 * * *", "Every day at 06:30 UTC")]
    public void Common_schedules_read_as_plain_English(string cron, string expected) =>
        Assert.Equal(expected, JobsModel.DescribeCron(cron));

    [Theory]
    [InlineData("0 0 9 * * MON")]        // day-of-week restricted
    [InlineData("0 0 0 1 * *")]          // monthly
    [InlineData("*/30 * * * * *")]       // sub-minute
    [InlineData("not a cron")]
    public void Unrecognised_expressions_fall_back_to_the_RAW_string(string cron)
    {
        // Deliberate: a wrong description is worse than an unreadable one — an organizer who
        // trusts "Every day at 00:00" for a Monday-only job plans around something untrue.
        Assert.Equal(cron, JobsModel.DescribeCron(cron));
    }

    [Fact]
    public void Blank_stays_blank() => Assert.Equal("", JobsModel.DescribeCron(null));
}
