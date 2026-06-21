using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: a speaker overrides their contact email. Many speakers do not use
/// the Sessionize/community email they registered with for day-to-day calendar
/// and mail, so they set a preferred address. When set, ALL outbound speaker
/// mail and .ics calendar invites — in the hub AND (when wired) Zoho Backstage —
/// use the override. The Sessionize/community email stays the IDENTITY + match
/// key, so login is unbroken and a Sessionize re-import keeps matching.
///
/// This proves the contract:
///  - EffectiveEmail = override ?? Sessionize (the single routing rule);
///  - the per-user .ics calendar feed addresses the override;
///  - task-deadline reminders deliver to the override but the idempotency ledger
///    keys on the identity address (so changing the override never re-sends);
///  - the welcome mail goes to the override;
///  - a Sessionize re-import NEVER overwrites the override (it is hub-collected);
///  - the override never changes the identity (login) address;
///  - the Backstage propagation queues the desired address as Pending (live
///    Backstage speaker-email wiring is ◻ pending — no Zoho call is faked).
///
/// NO real customer / person data — example.test + @expertslive.dk only.
/// </summary>
public sealed class SpeakerEmailOverrideScenarioTests
{
    private const string Override = "speaker.calendar@example.test";

    // ---- EffectiveEmail routing rule ---------------------------------------

    [Fact]
    public void EffectiveEmail_uses_override_when_set_else_sessionize()
    {
        Assert.Equal("id@example.test",
            SpeakerProfile.EffectiveEmailFor("id@example.test", null));
        Assert.Equal("id@example.test",
            SpeakerProfile.EffectiveEmailFor("id@example.test", "   "));
        Assert.Equal(Override,
            SpeakerProfile.EffectiveEmailFor("id@example.test", Override));
        // Trimmed.
        Assert.Equal(Override,
            SpeakerProfile.EffectiveEmailFor("id@example.test", "  " + Override + "  "));
    }

    [Fact]
    public void EffectiveEmail_instance_property_matches_static_helper()
    {
        var p = new Participant { Email = "id@example.test" };
        var prof = new SpeakerProfile { Participant = p, ContactEmailOverride = Override };
        Assert.Equal(Override, prof.EffectiveEmail);

        prof.ContactEmailOverride = null;
        Assert.Equal("id@example.test", prof.EffectiveEmail);
    }

    // ---- Calendar feed uses the override -----------------------------------

    [Fact]
    public async Task Calendar_feed_addresses_the_override_not_the_identity()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await SetOverrideAsync(db, seed.EventId, seed.SpeakerOneId, Override);

