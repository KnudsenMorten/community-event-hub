using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1077 — the daily re-check: what it records, what it must NOT undo, and that a re-run is safe.
/// </summary>
public sealed class VolumePackageSweepTests
{
    private const int EventId = 42;
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 3, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vps-{Guid.NewGuid():N}").Options);

    private static async Task<CommunityHubDbContext> SeedAsync(int people, Action<VolumePackageCompany>? tweak = null)
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "D", IsActive = true,
        });

        var company = new VolumePackageCompany
        {
            Id = 1, EventId = EventId, CustomName = "Globeteam", Domains = "globeteam.dk",
        };
        tweak?.Invoke(company);
        db.VolumePackageCompanies.Add(company);

        for (var i = 1; i <= people; i++)
        {
            db.Attendees.Add(new Attendee
            {
                EventId = EventId, Email = $"p{i}@globeteam.dk",
                BackstageTicketId = $"T{i}", MirrorState = MirrorState.Active,
            });
        }

        await db.SaveChangesAsync();
        return db;
    }

    private static VolumePackageSweep Sweep(CommunityHubDbContext db) =>
        new(db, new VolumePackageQualificationService(db), new FixedClock(Now));

    [Fact]
    public async Task A_qualifying_company_is_recorded_with_a_snapshot()
    {
        using var db = await SeedAsync(10);

        var r = await Sweep(db).RunAsync(EventId);

        Assert.Equal(1, r.Qualified);
        Assert.Equal(1, r.NewlyQualified);

        var company = await db.VolumePackageCompanies.FirstAsync();
        Assert.True(company.QualifiedNow);
        Assert.Equal(10, company.LastQualifiedCount);
        Assert.Equal(Now, company.FirstQualifiedAt);

        var snap = await db.VolumePackageQualificationSnapshots.SingleAsync();
        Assert.Equal(10, snap.AttendeeCount);
        Assert.True(snap.Qualified);
    }

    /// <summary>
    /// 🔒 A RE-RUN MUST NOT APPEND A SECOND ANSWER FOR THE SAME DAY. The job is safe to trigger by
    /// hand and safe to retry — without this, one manual click doubles the history and every "how
    /// many days did they qualify" question is wrong afterwards.
    /// </summary>
    [Fact]
    public async Task Running_twice_in_one_day_updates_rather_than_duplicates()
    {
        using var db = await SeedAsync(10);

        await Sweep(db).RunAsync(EventId);
        await Sweep(db).RunAsync(EventId);

        Assert.Single(await db.VolumePackageQualificationSnapshots.ToListAsync());
    }

    /// <summary>
    /// 🔴 STICKY APPROVAL. Dropping to nine flips <c>QualifiedNow</c> and REPORTS the drop — but must
    /// never clear an approval an organizer already gave. ⚠️ Otherwise one cancellation silently
    /// removes a company from a keynote slide that is already being designed.
    /// </summary>
    [Fact]
    public async Task Dropping_below_ten_reports_it_but_never_revokes_an_approval()
    {
        using var db = await SeedAsync(10, c =>
        {
            c.BenefitsApprovedAt = Now.AddDays(-3);
            c.BenefitsApprovedByEmail = "organizer@test.dk";
        });
        await Sweep(db).RunAsync(EventId);

        // One attendee cancels.
        var one = await db.Attendees.FirstAsync();
        one.MirrorState = MirrorState.Cancelled;
        await db.SaveChangesAsync();

        var r = await Sweep(db).RunAsync(EventId);

        Assert.Equal(1, r.DroppedOut);
        var company = await db.VolumePackageCompanies.FirstAsync();
        Assert.False(company.QualifiedNow);
        Assert.Equal(9, company.LastQualifiedCount);

        // 🔒 The approval stands, and so does the record that they once qualified.
        Assert.NotNull(company.BenefitsApprovedAt);
        Assert.Equal("organizer@test.dk", company.BenefitsApprovedByEmail);
        Assert.NotNull(company.FirstQualifiedAt);
    }

    /// <summary>
    /// The approver suggestion: the buyer who brought the most attendees. ⚠️ Two buyers on the same
    /// domain, so this measures "most tickets" rather than "first found".
    /// </summary>
    [Fact]
    public async Task The_suggested_approver_is_the_buyer_with_the_most_attendees()
    {
        using var db = await SeedAsync(0);
        db.Orders.Add(new Order { EventId = EventId, BackstageOrderId = "O-small", BuyerEmail = "small@globeteam.dk", BuyerName = "Small Buyer", MirrorState = MirrorState.Active });
        db.Orders.Add(new Order { EventId = EventId, BackstageOrderId = "O-big", BuyerEmail = "big@globeteam.dk", BuyerName = "Big Buyer", MirrorState = MirrorState.Active });
        for (var i = 0; i < 2; i++)
            db.Attendees.Add(new Attendee { EventId = EventId, Email = $"s{i}@x.dk", OrderId = "O-small", BackstageTicketId = $"S{i}", MirrorState = MirrorState.Active });
        for (var i = 0; i < 7; i++)
            db.Attendees.Add(new Attendee { EventId = EventId, Email = $"b{i}@x.dk", OrderId = "O-big", BackstageTicketId = $"B{i}", MirrorState = MirrorState.Active });
        await db.SaveChangesAsync();

        var s = await Sweep(db).SuggestApproverAsync(1);

        Assert.NotNull(s);
        Assert.Equal("big@globeteam.dk", s!.Value.Email);
        Assert.Equal(7, s.Value.Tickets);
    }

    /// <summary>
    /// ⚠️ A company that qualified purely on shared e-mail domains has NO purchaser — ten freelancers
    /// who each paid for themselves. Returning null is the honest answer; inventing one would put a
    /// stranger's name in front of an organizer as a recommendation to approve.
    /// </summary>
    [Fact]
    public async Task No_approver_is_suggested_when_nobody_bought_anything()
    {
        using var db = await SeedAsync(10);

        Assert.Null(await Sweep(db).SuggestApproverAsync(1));
    }
}
