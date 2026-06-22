using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: the speaker bio is seeded from Sessionize but OWNED by the speaker.
/// This proves the four-part contract for the Sessionize → hub speaker import +
/// editable bio:
///
///  - DELTA sync (scheduled / auto): adds NEW speakers and fills only genuinely
///    EMPTY, never-speaker-edited bio fields; it NEVER overwrites a field the
///    speaker has edited in the hub.
///  - SPEAKER EDIT marks the field "speaker-modified" (per-field dirty set +
///    BioLastEditedBySpeakerAt) so the next delta re-import preserves it.
///  - FULL import (organizer override): force-refreshes ALL bio fields from
///    Sessionize and clears the speaker-edited set — the deliberate re-seed.
///  - PhotoUrl (Sessionize profilePicture) is imported + tracked like the other
///    bio fields.
///  - the import never changes the role, never deletes, and never touches the
///    hub-collected fields (Accreditation / Country / Gender / ContactEmailOverride).
///
/// The bio fields are edited by simulating the speaker's own page save
/// (SpeakerProfile.MarkSpeakerEdited), which is the same call the
/// /Forms/Speaker page makes — keeping the test at the domain contract.
///
/// NO real customer / person data — example.test + @expertslive.dk only.
/// </summary>
public sealed class SpeakerBioOwnershipScenarioTests
{
    // A representative Sessionize "Speakers"-view payload WITH the speaker-emails
    // advanced field enabled. Mirrors a real accepted-speakers response: full
    // names, taglines, bios, social links and a profile picture. NO real ids.
    private const string SessionizeJson = """
    [
      {
        "firstName": "Session", "lastName": "Speaker One",
        "fullName": "Session Speaker One",
        "email": "speaker.one@example.test",
        "tagLine": "Cloud engineer",
        "bio": "Sessionize-sourced bio for speaker one.",
        "profilePicture": "https://sessionize.example/photos/one.jpg",
        "links": [
          { "title": "LinkedIn", "url": "https://linkedin.com/in/one", "linkType": "LinkedIn" },
          { "title": "X", "url": "https://x.com/one", "linkType": "Twitter" },
          { "title": "Blog", "url": "https://one.example", "linkType": "Blog" }
        ]
      },
      {
        "firstName": "Newly", "lastName": "Accepted",
        "fullName": "Newly Accepted",
        "email": "newly.accepted@example.test",
        "tagLine": "First-time speaker",
        "bio": "Brand new to the lineup.",
        "profilePicture": "https://sessionize.example/photos/new.jpg",
        "links": [ { "title": "LinkedIn", "url": "https://linkedin.com/in/new", "linkType": "LinkedIn" } ]
      }
    ]
    """;

    private static IReadOnlyList<SessionizeSpeaker> ParseSpeakers(out IReadOnlyList<string> warnings)
    {
        var parsed = SessionizeApiClient.ParseSpeakers(SessionizeJson);
        Assert.Null(parsed.Error);
        warnings = parsed.Warnings;
        return parsed.Speakers;
    }

    private static async Task<SpeakerProfile> ProfileFor(
        CommunityHubDbContext db, int eventId, string email)
    {
        var p = await db.Participants.SingleAsync(
            x => x.EventId == eventId && x.Email == email);
        return await db.SpeakerProfiles.SingleAsync(
            sp => sp.EventId == eventId && sp.ParticipantId == p.Id);
    }

    // ---- DEV VERIFICATION: which fields populate from a real-shaped payload ----

    [Fact]
    public async Task Import_populates_all_bio_fields_from_sessionize_payload()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (import, _) = ScenarioFixture.NewImporter(db);

