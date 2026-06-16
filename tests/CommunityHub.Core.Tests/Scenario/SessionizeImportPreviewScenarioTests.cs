using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: an organizer DRY-RUNS a Sessionize import before committing it
/// (REQUIREMENTS §21 organizer "import dry-run/preview"). The preview must report
/// created / updated / skipped counts AND exactly which speaker bios would be
/// overwritten — WITHOUT writing anything — so a blind Full import can't silently
/// clobber curated speaker content.
///
/// These drive <see cref="SessionizeImportPreviewService.ComputeAsync"/> directly
/// against the shared scenario seed (which has an existing speaker with a hub bio)
/// and prove:
///   - Full mode flags the bio fields it would overwrite, and marks the ones the
///     speaker had edited (the dangerous case),
///   - Delta mode reports the same row counts where relevant but overwrites NOTHING,
///   - a brand-new speaker previews as a Create with no overwrites,
///   - the preview is read-only (the DB is unchanged after a preview),
///   - the preview matches what the real importer actually does (lock-step).
///
/// No real customer / person data — example.test addresses + generic names only.
/// </summary>
public sealed class SessionizeImportPreviewScenarioTests
{
    // One brand-new speaker + one that exists in the seed with a curated hub bio.
    private static IReadOnlyList<SessionizeSpeaker> IncomingSpeakers() => new[]
    {
        new SessionizeSpeaker(
            Email: "newly.accepted@example.test",
            FirstName: "Newly", LastName: "Accepted",
            TagLine: "First-time speaker", Biography: "Brand new."),
        // Matches the seeded Master Class speaker (has Tagline + Biography already).
        // A different last name proves a name change counts as an Update too.
        new SessionizeSpeaker(
            Email: ScenarioSeed.MasterclassSpeakerEmail,
            FirstName: "Masterclass", LastName: "Mentor-Renamed",
            TagLine: "DIFFERENT tagline from Sessionize",
            Biography: "DIFFERENT biography from Sessionize"),
    };

    private static SessionizeImportPreviewService NewPreview(
        Data.CommunityHubDbContext db) =>
        new(db, parser: null!, apiClient: null!,
            apiOptions: new SessionizeApiOptions());

    [Fact]
    public async Task Full_preview_flags_bios_that_would_be_overwritten_including_speaker_edits()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        // The seeded Master Class speaker EDITED their tagline in the hub.
        var prof = await db.SpeakerProfiles
            .SingleAsync(sp => sp.ParticipantId == seed.MasterclassSpeakerId);
        prof.MarkSpeakerEdited(SpeakerProfile.BioFields.Tagline, ScenarioFixture.Clock.GetUtcNow());
        await db.SaveChangesAsync();

        var preview = NewPreview(db);
        var result = await preview.ComputeAsync(
            seed.EventId, SessionizeImportMode.Full,
            IncomingSpeakers(), Array.Empty<string>());

        Assert.Null(result.Error);
        Assert.Equal(2, result.Fetched);
        Assert.Equal(1, result.Created);   // Newly Accepted
        Assert.Equal(1, result.Updated);   // Masterclass Mentor (name + bios change)

        // The full import would overwrite the existing Tagline + Biography.
        var mentorRow = result.Rows.Single(
            r => r.Email == ScenarioSeed.MasterclassSpeakerEmail);
        Assert.Contains(mentorRow.OverwrittenFields,
            f => f.Field == SpeakerProfile.BioFields.Tagline && f.SpeakerEdited);
        Assert.Contains(mentorRow.OverwrittenFields,
            f => f.Field == SpeakerProfile.BioFields.Biography && !f.SpeakerEdited);

        // The dangerous summary surfaces the speaker-edited clobber.
        Assert.Single(result.RowsClobberingSpeakerEdits);
        Assert.Equal(1, result.SpeakerEditedFieldsOverwritten);

        // The new speaker previews as a Create with nothing to overwrite.
        var newRow = result.Rows.Single(r => r.Email == "newly.accepted@example.test");
        Assert.Equal(SessionizeImportAction.Create, newRow.Action);
        Assert.Empty(newRow.OverwrittenFields);
    }

    [Fact]
    public async Task Delta_preview_overwrites_nothing()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var preview = NewPreview(db);
        var result = await preview.ComputeAsync(
            seed.EventId, SessionizeImportMode.Delta,
            IncomingSpeakers(), Array.Empty<string>());

        Assert.Null(result.Error);
        Assert.Equal(0, result.FieldsOverwritten);
        Assert.Empty(result.RowsClobberingSpeakerEdits);
        // Delta still CREATES the new speaker.
        Assert.Equal(1, result.Created);
    }

    [Fact]
    public async Task Preview_writes_nothing_to_the_database()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var beforeParticipants = await db.Participants.CountAsync();
        var beforeProfiles = await db.SpeakerProfiles.CountAsync();

        var preview = NewPreview(db);
        await preview.ComputeAsync(
            seed.EventId, SessionizeImportMode.Full,
            IncomingSpeakers(), Array.Empty<string>());

        // A dry-run is read-only: row counts are unchanged.
        Assert.Equal(beforeParticipants, await db.Participants.CountAsync());
        Assert.Equal(beforeProfiles, await db.SpeakerProfiles.CountAsync());
        // The new speaker was NOT created by the preview.
        Assert.False(await db.Participants.AnyAsync(
            p => p.Email == "newly.accepted@example.test"));
    }

    [Fact]
    public async Task Preview_counts_match_the_real_importer()
    {
        // Lock-step: the dry-run's created/updated counts must equal what the real
        // importer produces, so the organizer sees the truth before committing.
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var preview = NewPreview(db);
        var dry = await preview.ComputeAsync(
            seed.EventId, SessionizeImportMode.Full,
            IncomingSpeakers(), Array.Empty<string>());

        var (import, _) = ScenarioFixture.NewImporter(db);
        var real = await import.ImportSpeakersAsync(
            seed.EventId, IncomingSpeakers(), Array.Empty<string>(),
            sendWelcome: false, mode: SessionizeImportMode.Full);

        Assert.Equal(real.Created, dry.Created);
        Assert.Equal(real.Updated, dry.Updated);
    }
}
