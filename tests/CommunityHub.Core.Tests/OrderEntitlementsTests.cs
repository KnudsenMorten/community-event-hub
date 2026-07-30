using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for the order-entitlement single source of truth
/// (<see cref="OrderEntitlements"/>) and the dedup-once counting service
/// (<see cref="OrderCountService"/>). The entitlement rules are pure (no DB);
/// the counting test uses the EF Core InMemory provider. Speaker-hat rules
/// follow the §299 6.2 category matrix (Community / Sponsor / Guest / null)
/// with DERIVED presenting days (<see cref="SpeakerDays"/>, §299 C5).
/// </summary>
public sealed class OrderEntitlementsTests
{
    private static Participant P(
        ParticipantRole role,
        bool isBoothMember = false,
        int id = 0)
        => new()
        {
            Id = id,
            EventId = 1,
            Email = "p@example.test",
            FullName = "P",
            Role = role,
            IsBoothMember = isBoothMember,
        };

    private static SpeakerProfile Speaker(
        SpeakerCategory? category,
        int? guestNights = null)
        => new()
        {
            EventId = 1,
            Category = category,
            GuestFundedNights = guestNights,
        };

    private static SpeakerDays Days(bool preDay = false, bool mainDay = false)
        => new(preDay, mainDay);

    [Fact]
    public void Community_speaker_gets_the_full_set_including_both_lunches()
    {
        var p = P(ParticipantRole.Speaker);
        var set = OrderEntitlements.Base(
            p, Speaker(SpeakerCategory.Community), Days(preDay: true, mainDay: true));

        Assert.Equal(
            new[]
            {
                OrderItem.Polo, OrderItem.Swag, OrderItem.Award, OrderItem.Hotel,
                OrderItem.TravelReimbursement, OrderItem.AppreciationDinner,
                OrderItem.LunchPreDay, OrderItem.LunchMainDay,
            }.ToHashSet(),
            set.ToHashSet());
    }

    [Fact]
    public void Community_speaker_main_day_only_still_gets_preday_lunch()
    {
        // §294 (operator 2026-07-11): ANY speaker can participate in the pre-day, so pre-day
        // lunch is granted regardless of which days they present (they opt in/out on the form).
        var p = P(ParticipantRole.Speaker);
        var set = OrderEntitlements.Base(
            p, Speaker(SpeakerCategory.Community), Days(preDay: false, mainDay: true));

        Assert.Contains(OrderItem.LunchMainDay, set);
        Assert.Contains(OrderItem.LunchPreDay, set);
    }

    [Fact]
    public void Community_speaker_with_no_linked_sessions_gets_no_main_day_lunch()
    {
        // §299 C5: main-day lunch follows the DERIVED days — no linked main-day
        // session (SpeakerDays.None) means no main-day lunch (yet).
        var p = P(ParticipantRole.Speaker);
        var set = OrderEntitlements.Base(p, Speaker(SpeakerCategory.Community), SpeakerDays.None);

        Assert.DoesNotContain(OrderItem.LunchMainDay, set);
        Assert.Contains(OrderItem.LunchPreDay, set);   // §294 — always
        Assert.Contains(OrderItem.Polo, set);
    }

    [Fact]
    public void Guest_speaker_is_community_minus_travel_reimbursement()
    {
        // §299 6.2 summary rule: Guest is IDENTICAL to Community, except the
        // travel-reimbursement option/tasks must never be presented to a Guest.
        var p = P(ParticipantRole.Speaker);
        var days = Days(preDay: true, mainDay: true);

        var community = OrderEntitlements.Base(p, Speaker(SpeakerCategory.Community), days);
        var guest = OrderEntitlements.Base(p, Speaker(SpeakerCategory.Guest), days);

        var expected = community.ToHashSet();
        expected.Remove(OrderItem.TravelReimbursement);
        Assert.Equal(expected, guest.ToHashSet());
        Assert.DoesNotContain(OrderItem.TravelReimbursement, guest);
    }

    [Fact]
    public void Sponsor_category_speaker_gets_dinner_and_both_lunches()
    {
        // §294: pre-day lunch is open to any speaker, so a sponsor-category speaker gets
        // main-day lunch (sponsor base) PLUS pre-day lunch.
        var p = P(ParticipantRole.Speaker);
        var set = OrderEntitlements.Base(
            p, Speaker(SpeakerCategory.Sponsor), Days(preDay: true, mainDay: true));

        Assert.Equal(
            new[] { OrderItem.AppreciationDinner, OrderItem.LunchMainDay, OrderItem.LunchPreDay }.ToHashSet(),
            set.ToHashSet());
    }

