using CommunityHub.Core.Sessions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1167.4 — <i>"Double 'Room Room' naming is showing."</i> (reviewer, 2026-09-02)
///
/// <para>🔑 Neither side was wrong alone: the page labels the value "Room", which is right for a room
/// called "A3", and the imported names already begin with "Room-". Together they read
/// <i>"Room Room-A3-Floor 0-Max 250-MC"</i>. Fixed in the display, because the stored name is the
/// event platform's own and the room FILTER matches on it.</para>
/// </summary>
public sealed class SessionRoomLabelTests
{
    /// <summary>The real values, taken from the live sessions page.</summary>
    [Theory]
    [InlineData("Room-A3-Floor 0-Max 250-MC", "A3-Floor 0-Max 250-MC")]
    [InlineData("Room-17-Floor 1-Max 48-MC", "17-Floor 1-Max 48-MC")]
    [InlineData("Room-A1 Keynote-Floor 0-Max 1500", "A1 Keynote-Floor 0-Max 1500")]
    [InlineData("Room-TreeHouse South-Floor 1-Max 57-MC", "TreeHouse South-Floor 1-Max 57-MC")]
    public void The_leading_Room_is_dropped_so_the_label_is_not_doubled(string raw, string expected)
    {
        Assert.Equal(expected, SessionRoomLabel.ForDisplay(raw));
    }

    [Theory]
    [InlineData("Room 21", "21")]
    [InlineData("room: 21", "21")]
    [InlineData("  Room - 21  ", "21")]
    public void The_separator_may_be_a_space_a_dash_or_a_colon(string raw, string expected)
    {
        Assert.Equal(expected, SessionRoomLabel.ForDisplay(raw));
    }

    /// <summary>
    /// 🔒 A name that does not start with "Room" is untouched.
    /// </summary>
    [Theory]
    [InlineData("A3")]
    [InlineData("Auditorium")]
    [InlineData("TreeHouse South")]
    [InlineData("Rooftop Terrace")]
    public void A_room_that_does_not_say_Room_is_left_alone(string raw)
    {
        Assert.Equal(raw, SessionRoomLabel.ForDisplay(raw));
    }

    /// <summary>
    /// 🔒 Stripping must never leave an empty tag.
    /// </summary>
    /// <remarks>
    /// A room genuinely called "Room" has to render as "Room". An empty tag beside the label reads
    /// as missing data, which is a worse answer than the slightly redundant one.
    /// </remarks>
    [Theory]
    [InlineData("Room")]
    [InlineData("  Room  ")]
    public void A_room_called_only_Room_keeps_its_name(string raw)
    {
        Assert.Equal("Room", SessionRoomLabel.ForDisplay(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_stays_blank(string? raw)
    {
        Assert.Equal(string.Empty, SessionRoomLabel.ForDisplay(raw));
    }

    /// <summary>
    /// ⚠️ Only the FIRST "Room" is dropped.
    /// </summary>
    /// <remarks>
    /// "Room-Room 5" is odd data, but eating both would leave "5" and quietly discard something a
    /// human deliberately typed. One label, one strip.
    /// </remarks>
    [Fact]
    public void Only_the_first_Room_is_removed()
    {
        Assert.Equal("Room 5", SessionRoomLabel.ForDisplay("Room-Room 5"));
    }
}
