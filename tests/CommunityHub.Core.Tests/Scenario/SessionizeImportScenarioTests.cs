using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: an organizer imports accepted speakers from Sessionize (the v2 view
/// API). The GUI counterpart (scenario-organizer-import.spec.ts) clicks "Pull
/// speakers from Sessionize" on /Organizer/SessionizeImport and reads the
/// "X read, Y created, Z updated" result banner; this backend half feeds the
/// SAME mock accepted-speakers JSON through the real parser + importer and proves
/// the upsert contract:
///
///  - match on email (case-insensitive),
///  - NEVER overwrite an existing participant's role (an organizer may have
///    re-classified a speaker),
///  - skip + report a speaker with no email (can't log in without one),
///  - never delete anyone,
///  - land genuinely NEW speakers as PRESELECTED + uncategorized (§299 6.1: the
///    activation hard gate — no welcome mail until an organizer categorizes and
///    activates them).
///
/// The JSON fixture mirrors a real Sessionize "Speakers" view response with the
/// "speaker emails" advanced field enabled. NO real Sessionize id, customer or
/// person data — example.test addresses + generic names only.
/// </summary>
public sealed class SessionizeImportScenarioTests
{
    // Mock accepted-speakers payload (Speakers-view shape). One brand-new
    // speaker, one that already exists in the seed (matched by email, in mixed
    // case), and one with NO email (must be skipped + reported).
    private const string AcceptedSpeakersJson = """
    [
      {
        "firstName": "Newly", "lastName": "Accepted",
        "fullName": "Newly Accepted",
        "email": "newly.accepted@example.test",
        "tagLine": "First-time speaker",
        "bio": "Brand new to the lineup.",
        "links": [ { "title": "LinkedIn", "url": "https://linkedin.com/in/newly", "linkType": "LinkedIn" } ]
      },
      {
        "firstName": "Masterclass", "lastName": "Mentor",
        "fullName": "Masterclass Mentor",
        "email": "MASTERCLASS.SPEAKER@EXAMPLE.TEST",
        "tagLine": "Updated tagline from Sessionize",
        "bio": "Refreshed bio.",
        "links": []
      },
      {
        "firstName": "No", "lastName": "Email",
        "fullName": "No Email",
        "links": []
      }
    ]
    """;

    [Fact]
    public async Task Import_upserts_speakers_matching_on_email_and_reports_skips()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (import, sender) = ScenarioFixture.NewImporter(db);

        var parsed = SessionizeApiClient.ParseSpeakers(AcceptedSpeakersJson);
        Assert.Null(parsed.Error);

        var result = await import.ImportSpeakersAsync(
            seed.EventId, parsed.Speakers, parsed.Warnings, sendWelcome: true);

        // 2 speakers with emails imported; the emailless one was dropped at parse.
        Assert.Equal(2, result.Fetched);
        Assert.Equal(1, result.Created);   // Newly Accepted
        // Masterclass Mentor already exists with the same name -> unchanged.
        Assert.Equal(1, result.Skipped);
        // The emailless speaker surfaces as a warning naming the advanced field.
        Assert.Contains(result.Warnings, w => w.Contains("No Email"));
        Assert.Contains(result.Warnings, w => w.Contains("speaker emails"));

        // §880 (operator 2026-08-05) — the new speaker lands ACTIVE. Sessionize has already
        // accepted them, so the two lifecycle conditions go and the ONE an organizer actually
        // decides stays: the speaker CATEGORY. This replaced §299 6.1's Preselected landing for the
        // import path, and it is what collapses their pending entry from three blockers to one.
        var created = await db.Participants.SingleAsync(
            p => p.Email == "newly.accepted@example.test");
        Assert.Equal(ParticipantRole.Speaker, created.Role);
        Assert.True(created.IsActive);
        Assert.Equal(ParticipantLifecycleState.Active, created.LifecycleState);
        // A profile exists but is UNCATEGORIZED — the organizer classifies it on review.
        var profile = await db.SpeakerProfiles.SingleAsync(s => s.ParticipantId == created.Id);
        Assert.Null(profile.Category);

        // 🔒 §880.6 — AND STILL NO WELCOME MAIL, even though the import was asked to send one and
        // the speaker is now Active. The hold moved from `IsActive` to `Category is null`, which is
        // the option he chose: the mail follows the organizer's decision, not the import.
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Import_never_overwrites_an_existing_role()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (import, _) = ScenarioFixture.NewImporter(db);

        // The seed has the Masterclass Mentor as a Speaker. The import payload
        // re-sends them — it must NOT change the existing participant's role.
        var before = await db.Participants.SingleAsync(p => p.Id == seed.MasterclassSpeakerId);
        Assert.Equal(ParticipantRole.Speaker, before.Role);

        var parsed = SessionizeApiClient.ParseSpeakers(AcceptedSpeakersJson);
        await import.ImportSpeakersAsync(
            seed.EventId, parsed.Speakers, parsed.Warnings, sendWelcome: false);

        var after = await db.Participants.SingleAsync(p => p.Id == seed.MasterclassSpeakerId);
        Assert.Equal(ParticipantRole.Speaker, after.Role); // unchanged
    }

    [Fact]
    public async Task Import_never_deletes_and_is_idempotent_on_re_run()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (import, sender) = ScenarioFixture.NewImporter(db);

        var countBefore = await db.Participants.CountAsync(p => p.EventId == seed.EventId);
        var parsed = SessionizeApiClient.ParseSpeakers(AcceptedSpeakersJson);

        var first = await import.ImportSpeakersAsync(
            seed.EventId, parsed.Speakers, parsed.Warnings, sendWelcome: true);
        var second = await import.ImportSpeakersAsync(
            seed.EventId, parsed.Speakers, parsed.Warnings, sendWelcome: true);

        // First run adds exactly one; second adds none and re-welcomes no one.
        Assert.Equal(countBefore + 1, await db.Participants.CountAsync(p => p.EventId == seed.EventId));
        Assert.Equal(1, first.Created);
        Assert.Equal(0, second.Created);
        // §299 6.1: the new speaker lands Preselected (not active), so no welcome is
        // sent at import time on EITHER run — welcomes/onboarding fire at activation.
        Assert.Empty(sender.Sent);
    }
}
