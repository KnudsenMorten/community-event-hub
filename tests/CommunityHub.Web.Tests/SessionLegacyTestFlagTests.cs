using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// 🔴 §1208 — THE LEGACY `Session.IsTestData` FLAG, VALIDATED AND MADE REACHABLE.
///
/// <para>Operator 2026-09-12: <i>"i think we use it so define if it is a tst session, validate if
/// that is true"</i>.</para>
///
/// <para>✅ <b>Validated, and it is not true.</b> "Mark as TEST session" writes
/// <c>Session.UsedForTesting</c>. The only writer of any <c>IsTestData</c> in the application is on
/// the SPONSORS page, for <c>SponsorInfo</c> (§905.1). No code has ever written the SESSION one.</para>
///
/// <para>🔴 <b>It is still load-bearing.</b> <c>SoMeSubjectScope</c> reads it as an exclusion, so a
/// session carrying it is absent from the whole campaign — and PROD rows DO carry it. That made it an
/// invisible reason for a session to be missing, with no way out except the database.</para>
///
/// <para>🔑 CLEAR-only: a second control meaning "test session" beside the one that already exists
/// would be two buttons for one idea. The fix for a flag nobody can reach is a way OUT of it.</para>
/// </summary>
public sealed class SessionLegacyTestFlagTests
{
    private static string View() =>
        File.ReadAllText(Path.Combine(
            FindDir("src", "CommunityHub", "Pages"), "Organizer", "Sessions.cshtml"));

    private static string Model() =>
        File.ReadAllText(Path.Combine(
            FindDir("src", "CommunityHub", "Pages"), "Organizer", "Sessions.cshtml.cs"));

    /// <summary>🔴 It is visible at all — the whole point, since it governs the campaign.</summary>
    [Fact]
    public void A_session_carrying_the_legacy_flag_says_so()
    {
        var page = View();

        Assert.Contains("r.IsTestData", page, StringComparison.Ordinal);
        Assert.Contains("Legacy test-data flag is set.", page, StringComparison.Ordinal);
    }

    /// <summary>And it can be cleared from the page, without touching the database.</summary>
    [Fact]
    public void It_can_be_cleared_from_the_sessions_page()
    {
        Assert.Contains("asp-page-handler=\"ClearLegacyTestData\"", View(), StringComparison.Ordinal);
        Assert.Contains("OnPostClearLegacyTestDataAsync", Model(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔒 CLEAR ONLY. There must be no control that SETS it: `UsedForTesting` is how a session is
    /// marked test, and a second flag meaning the same thing is what created this mess.
    /// </summary>
    [Fact]
    public void There_is_no_way_to_SET_it()
    {
        var model = Model();

        Assert.Contains("session.IsTestData = false;", model, StringComparison.Ordinal);
        Assert.DoesNotContain("session.IsTestData = true;", model, StringComparison.Ordinal);
        Assert.DoesNotContain("!session.IsTestData;", model, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ The button only appears on a session that HAS the flag — a control for a state nobody can
    /// enter would be noise on every other row.
    /// </summary>
    [Fact]
    public void The_button_is_hidden_on_sessions_that_do_not_carry_it()
    {
        var page = View();

        var guard = page.IndexOf("@if (r.IsTestData)", StringComparison.Ordinal);
        var button = page.IndexOf("ClearLegacyTestData", StringComparison.Ordinal);

        Assert.True(guard > 0 && button > guard,
            "the clear button must sit inside an `@if (r.IsTestData)` guard");
    }

    /// <summary>§854 — clearing it says what changed, and points at the two flags that DO mean something.</summary>
    [Fact]
    public void Clearing_it_explains_the_two_flags_that_are_still_real()
    {
        var model = Model();

        Assert.Contains("Legacy test-data flag cleared", model, StringComparison.Ordinal);
        Assert.Contains("Exclude from social media", model, StringComparison.Ordinal);
        Assert.Contains("Mark as TEST", model, StringComparison.Ordinal);
    }

    private static string FindDir(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(Path.Combine(parts));
    }
}
