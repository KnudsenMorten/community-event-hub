using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using CommunityHub.Forms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §738 — NEVER WELCOMED ⇒ NEVER CHASED.
///
/// <para>Both chasers anchored on <c>WelcomeWithLoginSentAt ?? CreatedAt</c>. With no welcome sent
/// the stamp is null, so the anchor fell back to the row's creation date — often months earlier —
/// and the person read as MAXIMALLY OVERDUE. They then fired the instant their ring opened.</para>
///
/// <para>🔥 That is not hypothetical. Brevo's own delivery log for July 2026 shows <b>28 people
/// nagged BEFORE their welcome</b> — 24 of them on 2026-07-31 with the nag at 10:00 and the welcome
/// at 10:50 — and <b>15 more nagged with no welcome ever delivered</b>. §232's rule is the exact
/// opposite: the welcome IS the day-0 nudge and the first chase waits a full interval after it.</para>
///
/// <para>🔒 <b>Each test proves the CONTROL case too.</b> "No mail" alone would pass even if the
/// guard were deleted and the wizard simply had nothing to chase — a false green. So each test then
/// stamps the welcome far enough in the past and asserts the chase DOES fire, which proves the
/// silence above was caused by the guard and nothing else.</para>
/// </summary>
public sealed class NeverWelcomedIsNeverChasedTests
{
    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public void Set(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>Today in these tests. The rows are created 60 days earlier — the shape that made a
    /// never-welcomed person look four intervals overdue.</summary>
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LongAgo = Now.AddDays(-60);

    private static EmailTemplateProvider Templates() =>
        new(Options.Create(new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));

    private static async Task<(int EventId, Participant P)> SeedAsync(
        CommunityHubDbContext db, ParticipantRole role, DateTimeOffset? welcomedAt)
    {
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "T", Code = "T27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = ev.Id, FullName = "Sam Sponsor", Email = "sam@x.dk",
            Role = role, IsActive = true, IsEventCoordinator = true,
            LifecycleState = ParticipantLifecycleState.Active,
            CreatedAt = LongAgo,
            WelcomeWithLoginSentAt = welcomedAt,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return (ev.Id, p);
    }

    // =====================================================================
    //  The party chaser — the simple one (db + templates + clock).
    // =====================================================================

    private static async Task SeedPartyTaskAsync(CommunityHubDbContext db, int eventId, int pid)
    {
        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId, AssignedParticipantId = pid, Title = "Sign up for the Party",
            State = TaskState.Open, DueDate = null, IsMandatory = false,
            SourceKey = PartyTaskSeeder.SourceKeyFor(pid),
            CreatedAt = LongAgo,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Party_chaser_sends_NOTHING_at_all_now_that_the_cadence_is_retired()
    {
        // 🔑 The party half of §738 is MOOT, and this test records why rather than leaving a gap.
        // §733.1 retired this cadence EARLIER THE SAME DAY (an early return at the top of
        // BuildDueAsync), so the mail that reached sponsors at 10:00 on 2026-07-31 can no longer be
        // produced at all — those sends predate that deploy. The never-welcomed guard is still added
        // to the preserved code below it, because §733.1 says re-enabling is "one line" and the
        // defect must not come back with it.
        using var db = ScenarioFixture.NewDb();
        var (ev, p) = await SeedAsync(db, ParticipantRole.Speaker, welcomedAt: Now.AddDays(-60));
        await SeedPartyTaskAsync(db, ev, p.Id);

        var builder = new AttendeePartyReminderBuilder(db, Templates(), new FixedClock(Now));

        // Welcomed 60 days ago with an open party task — the strongest possible case for a send.
        Assert.Empty(await builder.BuildDueAsync(ev));
    }

    // =====================================================================
    //  The Get Started digest — same rule, heavier wiring.
    // =====================================================================

    /// <summary>e-conomic is never reached in these tests; the sponsor wizard just needs an
    /// instance. CanWrite=false is the "not configured" state callers must not write through.</summary>
    private sealed class NoEconomicClient : IEconomicContactAdminClient
    {
        public bool CanWrite => false;
        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
            string? search, int? customerGroup = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(Array.Empty<EconomicCustomerRow>());
        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(
            int customerNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicContactRow>>(Array.Empty<EconomicContactRow>());
        public Task<int> CreateContactAsync(
            int customerNumber, EconomicContactInput input, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateContactAsync(
            int customerNumber, int contactNumber, EconomicContactInput input, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteContactAsync(
            int customerNumber, int contactNumber, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static GetStartedDigestBuilder NewDigest(CommunityHubDbContext db, TimeProvider clock)
    {
        var cmOptions = new CompanyManagerOptions();
        var sponsorWizard = new SponsorWizardService(
            db,
            new CompanyManagerClient(new HttpClient(), cmOptions),
            cmOptions,
            new EconomicContactAdminService(new NoEconomicClient()),
            NullLogger<SponsorWizardService>.Instance);

        return new GetStartedDigestBuilder(
            db, Templates(), clock,
            new SpeakerWizardService(db),
            new RoleWizardService(db),
            new AttendeeWizardService(db),
            sponsorWizard);
    }

    [Fact]
    public async Task Get_Started_digest_is_silent_until_the_welcome_has_actually_been_sent()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, p) = await SeedAsync(db, ParticipantRole.Speaker, welcomedAt: null);

        var clock = new FixedClock(Now);
        var builder = NewDigest(db, clock);

        // Created 60 days ago, never welcomed ⇒ the digest must NOT go out. Before §738 the
        // CreatedAt fallback made this person read as four intervals overdue.
        Assert.Empty(await builder.BuildDueAsync(ev));

        // 🔒 CONTROL: stamp the welcome 20 days back — now the same person IS chased.
        p.WelcomeWithLoginSentAt = Now.AddDays(-20);
        await db.SaveChangesAsync();

        var m = Assert.Single(await builder.BuildDueAsync(ev));
        Assert.Equal("getstarted-digest", m.ReminderType);
        Assert.Equal("sam@x.dk", m.RecipientEmail);
    }

    [Fact]
    public async Task Get_Started_digest_still_waits_a_full_interval_AFTER_the_welcome()
    {
        // 🔥 THE EXACT 2026-07-31 EVENT: the welcome goes out, and the nag must NOT follow it in the
        // same morning. §232 — the welcome IS the day-0 nudge. Brevo recorded the nag at 10:00 and
        // the welcome at 10:50 for 24 people; with the anchor now on the real welcome date, the
        // first chase is a full interval later.
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, ParticipantRole.Speaker, welcomedAt: Now);

        var clock = new FixedClock(Now);
        var builder = NewDigest(db, clock);

        Assert.Empty(await builder.BuildDueAsync(ev));    // welcomed today ⇒ quiet

        clock.Set(Now.AddDays(13));
        Assert.Empty(await builder.BuildDueAsync(ev));    // day 13 ⇒ still quiet

        clock.Set(Now.AddDays(14));
        Assert.Single(await builder.BuildDueAsync(ev));   // a full interval later ⇒ due
    }
}
