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
