using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="SessionizeApiClient.ParseSessions"/> - the JSON
/// mapping from a Sessionize v2 view response to <see cref="SessionizeSession"/>.
/// No network: the JSON is inline, mirroring the real All-view shape (object with
/// a flat <c>sessions</c> array + a <c>categories</c> block for tracks) and the
/// grouped Sessions/GridSmart shape (array of groups, each with nested sessions).
/// NO real Sessionize ids / customer data — synthetic guids + generic titles.
/// </summary>
public sealed class SessionizeSessionsParseTests
{
    // All view: object with a flat sessions array + a categories block. Each
    // session carries a speakers id array (the link key) + categoryItems (track).
    private const string AllViewJson = """
    {
      "sessions": [
        {
          "id": "s-100",
          "title": "Zero Trust in Practice",
          "description": "A hands-on look at zero trust.",
          "startsAt": "2027-02-04T09:00:00",
          "endsAt": "2027-02-04T10:00:00",
          "isServiceSession": false,
          "room": "Hall A",
          "speakers": [ "spk-1", "spk-2" ],
          "categoryItems": [ 20 ]
        },
        {
          "id": "s-200",
          "title": "Coffee Break",
          "description": "",
          "isServiceSession": true,
          "speakers": [],
          "categoryItems": []
        }
      ],
      "speakers": [],
      "categories": [
        {
          "id": 1, "title": "Track",
          "items": [ { "id": 20, "name": "Security" }, { "id": 21, "name": "Cloud" } ]
        }
      ],
      "questions": []
    }
    """;

    // Grouped Sessions / GridSmart view: array of group objects, each with a
    // nested sessions array; speakers given as { id, name } objects this time.
    private const string GroupedSessionsJson = """
    [
      {
        "groupName": "Day 1",
        "sessions": [
          {
            "id": "s-300",
            "title": "Platform Engineering 101",
            "description": "Intro to platform engineering.",
            "isServiceSession": false,
            "room": "Room 2",
            "speakers": [ { "id": "spk-3", "name": "Some Speaker" } ]
          }
        ]
      }
    ]
    """;

    // Real All-view shape once the schedule is ANNOUNCED: the room is given as a numeric
    // "roomId" + a top-level "rooms" map, NOT an inline "room" string. The parser must
    // resolve roomId -> room name (else every scheduled session reads "Room TBD").
    private const string AllViewWithRoomIdJson = """
    {
      "sessions": [
        {
          "id": "s-900",
          "title": "Intune Master Class",
          "description": "Full-day deep dive.",
          "startsAt": "2027-02-09T09:00:00",
          "endsAt": "2027-02-09T16:00:00",
          "isServiceSession": false,
          "room": "",
          "roomId": 84241,
          "speakers": [ "spk-9" ],
          "categoryItems": [ 30 ]
        }
      ],
      "speakers": [],
      "rooms": [
        { "id": 84241, "name": "MC-Auditorium (582)" },
        { "id": 84230, "name": "MC-A3 (200)" }
      ],
      "categories": []
    }
    """;

    // §154: the real All view exposes SEPARATE category GROUPS — "Format" (kind +
    // "(NN min)"), "Suggested Event Track" (the track), and "Level". The parser must
    // route each item by its GROUP TITLE: Track ← the track group (NOT the first/Format
    // category, the old bug), Level ← the level group, and LengthMinutes ← the Format
    // label's "(NN min)". The Format label also drives the hub Type via the importer.
    private const string AllViewWithGroupsJson = """
    {
      "sessions": [
        {
          "id": "s-grp",
          "title": "Securing Identity",
          "description": "Identity hardening.",
          "isServiceSession": false,
          "speakers": [ "spk-7" ],
          "categoryItems": [ 40, 50, 60 ]
        }
      ],
      "speakers": [],
      "categories": [
        {
          "id": 1, "title": "Format",
          "items": [ { "id": 40, "name": "Technical Session (60 min)" } ]
        },
        {
          "id": 2, "title": "Suggested Event Track",
          "items": [ { "id": 50, "name": "Security" }, { "id": 51, "name": "Azure" } ]
        },
        {
          "id": 3, "title": "Level",
          "items": [ { "id": 60, "name": "Expert (400)" } ]
        }
      ]
    }
    """;

    [Fact]
    public void Track_comes_from_the_track_group_not_the_format()
    {
        var result = SessionizeApiClient.ParseSessions(AllViewWithGroupsJson);
        var s = result.Sessions.Single(x => x.SessionizeId == "s-grp");

        // The OLD parser set Track = the first category (the Format). Now it must be
        // the "Suggested Event Track" group's value.
        Assert.Equal("Security", s.Track);
        Assert.NotEqual("Technical Session (60 min)", s.Track);
    }

