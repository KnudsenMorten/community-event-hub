using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1222 — a welcomed participant must be chaseable by the Get Started digest.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-14: <i>"sponsors are reporting that they dont get any weekly reminders when
/// get started is not completed"</i>. The digest anchors on <c>WelcomeWithLoginSentAt</c> (§738: never
/// welcomed ⇒ never chased), but <see cref="WelcomeEmailService"/> — the sponsor/speaker reconcile
/// path — wrote only the <c>welcome:{id}</c> ledger row. On PROD, 9 sponsor coordinators (plus other
/// roles) had been welcomed and were never chased.</para>
/// <para>FAKE addresses only.</para>
/// </remarks>
public sealed class WelcomeStampForDigestTests
{
    private static async Task<(int EventId, Participant P)> SeedAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, ParticipantRole role)
    {
        var ev = new Event
        {
            Code = "WS27", CommunityName = "C", DisplayName = "C 2027", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = ev.Id, Email = "coord@company.example", FullName = "Casey Coord", Role = role,
            IsActive = true, IsEventCoordinator = role == ParticipantRole.Sponsor, SponsorCompanyId = "77",
            CreatedAt = ScenarioFixture.Clock.GetUtcNow().AddDays(-30),
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return (ev.Id, p);
    }

    [Fact]
    public async Task The_reconcile_welcome_stamps_the_digest_anchor()
    {
        using var db = ScenarioFixture.NewDb();
        var (_, p) = await SeedAsync(db, ParticipantRole.Sponsor);
        var (welcome, sender) = ScenarioFixture.NewWelcomeService(db);

        Assert.True(await welcome.SendWelcomeAsync(p.Id));
        Assert.Single(sender.Sent);

        // 🔑 The bug: this was null, so GetStartedDigestBuilder skipped the person for ever.
        var after = await db.Participants.AsNoTracking().SingleAsync();
        Assert.Equal(ScenarioFixture.Clock.GetUtcNow(), after.WelcomeWithLoginSentAt);
    }

    /// <summary>🔒 A forced resend must not push an existing cadence anchor forward.</summary>
    [Fact]
    public async Task A_forced_resend_keeps_the_original_anchor()
    {
        using var db = ScenarioFixture.NewDb();
        var (_, p) = await SeedAsync(db, ParticipantRole.Sponsor);
        var original = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
        p.WelcomeWithLoginSentAt = original;
        await db.SaveChangesAsync();
        var (welcome, _) = ScenarioFixture.NewWelcomeService(db);

        await welcome.SendWelcomeAsync(p.Id);
        Assert.True(await welcome.SendWelcomeAsync(p.Id, force: true));

        Assert.Equal(original, (await db.Participants.AsNoTracking().SingleAsync()).WelcomeWithLoginSentAt);
    }

    /// <summary>
    /// 🔑 The self-heal for the people already affected: anchored on the date the welcome was SENT
    /// (from the ledger), not "now" — "now" would delay their first chase by another interval.
    /// </summary>
    [Fact]
    public async Task The_sweep_anchors_an_unstamped_welcome_on_its_ledger_date()
    {
        using var db = ScenarioFixture.NewDb();
        var (eventId, p) = await SeedAsync(db, ParticipantRole.Sponsor);
        var sentAt = new DateTimeOffset(2026, 8, 28, 13, 30, 0, TimeSpan.Zero);
        db.SentReminders.Add(new SentReminder
        {
            EventId = eventId, RecipientEmail = p.Email, ReminderType = "welcome",
            OccasionKey = $"welcome:{p.Id}", SentAt = sentAt,
        });
        await db.SaveChangesAsync();

        Assert.Equal(1, await new OrganizerWelcomeAnchorSeeder(db).RunAsync(eventId));
        Assert.Equal(sentAt, (await db.Participants.AsNoTracking().SingleAsync()).WelcomeWithLoginSentAt);

        // Idempotent: a second pass finds nothing to do.
        Assert.Equal(0, await new OrganizerWelcomeAnchorSeeder(db).RunAsync(eventId));
    }

    /// <summary>🔒 No welcome row ⇒ no anchor. Seeding one would fake a welcome that never went out.</summary>
    [Fact]
    public async Task The_sweep_never_anchors_someone_who_was_not_welcomed()
    {
        using var db = ScenarioFixture.NewDb();
        var (eventId, _) = await SeedAsync(db, ParticipantRole.Sponsor);

        Assert.Equal(0, await new OrganizerWelcomeAnchorSeeder(db).RunAsync(eventId));
        Assert.Null((await db.Participants.AsNoTracking().SingleAsync()).WelcomeWithLoginSentAt);
    }
}
