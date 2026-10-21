using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using CommunityHub.Core.Reminders;
using CommunityHub.Notify;
using CommunityHub.Core.Resources;
using CommunityHub.Forms.Steps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1296 — the hotel form tells a person, WHILE THEY REGISTER, how many nights Experts Live Denmark
/// covers and how many they pay themselves (operator 2026-09-22: <i>"we need to inform the people if
/// they choose more nights than eldk covers so they know they must cover rest themselves"</i> ·
/// <i>"for example in the gui"</i> · <i>"when they register hotel"</i>).
/// </summary>
/// <remarks>
/// <para>These drive <see cref="HotelFormService"/>, so they pin the WIRING — the rule and the
/// person's extra nights reaching the model the <c>_HotelFields</c> partial renders. The arithmetic
/// itself is pinned in Core (<c>HotelCoverageTests</c>), and the partial is shared by the standalone
/// form, the Get Started step and the inline task row, so getting it onto the model is what puts it
/// on all three.</para>
/// </remarks>
public sealed class HotelCoverageFormTests
{
    private const int EventId = 1;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-22T09:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"hotel-cov-form-{Guid.NewGuid():N}").Options);

    private static IStringLocalizer<SharedResource> Loc() =>
        new StringLocalizer<SharedResource>(
            new ResourceManagerStringLocalizerFactory(
                Microsoft.Extensions.Options.Options.Create(new LocalizationOptions()),
                NullLoggerFactory.Instance));

    private static HotelFormService NewSvc(CommunityHubDbContext db)
    {
        var clock = new FixedClock();
        return new HotelFormService(
            db, clock, new OrganizerActionItemService(db, clock), Loc(),
            NullLogger<HotelFormService>.Instance,
            new HotelCoverageService(db, clock));
    }

    private static async Task<int> SeedAsync(
        CommunityHubDbContext db, ParticipantRole role,
        DateOnly? checkIn = null, DateOnly? checkOut = null,
        SpeakerCategory? category = null)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "C27", CommunityName = "C", DisplayName = "C 2027", IsActive = true,
            PreDayDate = new DateOnly(2027, 2, 8),
            StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 9),
        });
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = EventId, Email = "person@example.test", FullName = "Ada Lovelace",
            Role = role, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        if (role == ParticipantRole.Speaker)
        {
            db.SpeakerProfiles.Add(new SpeakerProfile
            {
                EventId = EventId, ParticipantId = p.Id, Category = category,
            });
            await db.SaveChangesAsync();
        }

        if (checkIn is not null)
        {
            db.HotelBookings.Add(new HotelBooking
            {
                EventId = EventId, ParticipantId = p.Id, NeedsRoom = true,
                CheckInDate = checkIn, CheckOutDate = checkOut,
            });
            await db.SaveChangesAsync();
        }

        return p.Id;
    }

    [Fact]
    public async Task A_volunteer_booking_three_nights_is_told_they_pay_two()
    {
        using var db = NewDb();
        var pid = await SeedAsync(db, ParticipantRole.Volunteer,
            new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 11));

        var model = await NewSvc(db).LoadAsync(EventId, pid, ParticipantRole.Volunteer, default);

        Assert.Equal(HotelCoverageClass.Volunteer, model.CoverageClass);
        Assert.Equal(3, model.Coverage.Booked);
        Assert.Equal(1, model.Coverage.Covered);
        Assert.Equal(2, model.Coverage.OwnExpense);
        Assert.True(model.Coverage.HasOwnExpense);   // ⇒ the form shows the warning half
    }

    /// <summary>
    /// The numbers follow the dates the person is TYPING, not only the ones already saved — the
    /// model recomputes, and the same arithmetic runs client-side while they edit.
    /// </summary>
    [Fact]
    public async Task The_split_follows_the_dates_currently_in_the_form()
    {
        using var db = NewDb();
        var pid = await SeedAsync(db, ParticipantRole.Volunteer,
            new DateOnly(2027, 2, 9), new DateOnly(2027, 2, 10));   // saved: 1 night

        var model = await NewSvc(db).LoadAsync(EventId, pid, ParticipantRole.Volunteer, default);
        Assert.True(model.Coverage.FullyCovered);                   // 1 booked, 1 covered

        // They extend their stay in the form, before saving.
        var extended = model.CoverageFor(new DateOnly(2027, 2, 6), new DateOnly(2027, 2, 11));
        Assert.Equal(5, extended.Booked);
        Assert.Equal(1, extended.Covered);
        Assert.Equal(4, extended.OwnExpense);
    }

    [Fact]
    public async Task Extra_nights_granted_by_an_organizer_reach_the_form()
    {
        using var db = NewDb();
        var pid = await SeedAsync(db, ParticipantRole.Volunteer,
            new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 11));   // 3 nights

        await new HotelCoverageService(db, new FixedClock())
            .SetExtraNightsAsync(EventId, pid, 2, "agreed", "org@example.test");

        var model = await NewSvc(db).LoadAsync(EventId, pid, ParticipantRole.Volunteer, default);

        Assert.Equal(2, model.CoverageExtraNights);
        Assert.Equal(3, model.Coverage.Covered);
        Assert.True(model.Coverage.FullyCovered);   // ⇒ the form says "all of them are covered"
    }

    /// <summary>
    /// An organizer is a whole-stay class, so however long they book there is never an own-expense
    /// warning — the case the form must NOT get wrong in the alarming direction.
    /// </summary>
    [Fact]
    public async Task A_whole_stay_class_is_never_warned()
    {
        using var db = NewDb();
        var pid = await SeedAsync(db, ParticipantRole.Organizer,
            new DateOnly(2027, 2, 4), new DateOnly(2027, 2, 12));   // 8 nights

        var model = await NewSvc(db).LoadAsync(EventId, pid, ParticipantRole.Organizer, default);

        Assert.Equal(HotelCoverageMode.WholeStay, model.CoverageRule.Mode);
        Assert.Equal(8, model.Coverage.Covered);
        Assert.False(model.Coverage.HasOwnExpense);
    }

    /// <summary>
    /// A guest speaker is covered up to six nights — the operator's own rule, seen from the form
    /// the guest actually fills in.
    /// </summary>
    [Fact]
    public async Task A_guest_speaker_over_six_nights_is_told_about_the_rest()
    {
        using var db = NewDb();
        var pid = await SeedAsync(db, ParticipantRole.Speaker,
            new DateOnly(2027, 2, 4), new DateOnly(2027, 2, 12),    // 8 nights
            category: SpeakerCategory.Guest);

        var model = await NewSvc(db).LoadAsync(EventId, pid, ParticipantRole.Speaker, default);

        Assert.Equal(HotelCoverageClass.SpeakerGuest, model.CoverageClass);
        Assert.Equal(6, model.Coverage.Covered);
        Assert.Equal(2, model.Coverage.OwnExpense);
    }

    /// <summary>
    /// 🔒 An UNCATEGORIZED speaker gets no coverage CLASS, so the form makes no claim about their
    /// money at all rather than telling them they pay for everything.
    /// </summary>
    [Fact]
    public async Task An_uncategorized_speaker_is_told_nothing()
    {
        using var db = NewDb();
        var pid = await SeedAsync(db, ParticipantRole.Speaker,
            new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 11), category: null);

        var model = await NewSvc(db).LoadAsync(EventId, pid, ParticipantRole.Speaker, default);

        Assert.Null(model.CoverageClass);   // ⇒ the partial renders no coverage box
    }
}
