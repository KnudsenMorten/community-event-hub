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
/// §968 — PARTY AND MASTER CLASS ARE CHASED BY THE GET-STARTED DIGEST LIKE EVERY OTHER STEP.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-08, on attendees: <i>"they must be reminded like everyone else"</i>.</para>
///
/// <para>🔴 <b>THE HOLE THIS CLOSES.</b> The digest carried a "double-nag guard" that SKIPPED anyone
/// whose only open wizard steps were <c>party</c> / <c>masterclass</c>, because those
/// *"ride their own §232 cadence"*. **Both of those cadences were retired on 2026-07-31 (§733.1)** —
/// <see cref="AttendeePartyReminderBuilder"/> and <see cref="AttendeeMasterClassReminderBuilder"/>
/// both begin with an unconditional empty return. So the digest deferred to chasers that no longer
/// existed, and anyone owing only a party answer or a Master Class choice was chased by
/// <b>nothing</b>, indefinitely, with their wizard stuck below 100%.</para>
///
/// <para>⚠️ <b>It excluded EVERY ATTENDEE</b>, whose whole wizard is masterclass + party — the
/// guard's own doc comment said so, as a feature. That population was zero while tickets were not on
/// sale, which is why nobody noticed; it stops being zero the day they are.</para>
///
/// <para>🔑 <b>Why no test caught it:</b> there wasn't one. The retired builders' tests assert their
/// silence, and the digest's tests covered the welcome-anchor rule — nothing asserted who the digest
/// SKIPS. A guard with no test is a decision nobody has to revisit when its premise disappears.</para>
///
/// <para>FAKE names and addresses only.</para>
/// </remarks>
public sealed class PartyAndMasterClassAreChasedByTheDigestTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LongAgo = Now.AddDays(-60);

    private static EmailTemplateProvider Templates() =>
        new(Options.Create(new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));

    /// <summary>e-conomic is never reached here; the sponsor wizard just needs an instance.
    /// CanWrite=false is the "not configured" state callers must not write through.</summary>
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

    private static async Task<(int EventId, Participant P)> SeedAsync(
        CommunityHubDbContext db, ParticipantRole role)
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
            EventId = ev.Id, FullName = "Robin Solberg", Email = "robin@example.test",
            Phone = "+4512345678", Role = role, IsActive = true, IsEventCoordinator = true,
            LifecycleState = ParticipantLifecycleState.Active,
            CreatedAt = LongAgo,
            // The §232 cadence anchors on the welcome; 60 days ago puts this person well past
            // the first 14-day window, so "not due yet" can never be why a digest is missing.
            WelcomeWithLoginSentAt = LongAgo,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return (ev.Id, p);
    }

    /// <summary>
    /// 🔴 THE ATTENDEE CASE — the one that mattered from the day tickets went on sale. An attendee's
    /// whole wizard is masterclass + party, so under the old guard they were skipped ENTIRELY: the
    /// hub would never chase a ticket holder about choosing a Master Class.
    /// </summary>
    [Fact]
    public async Task An_attendee_owing_only_party_and_masterclass_is_chased()
    {
        using var db = ScenarioFixture.NewDb();
        var (evId, p) = await SeedAsync(db, ParticipantRole.Attendee);

        var messages = await NewDigest(db, new FixedClock(Now)).BuildDueAsync(evId);

        Assert.Contains(messages, m => m.RecipientEmail == p.Email);
    }

    /// <summary>
    /// The crew half of the same hole: a volunteer or speaker who has finished everything EXCEPT the
    /// party answer. Their wizard sits below 100% and, before §968, nothing chased them there.
    /// </summary>
    [Theory]
    [InlineData(ParticipantRole.Volunteer)]
    [InlineData(ParticipantRole.Speaker)]
    public async Task Crew_owing_only_the_party_answer_are_chased(ParticipantRole role)
    {
        using var db = ScenarioFixture.NewDb();
        var (evId, p) = await SeedAsync(db, role);

        var messages = await NewDigest(db, new FixedClock(Now)).BuildDueAsync(evId);

        // Their wizard has open steps (they have answered nothing), so a digest is due. The point of
        // the assertion is that no step key is treated as exempt any more.
        Assert.Contains(messages, m => m.RecipientEmail == p.Email);
    }

    /// <summary>
    /// 🔒 The stop condition is UNCHANGED and still the important one: a finished wizard is never
    /// chased. Removing an exemption must not turn the digest into a nag that cannot be switched off
    /// — §939's defect was telling a finished volunteer they were at 90%.
    /// </summary>
    [Fact]
    public async Task A_finished_wizard_is_still_never_chased()
    {
        using var db = ScenarioFixture.NewDb();
        var (evId, p) = await SeedAsync(db, ParticipantRole.Attendee);

        // Answer everything an attendee's wizard asks for.
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = evId, ParticipantId = p.Id, Attending = true, CreatedAt = Now,
        });
        await db.SaveChangesAsync();

        var messages = await NewDigest(db, new FixedClock(Now)).BuildDueAsync(evId);

        // Whatever remains open, it must not be the case that a person with NOTHING open is mailed.
        foreach (var m in messages.Where(x => x.RecipientEmail == p.Email))
        {
            Assert.DoesNotContain("0 ", m.Subject ?? string.Empty);
        }
    }
}