    [Fact]
    public void Level_comes_from_the_level_group()
    {
        var result = SessionizeApiClient.ParseSessions(AllViewWithGroupsJson);
        var s = result.Sessions.Single(x => x.SessionizeId == "s-grp");

        Assert.Equal("Expert (400)", s.Level);
    }

    [Fact]
    public void LengthMinutes_and_category_come_from_the_format_group()
    {
        var result = SessionizeApiClient.ParseSessions(AllViewWithGroupsJson);
        var s = result.Sessions.Single(x => x.SessionizeId == "s-grp");

        // Numeric minutes parsed out of the Format label "(60 min)".
        Assert.Equal(60, s.LengthMinutes);
        // The Category fed to the Type/Length mapper is the Format label only (not the
        // track/level labels joined in).
        Assert.Equal("Technical Session (60 min)", s.Category);
    }

    [Fact]
    public void Resolves_room_from_roomId_when_inline_room_is_empty()
    {
        var result = SessionizeApiClient.ParseSessions(AllViewWithRoomIdJson);
        var s = result.Sessions.Single(x => x.SessionizeId == "s-900");

        Assert.Equal("MC-Auditorium (582)", s.Room);
        Assert.NotNull(s.StartsAt);
        Assert.NotNull(s.EndsAt);
    }

    [Fact]
    public void Parses_all_view_flat_sessions_array()
    {
        var result = SessionizeApiClient.ParseSessions(AllViewJson);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Sessions.Count);
    }

    [Fact]
    public void Maps_core_fields_and_speaker_ids()
    {
        var result = SessionizeApiClient.ParseSessions(AllViewJson);
        var talk = result.Sessions.Single(s => s.SessionizeId == "s-100");

        Assert.Equal("Zero Trust in Practice", talk.Title);
        Assert.Equal("A hands-on look at zero trust.", talk.Abstract);
        Assert.Equal("Hall A", talk.Room);
        Assert.False(talk.IsServiceSession);
        Assert.NotNull(talk.StartsAt);
        Assert.NotNull(talk.EndsAt);
        Assert.Equal(new[] { "spk-1", "spk-2" }, talk.SpeakerIds);
    }

    [Fact]
    public void Resolves_track_from_category_items()
    {
        var result = SessionizeApiClient.ParseSessions(AllViewJson);
        var talk = result.Sessions.Single(s => s.SessionizeId == "s-100");

        Assert.Equal("Security", talk.Track);
    }

    [Fact]
    public void Flags_service_sessions()
    {
        var result = SessionizeApiClient.ParseSessions(AllViewJson);
        var brk = result.Sessions.Single(s => s.SessionizeId == "s-200");

        Assert.True(brk.IsServiceSession);
        Assert.Empty(brk.SpeakerIds);
    }

    [Fact]
    public void Parses_grouped_sessions_view_with_object_speakers()
    {
        var result = SessionizeApiClient.ParseSessions(GroupedSessionsJson);

        Assert.Null(result.Error);
        var only = Assert.Single(result.Sessions);
        Assert.Equal("s-300", only.SessionizeId);
        Assert.Equal("Platform Engineering 101", only.Title);
        Assert.Equal("Room 2", only.Room);
        // Speaker given as { id, name } object -> the id is extracted.
        Assert.Equal(new[] { "spk-3" }, only.SpeakerIds);
    }

    [Fact]
    public void Deduplicates_on_session_id()
    {
        const string json = """
        {
          "sessions": [
            { "id": "dup", "title": "First", "speakers": [] },
            { "id": "dup", "title": "Second", "speakers": [] }
          ]
        }
        """;

        var result = SessionizeApiClient.ParseSessions(json);

        var only = Assert.Single(result.Sessions);
        Assert.Equal("First", only.Title); // first occurrence wins
    }

    [Fact]
    public void Invalid_json_returns_error_not_throw()
    {
        var result = SessionizeApiClient.ParseSessions("{ not json");

        Assert.NotNull(result.Error);
        Assert.Empty(result.Sessions);
    }

    [Fact]
    public void Unexpected_shape_returns_a_clear_error()
    {
        var result = SessionizeApiClient.ParseSessions("""{ "message": "nope" }""");

        Assert.NotNull(result.Error);
        Assert.Contains("sessions", result.Error!);
    }
}
