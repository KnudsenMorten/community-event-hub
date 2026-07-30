using CommunityHub.Pages.Volunteer;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the legacy volunteer "My shifts" / "My tasks" routes (REQUIREMENTS
/// §234 Wave 3a): both standalone pages were merged into the unified
/// <c>/Volunteer/MySchedule</c> page (<see cref="MyScheduleModel"/>), and the old
/// routes only remain so bookmarks / e-mailed links keep working. The contract here
/// is a PERMANENT redirect to My schedule — no rendering, no handlers of their own.
/// The shift confirm/decline/swap behavior itself is covered at page level in
/// <see cref="VolunteerMyScheduleTests"/> and at service level in
/// VolunteerShiftServiceTests.
/// </summary>
public sealed class VolunteerMyShiftsTests
{
    [Fact]
    public void MyShifts_permanently_redirects_to_MySchedule()
    {
        var result = new MyShiftsModel().OnGet();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Volunteer/MySchedule", redirect.PageName);
        Assert.True(redirect.Permanent, "old /Volunteer/MyShifts links must 301 to MySchedule");
    }

    [Fact]
    public void MyTasks_permanently_redirects_to_MySchedule()
    {
        var result = new MyTasksModel().OnGet();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Volunteer/MySchedule", redirect.PageName);
        Assert.True(redirect.Permanent, "old /Volunteer/MyTasks links must 301 to MySchedule");
    }
}
