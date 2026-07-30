using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §705.3b — the Settings page's view of per-role rings, and the audit-derived role lists behind it.
/// </summary>
public sealed class EmailTemplateRingStateRoleTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"ringstate-{Guid.NewGuid():N}")
            .Options);

    private static EmailTemplateRingService NewService(CommunityHubDbContext db) =>
        new(db, new FeatureGateService(db), new FixedClock(Now));

    /// <summary>
    /// 🔒 THE ONE THAT WOULD HAVE 500'd THE PAGE. Reading the rings for the Settings page used to be a
    /// <c>ToDictionary</c> keyed on TemplateKey alone — which throws a duplicate-key exception the moment
    /// a template has BOTH an all-roles row and a per-role row. The very first per-role ring would have
    /// taken the whole page down.
    /// </summary>
    [Fact]
    public async Task GetAll_survives_a_template_with_both_an_all_roles_and_per_role_ring()
    {
        using var db = NewDb();
        var svc = NewService(db);
        const string mail = "task-allocation-committed";

        await svc.SetRingAsync(EventId, mail, Ring.Ring1, "op@test");                                     // all roles
        await svc.SetRingAsync(EventId, mail, Ring.Broad, "op@test", default, ParticipantRole.Organizer);  // one role
        await svc.SetRingAsync(EventId, mail, Ring.Ring1, "op@test", default, ParticipantRole.Volunteer);  // another

        var all = await svc.GetAllAsync(EventId);          // must not throw
        var row = all.Single(s => s.TemplateKey == mail);

        Assert.Equal(Ring.Ring1, row.OverrideRing);        // the all-roles ring
        Assert.Equal(2, row.RoleRings.Count);
        Assert.Equal(Ring.Broad, row.RoleRings[ParticipantRole.Organizer]);
        Assert.Equal(Ring.Ring1, row.RoleRings[ParticipantRole.Volunteer]);
    }

    /// <summary>A mail with only an all-roles ring reports no per-role rows — nothing to show.</summary>
    [Fact]
    public async Task A_mail_with_only_an_all_roles_ring_has_no_role_rows()
    {
        using var db = NewDb();
        var svc = NewService(db);

        await svc.SetRingAsync(EventId, "welcome-speaker", Ring.Ring2, "op@test");

        var row = (await svc.GetAllAsync(EventId)).Single(s => s.TemplateKey == "welcome-speaker");
        Assert.Empty(row.RoleRings);
        Assert.Equal(Ring.Ring2, row.OverrideRing);
    }

    /// <summary>
    /// §705.9 — the role lists come from what the SENDERS actually do. These are the multi-role mails the
    /// audit found; getting one wrong means offering a ring for a role that never receives the mail, or
    /// hiding one that does.
    /// </summary>
    [Theory]
    [InlineData("task-deadline-reminder", 7)]              // every role owns tasks (PartyTaskSeeder alone spans six + attendees)
    [InlineData("onboarding-getting-started", 6)]          // 5 persona sets; attendees get none
    [InlineData("getstarted-digest", 3)]                   // Speaker + Sponsor + Attendee
    [InlineData("welcome", 2)]                             // the roles with no welcome-{role} variant
    [InlineData("masterclass-question-posted", 2)]         // speakers of the class + signed-up attendees
    [InlineData("task-allocation-committed", 2)]           // volunteers + organizers
    public void The_audited_multi_role_mails_report_their_roles(string key, int expected) =>
        Assert.Equal(expected, EmailTemplateCatalog.RecipientRolesFor(key).Count);

    /// <summary>
    /// 🔴 The mis-classification the audit caught: `getstarted-digest` was filed under Speakers while its
    /// builder branches on Speaker, Sponsor AND Attendee — one row naming one role and mailing three.
    /// </summary>
    [Fact]
    public void getstarted_digest_reaches_sponsors_and_attendees_not_just_speakers()
    {
        var roles = EmailTemplateCatalog.RecipientRolesFor("getstarted-digest");

        Assert.Contains(ParticipantRole.Speaker, roles);
        Assert.Contains(ParticipantRole.Sponsor, roles);
        Assert.Contains(ParticipantRole.Attendee, roles);
    }

    /// <summary>
    /// A single-role mail reports exactly its one role, so the page shows no per-role controls for it.
    /// Seven dropdowns where six govern nothing is the §326bx defect.
    /// </summary>
    [Theory]
    [InlineData("welcome-speaker", ParticipantRole.Speaker)]
    [InlineData("welcome-sponsor", ParticipantRole.Sponsor)]
    [InlineData("app-game-gift-reminder", ParticipantRole.Sponsor)]
    [InlineData("hotel-cutoff-reminder", ParticipantRole.Organizer)]
    public void A_single_role_mail_reports_exactly_that_role(string key, ParticipantRole expected) =>
        Assert.Equal(new[] { expected }, EmailTemplateCatalog.RecipientRolesFor(key));

    /// <summary>
    /// 🔒 A RING-EXEMPT mail reports NO roles — no ring applies to it at all, so the page must not offer
    /// per-role rings for it either. Offering one would be the same "control that governs nothing" defect
    /// in a new place.
    /// </summary>
    [Theory]
    [InlineData("pin-signin")]
    [InlineData("calendar-invite")]
    [InlineData("some-published")]
    public void A_ring_exempt_mail_reports_no_roles(string key) =>
        Assert.Empty(EmailTemplateCatalog.RecipientRolesFor(key));

    /// <summary>
    /// Every role a mail claims to reach must be a REAL role — a stale enum value would render a control
    /// for a role that no longer exists. (The enum has an intentional gap where MasterclassSpeaker was
    /// dropped, which is exactly the sort of value that could creep in.)
    /// </summary>
    [Fact]
    public void Every_declared_role_is_a_real_ParticipantRole()
    {
        var valid = Enum.GetValues<ParticipantRole>().ToHashSet();

        foreach (var key in EmailTemplateCatalog.Map.Keys)
        {
            foreach (var role in EmailTemplateCatalog.RecipientRolesFor(key))
            {
                Assert.Contains(role, valid);
            }
        }
    }
}
