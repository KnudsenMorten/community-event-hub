using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §940 — <b>RING 1 IMPLIES <c>IsTestUser</c></b>. Operator 2026-08-07: *"when a person, nomatter the
/// role becomes a ring 1 user (test), you must set the flag IsTestUser = true"*.
///
/// <para>🔴 The bug: the flag was set by NAMING CONVENTION (<c>test-*@</c>) in the SQL seeds and by
/// nothing at all in the application, so a role-simulation account named anything else was a Ring-1
/// participant that <see cref="Integrations.TestDataScope"/> read as a real person — and could be
/// announced on the company page. These tests pin the rule at EVERY entry point that can put someone
/// on Ring 1, because "one entry point missed" is exactly how participant 116 happened.</para>
/// </summary>
public sealed class Ring1ImpliesTestUserTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"ring1testuser-{Guid.NewGuid():N}")
            .Options);

    private static Participant Person(
        string email, ParticipantRole role, Ring ring = Rings.Default,
        string? companyId = null, bool isTestUser = false) =>
        new()
        {
            EventId = EventId, FullName = email, Email = email, Role = role, Ring = ring,
            SponsorCompanyId = companyId, IsActive = true, IsTestUser = isTestUser,
        };

    private static async Task<int> SeedAsync(CommunityHubDbContext db, Participant p)
    {
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static ParticipantBulkOperationService NewBulk(CommunityHubDbContext db) =>
        new(db, new ParticipantDeactivationService(
            db, TimeProvider.System,
            new CommunityHub.Core.Audit.AuditTrailService(db, TimeProvider.System)));

    private static SpeakerApprovalService NewApproval(CommunityHubDbContext db) =>
        new(db, new FeatureGateService(db), TimeProvider.System);

    // ---------------------------------------------------------------- the rule itself

    [Theory]
    [InlineData(Ring.Ring0, false)]
    [InlineData(Ring.Ring1, true)]
    [InlineData(Ring.Ring2, false)]
    [InlineData(Ring.Broad, false)]
    public void Only_Ring1_flags_the_person(Ring ring, bool expected)
    {
        var p = Person("x@example.test", ParticipantRole.Volunteer);

        TestUserRule.AssignRing(p, ring);

        Assert.Equal(ring, p.Ring);
        Assert.Equal(expected, p.IsTestUser);
    }

    /// <summary>
    /// 🔒 ONE-WAY, and this is the test that says so. Moving OFF Ring 1 must NOT clear the flag:
    /// auto-clearing would promote a row created as test data into the real exports and onto the
    /// public surfaces — the same failure as §940, pointed the other way.
    /// </summary>
    [Fact]
    public void Moving_off_Ring1_does_not_clear_the_flag()
    {
        var p = Person("y@example.test", ParticipantRole.Speaker, Ring.Ring1, isTestUser: true);

        TestUserRule.AssignRing(p, Ring.Broad);

        Assert.Equal(Ring.Broad, p.Ring);
        Assert.True(p.IsTestUser);
    }

    /// <summary>
    /// The REPAIR case — participant 116 exactly. Someone already sitting on Ring 1 with the flag
    /// missing is reported as CHANGED, so re-applying the ring they already have fixes them instead
    /// of short-circuiting on "no ring change".
    /// </summary>
    [Fact]
    public void Re_assigning_the_same_Ring1_repairs_a_missing_flag_and_counts_as_a_change()
    {
        var p = Person("z@example.test", ParticipantRole.Volunteer, Ring.Ring1);

        Assert.True(TestUserRule.AssignRing(p, Ring.Ring1));
        Assert.True(p.IsTestUser);
        // Second pass: nothing left to do.
        Assert.False(TestUserRule.AssignRing(p, Ring.Ring1));
    }

    // ---------------------------------------------------------------- entry point 1: ring admin

    [Fact]
    public async Task ResourceRingService_flags_a_participant_moved_to_Ring1()
    {
        using var db = NewDb();
        var id = await SeedAsync(db, Person("vol@example.test", ParticipantRole.Volunteer));
        var svc = new ResourceRingService(db, TimeProvider.System);

        Assert.True(await svc.SetParticipantRingAsync(EventId, id, Ring.Ring1));

        var after = await db.Participants.FirstAsync(x => x.Id == id);
        Assert.Equal(Ring.Ring1, after.Ring);
        Assert.True(after.IsTestUser);
    }

    /// <summary>Every role, not just the ones that happen to be seeded — his words were "nomatter the role".</summary>
    [Theory]
    [InlineData(ParticipantRole.Speaker)]
    [InlineData(ParticipantRole.Volunteer)]
    [InlineData(ParticipantRole.Sponsor)]
    [InlineData(ParticipantRole.Attendee)]
    public async Task ResourceRingService_flags_every_role(ParticipantRole role)
    {
        using var db = NewDb();
        var id = await SeedAsync(db, Person($"{role}@example.test", role));

        await new ResourceRingService(db, TimeProvider.System)
            .SetParticipantRingAsync(EventId, id, Ring.Ring1);

        Assert.True((await db.Participants.FirstAsync(x => x.Id == id)).IsTestUser);
    }

    // ---------------------------------------------------------------- entry point 2: the company ring

    /// <summary>
    /// A sponsor COMPANY moved to Ring 1 makes its inheriting contacts Ring-1 PEOPLE — their own
    /// column never changes, but their effective ring does, and that is what every gate reads.
    /// </summary>
    [Fact]
    public async Task A_company_on_Ring1_flags_the_contacts_that_inherit_it()
    {
        using var db = NewDb();
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "c1", Ring = Rings.Default,
        });
        var inheriting = Person("inherit@example.test", ParticipantRole.Sponsor, Rings.Default, "c1");
        var narrowed = Person("narrow@example.test", ParticipantRole.Sponsor, Ring.Ring2, "c1");
        var elsewhere = Person("other@example.test", ParticipantRole.Sponsor, Rings.Default, "c2");
        db.Participants.AddRange(inheriting, narrowed, elsewhere);
        await db.SaveChangesAsync();

        Assert.True(await new ResourceRingService(db, TimeProvider.System)
            .SetSponsorCompanyRingAsync(EventId, "c1", Ring.Ring1, "op@example.test"));

        Assert.True((await db.Participants.FirstAsync(x => x.Email == "inherit@example.test")).IsTestUser);
        // Explicitly narrowed to Ring 2 ⇒ the contact ring wins, so they are NOT a Ring-1 person.
        Assert.False((await db.Participants.FirstAsync(x => x.Email == "narrow@example.test")).IsTestUser);
        // Another company entirely is untouched.
        Assert.False((await db.Participants.FirstAsync(x => x.Email == "other@example.test")).IsTestUser);
    }

    // ---------------------------------------------------------------- entry point 3: the bulk action

    [Fact]
    public async Task Bulk_ring_assignment_flags_everyone_it_moves_to_Ring1()
    {
        using var db = NewDb();
        var a = await SeedAsync(db, Person("a@example.test", ParticipantRole.Volunteer));
        var b = await SeedAsync(db, Person("b@example.test", ParticipantRole.Speaker));

        var result = await NewBulk(db)
            .SetRingAsync(EventId, new[] { a, b }, Ring.Ring1);

        Assert.Equal(2, result.Changed);
        Assert.All(await db.Participants.ToListAsync(), p => Assert.True(p.IsTestUser));
    }

    /// <summary>The bulk path is also the repair path — a selection already on Ring 1 gets fixed.</summary>
    [Fact]
    public async Task Bulk_ring_assignment_repairs_an_unflagged_Ring1_row()
    {
        using var db = NewDb();
        var id = await SeedAsync(db, Person("c@example.test", ParticipantRole.Volunteer, Ring.Ring1));

        var result = await NewBulk(db)
            .SetRingAsync(EventId, new[] { id }, Ring.Ring1);

        Assert.Equal(1, result.Changed);
        Assert.True((await db.Participants.FirstAsync(x => x.Id == id)).IsTestUser);
    }

    [Fact]
    public async Task Bulk_ring_assignment_to_a_non_test_ring_flags_nobody()
    {
        using var db = NewDb();
        var id = await SeedAsync(db, Person("d@example.test", ParticipantRole.Attendee));

        await NewBulk(db).SetRingAsync(EventId, new[] { id }, Ring.Ring2);

        var after = await db.Participants.FirstAsync(x => x.Id == id);
        Assert.Equal(Ring.Ring2, after.Ring);
        Assert.False(after.IsTestUser);
    }

    // ---------------------------------------------------------------- entry point 4: speaker approval

    [Fact]
    public async Task Approving_a_speaker_into_Ring1_flags_them()
    {
        using var db = NewDb();
        var id = await SeedAsync(db, Person("spk@example.test", ParticipantRole.Speaker));
        db.SpeakerProfiles.Add(new SpeakerProfile { EventId = EventId, ParticipantId = id });
        await db.SaveChangesAsync();

        Assert.True(await NewApproval(db)
            .ApproveAsync(EventId, id, SpeakerCategory.Community, Ring.Ring1));

        var after = await db.Participants.FirstAsync(x => x.Id == id);
        Assert.Equal(Ring.Ring1, after.Ring);
        Assert.True(after.IsTestUser);
    }

    /// <summary>
    /// 🔒 The regression that would hurt most: his ordinary one-click approval is Ring 3, and it must
    /// keep flagging NOBODY. A rule that over-reaches here would mark every approved speaker as test
    /// data and erase them from the campaign.
    /// </summary>
    [Fact]
    public async Task Approving_a_speaker_into_the_default_ring_flags_nobody()
    {
        using var db = NewDb();
        var id = await SeedAsync(db, Person("spk2@example.test", ParticipantRole.Speaker));
        db.SpeakerProfiles.Add(new SpeakerProfile { EventId = EventId, ParticipantId = id });
        await db.SaveChangesAsync();

        await NewApproval(db)
            .ApproveAsync(EventId, id, SpeakerCategory.Community, Rings.Default);

        Assert.False((await db.Participants.FirstAsync(x => x.Id == id)).IsTestUser);
    }
}
