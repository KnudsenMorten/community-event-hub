using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// §204 SCENARIO: a Sessionize speaker who has NOT accepted their invite has no
/// email on the SpeakersEmails view, so the old importer dropped them entirely and
/// the organizer never saw them. The fix brings such a speaker (keyed by their
/// stable Sessionize speaker id) into the PRE-SELECTION QUEUE as an INACTIVE
/// prospective participant, flagged "no email yet", instead of skipping them — and
/// reconciles onto the SAME row (no duplicate) once their real email appears.
///
/// Drives the REAL parser + importer end-to-end on the in-memory provider. NO real
/// person/customer names or Sessionize ids — generic descriptors only.
/// </summary>
public sealed class SessionizeEmailLessPreselectionTests
{
    // Two speakers on the main Speakers view: one with an email, one WITHOUT. Both
    // carry a Sessionize id (the main view always does). The emails side-view is
    // supplied (readable) but only resolves the first speaker — the second hasn't
    // accepted their invite yet.
    private const string SpeakersJson = """
    [
      { "id": "spk-accepted", "firstName": "Accepted", "lastName": "Speaker", "fullName": "Accepted Speaker", "links": [] },
      { "id": "spk-pending",  "firstName": "Pending",  "lastName": "Invite",  "fullName": "Pending Invite",  "links": [] }
    ]
    """;

    private static SessionizeParseResult Parse() =>
        SessionizeApiClient.ParseSpeakers(
            SpeakersJson,
            new Dictionary<string, string> { ["spk-accepted"] = "accepted.speaker@example.test" });

    [Fact]
    public async Task Email_less_speaker_lands_in_the_preselection_queue_inactive_and_flagged()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (import, _) = ScenarioFixture.NewImporter(db);

        var parsed = Parse();
        Assert.Null(parsed.Error);
        // The email-less-but-id'd speaker is surfaced for queueing, not just dropped.
        Assert.Single(parsed.EmailLessSpeakers);
        Assert.Equal("spk-pending", parsed.EmailLessSpeakers[0].SessionizeId);

        var result = await import.ImportSpeakersAsync(
            seed.EventId, parsed.Speakers, parsed.Warnings,
            sendWelcome: false, emailLessSpeakers: parsed.EmailLessSpeakers);

        // Reported as added to pre-selection, NOT as a skip.
        Assert.Equal(1, result.PreselectedNoEmail);

        // The prospect is a queue row: Speaker, inactive, can't sign in, Sessionize source,
        // parked under a recognizable placeholder address.
        var prospect = await db.Participants.SingleAsync(
            p => p.EventId == seed.EventId
                 && p.FullName == "Pending Invite");
        Assert.Equal(ParticipantRole.Speaker, prospect.Role);
        Assert.False(prospect.IsActive);
        Assert.Equal(ParticipantLifecycleState.Inactive, prospect.LifecycleState);
        Assert.Equal(ParticipantQueueSource.SessionizeSync, prospect.QueueSource);
        Assert.True(SessionizeImportService.IsPlaceholderEmail(prospect.Email));

        // It carries the Sessionize id so it can be reconciled later.
        var prof = await db.SpeakerProfiles.SingleAsync(sp => sp.ParticipantId == prospect.Id);
        Assert.Equal("spk-pending", prof.SessionizeSpeakerId);

        // 🔒 §756 — it is NOT in the pre-selection queue, and that is the new correct behaviour.
        // Operator 2026-08-01: "preselection queue must ONLY contain volunteers as all other roles
        // are selected and have their own onboarding." This assertion used to be
        // Assert.Contains(...); it is inverted deliberately, not deleted, so the change of home is
        // pinned rather than merely un-tested.
        //
        // ⚠️ The ROW still exists, still inactive, still flagged, still carrying its Sessionize id
        // (asserted above) — what changed is only WHICH organizer surface reviews it. Speakers are
        // reviewed on /Organizer/PendingSpeakers, which reads SpeakerProfiles, and the profile row
        // is confirmed present above.
        var queue = await new PreselectionQueueService(db).GetQueueAsync(
            seed.EventId, ParticipantQueueSource.SessionizeSync);
        Assert.DoesNotContain(queue, p => p.Id == prospect.Id);
    }

    [Fact]
    public async Task Re_importing_the_same_email_less_speaker_is_idempotent_no_duplicate()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (import, _) = ScenarioFixture.NewImporter(db);

        var parsed = Parse();
        await import.ImportSpeakersAsync(
            seed.EventId, parsed.Speakers, parsed.Warnings,
            sendWelcome: false, emailLessSpeakers: parsed.EmailLessSpeakers);

        // Second identical run: nothing new added.
        var second = await import.ImportSpeakersAsync(
            seed.EventId, parsed.Speakers, parsed.Warnings,
            sendWelcome: false, emailLessSpeakers: parsed.EmailLessSpeakers);

        Assert.Equal(0, second.PreselectedNoEmail);
        var rows = await db.SpeakerProfiles.CountAsync(sp => sp.SessionizeSpeakerId == "spk-pending");
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task When_the_email_appears_the_same_row_is_reconciled_by_sessionize_id()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (import, _) = ScenarioFixture.NewImporter(db);

        // 1) First sync: the speaker is email-less → parked in the queue.
        var first = Parse();
        await import.ImportSpeakersAsync(
            seed.EventId, first.Speakers, first.Warnings,
            sendWelcome: false, emailLessSpeakers: first.EmailLessSpeakers);

        var prospect = await db.Participants.SingleAsync(p => p.FullName == "Pending Invite");
        var prospectId = prospect.Id;

        // 2) Later sync: the SAME Sessionize id now resolves an email (invite accepted).
        var laterParsed = SessionizeApiClient.ParseSpeakers(
            SpeakersJson,
            new Dictionary<string, string>
            {
                ["spk-accepted"] = "accepted.speaker@example.test",
                ["spk-pending"] = "pending.invite@example.test",
            });
        Assert.Empty(laterParsed.EmailLessSpeakers); // now both have emails

        var second = await import.ImportSpeakersAsync(
            seed.EventId, laterParsed.Speakers, laterParsed.Warnings,
            sendWelcome: false, emailLessSpeakers: laterParsed.EmailLessSpeakers);

        // No new row — reconciled onto the existing one, by Sessionize id.
        Assert.Equal(0, second.PreselectedNoEmail);
        var rows = await db.SpeakerProfiles.CountAsync(sp => sp.SessionizeSpeakerId == "spk-pending");
        Assert.Equal(1, rows);

        var reconciled = await db.Participants.SingleAsync(p => p.Id == prospectId);
        Assert.Equal("pending.invite@example.test", reconciled.Email);
        Assert.False(SessionizeImportService.IsPlaceholderEmail(reconciled.Email));
        // Email is the match key once present; the row stays in the queue for the
        // organizer to review/activate (no silent auto-activation).
        Assert.Equal(ParticipantLifecycleState.Inactive, reconciled.LifecycleState);
    }
}
