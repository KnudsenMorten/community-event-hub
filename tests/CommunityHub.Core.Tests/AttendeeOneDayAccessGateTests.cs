using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §242 (operator 2026-07-07) — the 1-DAY attendee flow is SUSPENDED behind the
/// <c>attendee-1day-access</c> feature (default OFF), REVERSIBLY. These pin the pieces:
/// <list type="bullet">
/// <item>the catalog flag exists, is advanced and defaults OFF (suspension is the default);</item>
/// <item><see cref="AttendeeWelcomeProvisioningService.ReconcileOneDayAccessAsync"/> locks out
/// EXISTING 1-day-only logins while suspended and restores active-ticket holders when the
/// flag turns back on — never touching 2-day holders (§216 owns them), never resurrecting a
/// cancelled ticket's login, and never touching mirror-less (hand-added) participants;</item>
/// <item>the party surface goes quiet with the login: <see cref="PartyTaskSeeder"/> seeds no
/// task for a suspended (deactivated) 1-day attendee, and
/// <see cref="AttendeePartyReminderBuilder"/> skips a deactivated participant's existing task.</item>
/// </list>
/// The job-side gating (no ProvisionOneDayAsync / no 1-day welcome while off) is a one-line
/// IsFeatureEnabledAsync guard in AttendeeBackstageSyncJob + ZohoWebhookDrainJob around the
/// same provisioning calls pinned by AttendeeWelcomeProvisioningServiceTests.
/// </summary>
public sealed class AttendeeOneDayAccessGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 8, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static AttendeeWelcomeProvisioningService NewService(CommunityHubDbContext db) =>
        new(db, new FixedClock(Now), NullLogger<AttendeeWelcomeProvisioningService>.Instance);

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

    private static async Task SeedTicketAsync(
        CommunityHubDbContext db, int ev, string email, TicketStatus status,
        MirrorState mirror = MirrorState.Active)
    {
        db.Attendees.Add(new Attendee
        {
            EventId = ev, BackstageTicketId = Guid.NewGuid().ToString("N"),
            Email = email, FirstName = "Pat", LastName = "Lee",
            TicketStatus = status, MirrorState = mirror,
            CancelledAt = mirror == MirrorState.Cancelled ? Now : null,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Participant> SeedLoginAsync(
        CommunityHubDbContext db, int ev, string email, bool isActive = true,
        ParticipantRole role = ParticipantRole.Attendee)
    {
        var p = new Participant
        {
            EventId = ev, Email = email, FullName = "Pat Lee", Role = role,
            IsActive = isActive, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    // ------------------------------------------------------------------
    // The catalog flag: suspension is the DEFAULT (deploy springs nothing).
    // ------------------------------------------------------------------

    [Fact]
    public void Attendee_1day_access_flag_exists_advanced_and_defaults_off()
    {
        var d = FeatureCatalog.Find("attendee-1day-access");
        Assert.NotNull(d);
        Assert.Equal(FeatureTier.Advanced, d!.Tier);
        Assert.False(d.DefaultEnabled);            // §242: suspended by default
        Assert.True(d.IsUserImpact);               // sign-in/welcome/tasks are noticed
        Assert.Equal(FeatureGroup.Attendees, d.Group);
    }

    // ------------------------------------------------------------------
    // The reversible login sweep.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Suspended_locks_out_one_day_only_logins_and_never_touches_two_day()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedTicketAsync(db, ev, "oneday@x.dk", TicketStatus.Other);
        await SeedTicketAsync(db, ev, "twoday@x.dk", TicketStatus.TwoDay);
        var oneDay = await SeedLoginAsync(db, ev, "oneday@x.dk");
        var twoDay = await SeedLoginAsync(db, ev, "twoday@x.dk");

        var changed = await NewService(db).ReconcileOneDayAccessAsync(ev, enabled: false);

        Assert.Equal(1, changed);
        Assert.False((await db.Participants.FindAsync(oneDay.Id))!.IsActive);  // locked out
        Assert.True((await db.Participants.FindAsync(twoDay.Id))!.IsActive);   // §216 owns 2-day

        // Idempotent: a re-run toggles nothing further.
        Assert.Equal(0, await NewService(db).ReconcileOneDayAccessAsync(ev, enabled: false));
    }

    [Fact]
    public async Task Reenabling_restores_active_ticket_holders_but_not_cancelled_ones()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedTicketAsync(db, ev, "active@x.dk", TicketStatus.Other);
        await SeedTicketAsync(db, ev, "gone@x.dk", TicketStatus.Other, MirrorState.Cancelled);
        var active = await SeedLoginAsync(db, ev, "active@x.dk", isActive: false); // suspended earlier
        var gone = await SeedLoginAsync(db, ev, "gone@x.dk", isActive: false);     // §216 lockout

        var changed = await NewService(db).ReconcileOneDayAccessAsync(ev, enabled: true);

        Assert.Equal(1, changed);
        Assert.True((await db.Participants.FindAsync(active.Id))!.IsActive);   // restored
        Assert.False((await db.Participants.FindAsync(gone.Id))!.IsActive);    // stays locked (§216)
    }

    [Fact]
    public async Task Mirrorless_and_non_attendee_logins_are_untouched()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        // A hand-added attendee login with NO mirror row (tests / manual adds) — absence
        // of the mirror is not evidence of a 1-day ticket.
        var handAdded = await SeedLoginAsync(db, ev, "manual@x.dk");
        // A SPEAKER who also holds a 1-day ticket — never locked out by the attendee gate.
        await SeedTicketAsync(db, ev, "spk@x.dk", TicketStatus.Other);
        var speaker = await SeedLoginAsync(db, ev, "spk@x.dk", role: ParticipantRole.Speaker);

        Assert.Equal(0, await NewService(db).ReconcileOneDayAccessAsync(ev, enabled: false));
        Assert.True((await db.Participants.FindAsync(handAdded.Id))!.IsActive);
        Assert.True((await db.Participants.FindAsync(speaker.Id))!.IsActive);
    }

    // ------------------------------------------------------------------
    // Party task + reminders go quiet with the login.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Party_seeder_never_seeds_for_a_one_day_attendee_even_when_access_is_enabled()
    {
        // §299 7.1 (operator 2026-07-23): a 1-day holder gets NO tasks AT ALL — the party
        // task is withheld by TICKET TYPE, not by the access flag. Re-enabling
        // attendee-1day-access restores the login but still never seeds the party task.
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedTicketAsync(db, ev, "oneday@x.dk", TicketStatus.Other);
        var p = await SeedLoginAsync(db, ev, "oneday@x.dk");

        // The sync's §242 sweep runs before the daily seeder ever does.
        await NewService(db).ReconcileOneDayAccessAsync(ev, enabled: false);

        var seeded = await new PartyTaskSeeder(db, new FixedClock(Now)).SeedAsync(ev);
        Assert.Equal(0, seeded);
        Assert.False(await db.Tasks.AnyAsync(t => t.AssignedParticipantId == p.Id));

        // Re-enabled ⇒ the login is restored, but a 1-day holder STILL gets no party task.
        await NewService(db).ReconcileOneDayAccessAsync(ev, enabled: true);
        Assert.Equal(0, await new PartyTaskSeeder(db, new FixedClock(Now)).SeedAsync(ev));
        Assert.False(await db.Tasks.AnyAsync(t => t.AssignedParticipantId == p.Id));
    }

    [Fact]
    public async Task Party_reminder_builder_skips_a_deactivated_participants_existing_task()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedTicketAsync(db, ev, "oneday@x.dk", TicketStatus.Other);
        var p = await SeedLoginAsync(db, ev, "oneday@x.dk");
        // The task was seeded BEFORE the suspension (pre-existing open task).
        db.Tasks.Add(new ParticipantTask
        {
            EventId = ev, AssignedParticipantId = p.Id, Title = "Sign up for the Party",
            State = TaskState.Open, SourceKey = PartyTaskSeeder.SourceKeyFor(p.Id),
            CreatedAt = Now.AddDays(-30),   // well past the §232 quiet window
        });
        await db.SaveChangesAsync();

        var templates = new EmailTemplateProvider(Options.Create(
            new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));
        var builder = new AttendeePartyReminderBuilder(db, templates, new FixedClock(Now));

        // Active ⇒ the cadence would fire...
        Assert.Single(await builder.BuildDueAsync(ev));

        // ...suspended (login deactivated by the §242 sweep) ⇒ silence.
        await NewService(db).ReconcileOneDayAccessAsync(ev, enabled: false);
        Assert.Empty(await builder.BuildDueAsync(ev));

        // ...and reversible: re-enabling resumes the cadence.
        await NewService(db).ReconcileOneDayAccessAsync(ev, enabled: true);
        Assert.Single(await builder.BuildDueAsync(ev));
    }

    // ---- §326ap: the block is resolved from the E-MAIL, at the FIRST sign-in step -------

    [Fact]
    public async Task One_day_holder_is_blocked_by_email_before_a_pin_is_ever_sent()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedTicketAsync(db, ev, "oneday@example.com", TicketStatus.Other);
        db.Participants.Add(new Participant
        {
            EventId = ev, FullName = "Pat Lee", Email = "oneday@example.com",
            Role = ParticipantRole.Attendee, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active, Ring = Ring.Broad,
        });
        await db.SaveChangesAsync();

        var gate = new Auth.OneDayAccessGate(db, new FeatureGateService(db), new RingResolver(db));

        // The e-mail-keyed verdict matches the participant-keyed one the PIN step uses, so
        // step 1 can answer immediately instead of mailing a PIN that can never be used.
        var pid = db.Participants.Single().Id;
        Assert.True(await gate.IsBlockedAsync(pid));
        Assert.True(await gate.IsBlockedByEmailAsync(ev, "oneday@example.com"));
        // Case- and whitespace-insensitive — people type their address by hand.
        Assert.True(await gate.IsBlockedByEmailAsync(ev, "  OneDay@Example.COM "));
    }

    [Fact]
    public async Task Unknown_and_two_day_addresses_are_not_blocked_at_the_email_step()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedTicketAsync(db, ev, "twoday@example.com", TicketStatus.TwoDay);
        db.Participants.Add(new Participant
        {
            EventId = ev, FullName = "Sam Ray", Email = "twoday@example.com",
            Role = ParticipantRole.Attendee, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active, Ring = Ring.Broad,
        });
        await db.SaveChangesAsync();

        var gate = new Auth.OneDayAccessGate(db, new FeatureGateService(db), new RingResolver(db));

        // A 2-day holder signs in normally...
        Assert.False(await gate.IsBlockedByEmailAsync(ev, "twoday@example.com"));
        // ...and an address we do not know falls through to the UNCHANGED neutral PIN
        // response, so the endpoint still refuses to enumerate who is registered.
        Assert.False(await gate.IsBlockedByEmailAsync(ev, "stranger@example.com"));
        Assert.False(await gate.IsBlockedByEmailAsync(ev, null));
        Assert.False(await gate.IsBlockedByEmailAsync(ev, "   "));
    }
}
