using CommunityHub.Core.Domain.Signage;
using CommunityHub.Core.Signage;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §754 §5–§7 + §10 — the two pure contracts behind the venue screens: which activities are on the
/// wall right now, and whether a screen gets content at all.
/// </summary>
public sealed class SignageAccessAndSlotTests
{
    private const string Zone = "Europe/Copenhagen";
    private static readonly TimeZoneInfo Cph = CommunityHub.Core.Config.EventLocalTime.Resolve(Zone);

    private static AgendaActivity Card(
        string id, int startHourUtc, int minutes, string? track = "Cloud",
        string? room = "Room-01-Floor-1", string title = "Talk") => new()
        {
            EventId = 1,
            BackstageSessionId = id,
            Title = title,
            StartsAt = new DateTimeOffset(2027, 2, 9, startHourUtc, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2027, 2, 9, startHourUtc, 0, 0, TimeSpan.Zero).AddMinutes(minutes),
            Track = track,
            Room = room,
            DayIndex = 1,
        };

    // ---------------------------------------------------------------- slots

    [Fact]
    public void Happening_now_is_the_CURRENT_full_hour_and_next_is_the_following_one()
    {
        // 10:30 local (09:30 UTC in February) ⇒ the 10:00–11:00 slot, next is 11:00–12:00.
        var at = new DateTimeOffset(2027, 2, 9, 9, 30, 0, TimeSpan.Zero);

        var now = SignageSlotBuilder.HourSlot(at, Cph);
        var next = SignageSlotBuilder.HourSlot(at, Cph, hoursAhead: 1);

        Assert.Equal(10, now.Start.Hour);
        Assert.Equal(11, now.End.Hour);
        Assert.Equal(11, next.Start.Hour);
        Assert.Equal(12, next.End.Hour);
    }

    [Fact]
    public void A_MULTI_HOUR_activity_appears_in_every_slot_it_overlaps()
    {
        // A 10:00–12:00 local master class (09:00–11:00 UTC in February).
        var masterClass = Card("mc", startHourUtc: 9, minutes: 120, title: "Identity Master Class");
        var at = new DateTimeOffset(2027, 2, 9, 9, 5, 0, TimeSpan.Zero);   // 10:05 local

        var now = SignageSlotBuilder.Build(new[] { masterClass }, at, Cph, pageSize: 8);
        var next = SignageSlotBuilder.Build(new[] { masterClass }, at, Cph, pageSize: 8, hoursAhead: 1);

        // 🔒 In BOTH. Anything else makes a running session vanish from "Happening Now" while it is
        // still running — the most misleading thing a schedule screen can do.
        Assert.Single(now.AllCards);
        Assert.Single(next.AllCards);
    }

    [Fact]
    public void An_activity_starting_exactly_on_the_next_hour_belongs_to_the_NEXT_slot_only()
    {
        var talk = Card("t", startHourUtc: 10, minutes: 60);   // 11:00–12:00 local
        var at = new DateTimeOffset(2027, 2, 9, 9, 30, 0, TimeSpan.Zero);   // 10:30 local

        Assert.Empty(SignageSlotBuilder.Build(new[] { talk }, at, Cph, 8).AllCards);
        Assert.Single(SignageSlotBuilder.Build(new[] { talk }, at, Cph, 8, hoursAhead: 1).AllCards);
    }

    [Fact]
    public void An_activity_ending_exactly_as_the_slot_opens_is_NOT_happening_now()
    {
        var talk = Card("t", startHourUtc: 8, minutes: 60);    // 09:00–10:00 local
        var at = new DateTimeOffset(2027, 2, 9, 9, 15, 0, TimeSpan.Zero);   // 10:15 local

        // A session that has just finished is not "happening now".
        Assert.Empty(SignageSlotBuilder.Build(new[] { talk }, at, Cph, 8).AllCards);
    }

    [Fact]
    public void Cards_sort_by_track_then_room_and_a_blank_track_sorts_LAST()
    {
        var at = new DateTimeOffset(2027, 2, 9, 9, 5, 0, TimeSpan.Zero);
        var cards = new[]
        {
            Card("c", 9, 60, track: "Security", room: "Room-02-Floor-1"),
            Card("a", 9, 60, track: "Cloud", room: "Room-09-Floor-2"),
            Card("b", 9, 60, track: "Cloud", room: "Room-01-Floor-1"),
            Card("z", 9, 60, track: null, room: null, title: "Coffee break"),
        };

        var order = SignageSlotBuilder.Build(cards, at, Cph, 8).AllCards
            .Select(x => x.BackstageSessionId).ToList();

        // Track, then room — and the break (no track) last, so it never heads the wall above the
        // sessions people are actually looking for.
        Assert.Equal(new[] { "b", "a", "c", "z" }, order);
    }

