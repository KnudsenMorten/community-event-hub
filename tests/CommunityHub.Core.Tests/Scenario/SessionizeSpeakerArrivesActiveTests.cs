using CommunityHub.Core.Settings;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// §880 SCENARIO — a Sessionize speaker arrives <b>ACTIVE</b>, and the ONLY thing still holding them
/// is the missing speaker <b>category</b>.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-05: <i>"can we change so speakers synced from sessionize are active by
/// default … i need the blocking, but if we could keep the speakers out of the digest queue it could
/// simplify it"</i>.</para>
///
/// <para>🔒 <b>THIS FILE EXISTS TO PROVE THE HALF THAT IS EASY TO LOSE.</b> Arriving Active is one
/// line; the version he ACCEPTED is that line <i>plus</i> a welcome that waits for the category
/// (§880.6). He was first told rings would contain the mail — they do not: <c>welcome-email</c> is
/// released to Ring 3 in PROD and imported speakers arrive Ring 3, so without the second gate this
/// change mails an unreviewed import within ~10 minutes. A test asserting only
/// <c>IsActive == true</c> would pass on the version he rejected, so the assertions below are the
/// gate, not the flag.</para>
/// </remarks>
public sealed class SessionizeSpeakerArrivesActiveTests
{
    private const string SpeakersJson = """
    [
      { "id": "spk-accepted", "firstName": "Accepted", "lastName": "Speaker", "fullName": "Accepted Speaker", "links": [] }
    ]
    """;

    private static SessionizeParseResult Parse() =>
        SessionizeApiClient.ParseSpeakers(
            SpeakersJson,
            new Dictionary<string, string> { ["spk-accepted"] = "accepted.speaker@example.test" });

    private static async Task<Participant> ImportOneAsync(
        CommunityHubDbContext db, int eventId, bool sendWelcome)
    {
        var (import, _) = ScenarioFixture.NewImporter(db);
        var parsed = Parse();
        Assert.Null(parsed.Error);

        var result = await import.ImportSpeakersAsync(
            eventId, parsed.Speakers, parsed.Warnings, sendWelcome: sendWelcome);
        Assert.Equal(1, result.Created);

        return await db.Participants.SingleAsync(
            p => p.EventId == eventId && p.Email == "accepted.speaker@example.test");
    }

    /// <summary>
    /// An accepted Sessionize speaker lands ACTIVE and at ring 3 — the two conditions §880 removes —
    /// while the category stays null, which is the one an organizer actually decides.
    /// </summary>
    [Fact]
    public async Task An_imported_speaker_is_active_at_ring_three_and_uncategorized()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var speaker = await ImportOneAsync(db, seed.EventId, sendWelcome: false);

        Assert.True(speaker.IsActive);
        Assert.Equal(ParticipantLifecycleState.Active, speaker.LifecycleState);
        // 🔒 §26c/§869.1 — the RING is untouched by §880. Changing it here would widen an audience
        // nobody asked to widen, in a change that is about a lifecycle flag.
        Assert.Equal(Ring.Broad, speaker.Ring);

        var profile = await db.SpeakerProfiles.SingleAsync(sp => sp.ParticipantId == speaker.Id);
        Assert.Null(profile.Category);
    }

    /// <summary>
    /// 🔑 THE BLOCKING SURVIVES. This is the assertion §880 rests on: the push gate's three
    /// conditions are INDEPENDENT, so dropping two of them leaves the speaker held on the third.
    /// </summary>
    [Fact]
    public async Task An_imported_speaker_is_still_HELD_from_the_Zoho_flow_while_uncategorized()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var speaker = await ImportOneAsync(db, seed.EventId, sendWelcome: false);

        var approval = new CommunityHub.Core.Organizer.SpeakerApprovalService(
            db, new CommunityHub.Core.Settings.FeatureGateService(db), TimeProvider.System);
        var pending = await approval.PendingAsync(seed.EventId);

        var held = Assert.Single(pending.Speakers, s => s.ParticipantId == speaker.Id);

        // 🔑 AND THE POINT OF THE CHANGE: ONE blocker for ONE decision, not three for one.
        // Before §880 this read "no speaker category; participant inactive; not activated
        // (lifecycle Preselected)" — three lines of plumbing for a single organizer choice.
        var blocker = Assert.Single(held.Blockers);
        Assert.Contains("category", blocker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 🔒 §880.6 — AND THEY ARE NOT WELCOMED YET. The option he chose, and the one that is invisible
    /// unless something asserts it.
    /// </summary>
    [Fact]
    public async Task An_imported_speaker_is_NOT_welcomed_while_they_have_no_category()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        // sendWelcome: TRUE — the import is actively asked to welcome, and the gate must still hold.
        var (import, sender) = ScenarioFixture.NewImporter(db);
        var parsed = Parse();
        var result = await import.ImportSpeakersAsync(
            seed.EventId, parsed.Speakers, parsed.Warnings, sendWelcome: true);
        Assert.Equal(1, result.Created);

        Assert.DoesNotContain(sender.Messages, m => m.To == "accepted.speaker@example.test");

        // 🔒 UNRECORDED, not "sent and suppressed": nothing may be written to the ledger, or the
        // welcome would be lost forever the moment the category is finally set.
        var speaker = await db.Participants.SingleAsync(
            p => p.EventId == seed.EventId && p.Email == "accepted.speaker@example.test");
        Assert.False(await db.SentReminders.AnyAsync(
            s => s.ReminderType == "welcome" && s.OccasionKey == $"welcome:{speaker.Id}"));
    }

    /// <summary>
    /// …and the moment an organizer sets the category, the SAME reconcile pass welcomes them. The
    /// hold has to be a hold, not a silent loss — that is what makes the category the decision point
    /// for the mail as well as for the Zoho push.
    /// </summary>
    [Fact]
    public async Task Setting_the_category_releases_the_welcome_on_the_next_pass()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var speaker = await ImportOneAsync(db, seed.EventId, sendWelcome: false);

        var (welcome, sender) = ScenarioFixture.NewWelcomeService(db);

        Assert.False(await welcome.SendWelcomeAsync(speaker.Id));
        Assert.Empty(sender.Messages);

        var profile = await db.SpeakerProfiles.SingleAsync(sp => sp.ParticipantId == speaker.Id);
        profile.Category = SpeakerCategory.Community;
        await db.SaveChangesAsync();

        Assert.True(await welcome.SendWelcomeAsync(speaker.Id));
        Assert.Contains(sender.Messages, m => m.To == "accepted.speaker@example.test");
    }
}
