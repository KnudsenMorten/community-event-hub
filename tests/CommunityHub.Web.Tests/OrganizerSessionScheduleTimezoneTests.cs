using CommunityHub.Core.Integrations;
using CommunityHub.Pages.Organizer;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// 🔴 §1010 — the <c>/Organizer/Sessions</c> schedule editor's timezone round trip.
/// </summary>
/// <remarks>
/// <para><b>The defect these pin.</b> The two schedule boxes were labelled <i>"Starts (UTC)"</i>,
/// DREW the stored value's own offset — Danish, for every Sessionize-imported row (§305) — and
/// PARSED what came back as UTC. A <c>datetime-local</c> input always posts its pre-filled value,
/// so the round trip needed nobody to touch the time: correcting a ROOM re-saved 09:00 Danish as
/// 09:00Z, and the session moved one hour (CET) or two (CEST).</para>
///
/// <para>⚠️ <b>Why it stopped being cosmetic.</b> §1000 made CEH the OWNER of the schedule, so a
/// shifted value is now pushed at the public agenda as a difference to apply, and §1004 mails every
/// speaker on the session that their time changed. A display wart became a write that reaches the
/// attendees and the speakers.</para>
///
/// <para>🔑 <b>The round-trip fact is the one that matters.</b> Asserting only that the box shows
/// Danish time would have passed on the broken code — the DRAW was already Danish. The bug lived in
/// the disagreement between the two halves, so the test has to exercise both.</para>
/// </remarks>
public sealed class OrganizerSessionScheduleTimezoneTests
{
    /// <summary>
    /// 🔒 The PAGE's own parse, not a re-implementation of it. Calling
    /// <c>EventTimezone.ParseEventLocal</c> here instead would keep passing on the day somebody
    /// changed the page back to reading the box as UTC — which is the defect itself.
    /// </summary>
    private static System.DateTimeOffset? Parse(string? posted) =>
        SessionsModel.ParseEventLocal(posted);

    [Theory]
    // CET (winter) — the event itself, offset +01:00.
    [InlineData("2027-02-10T09:00")]
    // CEST (summer) — offset +02:00, so a fix that hard-codes one offset fails here.
    [InlineData("2027-07-01T14:30")]
    public void Editing_a_session_without_touching_the_time_leaves_the_instant_alone(string posted)
    {
        // The box is drawn, posted back unchanged (what happens when the organizer edits the ROOM),
        // and parsed. The stored instant must be identical — this is the whole defect.
        var stored = Parse(posted);
        Assert.NotNull(stored);

        var redrawn = SessionsModel.FormatEventLocal(stored);
        Assert.Equal(posted, redrawn);

        var reparsed = Parse(redrawn);
        Assert.Equal(stored, reparsed);
    }

    [Fact]
    public void A_time_typed_into_the_box_is_read_as_Danish_wall_time_not_UTC()
    {
        // 09:00 on the event day is 08:00 UTC (CET, +01:00). The old parser stored 09:00Z, which
        // Zoho and the public agenda would have shown as 10:00.
        var stored = Parse("2027-02-10T09:00");

        Assert.Equal(new System.DateTimeOffset(2027, 2, 10, 8, 0, 0, System.TimeSpan.Zero),
            stored!.Value.ToUniversalTime());
    }

    [Fact]
    public void A_value_stored_with_a_UTC_offset_is_drawn_in_Danish_time()
    {
        // 🔒 The conversion must be EXPLICIT, not "print whatever offset the row carries".
        // Sessionize rows carry the Danish offset and looked right either way — but a row written
        // by this very form carried offset ZERO, so the two sources drew differently in the same
        // column. 08:00Z on the event day IS 09:00 Danish.
        var utcRow = new System.DateTimeOffset(2027, 2, 10, 8, 0, 0, System.TimeSpan.Zero);

        Assert.Equal("2027-02-10T09:00", SessionsModel.FormatEventLocal(utcRow));
    }

    [Fact]
    public void A_blank_schedule_draws_blank_and_parses_to_nothing()
    {
        // Blank must stay blank: the page treats an unparseable box as "leave the schedule as is",
        // so a null that turned into a value here would wipe or invent a time.
        Assert.Equal(string.Empty, SessionsModel.FormatEventLocal(null));
        Assert.Null(Parse(""));
        Assert.Null(Parse(null));
    }
}