    [Fact]
    public void The_sort_is_DETERMINISTIC_for_two_activities_that_are_alike()
    {
        var at = new DateTimeOffset(2027, 2, 9, 9, 5, 0, TimeSpan.Zero);
        // Same track, same room, same time, same title — two runs of one workshop.
        var twin1 = Card("id-b", 9, 60, title: "Workshop");
        var twin2 = Card("id-a", 9, 60, title: "Workshop");

        var first = SignageSlotBuilder.Build(new[] { twin1, twin2 }, at, Cph, 8).AllCards;
        var second = SignageSlotBuilder.Build(new[] { twin2, twin1 }, at, Cph, 8).AllCards;

        // 🔒 Identical regardless of input order: a tie broken by chance would let a card swap pages
        // between renders and vanish in front of someone reading it.
        Assert.Equal(first.Select(c => c.BackstageSessionId), second.Select(c => c.BackstageSessionId));
        Assert.Equal("id-a", first[0].BackstageSessionId);
    }

    [Fact]
    public void A_slot_bigger_than_one_page_paginates_and_every_card_appears_exactly_once()
    {
        var at = new DateTimeOffset(2027, 2, 9, 9, 5, 0, TimeSpan.Zero);
        // §7's own example: 14 activities in portrait ⇒ 8 on page 1, 6 on page 2.
        var cards = Enumerable.Range(1, 14)
            .Select(i => Card($"s{i:D2}", 9, 60, room: $"Room-{i:D2}-Floor-1"))
            .ToArray();

        var slot = SignageSlotBuilder.Build(cards, at, Cph, pageSize: 8);

        Assert.Equal(2, slot.Pages.Count);
        Assert.Equal(8, slot.Pages[0].Cards.Count);
        Assert.Equal(6, slot.Pages[1].Cards.Count);
        Assert.All(slot.Pages, p => Assert.Equal(2, p.Of));
        Assert.Equal(14, slot.AllCards.Select(c => c.BackstageSessionId).Distinct().Count());
    }

    [Fact]
    public void An_empty_slot_has_no_pages_at_all()
    {
        var at = new DateTimeOffset(2027, 2, 9, 3, 0, 0, TimeSpan.Zero);
        var slot = SignageSlotBuilder.Build(new[] { Card("t", 9, 60) }, at, Cph, 8);

        Assert.True(slot.IsEmpty);
        Assert.Empty(slot.Pages);
    }

    [Fact]
    public void Page_size_comes_from_the_configured_grid()
    {
        var s = new SignageSettings();
        Assert.Equal(8, SignageSlotBuilder.PageSize(s, SignageOrientation.Portrait));    // 2 × 4
        Assert.Equal(15, SignageSlotBuilder.PageSize(s, SignageOrientation.Landscape));  // 5 × 3
    }

    // --------------------------------------------------------------- access

    private static SignageSettings Configured() => new()
    {
        EventId = 1,
        PortraitToken = "portrait-token",
        LandscapeToken = "landscape-token",
    };

    private static readonly DateTimeOffset Midday = new(2027, 2, 9, 11, 0, 0, TimeSpan.Zero);

    private static SignageAccessService.Decision Check(
        SignageSettings? s, string? token,
        SignageOrientation o = SignageOrientation.Portrait,
        SignageView v = SignageView.Feedback,
        DateTimeOffset? at = null) =>
        SignageAccessService.Check(s, o, v, token, at ?? Midday, Zone);

    [Fact]
    public void The_right_token_for_the_orientation_gets_content()
    {
        Assert.True(Check(Configured(), "portrait-token").ShowContent);
        Assert.True(Check(Configured(), "landscape-token", SignageOrientation.Landscape).ShowContent);
    }

