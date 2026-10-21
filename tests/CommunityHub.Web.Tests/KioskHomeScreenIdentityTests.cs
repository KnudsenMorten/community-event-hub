using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1275 — an iPad that is added to the Home Screen after enrolling must stay the SAME device.
/// </summary>
/// <remarks>
/// Operator 2026-09-21: <i>"i started enrolling from browser. then i added page to home page and then
/// i met enrollment again so now i have same device 2 times - with different serial numbers"</i>. On
/// iOS a Home Screen web app has its own storage, so the identity Safari saved is invisible to it.
/// The one thing iOS carries over is the ADDRESS, so the reconnect link must be in it: put there on
/// approval, and left there in a Safari tab. The page has no render harness; these pin the script.
/// </remarks>
public sealed class KioskHomeScreenIdentityTests
{
    private static string Markup()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages", "Eval", "Index.cshtml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Eval/Index.cshtml not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Approval_puts_the_reconnect_link_in_the_address_bar_in_a_Safari_tab()
    {
        var m = Markup();
        Assert.Contains("b.enrolToken", m);
        Assert.Contains("history.replaceState(null, \"\", location.pathname + \"?k=\"", m);
    }

    [Fact]
    public void The_link_is_stripped_ONLY_in_the_Home_Screen_app()
    {
        var m = Markup();
        Assert.Contains("if (isStandalone()) { stripToken(); }", m);
        // The old unconditional strip is what made "Add to Home Screen" save a bare /eval.
        Assert.DoesNotContain("save(\"ceh.eval.enrol\", enrolToken); stripToken(); }", m);
    }
}