    [Fact]
    public void Uncategorized_speaker_contributes_nothing_from_the_speaker_hat()
    {
        // §299 6.1: a null Category means the speaker hat grants NOTHING — a
        // pure-Attendee role wearing an uncategorized speaker hat ends up empty,
        // proving the hat itself adds nothing (the role hat is empty here).
        var p = P(ParticipantRole.Attendee);
        var set = OrderEntitlements.Base(
            p, Speaker(category: null), Days(preDay: true, mainDay: true));

        Assert.Empty(set);
    }

    [Fact]
    public void Organizer_with_uncategorized_speaker_hat_keeps_organizer_set_only()
    {
        // The migrated legacy SpeakerFunding.Organizer value = null Category:
        // exactly the Organizer-role set, nothing EXTRA from the speaker hat
        // (e.g. no Award/Travel which a Community speaker would have). Frozen
        // pending ❓OPEN-22 SamePersonAsId linking.
        var p = P(ParticipantRole.Organizer);
        var set = OrderEntitlements.Base(
            p, Speaker(category: null), Days(preDay: true, mainDay: true));

        Assert.Equal(
            new[]
            {
                OrderItem.Polo, OrderItem.Swag, OrderItem.Hotel, OrderItem.AppreciationDinner,
                OrderItem.LunchPreDay, OrderItem.LunchMainDay,
            }.ToHashSet(),
            set.ToHashSet());
        Assert.Contains(OrderItem.Hotel, set);
        Assert.DoesNotContain(OrderItem.Award, set);
        Assert.DoesNotContain(OrderItem.TravelReimbursement, set);
    }

    [Fact]
    public void Media_role_set_is_polo_swag_hotel_dinner_both_lunches_no_travel()
    {
        // §299 7.2 + OPEN-27 (operator 2026-07-23): polo + swag like volunteers, hotel,
        // dinner, lunch BOTH days; never the travel-reimbursement task. Asserted PER ROLE
        // (not shared with EventPartner) — the two populations may diverge independently.
        var set = OrderEntitlements.Base(P(ParticipantRole.Media), speaker: null, days: null);

        Assert.Equal(
            new[]
            {
                OrderItem.Polo, OrderItem.Swag, OrderItem.Hotel, OrderItem.AppreciationDinner,
                OrderItem.LunchPreDay, OrderItem.LunchMainDay,
            }.ToHashSet(),
            set.ToHashSet());
        Assert.DoesNotContain(OrderItem.TravelReimbursement, set);
    }

    [Fact]
    public void EventPartner_role_set_is_polo_swag_hotel_dinner_both_lunches_no_travel()
    {
        // §299 7.3 + OPEN-27: identical to Media TODAY, but deliberately asserted as its
        // OWN role — a change to one must never silently ride along on the other.
        var set = OrderEntitlements.Base(P(ParticipantRole.EventPartner), speaker: null, days: null);

        Assert.Equal(
            new[]
            {
                OrderItem.Polo, OrderItem.Swag, OrderItem.Hotel, OrderItem.AppreciationDinner,
                OrderItem.LunchPreDay, OrderItem.LunchMainDay,
            }.ToHashSet(),
            set.ToHashSet());
        Assert.DoesNotContain(OrderItem.TravelReimbursement, set);
    }

    [Fact]
    public void Booth_member_sponsor_gets_polo_and_main_lunch_but_NOT_dinner()
    {
        // Booth members are NOT invited to the appreciation dinner (operator
        // 2026-06-22). A booth member who also speaks gets it via the speaker hat
        // (covered by Booth_member_who_also_speaks_* below).
        var set = OrderEntitlements.Base(
            P(ParticipantRole.Sponsor, isBoothMember: true), speaker: null, days: null);

        Assert.Equal(
            new[] { OrderItem.Polo, OrderItem.LunchMainDay }.ToHashSet(),
            set.ToHashSet());
        Assert.DoesNotContain(OrderItem.AppreciationDinner, set);
    }

    [Fact]
    public void NonBooth_sponsor_with_no_speaker_hat_gets_nothing()
    {
        var set = OrderEntitlements.Base(
            P(ParticipantRole.Sponsor, isBoothMember: false), speaker: null, days: null);

        Assert.Empty(set);
    }

