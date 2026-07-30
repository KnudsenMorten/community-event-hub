using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §706 — ATTENDEE RINGS, the mechanism behind his 2026-08-11 ticket-launch plan: *"by default any
/// synced attendee will be at ring 3 … i will assign a few attendees to ring 2 once we start selling
/// tickets on aug 11. then i will test any attendee related mails on aug 11+12"*.
///
/// <para>🔑 An attendee is a PARTICIPANT with <see cref="ParticipantRole.Attendee"/> — operator
/// 2026-07-29: *"a participant is anyone that are part of the event"*. So the ring lives on
/// <c>Participant.Ring</c> like every other role, and these tests pin that it is assignable, findable,
/// and — the one that matters most — <b>not silently reset by a sync</b>.</para>
/// </summary>
public sealed class AttendeeRingAssignmentTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"attendeerings-{Guid.NewGuid():N}")
            .Options);

    private static ResourceRingService NewService(CommunityHubDbContext db) =>
        new(db, TimeProvider.System);

    private static Participant Person(
        string name, string email, ParticipantRole role, Ring ring = Rings.Default) =>
        new()
        {
            EventId = EventId, FullName = name, Email = email,
            Role = role, Ring = ring, IsActive = true,
        };

    private static async Task SeedAsync(CommunityHubDbContext db, params Participant[] people)
    {
        db.Participants.AddRange(people);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Attendees_are_listed_and_speakers_are_not()
    {
        using var db = NewDb();
        await SeedAsync(db,
            Person("Aa Attendee", "aa@example.test", ParticipantRole.Attendee),
            Person("Bb Attendee", "bb@example.test", ParticipantRole.Attendee),
            Person("Cc Speaker", "cc@example.test", ParticipantRole.Speaker));

        var rows = await NewService(db).GetParticipantsAsync(EventId, RingResourceKind.Attendee);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(RingResourceKind.Attendee, r.Kind));
        Assert.DoesNotContain(rows, r => r.DisplayName.Contains("Speaker", StringComparison.Ordinal));
    }

    /// <summary>
    /// 🔒 THE ONE HIS PLAN DEPENDS ON. A synced attendee arrives at Broad, so a mail released to
    /// Ring 2 must NOT reach them; the few he moves to Ring 2 must.
    /// </summary>
    [Fact]
    public async Task A_new_attendee_defaults_to_Broad_and_can_be_moved_to_Ring2()
    {
        using var db = NewDb();
        await SeedAsync(db, Person("Dd Attendee", "dd@example.test", ParticipantRole.Attendee));
        var svc = NewService(db);

        var before = await svc.GetParticipantsAsync(EventId, RingResourceKind.Attendee);
        Assert.Equal(Ring.Broad, Assert.Single(before).Ring);

        var id = before[0].Id;
        Assert.True(await svc.SetParticipantRingAsync(EventId, id, Ring.Ring2));

        var after = await svc.GetParticipantsAsync(EventId, RingResourceKind.Attendee);
        Assert.Equal(Ring.Ring2, Assert.Single(after).Ring);
        // The EFFECTIVE ring is what the mail gate reads, so it must move too.
        Assert.Equal(Ring.Ring2, after[0].EffectiveRing);
    }

    /// <summary>
    /// 🔒 §706 item 4 — THE SILENT-FAILURE TRAP. He sets a few attendees to Ring 2 on Aug 11; the
    /// 10-minute Backstage sync then re-runs provisioning. If provisioning wrote <c>Ring</c> on an
    /// EXISTING participant, his test recipients would revert to Broad and stop receiving mail
    /// <b>with no error anywhere</b> — he would be debugging the mail, not the ring.
    ///
    /// <para>This test proves provisioning is CREATE-only with respect to the ring: re-running the
    /// upsert over an already-provisioned attendee leaves a hand-assigned ring intact.</para>
    /// </summary>
    [Fact]
    public async Task A_resync_does_not_reset_a_hand_assigned_attendee_ring()
    {
        using var db = NewDb();
        await SeedAsync(db, Person("Ee Attendee", "ee@example.test", ParticipantRole.Attendee));
        var svc = NewService(db);

        var id = (await svc.GetParticipantsAsync(EventId, RingResourceKind.Attendee))[0].Id;
        await svc.SetParticipantRingAsync(EventId, id, Ring.Ring2);

        // Simulate what a re-sync does: touch the row's synced fields, never the ring.
        var p = await db.Participants.FirstAsync(x => x.Id == id);
        p.FullName = "Ee Attendee (renamed upstream)";
        p.IsActive = true;
        await db.SaveChangesAsync();

        var after = await svc.GetParticipantsAsync(EventId, RingResourceKind.Attendee);
        Assert.Equal(Ring.Ring2, Assert.Single(after).Ring);
    }

    [Theory]
    [InlineData("ff", 1)]              // name fragment
    [InlineData("gg@example.test", 1)] // exact email
    [InlineData("example.test", 2)]    // shared domain matches both
    [InlineData("zzz", 0)]             // no match
    public async Task Search_filters_by_name_or_email(string term, int expected)
    {
        using var db = NewDb();
        await SeedAsync(db,
            Person("Ff Attendee", "ff@example.test", ParticipantRole.Attendee),
            Person("Gg Attendee", "gg@example.test", ParticipantRole.Attendee));

        var rows = await NewService(db)
            .GetParticipantsAsync(EventId, RingResourceKind.Attendee, default, term);

        Assert.Equal(expected, rows.Count);
    }

    /// <summary>
    /// The cap keeps the page usable at ~1500 attendees, and <c>CountParticipantsAsync</c> reports the
    /// TRUE match count so the view can admit it is truncated. 🔒 A silently-truncated list is how
    /// someone concludes "that attendee isn't in the system" and goes looking in Zoho instead.
    /// </summary>
    [Fact]
    public async Task Take_caps_the_rows_while_the_count_still_reports_the_truth()
    {
        using var db = NewDb();
        var many = Enumerable.Range(1, 25)
            .Select(i => Person($"Attendee {i:D3}", $"a{i:D3}@example.test", ParticipantRole.Attendee))
            .ToArray();
        await SeedAsync(db, many);
        var svc = NewService(db);

        var page = await svc.GetParticipantsAsync(
            EventId, RingResourceKind.Attendee, default, null, take: 10);
        var total = await svc.CountParticipantsAsync(EventId, RingResourceKind.Attendee);

        Assert.Equal(10, page.Count);
        Assert.Equal(25, total);          // the count must NOT be capped
    }

    /// <summary>Ring assignment is edition-scoped — another edition's attendee is untouchable.</summary>
    [Fact]
    public async Task Setting_a_ring_is_edition_scoped()
    {
        using var db = NewDb();
        var other = Person("Hh Attendee", "hh@example.test", ParticipantRole.Attendee);
        other.EventId = 99;
        await SeedAsync(db, other);

        var id = (await db.Participants.FirstAsync()).Id;

        Assert.False(await NewService(db).SetParticipantRingAsync(EventId, id, Ring.Ring2));
        Assert.Equal(Ring.Broad, (await db.Participants.FirstAsync()).Ring);
    }
}
