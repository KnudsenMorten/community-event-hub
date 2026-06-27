using System;
using System.IO;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// REQUIREMENTS §21 Participant [L] "Micro-polish: clipboard 'copied' feedback,
/// char counters, submit loading state (double-click guard), unsaved-changes
/// guard". These are progressive-enhancement behaviours wired in _Layout.cshtml
/// and opted into per-page via data-attributes — so the cheapest faithful guard
/// is a static check over the Razor sources (no app host needed), in the same
/// spirit as <see cref="LayoutSectionsTests"/>. It proves:
///   1. the four shared behaviours exist in the layout script, and
///   2. the high-value participant/attendee/speaker/sponsor forms actually opt in
///      (so a future edit that drops the attribute is caught).
/// </summary>
public sealed class MicroPolishWiringTests
{
    private static string PagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate src/CommunityHub/Pages from " + AppContext.BaseDirectory);
    }

    private static string Page(params string[] parts) =>
        File.ReadAllText(Path.Combine(PagesDir(), Path.Combine(parts)));

    // §27: the layout's client behaviours + styles now live in shared partials that
    // _Layout (and _PublicLayout) include. "The layout" for wiring checks = the sum.
    private static string LayoutAll() =>
        Page("Shared", "_Layout.cshtml")
        + "\n" + Page("Shared", "_LayoutClientScripts.cshtml")
        + "\n" + Page("Shared", "_LayoutStyles.cshtml");

    [Theory]
    [InlineData("data-ceh-copy")]      // 4. copy-to-clipboard confirmation
    [InlineData("data-ceh-counter")]   // 5. live character counter
    [InlineData("data-ceh-loading")]   // 6. submit loading + double-click guard
    [InlineData("data-ceh-dirty")]     // 7. unsaved-changes guard
    public void Layout_script_implements_each_micro_polish_behaviour(string attribute)
    {
        var layout = LayoutAll();
        Assert.Contains(attribute, layout);
    }

    [Fact]
    public void Layout_copy_behaviour_uses_the_clipboard_api_with_a_fallback()
    {
        var layout = LayoutAll();
        Assert.Contains("navigator.clipboard", layout);
        Assert.Contains("execCommand", layout); // graceful old-browser fallback
    }

    [Fact]
    public void Loading_guard_disables_the_submit_button_so_a_double_click_cannot_post_twice()
    {
        var layout = LayoutAll();
        // The guard sets a one-shot marker and disables the submit control.
        Assert.Contains("cehSubmitted", layout);
        Assert.Contains("btn.disabled = true", layout);
    }

    [Fact]
    public void Dirty_guard_warns_via_beforeunload_only_after_an_edit()
    {
        var layout = LayoutAll();
        Assert.Contains("beforeunload", layout);
    }

    [Theory]
    // Public attendee no-login forms.
    [InlineData("Sessions", "Ask.cshtml")]
    [InlineData("Sessions", "Evaluate.cshtml")]
    // Public volunteer application.
    [InlineData("Volunteer", "Signup.cshtml")]
    // Speaker self-service bio.
    [InlineData("Forms", "Speaker.cshtml")]
    // Sponsor on-booth lead capture.
    [InlineData("Sponsor", "CaptureLead.cshtml")]
    public void High_value_forms_opt_into_the_loading_and_dirty_guards(string folder, string file)
    {
        var page = Page(folder, file);
        Assert.Contains("data-ceh-loading", page);
        Assert.Contains("data-ceh-dirty", page);
    }

    [Theory]
    // Each of these has a free-text field that should carry a live char counter.
    [InlineData("Sessions", "Ask.cshtml")]
    [InlineData("Sessions", "Evaluate.cshtml")]
    // Volunteer/Signup dropped — the 3-step wizard has no prominent free-text field.
    [InlineData("Forms", "Speaker.cshtml")]
    [InlineData("Sponsor", "CaptureLead.cshtml")]
    public void Free_text_fields_carry_a_live_character_counter(string folder, string file)
    {
        var page = Page(folder, file);
        Assert.Contains("data-ceh-counter", page);
    }

    // (Removed: Calendar_copy_buttons_use_the_shared_copy_behaviour — the user-facing
    // "Add to my calendar" subscribe cards on Index/MySchedule were retired, so there
    // are no calendar copy buttons left to assert.)
}
