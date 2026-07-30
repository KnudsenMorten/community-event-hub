using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using CommunityHub.Forms;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §326b (operator 2026-07-25) — the ONE-SHOT speaker "complete Get Started before the
/// deadline" reminder. Pins: (a) fires ONLY inside the configured
/// reminderDate..deadline window; (b) only ACTIVE SPEAKERS with an INCOMPLETE wizard
/// are mailed — a completed speaker, a non-speaker and an inactive speaker get
/// nothing; (c) the OccasionKey carries NO window index (once ever per speaker,
/// self-healing inside the window); (d) a config without the getStartedDeadline
/// block is inert.
/// </summary>
public sealed class GetStartedDeadlineReminderBuilderTests
{
    // 30 Sep 2026 08:00 UTC = 10:00 Danish (CEST) — the daily ReminderJob tick.
    private static readonly DateTimeOffset OnReminderDay = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static string WriteConfig(bool withDeadlineBlock)
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"ceh-getstarted-deadline-{Guid.NewGuid():N}.json");
        var block = withDeadlineBlock
            ? "\"getStartedDeadline\": { \"reminderDate\": \"2026-09-30\", \"deadline\": \"2026-10-01\" },"
            : string.Empty;
        File.WriteAllText(path, "{ " + block + " \"deadlines\": [] }");
        return path;
    }

    private static GetStartedDeadlineReminderBuilder Builder(
        CommunityHubDbContext db, string configPath, DateTimeOffset now)
    {
        var templates = new EmailTemplateProvider(Options.Create(
            new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));
        return new GetStartedDeadlineReminderBuilder(
            db, templates, new FixedClock(now), new SpeakerWizardService(db),
            new SpeakerDeadlineOptions { ConfigPath = configPath });
    }

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var e = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
    }

    private static async Task<Participant> SeedSpeakerAsync(
        CommunityHubDbContext db, int ev, string email, bool isActive = true)
    {
        var p = new Participant
        {
            EventId = ev, Email = email, FullName = "Sam Speaker",
            Role = ParticipantRole.Speaker, IsActive = isActive,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        // Uncategorized profile ⇒ the wizard is just calendar/details/party/accept
        // (no entitlement-gated logistics steps) — keeps the complete-case cheap.
        db.SpeakerProfiles.Add(new SpeakerProfile { EventId = ev, ParticipantId = p.Id });
        await db.SaveChangesAsync();
        return p;
    }

    private static async Task CompleteWizardAsync(CommunityHubDbContext db, int ev, Participant p)
    {
        var profile = db.SpeakerProfiles.Single(s => s.EventId == ev && s.ParticipantId == p.Id);
        profile.CalendarEmailSetAt = OnReminderDay.AddDays(-30);
        profile.BioLastEditedBySpeakerAt = OnReminderDay.AddDays(-30);
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = ev, ParticipantId = p.Id, Name = p.FullName, Email = p.Email, Attending = true,
        });
        db.ParticipantPolicyAcceptances.Add(new ParticipantPolicyAcceptance
        {
            EventId = ev, ParticipantId = p.Id, AcceptedByEmail = p.Email,
            AcceptedAt = OnReminderDay.AddDays(-30),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Fires_on_reminder_day_only_for_incomplete_active_speakers()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var incomplete = await SeedSpeakerAsync(db, ev, "open@x.dk");
        var complete = await SeedSpeakerAsync(db, ev, "done@x.dk");
        await CompleteWizardAsync(db, ev, complete);
        await SeedSpeakerAsync(db, ev, "inactive@x.dk", isActive: false);
        db.Participants.Add(new Participant
        {
            EventId = ev, Email = "vol@x.dk", FullName = "Val Volunteer",
            Role = ParticipantRole.Volunteer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        });
        await db.SaveChangesAsync();

        var config = WriteConfig(withDeadlineBlock: true);
        var messages = await Builder(db, config, OnReminderDay).BuildDueAsync(ev);

        var m = Assert.Single(messages);
        Assert.Equal("open@x.dk", m.RecipientEmail);
        Assert.Equal(incomplete.Id, m.ParticipantId);
        Assert.Equal("getstarted-deadline", m.ReminderType);
        // NO window index — the ledger makes this once ever per speaker.
        Assert.Equal($"getstarted-deadline:{incomplete.Id}", m.OccasionKey);
        Assert.Equal("welcome-email", m.FeatureKey);
        Assert.Contains("1 October 2026", m.Subject);
    }

    [Fact]
    public async Task Quiet_before_the_window_and_after_the_deadline_self_heals_inside_it()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var speaker = await SeedSpeakerAsync(db, ev, "open@x.dk");
        var config = WriteConfig(withDeadlineBlock: true);

        // 29 Sep (day before) — nothing.
        Assert.Empty(await Builder(db, config, OnReminderDay.AddDays(-1)).BuildDueAsync(ev));

        // 30 Sep and 1 Oct (deadline day) both build the SAME occasion — the
        // engine ledger turns the second into a no-op unless the first was
        // ring-dropped, which is exactly the self-heal we want.
        var day1 = Assert.Single(await Builder(db, config, OnReminderDay).BuildDueAsync(ev));
        var day2 = Assert.Single(await Builder(db, config, OnReminderDay.AddDays(1)).BuildDueAsync(ev));
        Assert.Equal(day1.OccasionKey, day2.OccasionKey);
        Assert.Equal($"getstarted-deadline:{speaker.Id}", day1.OccasionKey);

        // 2 Oct (past the deadline) — nothing, forever.
        Assert.Empty(await Builder(db, config, OnReminderDay.AddDays(2)).BuildDueAsync(ev));
    }

    [Fact]
    public async Task Missing_deadline_block_or_config_is_inert()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedSpeakerAsync(db, ev, "open@x.dk");

        var noBlock = WriteConfig(withDeadlineBlock: false);
        Assert.Empty(await Builder(db, noBlock, OnReminderDay).BuildDueAsync(ev));

        var missingFile = Path.Combine(Path.GetTempPath(), $"ceh-nope-{Guid.NewGuid():N}.json");
        Assert.Empty(await Builder(db, missingFile, OnReminderDay).BuildDueAsync(ev));
    }
}
