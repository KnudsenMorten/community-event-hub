using CommunityHub.Core.Integrations.Sessions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="BackstageSessionParser"/> — the Zoho Backstage v3
/// halls + sessions JSON → neutral <c>SessionizeSession</c> mapping. (The live
/// pull is gated on the ZohoBackstage.agenda.READ scope; the mapping is real +
/// tested here so it's flip-ready.)
/// </summary>
public class BackstageSessionParserTests
{
    private const string HallsJson = """
    { "halls": [
        { "id": "h1", "name": "Main Stage" },
        { "id": "h2", "name": "Room 2" }
    ] }
    """;

    private const string SessionsJson = """
    { "sessions": [
        {
          "id": "s1", "title": "Keynote", "description": "The big one",
          "hallId": "h1", "startTime": "2027-02-10T09:00:00+01:00",
          "endTime": "2027-02-10T10:00:00+01:00",
          "speakers": [ { "id": "sp1", "email": "Speaker@Example.com" } ]
        },
        {
          "id": "s2", "name": "Unassigned talk",
          "speakers": [ "bare@example.com" ]
        },
        { "id": "s1", "title": "Duplicate id (ignored)" }
    ] }
    """;

    [Fact]
    public void ParseHalls_maps_id_to_name_and_tolerates_bad_json()
    {
        var halls = BackstageSessionParser.ParseHalls(HallsJson);
        Assert.Equal(2, halls.Count);
        Assert.Equal("Main Stage", halls["h1"]);
        Assert.Empty(BackstageSessionParser.ParseHalls("{ not json"));
    }

    [Fact]
    public void ParseSessions_maps_fields_resolves_room_and_dedupes()
    {
        var halls = BackstageSessionParser.ParseHalls(HallsJson);
        var result = BackstageSessionParser.ParseSessions(SessionsJson, halls);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Sessions.Count); // s1 once (dupe dropped) + s2

        var s1 = result.Sessions.Single(s => s.SessionizeId == "s1");
        Assert.Equal("Keynote", s1.Title);
        Assert.Equal("The big one", s1.Abstract);
        Assert.Equal("Main Stage", s1.Room);                       // hall id -> name
        Assert.NotNull(s1.StartsAt);
        Assert.Equal(new[] { "Speaker@Example.com" }, s1.SpeakerIds); // email key for linking
    }

    [Fact]
    public void ParseSessions_leaves_room_blank_when_no_hall_assigned()
    {
        var result = BackstageSessionParser.ParseSessions(SessionsJson,
            BackstageSessionParser.ParseHalls(HallsJson));
        var s2 = result.Sessions.Single(s => s.SessionizeId == "s2");
        Assert.Equal("Unassigned talk", s2.Title);  // title falls back to "name"
        Assert.Null(s2.Room);                        // rooms TBD until defined in Backstage
        Assert.Equal(new[] { "bare@example.com" }, s2.SpeakerIds); // bare-string speaker
    }

    [Fact]
    public void ParseSessions_errors_on_missing_array_but_never_throws()
    {
        var result = BackstageSessionParser.ParseSessions("{ not json");
        Assert.NotNull(result.Error);
        Assert.Empty(result.Sessions);
    }
}
