using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// REQUIREMENTS §58 (PART 2): the persisted Sessionize SPEAKER id. Every speaker must carry
/// all three ids for the 1:1 map + delta sync — CEH id (Participant/SpeakerProfile PK), the
/// Sessionize speaker id (<see cref="SpeakerProfile.SessionizeSpeakerId"/>), and the Zoho
/// id (<see cref="SpeakerProfile.BackstageSpeakerId"/>). This proves the Sessionize import
/// STORES the Sessionize speaker id on the profile (not just matches-and-discards it).
/// </summary>
public sealed class SessionizeSpeakerIdPersistedScenarioTests
{
    [Fact]
    public async Task Import_persists_the_sessionize_speaker_id_on_the_profile()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (import, _) = ScenarioFixture.NewImporter(db);

        // A speaker carrying a Sessionize speaker id (the v2 view API supplies it as `id`).
        var speaker = new SessionizeSpeaker(
            Email: "id.persist@example.test",
            FirstName: "Ida",
            LastName: "Persist",
            TagLine: "Speaker with an id",
            SessionizeId: "sz-speaker-9001");

        await import.ImportSpeakersAsync(
            seed.EventId, new[] { speaker }, Array.Empty<string>(), sendWelcome: false);

        var participant = await db.Participants.SingleAsync(p => p.Email == "id.persist@example.test");
        var profile = await db.SpeakerProfiles.SingleAsync(sp => sp.ParticipantId == participant.Id);

        // CEH id (PK) is the participant/profile key; the Sessionize id is now STORED.
        Assert.True(participant.Id > 0);
        Assert.Equal("sz-speaker-9001", profile.SessionizeSpeakerId);

        // BackstageSpeakerId (the Zoho id) exists on the entity for the third leg of the map
        // (null until a Backstage create/link writes it — the import never invents one).
        Assert.Null(profile.BackstageSpeakerId);
    }

    [Fact]
    public async Task Reimport_keeps_the_sessionize_speaker_id_stable()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (import, _) = ScenarioFixture.NewImporter(db);

        var speaker = new SessionizeSpeaker(
            Email: "id.persist@example.test", FirstName: "Ida", LastName: "Persist",
            TagLine: null, SessionizeId: "sz-speaker-9001");

        await import.ImportSpeakersAsync(
            seed.EventId, new[] { speaker }, Array.Empty<string>(), sendWelcome: false);
        await import.ImportSpeakersAsync(
            seed.EventId, new[] { speaker }, Array.Empty<string>(), sendWelcome: false);

        var participant = await db.Participants.SingleAsync(p => p.Email == "id.persist@example.test");
        var profile = await db.SpeakerProfiles.SingleAsync(sp => sp.ParticipantId == participant.Id);
        Assert.Equal("sz-speaker-9001", profile.SessionizeSpeakerId);
        Assert.Single(db.SpeakerProfiles.Where(sp => sp.ParticipantId == participant.Id));
    }
}