    [Fact]
    public void The_OTHER_orientations_token_does_not_work()
    {
        // Two playlists, two tokens — one leaking must not open the other wall.
        var d = Check(Configured(), "landscape-token", SignageOrientation.Portrait);
        Assert.False(d.ShowContent);
        Assert.Equal(SignageAccessService.HoldingReason.TokenInvalid, d.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("guessed")]
    public void A_missing_or_wrong_token_HOLDS_it_never_errors(string? token)
    {
        var d = Check(Configured(), token);

        // 🔒 The whole access contract: every negative is the same HTTP 200 holding screen. An error
        // page does not protect anything here — it renders two metres tall in a public corridor.
        Assert.False(d.ShowContent);
        Assert.NotEqual(SignageAccessService.HoldingReason.None, d.Reason);
    }

    [Fact]
    public void An_UNSET_token_column_never_matches_a_blank_query_string()
    {
        // Both empty: a naive constant-time compare of two empty strings SUCCEEDS, which would
        // authorise every screen on the internet.
        var unset = new SignageSettings { EventId = 1, PortraitToken = "", LandscapeToken = "" };

        Assert.False(Check(unset, "").ShowContent);
        Assert.False(Check(unset, null).ShowContent);
    }

    [Fact]
    public void No_settings_row_at_all_holds_rather_than_throwing()
    {
        var d = Check(null, "anything");
        Assert.False(d.ShowContent);
        Assert.Equal(SignageAccessService.HoldingReason.NotConfigured, d.Reason);
    }

    [Fact]
    public void A_switched_off_view_holds_while_the_others_keep_serving()
    {
        var s = Configured();
        s.PortraitFeedbackEnabled = false;

        Assert.Equal(SignageAccessService.HoldingReason.ViewSwitchedOff,
            Check(s, "portrait-token", v: SignageView.Feedback).Reason);
        Assert.True(Check(s, "portrait-token", v: SignageView.Now).ShowContent);
        // The switch is per ORIENTATION too — landscape feedback is untouched.
        Assert.True(Check(s, "landscape-token", SignageOrientation.Landscape, SignageView.Feedback).ShowContent);
    }

    [Fact]
    public void An_UNSET_schedule_means_always_on()
    {
        // 🔒 The failure direction matters: a schedule that blanks every screen during the keynote
        // is unrecoverable in the moment; one that leaves them on overnight is merely untidy.
        var at = new DateTimeOffset(2027, 2, 9, 2, 0, 0, TimeSpan.Zero);
        Assert.True(Check(Configured(), "portrait-token", at: at).ShowContent);
    }

    [Fact]
    public void Outside_the_daily_window_it_holds_and_inside_it_serves()
    {
        var s = Configured();
        s.DailyFromLocal = new TimeOnly(8, 0);
        s.DailyToLocal = new TimeOnly(18, 0);

        // 12:00 local (11:00 UTC in February) — inside.
        Assert.True(Check(s, "portrait-token", at: Midday).ShowContent);
        // 06:00 local — before the window.
        Assert.Equal(SignageAccessService.HoldingReason.OutsideSchedule,
            Check(s, "portrait-token", at: new DateTimeOffset(2027, 2, 9, 5, 0, 0, TimeSpan.Zero)).Reason);
    }

    [Fact]
    public void A_window_that_crosses_midnight_covers_the_evening_and_the_small_hours()
    {
        // 22:00 → 02:00 — the party. Read as empty, this would switch the screens off exactly when
        // the venue is busiest.
        var s = Configured();
        s.DailyFromLocal = new TimeOnly(22, 0);
        s.DailyToLocal = new TimeOnly(2, 0);

        Assert.True(SignageAccessService.IsWithinSchedule(
            s, new DateTimeOffset(2027, 2, 9, 22, 30, 0, TimeSpan.Zero), Zone));   // 23:30 local
        Assert.True(SignageAccessService.IsWithinSchedule(
            s, new DateTimeOffset(2027, 2, 10, 0, 30, 0, TimeSpan.Zero), Zone));   // 01:30 local
        Assert.False(SignageAccessService.IsWithinSchedule(
            s, Midday, Zone));                                                      // 12:00 local
    }

    [Fact]
    public void Outside_the_active_DATE_range_it_holds()
    {
        var s = Configured();
        s.ActiveFromLocal = new DateOnly(2027, 2, 9);
        s.ActiveToLocal = new DateOnly(2027, 2, 10);

        Assert.True(Check(s, "portrait-token", at: Midday).ShowContent);
        Assert.False(Check(s, "portrait-token",
            at: new DateTimeOffset(2027, 2, 11, 11, 0, 0, TimeSpan.Zero)).ShowContent);
        Assert.False(Check(s, "portrait-token",
            at: new DateTimeOffset(2027, 2, 8, 11, 0, 0, TimeSpan.Zero)).ShowContent);
    }

    [Fact]
    public void A_minted_token_is_long_url_safe_and_never_repeats()
    {
        var a = SignageAccessService.NewToken();
        var b = SignageAccessService.NewToken();

        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 40);
        // It lives in a URL pasted into a third-party playlist — no padding, no +/ characters.
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
    }

    // -------------------------------------------------------------- palette

    [Fact]
    public void Rating_colours_come_from_the_report_palette_and_rating_2_is_YELLOW()
    {
        // 🔒 §754 conflict 3: the spec said "light red"; the device's third button is yellow, and
        // every CEH surface uses it. The wall and the speaker's PDF must never disagree.
        Assert.Equal("#E4B400", SignagePalette.RatingHex(2));
        Assert.Equal("#1E7A3C", SignagePalette.RatingHex(4));
        Assert.Equal("#6DBE45", SignagePalette.RatingHex(3));
        Assert.Equal("#C8322A", SignagePalette.RatingHex(1));
    }

    [Fact]
    public void No_view_ever_puts_white_text_on_Experts_Live_Green()
    {
        // 🔒 The current OptiSigns portrait template does exactly this, at roughly 2:1 — unreadable
        // from across a hall. Text on green is always DarkBlue.
        foreach (var view in Enum.GetValues<SignageView>())
        {
            var page = SignagePalette.ViewColours(view);
            var card = SignagePalette.CardColours(view);

            if (page.Background == SignagePalette.Green)
                Assert.NotEqual(SignagePalette.White, page.Foreground);
            if (card.Background == SignagePalette.Green)
                Assert.NotEqual(SignagePalette.White, card.Foreground);
        }
    }
}