    [Fact]
    public void Digital_sponsor_with_sponsor_category_preday_speaker_gets_dinner_and_preday_lunch()
    {
        // Role=Sponsor, IsSigner, NOT a booth member, BUT a Sponsor-category
        // SpeakerProfile. The sponsor hat adds nothing; the speaker hat adds dinner +
        // main-day lunch (sponsor base) + pre-day lunch (§294: any speaker can join pre-day).
        var p = P(ParticipantRole.Sponsor, isBoothMember: false);
        p.IsSigner = true;
        var set = OrderEntitlements.Base(
            p, Speaker(SpeakerCategory.Sponsor), Days(preDay: true, mainDay: false));

        Assert.Equal(
            new[] { OrderItem.AppreciationDinner, OrderItem.LunchMainDay, OrderItem.LunchPreDay }.ToHashSet(),
            set.ToHashSet());
    }

    [Fact]
    public void Override_include_adds_and_exclude_removes()
    {
        var p = P(ParticipantRole.Attendee, id: 7); // base = nothing
        var overrides = new[]
        {
            new ParticipantOrderOverride
            {
                EventId = 1, ParticipantId = 7, Item = OrderItem.Polo, Include = true,
            },
            new ParticipantOrderOverride
            {
                // Exclude something they don't have (no-op) + something they do.
                EventId = 1, ParticipantId = 7, Item = OrderItem.Swag, Include = true,
            },
            new ParticipantOrderOverride
            {
                EventId = 1, ParticipantId = 7, Item = OrderItem.Swag, Include = false,
            },
            new ParticipantOrderOverride
            {
                // Belongs to another participant — must be ignored.
                EventId = 1, ParticipantId = 99, Item = OrderItem.Hotel, Include = true,
            },
        };

        var set = OrderEntitlements.Effective(p, speaker: null, days: null, overrides);

        Assert.Equal(new[] { OrderItem.Polo }.ToHashSet(), set.ToHashSet());
    }

    [Fact]
    public void Booth_member_who_also_speaks_is_counted_once_for_polo()
    {
        // Union of speaker (Community) + booth-sponsor hats: both grant Polo, but
        // the set is deduped so Polo appears once.
        var p = P(ParticipantRole.Sponsor, isBoothMember: true);
        var set = OrderEntitlements.Base(
            p, Speaker(SpeakerCategory.Community), Days(mainDay: true));

        Assert.Single(set, OrderItem.Polo);
        // And it still has speaker-only items (Award/Hotel) from the union.
        Assert.Contains(OrderItem.Award, set);
        Assert.Contains(OrderItem.Hotel, set);
    }

    // --- SpeakerDayScope funded quantities (§299 6.3) ----------------------

    [Theory]
    [InlineData(true, true, 2)]   // presents pre-day + main day → 2 polos
    [InlineData(false, true, 1)]  // main day only → 1
    [InlineData(true, false, 1)]  // pre-day only → 1
    [InlineData(false, false, 0)] // no linked sessions → 0 (not a default 1)
    public void Funded_polos_for_community_speaker_follow_distinct_presenting_days(
        bool preDay, bool mainDay, int expected)
    {
        Assert.Equal(expected, SpeakerDayScope.FundedPolos(
            SpeakerCategory.Community, Days(preDay, mainDay)));
        // Guest speakers follow the same distinct-day polo rule.
        Assert.Equal(expected, SpeakerDayScope.FundedPolos(
            SpeakerCategory.Guest, Days(preDay, mainDay)));
    }

    [Fact]
    public void Funded_polos_are_zero_for_sponsor_and_uncategorized_speakers()
    {
        var bothDays = Days(preDay: true, mainDay: true);
        Assert.Equal(0, SpeakerDayScope.FundedPolos(SpeakerCategory.Sponsor, bothDays));
        Assert.Equal(0, SpeakerDayScope.FundedPolos(null, bothDays));
    }

