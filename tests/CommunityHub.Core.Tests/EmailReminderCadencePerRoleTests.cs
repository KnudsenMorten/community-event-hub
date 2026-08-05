using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §881 — a recurring mail's repeat interval is settable PER ROLE, because a shared template reaches
/// several roles and chasing them is not the same job.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-05: <i>"i need to define the cadence for reminders for get started pending
/// for sponsor — like this one mentioned under speaker"</i>. <c>getstarted-digest</c> reaches
/// Speaker, Sponsor and Attendee and is filed under Speakers, so the cadence box appeared in the
/// Speakers section and the Sponsor section explained where the control lived instead of carrying
/// one.</para>
///
/// <para>🔑 The resolution rule is <c>per-role ?? all-roles ?? shipped default</c> — the same model
/// the page already teaches for RINGS, so there is no second mental model to learn.</para>
/// </remarks>
public sealed class EmailReminderCadencePerRoleTests
{
    private const string SharedMail = "getstarted-digest";

    private static async Task<int> NewEventAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "ELDK27", CommunityName = "C", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 9, 1), EndDate = new DateOnly(2027, 9, 2), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        return evt.Id;
    }

    private static EmailReminderCadenceService NewService(CommunityHubDbContext db) =>
        new(db, TimeProvider.System);

    /// <summary>
    /// The ask itself: sponsors get their own interval and NOBODY ELSE MOVES. That last half is the
    /// property that makes this safe to use — the same guarantee the per-role ring picker gives.
    /// </summary>
    [Fact]
    public async Task A_role_can_carry_its_own_interval_without_moving_the_others()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await NewEventAsync(db);
        var svc = NewService(db);

        // The all-roles value everyone follows to begin with.
        Assert.True(await svc.SetIntervalAsync(eventId, SharedMail, 14, "org@example.test"));
        // …and the sponsors' own.
        Assert.True(await svc.SetIntervalAsync(
            eventId, SharedMail, 7, "org@example.test", ParticipantRole.Sponsor));

        Assert.Equal(7, await svc.GetIntervalDaysAsync(eventId, SharedMail, ParticipantRole.Sponsor));
        Assert.Equal(14, await svc.GetIntervalDaysAsync(eventId, SharedMail, ParticipantRole.Speaker));
        Assert.Equal(14, await svc.GetIntervalDaysAsync(eventId, SharedMail, ParticipantRole.Attendee));
        // The all-roles value itself is untouched by a per-role write.
        Assert.Equal(14, await svc.GetIntervalDaysAsync(eventId, SharedMail));
    }

    /// <summary>
    /// 🔒 The way BACK. Without it a per-role number could be set and never un-set, because 0 already
    /// means "send once, ever" and cannot double as "unset".
    /// </summary>
    [Fact]
    public async Task Clearing_a_role_makes_it_follow_the_all_roles_interval_again()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await NewEventAsync(db);
        var svc = NewService(db);

        await svc.SetIntervalAsync(eventId, SharedMail, 14, "org@example.test");
        await svc.SetIntervalAsync(eventId, SharedMail, 7, "org@example.test", ParticipantRole.Sponsor);
        Assert.Equal(7, await svc.GetIntervalDaysAsync(eventId, SharedMail, ParticipantRole.Sponsor));

        Assert.True(await svc.ClearIntervalAsync(eventId, SharedMail, ParticipantRole.Sponsor));
        Assert.Equal(14, await svc.GetIntervalDaysAsync(eventId, SharedMail, ParticipantRole.Sponsor));

        // Clearing what is not set is a no-op, not an error — the button may be pressed twice.
        Assert.False(await svc.ClearIntervalAsync(eventId, SharedMail, ParticipantRole.Sponsor));
    }

    /// <summary>
    /// With no rows at all, every role resolves to the SHIPPED default — so this feature moves nobody
    /// on the day it deploys (the §707.1 pattern).
    /// </summary>
    [Fact]
    public async Task With_nothing_set_every_role_gets_the_shipped_default()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await NewEventAsync(db);
        var svc = NewService(db);

        var shipped = EmailTemplateCatalog.DefaultIntervalDaysFor(SharedMail);

        Assert.Equal(shipped, await svc.GetIntervalDaysAsync(eventId, SharedMail));
        Assert.Equal(shipped, await svc.GetIntervalDaysAsync(eventId, SharedMail, ParticipantRole.Sponsor));
        Assert.Equal(shipped, await svc.GetIntervalDaysAsync(eventId, SharedMail, ParticipantRole.Speaker));
    }

    /// <summary>
    /// 🔒 A cadence for a role the mail never reaches is a control that governs nothing (§326bx) —
    /// and worse, a stored number the operator would believe in. It is refused.
    /// </summary>
    [Fact]
    public async Task A_role_the_mail_never_reaches_is_refused()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await NewEventAsync(db);
        var svc = NewService(db);

        var reaches = EmailTemplateCatalog.RecipientRolesFor(SharedMail);
        var stranger = Enum.GetValues<ParticipantRole>().First(r => !reaches.Contains(r));

        Assert.False(await svc.SetIntervalAsync(eventId, SharedMail, 3, "org@example.test", stranger));
        Assert.Empty(await db.EmailReminderCadences.Where(c => c.Role == stranger).ToListAsync());
    }

    /// <summary>
    /// The pure rule, stated once and asserted directly: per-role wins, else all-roles, else shipped.
    /// </summary>
    [Fact]
    public void Resolve_prefers_the_role_then_the_all_roles_value_then_the_shipped_default()
    {
        var shipped = EmailTemplateCatalog.DefaultIntervalDaysFor(SharedMail);

        var both = new EmailReminderCadenceService.CadenceMap(
            HasAllRoles: true, AllRoles: 14,
            PerRole: new Dictionary<ParticipantRole, int?> { [ParticipantRole.Sponsor] = 7 });
        Assert.Equal(7, EmailReminderCadenceService.Resolve(both, SharedMail, ParticipantRole.Sponsor));
        Assert.Equal(14, EmailReminderCadenceService.Resolve(both, SharedMail, ParticipantRole.Speaker));
        Assert.Equal(14, EmailReminderCadenceService.Resolve(both, SharedMail, null));

        var none = new EmailReminderCadenceService.CadenceMap(
            HasAllRoles: false, AllRoles: null,
            PerRole: new Dictionary<ParticipantRole, int?>());
        Assert.Equal(shipped, EmailReminderCadenceService.Resolve(none, SharedMail, ParticipantRole.Sponsor));

        // 🔑 A per-role NULL means "this role hears it once, ever" — a real setting, not "unset". It
        // must NOT fall through to the all-roles value, or "once only" would be impossible to
        // express for a single role. PRESENCE decides, never the value.
        var roleOnce = new EmailReminderCadenceService.CadenceMap(
            HasAllRoles: true, AllRoles: 14,
            PerRole: new Dictionary<ParticipantRole, int?> { [ParticipantRole.Sponsor] = null });
        Assert.Null(EmailReminderCadenceService.Resolve(roleOnce, SharedMail, ParticipantRole.Sponsor));

        // …and the same rule one level up: an all-roles row storing null means "once, ever" for
        // everyone, and must not fall through to the shipped default.
        var allOnce = new EmailReminderCadenceService.CadenceMap(
            HasAllRoles: true, AllRoles: null,
            PerRole: new Dictionary<ParticipantRole, int?>());
        Assert.Null(EmailReminderCadenceService.Resolve(allOnce, SharedMail, ParticipantRole.Speaker));
    }

    /// <summary>
    /// 🔒 The all-roles row keeps its OWN uniqueness. §705.3b's trap: EF's default filtered index on a
    /// nullable key column constrains only the role-specific rows and lets all-roles rows multiply,
    /// after which resolution picks one arbitrarily. The upsert must keep updating the same row.
    /// </summary>
    [Fact]
    public async Task Setting_the_all_roles_value_twice_updates_one_row()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await NewEventAsync(db);
        var svc = NewService(db);

        await svc.SetIntervalAsync(eventId, SharedMail, 14, "org@example.test");
        await svc.SetIntervalAsync(eventId, SharedMail, 21, "org@example.test");

        var rows = await db.EmailReminderCadences
            .Where(c => c.EventId == eventId && c.TemplateKey == SharedMail && c.Role == null)
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal(21, rows[0].IntervalDays);
        Assert.Equal(21, await svc.GetIntervalDaysAsync(eventId, SharedMail));
    }
}