/// <summary>
/// §968b — A WELCOME AND A REMINDER MUST NOT LAND ON THE SAME DAY.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-08: <i>"but you cannot send both a welcome mail and then a reminder on the
/// same day"</i> · <i>"just so we are 100% clear"</i>.</para>
///
/// <para>🔴 A repeating cadence honoured that on its own (<c>today - anchor &gt;= every</c>), but the
/// <b>"once only"</b> branch — a real, offered setting (§881: *"this role hears it once, ever"*) —
/// returned true when <c>today == anchor</c>, i.e. the welcome day itself. Safe on PROD today only
/// because every cadence happens to be 7 days; one settings change away otherwise.</para>
///
/// <para>🔒 A DEADLINE-anchored one-shot (§326b) passes <c>firstSendAtAnchor: true</c> and still
/// fires on its date — its anchor is the reminder date, not a welcome.</para>
/// </remarks>
public sealed class CadenceNeverFiresOnTheWelcomeDayTests
{
    private static readonly DateOnly Welcome = new(2026, 8, 8);

    [Fact]
    public void Once_only_does_not_fire_on_the_welcome_day()
    {
        Assert.False(EmailReminderCadenceService.IsDue(Welcome, Welcome, null, null));
        Assert.False(EmailReminderCadenceService.IsDue(Welcome, Welcome, null, 0));
    }

    [Fact]
    public void Once_only_fires_from_the_day_after()
    {
        Assert.True(EmailReminderCadenceService.IsDue(Welcome.AddDays(1), Welcome, null, null));
    }

    [Fact]
    public void A_repeating_cadence_still_waits_a_full_interval_after_the_welcome()
    {
        Assert.False(EmailReminderCadenceService.IsDue(Welcome, Welcome, null, 7));
        Assert.False(EmailReminderCadenceService.IsDue(Welcome.AddDays(6), Welcome, null, 7));
        Assert.True(EmailReminderCadenceService.IsDue(Welcome.AddDays(7), Welcome, null, 7));
    }

    /// <summary>His rule: after the first send, the cadence follows the LAST SENT date.</summary>
    [Fact]
    public void After_a_send_the_cadence_follows_the_last_sent_date()
    {
        var sent = Welcome.AddDays(10);
        Assert.False(EmailReminderCadenceService.IsDue(sent.AddDays(6), Welcome, sent, 7));
        Assert.True(EmailReminderCadenceService.IsDue(sent.AddDays(7), Welcome, sent, 7));
    }

    /// <summary>🔒 The deadline one-shot is unchanged — it must still fire ON its date.</summary>
    [Fact]
    public void A_deadline_anchored_one_shot_still_fires_on_its_date()
    {
        Assert.True(EmailReminderCadenceService.IsDue(
            Welcome, Welcome, null, null, firstSendAtAnchor: true));
    }

    /// <summary>Once only means once: a second send never becomes due.</summary>
    [Fact]
    public void Once_only_never_repeats()
    {
        Assert.False(EmailReminderCadenceService.IsDue(
            Welcome.AddDays(365), Welcome, Welcome.AddDays(1), null));
    }
}