    [Fact]
    public void Funded_hotel_nights_follow_category_rules()
    {
        var bothDays = Days(preDay: true, mainDay: true);

        // Community: matches the polo rule (one night per distinct presenting day).
        Assert.Equal(2, SpeakerDayScope.FundedHotelNights(SpeakerCategory.Community, bothDays, guestFundedNights: null));
        Assert.Equal(1, SpeakerDayScope.FundedHotelNights(SpeakerCategory.Community, Days(mainDay: true), guestFundedNights: null));
        Assert.Equal(0, SpeakerDayScope.FundedHotelNights(SpeakerCategory.Community, SpeakerDays.None, guestFundedNights: null));

        // Guest: the ORGANIZER-ENTERED nights (individual agreement), never the schedule.
        Assert.Equal(3, SpeakerDayScope.FundedHotelNights(SpeakerCategory.Guest, Days(mainDay: true), guestFundedNights: 3));
        Assert.Equal(0, SpeakerDayScope.FundedHotelNights(SpeakerCategory.Guest, bothDays, guestFundedNights: null));

        // Sponsor + uncategorized: nothing, ever.
        Assert.Equal(0, SpeakerDayScope.FundedHotelNights(SpeakerCategory.Sponsor, bothDays, guestFundedNights: 3));
        Assert.Equal(0, SpeakerDayScope.FundedHotelNights(null, bothDays, guestFundedNights: 3));
    }

