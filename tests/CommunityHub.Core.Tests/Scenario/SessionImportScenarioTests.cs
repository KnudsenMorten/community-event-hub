using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: an organizer imports SESSIONS from Sessionize (the same v2 view API
/// that feeds the speaker import). Sessions are created in the hub, linked to their
/// speaker(s) by the Sessionize speaker id, and surfaced in the speaker overview.
///
/// Proves the contract:
///  - a session is upserted by its Sessionize session id (create then update-in-place),
///  - many-to-many linking: a session with two speakers links to both; a speaker
///    with two sessions shows both,
///  - links match on the Sessionize speaker id -> email -> the participant the
///    speaker import created,
///  - a session speaker id with no matching participant is reported + left unlinked,
///  - FULL re-import vs DELTA: re-running upserts (no duplicate sessions) and a
///    changed speaker set re-links (stale link removed, new link added) without
///    deleting the session,
///  - the speaker-overview projection (the /Organizer/Speakers grid) shows each
///    speaker's linked session titles.
///
/// Builds on the same seed as the speaker scenario; uses the real parser + importer.
/// NO real Sessionize ids / customer / person data — synthetic guids + example.test.
/// </summary>
public sealed class SessionImportScenarioTests
{
    // The seed's two plain speakers, given Sessionize speaker ids. The speaker
    // import normally captures these from the API; here the parsed-speakers list
    // carries them so the session importer can map id -> email -> participant.
    private const string SpeakerOneId = "spk-one";
    private const string SpeakerTwoId = "spk-two";

    // Mock accepted-sessions payload (All-view shape). One joint session (both
    // speakers), one solo session for speaker two, and one session referencing an
    // unknown speaker id (must be reported + left unlinked).
    private const string SessionsJson = """
    {
      "sessions": [
        {
          "id": "sess-joint",
          "title": "Joint Keynote",
          "description": "Two speakers together.",
          "room": "Main Stage",
          "isServiceSession": false,
          "speakers": [ "spk-one", "spk-two" ]
        },
        {
          "id": "sess-solo",
          "title": "Deep Dive",
          "description": "Solo session.",
          "isServiceSession": false,
          "speakers": [ "spk-two" ]
        },
        {
          "id": "sess-ghost",
          "title": "Mystery Talk",
          "speakers": [ "spk-unknown" ]
        }
      ],
      "speakers": []
    }
    """;

    private static IReadOnlyList<SessionizeSpeaker> ParsedSpeakers(string oneEmail, string twoEmail) =>
        new[]
        {
            new SessionizeSpeaker(oneEmail, "Session", "SpeakerOne", null, SessionizeId: SpeakerOneId),
            new SessionizeSpeaker(twoEmail, "Session", "SpeakerTwo", null, SessionizeId: SpeakerTwoId),
        };

    [Fact]
    public async Task Imports_sessions_and_links_speakers_many_to_many()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var importer = ScenarioFixture.NewSessionImporter(db);

        var fetch = SessionizeApiClient.ParseSessions(SessionsJson);
        Assert.Null(fetch.Error);

        var result = await importer.ImportSessionsAsync(
            seed.EventId, fetch.Sessions,
            ParsedSpeakers(ScenarioSeed.SpeakerOneEmail, ScenarioSeed.SpeakerTwoEmail),
            fetch.Warnings);

        Assert.Null(result.Error);
        Assert.Equal(3, result.Fetched);
        Assert.Equal(3, result.Created);
        // joint(2) + solo(1) = 3 links; the ghost speaker links nothing.
        Assert.Equal(3, result.LinksCreated);
        Assert.Contains(result.Warnings, w => w.Contains("spk-unknown"));

        // The joint session links to BOTH seeded speakers.
        var joint = await db.Sessions
            .Include(s => s.SessionSpeakers)
            .SingleAsync(s => s.SessionizeId == "sess-joint");
        Assert.Equal("Main Stage", joint.Room);
        Assert.Equal(2, joint.SessionSpeakers.Count);
        Assert.Contains(joint.SessionSpeakers, l => l.ParticipantId == seed.SpeakerOneId);
        Assert.Contains(joint.SessionSpeakers, l => l.ParticipantId == seed.SpeakerTwoId);

        // Speaker Two delivers TWO sessions (joint + solo).
        var twoSessionCount = await db.SessionSpeakers
            .CountAsync(l => l.ParticipantId == seed.SpeakerTwoId);
        Assert.Equal(2, twoSessionCount);

        // The ghost-speaker session exists but has no links.
        var ghost = await db.Sessions
            .Include(s => s.SessionSpeakers)
            .SingleAsync(s => s.SessionizeId == "sess-ghost");
        Assert.Empty(ghost.SessionSpeakers);
    }

    [Fact]
    public async Task Re_import_is_a_delta_upsert_never_duplicates_or_deletes()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var importer = ScenarioFixture.NewSessionImporter(db);
        var speakers = ParsedSpeakers(ScenarioSeed.SpeakerOneEmail, ScenarioSeed.SpeakerTwoEmail);

        var first = SessionizeApiClient.ParseSessions(SessionsJson);
        await importer.ImportSessionsAsync(seed.EventId, first.Sessions, speakers, first.Warnings);

        // DELTA: the joint session now drops speaker two (only speaker one) and
        // its title changed; the solo + ghost are unchanged. No session removed.
        const string changedJson = """
        {
          "sessions": [
            {
              "id": "sess-joint",
              "title": "Renamed Keynote",
              "room": "Main Stage",
              "speakers": [ "spk-one" ]
            },
            { "id": "sess-solo", "title": "Deep Dive", "speakers": [ "spk-two" ] },
            { "id": "sess-ghost", "title": "Mystery Talk", "speakers": [ "spk-unknown" ] }
          ]
        }
        """;
        var second = SessionizeApiClient.ParseSessions(changedJson);
        var delta = await importer.ImportSessionsAsync(
            seed.EventId, second.Sessions, speakers, second.Warnings);

        // No new sessions; all three are updates.
        Assert.Equal(0, delta.Created);
        Assert.Equal(3, delta.Updated);
        Assert.Equal(3, await db.Sessions.CountAsync(s => s.EventId == seed.EventId));

        // The changed field is applied in place.
        var joint = await db.Sessions
            .Include(s => s.SessionSpeakers)
            .SingleAsync(s => s.SessionizeId == "sess-joint");
        Assert.Equal("Renamed Keynote", joint.Title);
        // Speaker two's link to the joint session was removed; speaker one kept.
        Assert.Single(joint.SessionSpeakers);
        Assert.Contains(joint.SessionSpeakers, l => l.ParticipantId == seed.SpeakerOneId);
        Assert.Equal(1, delta.LinksRemoved);

        // Speaker two now has only the solo session.
        Assert.Equal(1, await db.SessionSpeakers.CountAsync(l => l.ParticipantId == seed.SpeakerTwoId));
    }

    [Fact]
    public async Task Speaker_overview_projection_shows_linked_session_titles()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var importer = ScenarioFixture.NewSessionImporter(db);

        var fetch = SessionizeApiClient.ParseSessions(SessionsJson);
        await importer.ImportSessionsAsync(
            seed.EventId, fetch.Sessions,
            ParsedSpeakers(ScenarioSeed.SpeakerOneEmail, ScenarioSeed.SpeakerTwoEmail),
            fetch.Warnings);

        // Mirror the /Organizer/Speakers overview projection: for each speaker,
        // the titles of their linked sessions (ordered by start then title).
        var overview = await db.Participants
            .Where(p => p.EventId == seed.EventId
                        && (p.Role == ParticipantRole.Speaker
                            || p.Role == ParticipantRole.MasterclassSpeaker))
            .Select(p => new
            {
                p.Id,
                Sessions = db.SessionSpeakers
                    .Where(ss => ss.ParticipantId == p.Id && ss.Session.EventId == seed.EventId)
                    .OrderBy(ss => ss.Session.StartsAt).ThenBy(ss => ss.Session.Title)
                    .Select(ss => ss.Session.Title)
                    .ToList()
            })
            .ToListAsync();

        var one = overview.Single(o => o.Id == seed.SpeakerOneId);
        Assert.Equal(new[] { "Joint Keynote" }, one.Sessions);

        var two = overview.Single(o => o.Id == seed.SpeakerTwoId);
        Assert.Equal(new[] { "Deep Dive", "Joint Keynote" }, two.Sessions);

        // The Master Class speaker (no imported session) shows none.
        var mc = overview.Single(o => o.Id == seed.MasterclassSpeakerId);
        Assert.Empty(mc.Sessions);

        // Header stat: total sessions imported for the edition.
        Assert.Equal(3, await db.Sessions.CountAsync(s => s.EventId == seed.EventId));
    }
}
