using CommunityHub.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1265 — the kiosk learns about a deploy from <c>GET /evaluation/v1/kiosk/build</c> and reloads
/// itself, instead of running yesterday's page until the 03:00 reload.
/// </summary>
public sealed class EvaluationKioskBuildTests
{
    [Fact]
    public void The_build_endpoint_answers_the_running_build_and_is_never_cached()
    {
        // The endpoint touches no service, so none is supplied.
        var controller = new EvaluationKioskController(null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var ok = Assert.IsType<OkObjectResult>(controller.Build());

        var build = ok.Value!.GetType().GetProperty("build")!.GetValue(ok.Value) as string;
        Assert.Equal(EvaluationKioskController.BuildId, build);
        // 🔒 A cached answer is exactly the bug this fixes.
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void The_build_id_is_a_stable_opaque_token()
    {
        // 32 hex characters (a module version id) — present, and naming nothing about the environment.
        Assert.Matches("^[0-9a-f]{32}$", EvaluationKioskController.BuildId);
        Assert.Equal(EvaluationKioskController.BuildId, EvaluationKioskController.BuildId);
    }
}
