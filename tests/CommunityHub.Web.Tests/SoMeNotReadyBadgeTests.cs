using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1206 — "SHOW THEN AS NOT READY, LIKE SOME GRAPHICS MISSING".
///
/// <para>Operator 2026-09-12, asking first whether the 19 held-back posts exist in the planner at
/// all. They do — a held-back post is a planned post with a date, and it holds a seat in the capacity
/// report. What was missing was the LABEL on the two screens he works in.</para>
///
/// <para>🔴 <b>The queue carried one blocker out of a dozen.</b> "no graphic" was the only readiness
/// signal on a row, so a post waiting on a sponsor's social text, a session description or an
/// unlinked co-speaker looked perfectly fine — and Activate on it did nothing visible, which is
/// exactly the §1193 report. The CALENDAR said "held — not approved" for both a post nobody has got
/// to and a post nobody CAN approve: one badge, two situations, which is §335.</para>
/// </summary>
public sealed class SoMeNotReadyBadgeTests
{
    private static string Page(string name) =>
        File.ReadAllText(Path.Combine(
            FindDir("src", "CommunityHub", "Pages"), "Organizer", name));

    private static string Queue() => Page("SoMeQueue.cshtml");
    private static string Calendar() => Page("SoMeCalendar.cshtml");

    /// <summary>🔴 The chip is on the ROW, inside the loop — not only in a page header.</summary>
    [Fact]
    public void The_queue_marks_a_held_back_row_as_not_ready()
    {
        var page = Queue();

        Assert.Contains("Model.NotReadyReasons.TryGetValue(p.Id", page, StringComparison.Ordinal);

        var rowLoop = page.IndexOf("foreach (var p in Model.Posts)", StringComparison.Ordinal);
        var onRow = page.IndexOf("Model.NotReadyReasons.TryGetValue(p.Id", StringComparison.Ordinal);
        Assert.True(rowLoop > 0 && onRow > rowLoop,
            "The not-ready chip must render inside the row loop.");
    }

    /// <summary>
    /// 🔑 The chip names WHAT is missing — his own example was "some graphics missing" — and the full
    /// sentence stays on the row as a tooltip, so nothing is lost to the shortening.
    /// </summary>
    [Fact]
    public void The_chip_names_the_missing_thing_and_keeps_the_full_reason()
    {
        var page = Queue();

        Assert.Contains("SoMeReadiness.Chip(why)", page, StringComparison.Ordinal);
        Assert.Contains("title=\"@why\"", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🛑 §887 — "why show it twice". A graphic-blocked post reports it ONCE: the readiness chip
    /// wins, and the older "no graphic" badge is the `else` branch, where it means something
    /// different (the post is ready and WILL publish text-only).
    /// </summary>
    [Fact]
    public void The_old_no_graphic_badge_only_shows_when_the_post_is_otherwise_ready()
    {
        var page = Queue();

        var chip = page.IndexOf("Model.NotReadyReasons.TryGetValue(p.Id", StringComparison.Ordinal);
        var awaiting = page.IndexOf(
            "else if (Model.AwaitingGraphicPostIds.Contains(p.Id))", StringComparison.Ordinal);

        Assert.True(awaiting > chip && chip > 0,
            "the graphic badge must be the ELSE of the readiness chip, not a second badge beside it");
    }

    /// <summary>
    /// 🔑 The count is answerable where the plan is, and it says the two things he asked about:
    /// they ARE in the plan, and they move if their date passes (§1205).
    /// </summary>
    [Fact]
    public void The_queue_says_how_many_are_not_ready_and_that_they_stay_in_the_plan()
    {
        var page = Queue();

        Assert.Contains("Model.NotReadyCount", page, StringComparison.Ordinal);
        Assert.Contains("not ready", page, StringComparison.Ordinal);
        Assert.Contains("next\n            free slot", page.Replace("\r", ""), StringComparison.Ordinal);
    }

    /// <summary>🔴 The calendar separates "waiting for you" from "waiting for someone else".</summary>
    [Fact]
    public void The_calendar_distinguishes_not_ready_from_merely_unapproved()
    {
        var page = Calendar();

        var notReady = page.IndexOf("not ready —", StringComparison.Ordinal);
        var held = page.IndexOf("held — not approved", StringComparison.Ordinal);

        Assert.True(notReady > 0, "the calendar must say when a held post CANNOT be approved");
        Assert.True(held > notReady,
            "\"held — not approved\" must be the fallback, so a blocked post never wears it");
    }

    /// <summary>§854 — the full sentence too, because it names WHO owes the missing piece.</summary>
    [Fact]
    public void The_calendar_names_what_each_post_waits_on()
    {
        var page = Calendar();

        Assert.Contains("Waiting on:", page, StringComparison.Ordinal);
        Assert.Contains("SoMeReadiness.Short(post.Blocker)", page, StringComparison.Ordinal);
        Assert.Contains("Model.NotReady", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// §1209 — the Type column prints the CATEGORY (2a/2b/2c), not the template kind. Operator
    /// 2026-09-12: *"can you update the type column to reflect the 2a, 2b, 2c names instead if
    /// type = 2"*.
    /// </summary>
    [Fact]
    public void The_type_column_shows_the_category_not_the_template_kind()
    {
        var page = Queue();

        Assert.Contains("Model.TypeLabel(p)", page, StringComparison.Ordinal);
        // 🔒 The old call must be GONE from the column, or three categories keep sharing one label.
        Assert.DoesNotContain(
            "SoMeTemplateCatalog.Title(p.TemplateKind.Value)", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// §1209 — and the FILTER offers the same names. Operator 2026-09-12: *"remember to include it in
    /// filter as well"*. A column saying 2a beside a filter that only offers "Type 2" leaves no way to
    /// see the rest of the category you just read.
    /// </summary>
    [Fact]
    public void The_type_filter_offers_the_same_category_names()
    {
        var page = Queue();

        Assert.Contains("name=\"Category\"", page, StringComparison.Ordinal);
        Assert.Contains("SoMeCategoryRules.Label(c)", page, StringComparison.Ordinal);
        // 🔒 The old kind-based dropdown must be gone, or there are two Type filters.
        Assert.DoesNotContain("SoMeTemplateCatalog.Title(k)", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ An empty filtered view must offer the way back, or "no posts match" reads as "nothing is
    /// scheduled".
    /// </summary>
    [Fact]
    public void An_empty_category_view_still_offers_to_clear_the_filters()
    {
        Assert.Contains("Model.Category is not null", Queue(), StringComparison.Ordinal);
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
