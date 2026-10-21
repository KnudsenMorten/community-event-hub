using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1278 — the follow-up questions on the kiosk and the QR page. No render harness exists for either
/// page, so the contract is pinned in the markup: the kiosk HOLDS the queued vote while asking and
/// sends the answers with it; the QR page posts them with the rating.
/// </summary>
public sealed class KioskFollowUpMarkupTests
{
    private static string Page(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName, "src", "CommunityHub", "Pages" }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }

    [Fact]
    public void The_kiosk_queues_the_vote_FIRST_then_holds_it_for_the_follow_ups()
    {
        var m = Page("Eval", "Index.cshtml");
        var vote = m[m.IndexOf("function vote(rating, el)", StringComparison.Ordinal)..];
        vote = vote[..(vote.IndexOf("askFollowUps(v, el);", StringComparison.Ordinal) + 20)];
        // Durable before anything else: pushed and saved, with a hold TIME (never a flag that could strand it).
        // §1353 — the hold is only set when follow-ups are asked.
        Assert.Contains("if (followUpsOn) { v.hold = Date.now() + HOLD_MS; }", vote);
        Assert.True(vote.IndexOf("save(K_Q, queue)", StringComparison.Ordinal)
                    < vote.IndexOf("askFollowUps", StringComparison.Ordinal));
    }

    [Fact]
    public void In_one_tap_mode_the_vote_is_recorded_and_sent_at_once_without_follow_ups()
    {
        // §1353 — operator: *"turn off page 2+3 and switch to only page 1 (4 buttons)"*.
        var m = Page("Eval", "Index.cshtml");
        var vote = m[m.IndexOf("function vote(rating, el)", StringComparison.Ordinal)..];
        vote = vote[..(vote.IndexOf("askFollowUps(v, el);", StringComparison.Ordinal) + 20)];
        var oneTap = vote.IndexOf("if (!followUpsOn) { flash(el); flush(); return; }", StringComparison.Ordinal);
        Assert.True(oneTap > 0, "one-tap branch missing");
        Assert.True(vote.IndexOf("save(K_Q, queue)", StringComparison.Ordinal) < oneTap, "the vote must be saved before the one-tap return");
        Assert.True(oneTap < vote.IndexOf("askFollowUps(v, el);", StringComparison.Ordinal));
    }

    [Fact]
    public void The_kiosk_stamps_each_vote_with_its_policy_and_sends_it()
    {
        // §1354 — a vote queued before a switch must keep the policy it was asked under.
        var m = Page("Eval", "Index.cshtml");
        Assert.Contains("if (typeof b.policy === \"number\") { capturePolicy = b.policy; }", m);
        Assert.Contains("if (capturePolicy !== null) { v.policy = capturePolicy; }", m);
        Assert.Contains("policy: (typeof v.policy === \"number\") ? v.policy : null", m);
    }

    [Fact]
    public void The_kiosk_draws_every_symbol_on_every_button_and_the_server_picks_which_show()
    {
        // §1355 — faces / stars / points, one or two; the vote carries what was shown.
        var m = Page("Eval", "Index.cshtml");
        foreach (var r in new[] { 4, 3, 2, 1 })
        {
            Assert.Contains($"RatingSymbolChoices.Stars({r})</span>", m);
            Assert.Contains($"<span class=\"sym sym-points\" aria-hidden=\"true\">{r}</span>", m);
        }
        Assert.Contains("if (typeof b.symbols === \"number\") { applySymbols(b.symbols); }", m);
        Assert.Contains("cl.toggle(\"s-nofaces\", !(s & 1));", m);
        Assert.Contains("if (ratingSymbols !== null) { v.symbols = ratingSymbols; }", m);
        Assert.Contains("symbols: (typeof v.symbols === \"number\") ? v.symbols : null", m);
        // Two symbols shrink so the word stays on the tile.
        Assert.Contains("body.s-two button.face svg { height: 36%; }", m);
    }

    [Fact]
    public void The_kiosk_shows_its_battery_on_the_bottom_line_and_sends_it_with_the_heartbeat()
    {
        // §1358 — operator: "i cannot see the battery status of the device".
        var m = Page("Eval", "Index.cshtml");
        Assert.Contains("<span id=\"stBattery\" class=\"conn\" hidden></span>", m);
        Assert.Contains("if (navigator.getBattery) {", m);   // feature-detected: nothing shown where unsupported
        Assert.Contains("batteryPercent: batteryPct,", m);
    }

    [Fact]
    public void The_QR_page_draws_only_the_symbols_it_is_given()
    {
        var m = Page("Feedback.cshtml");
        Assert.Contains("@if (ratingSymbols.HasFlag(RatingSymbols.Faces)) { @FeedbackModel.FaceSvg(rating) }", m);
        Assert.Contains("@if (ratingSymbols.HasFlag(RatingSymbols.Stars))", m);
        Assert.Contains("@if (ratingSymbols.HasFlag(RatingSymbols.Points))", m);
    }

    [Fact]
    public void The_QR_page_shows_only_the_sections_its_policy_asks()
    {
        // §1354 — one form, sections shown/hidden (operator 2026-10-19).
        var m = Page("Feedback.cshtml");
        Assert.Contains("@if (askFollowUps)", m);
        Assert.Contains("@if (askFreeText)", m);
        Assert.True(m.IndexOf("@if (askFollowUps)", StringComparison.Ordinal) < m.IndexOf("name=\"PositivePoint\"", StringComparison.Ordinal));
        Assert.True(m.IndexOf("@if (askFreeText)", StringComparison.Ordinal) < m.IndexOf("asp-for=\"Comment\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_kiosk_takes_the_mode_from_every_session_answer_and_defaults_to_follow_ups()
    {
        var m = Page("Eval", "Index.cshtml");
        Assert.Contains("var followUpsOn = true, capturePolicy = null, ratingSymbols = null;", m);   // §1355 added the symbols
        var render = m[m.IndexOf("function render(b)", StringComparison.Ordinal)..];
        // Read BEFORE render rebuilds b (the rebuilt objects drop the field).
        Assert.True(render.IndexOf("followUpsOn = b.followUps", StringComparison.Ordinal)
                    < render.IndexOf("var now = Date.now();", StringComparison.Ordinal));
    }

    [Fact]
    public void The_kiosk_sends_the_answers_with_the_vote_and_skips_only_held_ones()
    {
        var m = Page("Eval", "Index.cshtml");
        Assert.Contains("positivePoint: v.pos || null, improvementPoint: v.imp || null", m);
        Assert.Contains("return !v.hold || v.hold <= now;", m);
        Assert.Contains("data-kind=\"pos\"", m);
        Assert.Contains("data-kind=\"imp\"", m);
    }

    [Fact]
    public void Both_pages_say_to_pick_only_one_per_question()
    {
        // §1280 — twice on each page: once under each question.
        foreach (var m in new[] { Page("Eval", "Index.cshtml"), Page("Feedback.cshtml") })
        {
            var hits = m.Split("EvaluationFollowUps.PickOneHint").Length - 1;
            Assert.Equal(2, hits);
        }
    }

    [Fact]
    public void The_QR_page_posts_both_answers_with_the_rating()
    {
        var m = Page("Feedback.cshtml");
        Assert.Contains("name=\"PositivePoint\"", m);
        Assert.Contains("name=\"ImprovementPoint\"", m);
    }
}