    // --- Derived presenting days (§299 C5) ---------------------------------

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"orders-{Guid.NewGuid():N}")
            .Options);

    private static Event Eldk27Event() => new()
    {
        Id = 1, Code = "OE27", CommunityName = "C", DisplayName = "C 2027",
        StartDate = new DateOnly(2027, 2, 10), EndDate = new DateOnly(2027, 2, 10),
        PreDayDate = new DateOnly(2027, 2, 9),
        IsActive = true,
    };

    [Fact]
    public async Task Days_derive_from_linked_sessions_including_non_masterclass_on_preday()
    {
        using var db = NewDb();
        db.Events.Add(Eldk27Event());

        // s10: a MasterClass (untimed) + a scheduled main-day session → BOTH days.
        // s11: a NON-MasterClass session scheduled ON PreDayDate → pre-day only
        //      (a non-MC session on the pre-day counts as pre-day, not main day).
        // s12: an unscheduled technical session → main day (typed only).
        // s13: only a SERVICE session → never counts (no entry at all).
        // s14: a UsedForTesting session → still counts (real rehearsal content).
        var mc = new Session
        {
            Id = 1, EventId = 1, SessionizeId = "mc", Title = "MC",
            Type = SessionType.MasterClass,
        };
        var mainDay = new Session
        {
            Id = 2, EventId = 1, SessionizeId = "main", Title = "Main",
            Type = SessionType.TechnicalSession,
            StartsAt = new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.FromHours(1)),
        };
        var preDayNonMc = new Session
        {
            Id = 3, EventId = 1, SessionizeId = "pre", Title = "Pre-day panel",
            Type = SessionType.PanelDiscussion,
            StartsAt = new DateTimeOffset(2027, 2, 9, 13, 0, 0, TimeSpan.FromHours(1)),
        };
        var untimed = new Session
        {
            Id = 4, EventId = 1, SessionizeId = "untimed", Title = "Untimed",
            Type = SessionType.TechnicalSession,
        };
        var service = new Session
        {
            Id = 5, EventId = 1, SessionizeId = "svc", Title = "Lunch break",
            Type = SessionType.TechnicalSession, IsServiceSession = true,
            StartsAt = new DateTimeOffset(2027, 2, 10, 12, 0, 0, TimeSpan.FromHours(1)),
        };
        var testing = new Session
        {
            Id = 6, EventId = 1, SessionizeId = "test", Title = "Rehearsal",
            Type = SessionType.TechnicalSession, UsedForTesting = true,
            StartsAt = new DateTimeOffset(2027, 2, 10, 15, 0, 0, TimeSpan.FromHours(1)),
        };
        db.Sessions.AddRange(mc, mainDay, preDayNonMc, untimed, service, testing);
        db.SessionSpeakers.AddRange(
            new SessionSpeaker { SessionId = 1, ParticipantId = 10 },
            new SessionSpeaker { SessionId = 2, ParticipantId = 10 },
            new SessionSpeaker { SessionId = 3, ParticipantId = 11 },
            new SessionSpeaker { SessionId = 4, ParticipantId = 12 },
            new SessionSpeaker { SessionId = 5, ParticipantId = 13 },
            new SessionSpeaker { SessionId = 6, ParticipantId = 14 });
        await db.SaveChangesAsync();

        var days = await SpeakerDayScope.DaysBySpeakerAsync(db, 1);

        Assert.Equal(new SpeakerDays(true, true), days[10]);
        Assert.Equal(2, days[10].DistinctFundedDays);

        Assert.Equal(new SpeakerDays(true, false), days[11]);
        Assert.Equal(1, days[11].DistinctFundedDays);

        Assert.Equal(new SpeakerDays(false, true), days[12]);

        Assert.False(days.ContainsKey(13)); // service sessions never count

        Assert.Equal(new SpeakerDays(false, true), days[14]); // testing counts

        // The per-speaker loader agrees with the batch loader (and misses = None).
        Assert.Equal(days[11], await SpeakerDayScope.DaysForSpeakerAsync(db, 1, 11));
        Assert.Equal(SpeakerDays.None, await SpeakerDayScope.DaysForSpeakerAsync(db, 1, 13));
        Assert.Equal(SpeakerDays.None, await SpeakerDayScope.DaysForSpeakerAsync(db, 1, 999));
    }

    // --- OrderCountService -------------------------------------------------

    [Fact]
    public async Task Count_dedups_a_same_person_duplicate()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = 1, Code = "OE27", CommunityName = "C", DisplayName = "C 2027",
            StartDate = new DateOnly(2027, 2, 1), EndDate = new DateOnly(2027, 2, 2),
            IsActive = true,
        });

        // Primary volunteer (gets Polo) + a DUPLICATE row pointing at them.
        var primary = new Participant
        {
            Id = 10, EventId = 1, Email = "vol@example.test", FullName = "Vol",
            Role = ParticipantRole.Volunteer,
        };
        var duplicate = new Participant
        {
            Id = 11, EventId = 1, Email = "vol.alt@example.test", FullName = "Vol Alt",
            Role = ParticipantRole.Volunteer, SamePersonAsId = 10,
        };
        // An independent organizer (also gets Polo).
        var org = new Participant
        {
            Id = 12, EventId = 1, Email = "org@expertslive.dk", FullName = "Org",
            Role = ParticipantRole.Organizer,
        };
        db.Participants.AddRange(primary, duplicate, org);
        await db.SaveChangesAsync();

        var svc = new OrderCountService(db);
        var counts = await svc.CountsAsync(1);

        // Two DISTINCT people entitled to Polo (primary volunteer + organizer);
        // the duplicate row is NOT counted on its own.
        Assert.Equal(2, counts[OrderItem.Polo]);

        var byItem = await svc.EntitledByItemAsync(1);
        Assert.DoesNotContain(byItem[OrderItem.Polo], e => e.ParticipantId == 11);
        Assert.Contains(byItem[OrderItem.Polo], e => e.ParticipantId == 10);
        Assert.Contains(byItem[OrderItem.Polo], e => e.ParticipantId == 12);
    }

    [Fact]
    public async Task Count_excludes_uncategorized_speakers_and_gates_main_day_lunch_on_derived_days()
    {
        using var db = NewDb();
        db.Events.Add(Eldk27Event());

        // A Community speaker presenting a scheduled main-day session, and an
        // UNCATEGORIZED speaker presenting the same day (excluded from counts).
        db.Participants.AddRange(
            new Participant
            {
                Id = 10, EventId = 1, Email = "comm@example.test", FullName = "Comm",
                Role = ParticipantRole.Speaker,
            },
            new Participant
            {
                Id = 11, EventId = 1, Email = "uncat@example.test", FullName = "Uncat",
                Role = ParticipantRole.Speaker,
            });
        db.SpeakerProfiles.AddRange(
            new SpeakerProfile { Id = 1, EventId = 1, ParticipantId = 10, Category = SpeakerCategory.Community },
            new SpeakerProfile { Id = 2, EventId = 1, ParticipantId = 11, Category = null });
        db.Sessions.Add(new Session
        {
            Id = 1, EventId = 1, SessionizeId = "s", Title = "S",
            Type = SessionType.TechnicalSession,
            StartsAt = new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.FromHours(1)),
        });
        db.SessionSpeakers.AddRange(
            new SessionSpeaker { SessionId = 1, ParticipantId = 10 },
            new SessionSpeaker { SessionId = 1, ParticipantId = 11 });
        await db.SaveChangesAsync();

        var counts = await new OrderCountService(db).CountsAsync(1);

        // Only the Community speaker counts — for polo AND for the derived
        // main-day lunch; the uncategorized speaker contributes to NOTHING.
        Assert.Equal(1, counts[OrderItem.Polo]);
        Assert.Equal(1, counts[OrderItem.LunchMainDay]);
        Assert.Equal(1, counts[OrderItem.TravelReimbursement]);
        Assert.Equal(1, counts[OrderItem.AppreciationDinner]);
    }
}