        // Give the speaker a dated task so the feed has an item carrying ORGANIZER/ATTENDEE.
        db.Tasks.Add(new ParticipantTask
        {
            EventId = seed.EventId,
            AssignedParticipantId = seed.SpeakerOneId,
            Title = "Upload final deck",
            DueDate = new DateOnly(2027, 2, 3),
            State = TaskState.Open,
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        var builder = new ParticipantCalendarBuilder(db);
        var ics = await builder.BuildFeedAsync(seed.SpeakerOneId, "hub.example.test");

        Assert.Contains($"mailto:{Override}", ics);
        Assert.DoesNotContain($"mailto:{ScenarioSeed.SpeakerOneEmail}", ics);
    }

    [Fact]
    public async Task Calendar_feed_falls_back_to_identity_when_no_override()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        db.Tasks.Add(new ParticipantTask
        {
            EventId = seed.EventId,
            AssignedParticipantId = seed.SpeakerOneId,
            Title = "Upload final deck",
            DueDate = new DateOnly(2027, 2, 3),
            State = TaskState.Open,
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        var builder = new ParticipantCalendarBuilder(db);
        var ics = await builder.BuildFeedAsync(seed.SpeakerOneId, "hub.example.test");

        Assert.Contains($"mailto:{ScenarioSeed.SpeakerOneEmail}", ics);
    }

    // ---- Reminders deliver to the override; ledger keys on identity --------

    [Fact]
    public async Task Task_reminder_delivers_to_override_but_dedups_on_identity()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await SetOverrideAsync(db, seed.EventId, seed.SpeakerOneId, Override);

        // A task due in 1 day -> inside the reminder window.
        var due = DateOnly.FromDateTime(ScenarioFixture.Clock.GetUtcNow().UtcDateTime).AddDays(1);
        db.Tasks.Add(new ParticipantTask
        {
            EventId = seed.EventId,
            AssignedParticipantId = seed.SpeakerOneId,
            Title = "Verify bio + photo",
            DueDate = due,
            State = TaskState.Open,
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        var templates = NewTemplates();
        var builder = new TaskReminderBuilder(
            db, templates, ScenarioFixture.Clock,
            new CommunityHub.Core.Email.SponsorRecipientResolver(db));
        var due_messages = await builder.BuildDueAsync(seed.EventId);

        var msg = Assert.Single(due_messages, m => m.RecipientEmail == ScenarioSeed.SpeakerOneEmail);
        // Ledger key is the identity; delivery is the override.
        Assert.Equal(ScenarioSeed.SpeakerOneEmail, msg.RecipientEmail);
        Assert.Equal(Override, msg.EffectiveRecipient);

        // The engine sends to the EFFECTIVE address.
        var sender = new CapturingEmailSender();
        var engine = new ReminderEngine(db, sender, ScenarioFixture.Clock);
        var sent = await engine.SendDueAsync(seed.EventId, due_messages);
        Assert.True(sent >= 1);
        Assert.Contains(sender.Sent, s => s.To == Override);
        Assert.DoesNotContain(sender.Sent, s => s.To == ScenarioSeed.SpeakerOneEmail);

        // Ledger recorded the IDENTITY address (stable across override changes).
        Assert.True(await db.SentReminders.AnyAsync(
            s => s.RecipientEmail == ScenarioSeed.SpeakerOneEmail
                 && s.ReminderType == "task-deadline"));
    }

    // ---- Welcome mail uses the override ------------------------------------

    [Fact]
    public async Task Welcome_mail_goes_to_the_override()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await SetOverrideAsync(db, seed.EventId, seed.SpeakerOneId, Override);

        var sender = new CapturingEmailSender();
        var welcome = new WelcomeEmailService(db, NewTemplates(), sender, ScenarioFixture.Clock);

        var sentOne = await welcome.SendWelcomeAsync(seed.SpeakerOneId);
        Assert.True(sentOne);
        Assert.Single(sender.Sent);
        Assert.Equal(Override, sender.Sent[0].To);

        // Idempotent: keyed on the identity, so a re-call never re-welcomes.
        var sentAgain = await welcome.SendWelcomeAsync(seed.SpeakerOneId);
        Assert.False(sentAgain);
        Assert.Single(sender.Sent);
    }

    // ---- Sessionize re-import must NOT overwrite the override --------------

    [Fact]
    public async Task Sessionize_reimport_preserves_the_override_and_identity()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await SetOverrideAsync(db, seed.EventId, seed.SpeakerOneId, Override);

        var (import, _) = ScenarioFixture.NewImporter(db);

        // Re-import the speaker (matched on the SAME identity email) with a fresh
        // bio — the import writes Sessionize bio fields only, never the override.
        // Use the FULL import mode so a bio field is actually refreshed (the seed
        // already has a Tagline; delta would keep it), which makes this a strong
        // proof: even an import that DOES rewrite bio fields never touches the
        // hub-collected ContactEmailOverride.
        var speakers = new[]
        {
            new SessionizeSpeaker(
                Email: ScenarioSeed.SpeakerOneEmail,
                FirstName: "Session", LastName: "Speaker One",
                TagLine: "Refreshed tagline", Biography: "Refreshed bio",
                Blog: null, LinkedIn: null, Twitter: null, ProfilePictureUrl: null),
        };
        await import.ImportSpeakersAsync(
            seed.EventId, speakers, System.Array.Empty<string>(), sendWelcome: false,
            mode: SessionizeImportMode.Full);

        var profile = await db.SpeakerProfiles.SingleAsync(
            sp => sp.ParticipantId == seed.SpeakerOneId);
        // Override survives the re-import.
        Assert.Equal(Override, profile.ContactEmailOverride);
        // Sessionize field was refreshed (proves the import actually ran).
        Assert.Equal("Refreshed tagline", profile.Tagline);

        // Identity (login) address is unchanged — re-import still matched on it.
        var p = await db.Participants.SingleAsync(x => x.Id == seed.SpeakerOneId);
        Assert.Equal(ScenarioSeed.SpeakerOneEmail, p.Email);
    }

    // ---- Backstage propagation queues a Pending marker (◻ live wiring) -----

    [Fact]
    public async Task Backstage_propagation_queues_pending_and_makes_no_zoho_call()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var backstage = new NullBackstageSpeakerEmailApi(); // CanWrite = false
        var svc = new SpeakerEmailPropagationService(db, backstage, ScenarioFixture.Clock);

        var state = await svc.QueueAsync(
            seed.EventId, seed.SpeakerOneId, ScenarioSeed.SpeakerOneEmail, Override);

        Assert.Equal(BackstageEmailSyncState.Pending, state);

        var row = await db.SpeakerBackstageEmailSyncs.SingleAsync(
            x => x.ParticipantId == seed.SpeakerOneId);
        Assert.Equal(ScenarioSeed.SpeakerOneEmail, row.IdentityEmail); // match key
        Assert.Equal(Override, row.DesiredEmail);                       // effective
        Assert.Null(row.SyncedAt);

        // Re-queue (e.g. override cleared) upserts the SAME row, not a duplicate.
        var cleared = await svc.QueueAsync(
            seed.EventId, seed.SpeakerOneId, ScenarioSeed.SpeakerOneEmail, null);
        Assert.Equal(BackstageEmailSyncState.Pending, cleared);
        var rows = await db.SpeakerBackstageEmailSyncs
            .Where(x => x.ParticipantId == seed.SpeakerOneId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(ScenarioSeed.SpeakerOneEmail, rows[0].DesiredEmail); // back to identity
    }

    [Fact]
    public async Task Backstage_propagation_syncs_when_a_live_writer_is_wired()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var live = new FakeLiveBackstageSpeakerEmailApi();
        var svc = new SpeakerEmailPropagationService(db, live, ScenarioFixture.Clock);

        var state = await svc.QueueAsync(
            seed.EventId, seed.SpeakerOneId, ScenarioSeed.SpeakerOneEmail, Override);

        Assert.Equal(BackstageEmailSyncState.Synced, state);
        var record = Assert.Single(live.Writes);
        Assert.Equal(ScenarioSeed.SpeakerOneEmail, record.IdentityEmail);
        Assert.Equal(Override, record.DesiredEmail);

        var row = await db.SpeakerBackstageEmailSyncs.SingleAsync(
            x => x.ParticipantId == seed.SpeakerOneId);
        Assert.Equal(BackstageEmailSyncState.Synced, row.State);
        Assert.NotNull(row.SyncedAt);
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task SetOverrideAsync(
        Data.CommunityHubDbContext db, int eventId, int participantId, string? value)
    {
        var prof = await db.SpeakerProfiles.FirstOrDefaultAsync(
            sp => sp.ParticipantId == participantId);
        if (prof is null)
        {
            prof = new SpeakerProfile
            {
                EventId = eventId, ParticipantId = participantId,
                CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
            };
            db.SpeakerProfiles.Add(prof);
        }
        prof.ContactEmailOverride = value;
        await db.SaveChangesAsync();
    }

    private static Email.EmailTemplateProvider NewTemplates() =>
        new(Microsoft.Extensions.Options.Options.Create(new Email.EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
        }));

    /// <summary>A live Backstage speaker-email writer (test double) that records writes.</summary>
    private sealed class FakeLiveBackstageSpeakerEmailApi : IBackstageSpeakerEmailApi
    {
        public List<SpeakerEmailRecord> Writes { get; } = new();
        public bool CanWrite => true;
        public Task SetSpeakerEmailAsync(SpeakerEmailRecord record, CancellationToken ct)
        {
            Writes.Add(record);
            return Task.CompletedTask;
        }
    }
}
