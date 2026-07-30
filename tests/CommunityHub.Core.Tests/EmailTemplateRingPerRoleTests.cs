using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §705.3b — <b>THE UNIT OF A RING IS <c>(mail type × role)</c>.</b> Operator 2026-07-29: *"1 mail type
/// to a role = 1 ring gate. that is it. so reminder to speaker is NOT the same as reminder to organizer
/// or reminder to sponsor."*
///
/// <para>One template genuinely serves several roles — <c>task-deadline-reminder</c> reaches all seven —
/// so a single ring per template could never express *"hold the sponsors but release the speakers"*,
/// which is the control he kept asking for and could not find.</para>
///
/// <para>Resolution: <c>(template, role) ?? (template, all-roles) ?? Ring0</c>.</para>
/// </summary>
public sealed class EmailTemplateRingPerRoleTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"ringrole-{Guid.NewGuid():N}")
            .Options);

    private static EmailTemplateRingService NewService(CommunityHubDbContext db) =>
        new(db, new FeatureGateService(db), new FixedClock(Now));

    /// <summary>
    /// 🔑 THE HEADLINE. His exact example: the task reminder released to Ring 2 for speakers and
    /// sponsors, but held at Ring 1 for volunteers — one mail, three different audiences.
    /// </summary>
    [Fact]
    public async Task One_mail_can_hold_a_DIFFERENT_ring_per_role()
    {
        using var db = NewDb();
        var svc = NewService(db);
        const string mail = "task-deadline-reminder";

        await svc.SetRingAsync(EventId, mail, Ring.Ring2, "op@test", default, ParticipantRole.Speaker);
        await svc.SetRingAsync(EventId, mail, Ring.Ring2, "op@test", default, ParticipantRole.Sponsor);
        await svc.SetRingAsync(EventId, mail, Ring.Ring1, "op@test", default, ParticipantRole.Volunteer);

        Assert.Equal(Ring.Ring2, await svc.GetEffectiveRingAsync(EventId, mail, default, ParticipantRole.Speaker));
        Assert.Equal(Ring.Ring2, await svc.GetEffectiveRingAsync(EventId, mail, default, ParticipantRole.Sponsor));
        Assert.Equal(Ring.Ring1, await svc.GetEffectiveRingAsync(EventId, mail, default, ParticipantRole.Volunteer));
    }

    /// <summary>
    /// The all-roles row (<c>Role == null</c>) is the fallback — which is exactly what every row seeded
    /// before this column existed means, so the migration changed no behaviour.
    /// </summary>
    [Fact]
    public async Task An_all_roles_ring_governs_every_role_that_has_no_row_of_its_own()
    {
        using var db = NewDb();
        var svc = NewService(db);
        const string mail = "task-deadline-reminder";

        await svc.SetRingAsync(EventId, mail, Ring.Ring1, "op@test");   // role: null = all roles

        foreach (var role in new[]
                 {
                     ParticipantRole.Speaker, ParticipantRole.Sponsor, ParticipantRole.Volunteer,
                     ParticipantRole.Organizer, ParticipantRole.Attendee,
                 })
        {
            Assert.Equal(Ring.Ring1, await svc.GetEffectiveRingAsync(EventId, mail, default, role));
        }
    }

    /// <summary>
    /// 🔒 SPECIFIC BEATS GENERAL, AND THE OTHERS DO NOT MOVE. This is the §515 trap solved properly:
    /// releasing one role must never drag the rest along.
    /// </summary>
    [Fact]
    public async Task A_role_specific_ring_wins_and_leaves_the_other_roles_alone()
    {
        using var db = NewDb();
        var svc = NewService(db);
        const string mail = "task-deadline-reminder";

        await svc.SetRingAsync(EventId, mail, Ring.Ring1, "op@test");                                  // all roles
        await svc.SetRingAsync(EventId, mail, Ring.Broad, "op@test", default, ParticipantRole.Speaker); // speakers only

        Assert.Equal(Ring.Broad, await svc.GetEffectiveRingAsync(EventId, mail, default, ParticipantRole.Speaker));
        Assert.Equal(Ring.Ring1, await svc.GetEffectiveRingAsync(EventId, mail, default, ParticipantRole.Sponsor));
        Assert.Equal(Ring.Ring1, await svc.GetEffectiveRingAsync(EventId, mail, default, ParticipantRole.Volunteer));
    }

    /// <summary>
    /// 🔒 Setting a per-role ring must ADD a row beside the all-roles one, never overwrite it —
    /// otherwise releasing speakers would silently change every other role's audience.
    /// </summary>
    [Fact]
    public async Task Setting_a_role_ring_does_not_overwrite_the_all_roles_row()
    {
        using var db = NewDb();
        var svc = NewService(db);
        const string mail = "welcome";

        await svc.SetRingAsync(EventId, mail, Ring.Ring1, "op@test");
        await svc.SetRingAsync(EventId, mail, Ring.Broad, "op@test", default, ParticipantRole.Speaker);

        var rows = await db.EmailTemplateRings.Where(r => r.TemplateKey == mail).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Role is null && r.ReleasedToRing == Ring.Ring1);
        Assert.Contains(rows, r => r.Role == ParticipantRole.Speaker && r.ReleasedToRing == Ring.Broad);
    }

    /// <summary>
    /// A recipient whose role cannot be resolved — an attendee with no participant row, an ad-hoc
    /// address — falls to the ALL-ROLES row only. It must never pick up a role-specific ring it was
    /// never meant to have.
    /// </summary>
    [Fact]
    public async Task An_unknown_role_uses_only_the_all_roles_ring()
    {
        using var db = NewDb();
        var svc = NewService(db);
        const string mail = "welcome";

        await svc.SetRingAsync(EventId, mail, Ring.Broad, "op@test", default, ParticipantRole.Speaker);

        // No all-roles row exists, so an unknown role FAILS CLOSED rather than inheriting Broad.
        Assert.Equal(Ring.Ring0, await svc.GetEffectiveRingAsync(EventId, mail, default, null));

        await svc.SetRingAsync(EventId, mail, Ring.Ring1, "op@test");
        Assert.Equal(Ring.Ring1, await svc.GetEffectiveRingAsync(EventId, mail, default, null));
        // ...and the speaker row is still untouched.
        Assert.Equal(Ring.Broad, await svc.GetEffectiveRingAsync(EventId, mail, default, ParticipantRole.Speaker));
    }

    /// <summary>
    /// Clearing a ROLE's ring drops that role back to the all-roles row, and to fail-closed when there
    /// isn't one. Never a one-way door, and never a silent widening.
    /// </summary>
    [Fact]
    public async Task Clearing_a_role_ring_falls_back_to_all_roles_then_to_fail_closed()
    {
        using var db = NewDb();
        var svc = NewService(db);
        const string mail = "welcome";

        await svc.SetRingAsync(EventId, mail, Ring.Ring1, "op@test");
        await svc.SetRingAsync(EventId, mail, Ring.Broad, "op@test", default, ParticipantRole.Speaker);

        Assert.True(await svc.ClearRingAsync(EventId, mail, default, ParticipantRole.Speaker));
        Assert.Equal(Ring.Ring1, await svc.GetEffectiveRingAsync(EventId, mail, default, ParticipantRole.Speaker));

        Assert.True(await svc.ClearRingAsync(EventId, mail));   // the all-roles row
        Assert.Equal(Ring.Ring0, await svc.GetEffectiveRingAsync(EventId, mail, default, ParticipantRole.Speaker));
    }

    /// <summary>Re-setting the same (mail, role) UPSERTS — it must not accumulate duplicate rows.</summary>
    [Fact]
    public async Task Setting_the_same_mail_and_role_twice_upserts()
    {
        using var db = NewDb();
        var svc = NewService(db);
        const string mail = "welcome";

        await svc.SetRingAsync(EventId, mail, Ring.Ring1, "op@test", default, ParticipantRole.Speaker);
        await svc.SetRingAsync(EventId, mail, Ring.Ring2, "op@test", default, ParticipantRole.Speaker);

        var rows = await db.EmailTemplateRings.Where(r => r.TemplateKey == mail).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(Ring.Ring2, rows[0].ReleasedToRing);
    }

    /// <summary>An unknown template key is still refused, per role too — a typo cannot create a row.</summary>
    [Fact]
    public async Task An_unregistered_mail_is_refused_for_a_role_as_well()
    {
        using var db = NewDb();
        var svc = NewService(db);

        Assert.False(await svc.SetRingAsync(
            EventId, "not-a-real-mail", Ring.Ring1, "op@test", default, ParticipantRole.Speaker));
        Assert.Empty(await db.EmailTemplateRings.ToListAsync());
    }
}