        var speakers = ParseSpeakers(out var warnings);
        var result = await import.ImportSpeakersAsync(
            seed.EventId, speakers, warnings, sendWelcome: false,
            mode: SessionizeImportMode.Delta);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Fetched);

        // Every imported bio field populates: name+email (identity), tagline,
        // bio, the three split social links, and the photo.
        var p = await db.Participants.SingleAsync(
            x => x.Email == "newly.accepted@example.test");
        Assert.Equal("Newly Accepted", p.FullName);

        var prof = await ProfileFor(db, seed.EventId, "newly.accepted@example.test");
        Assert.Equal("First-time speaker", prof.Tagline);
        Assert.Equal("Brand new to the lineup.", prof.Biography);
        Assert.Equal("https://linkedin.com/in/new", prof.LinkedIn);
        Assert.Equal("https://sessionize.example/photos/new.jpg", prof.PhotoUrl);
    }

    // ---- DELTA: add new, fill empty, never flush speaker edits -----------------

    [Fact]
    public async Task Delta_does_not_overwrite_a_field_the_speaker_edited()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (import, _) = ScenarioFixture.NewImporter(db);
        var now = ScenarioFixture.Clock.GetUtcNow();

        // First import seeds speaker.one's bio from Sessionize.
        var speakers = ParseSpeakers(out var warnings);
        await import.ImportSpeakersAsync(
            seed.EventId, speakers, warnings, sendWelcome: false,
            mode: SessionizeImportMode.Delta);

        // Speaker edits their own Biography + Tagline on /Forms/Speaker.
        var prof = await ProfileFor(db, seed.EventId, "speaker.one@example.test");
        prof.Biography = "MY OWN words, not Sessionize's.";
        prof.MarkSpeakerEdited(SpeakerProfile.BioFields.Biography, now);
        prof.Tagline = "My own tagline";
        prof.MarkSpeakerEdited(SpeakerProfile.BioFields.Tagline, now);
        await db.SaveChangesAsync();

        // A later delta re-import (e.g. the nightly job) runs again.
        var reparsed = SessionizeApiClient.ParseSpeakers(SessionizeJson);
        await import.ImportSpeakersAsync(
            seed.EventId, reparsed.Speakers, reparsed.Warnings, sendWelcome: false,
            mode: SessionizeImportMode.Delta);

        var after = await ProfileFor(db, seed.EventId, "speaker.one@example.test");
        // Speaker-edited fields are PRESERVED.
        Assert.Equal("MY OWN words, not Sessionize's.", after.Biography);
        Assert.Equal("My own tagline", after.Tagline);
        // Untouched fields the speaker never edited were filled from Sessionize.
        Assert.Equal("https://linkedin.com/in/one", after.LinkedIn);
        Assert.Equal("https://sessionize.example/photos/one.jpg", after.PhotoUrl);
        Assert.True(after.IsSpeakerEdited(SpeakerProfile.BioFields.Biography));
        Assert.NotNull(after.BioLastEditedBySpeakerAt);
    }

    [Fact]
    public async Task Delta_fills_an_empty_untouched_field_but_not_a_populated_one()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (import, _) = ScenarioFixture.NewImporter(db);

        // The seed gives speaker.one a Tagline ("Cloud engineer") but NO bio.
        var before = await ProfileFor(db, seed.EventId, "speaker.one@example.test");
        Assert.Equal("Cloud engineer", before.Tagline);
        Assert.Null(before.Biography);

        var speakers = ParseSpeakers(out var warnings);
        await import.ImportSpeakersAsync(
            seed.EventId, speakers, warnings, sendWelcome: false,
            mode: SessionizeImportMode.Delta);

        var after = await ProfileFor(db, seed.EventId, "speaker.one@example.test");
        // Empty Biography got filled; the already-populated Tagline was kept
        // as-is (delta never overwrites a non-empty value, even unedited).
        Assert.Equal("Sessionize-sourced bio for speaker one.", after.Biography);
        Assert.Equal("Cloud engineer", after.Tagline);
    }

    // ---- FULL: force-refresh everything + clear the speaker-edited set ---------

    [Fact]
    public async Task Full_import_overwrites_speaker_edits_and_clears_the_dirty_set()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (import, _) = ScenarioFixture.NewImporter(db);
        var now = ScenarioFixture.Clock.GetUtcNow();

        // Speaker edits their bio.
        var prof = await ProfileFor(db, seed.EventId, "speaker.one@example.test");
        prof.Biography = "Speaker-owned bio.";
        prof.MarkSpeakerEdited(SpeakerProfile.BioFields.Biography, now);
        await db.SaveChangesAsync();

        // Organizer runs the FULL import override.
        var speakers = ParseSpeakers(out var warnings);
        await import.ImportSpeakersAsync(
            seed.EventId, speakers, warnings, sendWelcome: false,
            mode: SessionizeImportMode.Full);

        var after = await ProfileFor(db, seed.EventId, "speaker.one@example.test");
        // Full mode WINS: Sessionize value replaces the speaker's edit...
        Assert.Equal("Sessionize-sourced bio for speaker one.", after.Biography);
        // ...and the speaker-edited markers are cleared (re-seeded from Sessionize).
        Assert.False(after.IsSpeakerEdited(SpeakerProfile.BioFields.Biography));
        Assert.Null(after.SpeakerEditedFields);
        Assert.Null(after.BioLastEditedBySpeakerAt);
    }

    // ---- Invariants the import must never break -------------------------------

    [Fact]
    public async Task Import_never_changes_role_or_touches_hub_collected_fields()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (import, _) = ScenarioFixture.NewImporter(db);
        var now = ScenarioFixture.Clock.GetUtcNow();

        // Give the pre-day speaker hub-collected facts + an email override.
        var p = await db.Participants.SingleAsync(x => x.Id == seed.MasterclassSpeakerId);
        var prof = await db.SpeakerProfiles.SingleAsync(
            sp => sp.EventId == seed.EventId && sp.ParticipantId == p.Id);
        prof.Accreditation = "Microsoft MVP";
        prof.Country = "Denmark";
        prof.ContactEmailOverride = "mc.calendar@example.test";
        await db.SaveChangesAsync();

        // Sessionize payload would create a plain Speaker for this email.
        var json = SessionizeJson.Replace(
            "speaker.one@example.test", p.Email);
        var parsed = SessionizeApiClient.ParseSpeakers(json);

        foreach (var mode in new[] { SessionizeImportMode.Delta, SessionizeImportMode.Full })
        {
            await import.ImportSpeakersAsync(
                seed.EventId, parsed.Speakers, parsed.Warnings, sendWelcome: false,
                mode: mode);

            var after = await db.Participants.SingleAsync(x => x.Id == p.Id);
            Assert.Equal(ParticipantRole.Speaker, after.Role); // role unchanged
            var profAfter = await db.SpeakerProfiles.SingleAsync(
                sp => sp.EventId == seed.EventId && sp.ParticipantId == p.Id);
            // Hub-collected fields are NEVER touched by either import mode.
            Assert.Equal("Microsoft MVP", profAfter.Accreditation);
            Assert.Equal("Denmark", profAfter.Country);
            Assert.Equal("mc.calendar@example.test", profAfter.ContactEmailOverride);
        }
    }

    // ---- Domain helper unit checks (the speaker-edited dirty set) -------------

    [Fact]
    public void MarkSpeakerEdited_is_idempotent_and_clear_resets()
    {
        var now = new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);
        var prof = new SpeakerProfile();

        Assert.False(prof.IsSpeakerEdited(SpeakerProfile.BioFields.Biography));

        prof.MarkSpeakerEdited(SpeakerProfile.BioFields.Biography, now);
        prof.MarkSpeakerEdited(SpeakerProfile.BioFields.Biography, now); // idempotent
        prof.MarkSpeakerEdited(SpeakerProfile.BioFields.LinkedIn, now);

        Assert.True(prof.IsSpeakerEdited(SpeakerProfile.BioFields.Biography));
        Assert.True(prof.IsSpeakerEdited(SpeakerProfile.BioFields.LinkedIn));
        Assert.False(prof.IsSpeakerEdited(SpeakerProfile.BioFields.Twitter));
        // No duplicate tokens.
        Assert.Equal("biography,linkedin", prof.SpeakerEditedFields);

        prof.ClearSpeakerEdited();
        Assert.Null(prof.SpeakerEditedFields);
        Assert.Null(prof.BioLastEditedBySpeakerAt);
        Assert.False(prof.IsSpeakerEdited(SpeakerProfile.BioFields.Biography));
    }
}
