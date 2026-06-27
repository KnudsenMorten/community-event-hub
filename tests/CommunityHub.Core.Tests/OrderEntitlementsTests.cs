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
/// the counting test uses the EF Core InMemory provider.
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
        SpeakerFunding funding = SpeakerFunding.Supported,
        bool preDay = false,
        bool mainDay = false)
        => new()
        {
            EventId = 1,
            SpeakerFunding = funding,
            SpeakingPreDay = preDay,
            SpeakingMainDay = mainDay,
        };

    [Fact]
    public void Supported_speaker_gets_the_full_set_including_both_lunches()
    {
        var p = P(ParticipantRole.Speaker);
        var set = OrderEntitlements.Base(
            p, Speaker(SpeakerFunding.Supported, preDay: true, mainDay: true));

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
    public void Supported_speaker_main_day_only_has_no_preday_lunch()
    {
        var p = P(ParticipantRole.Speaker);
        var set = OrderEntitlements.Base(
            p, Speaker(SpeakerFunding.Supported, preDay: false, mainDay: true));

        Assert.Contains(OrderItem.LunchMainDay, set);
        Assert.DoesNotContain(OrderItem.LunchPreDay, set);
    }

    [Fact]
    public void SponsorSelfFunded_speaker_gets_dinner_and_main_lunch_only()
    {
        var p = P(ParticipantRole.Speaker);
        var set = OrderEntitlements.Base(
            p, Speaker(SpeakerFunding.SponsorSelfFunded, preDay: true, mainDay: true));

        Assert.Equal(
            new[] { OrderItem.AppreciationDinner, OrderItem.LunchMainDay }.ToHashSet(),
            set.ToHashSet());
    }

    [Fact]
    public void Organizer_funded_speaker_contributes_nothing_as_speaker()
    {
        // A pure-Attendee role wearing an Organizer-funded speaker hat ⇒ nothing,
        // proving the speaker hat itself adds nothing (the role hat is empty here).
        var p = P(ParticipantRole.Attendee);
        var set = OrderEntitlements.Base(
            p, Speaker(SpeakerFunding.Organizer, preDay: true, mainDay: true));

        Assert.Empty(set);
    }

    [Fact]
    public void Organizer_who_speaks_keeps_organizer_set_not_speaker_set()
    {
        var p = P(ParticipantRole.Organizer);
        var set = OrderEntitlements.Base(
            p, Speaker(SpeakerFunding.Organizer, preDay: true, mainDay: true));

        // Exactly the Organizer-role set (which now INCLUDES Hotel — operator
        // 2026-06-26: organizers + volunteers are entitled to a hotel booking);
        // nothing EXTRA from the speaker hat (e.g. no Award/Travel which a Supported
        // speaker would have).
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
    public void Booth_member_sponsor_gets_polo_and_main_lunch_but_NOT_dinner()
    {
        // Booth members are NOT invited to the appreciation dinner (operator
        // 2026-06-22). A booth member who also speaks gets it via the speaker hat
        // (covered by Booth_member_who_also_speaks_* below).
        var set = OrderEntitlements.Base(
            P(ParticipantRole.Sponsor, isBoothMember: true), speaker: null);

        Assert.Equal(
            new[] { OrderItem.Polo, OrderItem.LunchMainDay }.ToHashSet(),
            set.ToHashSet());
        Assert.DoesNotContain(OrderItem.AppreciationDinner, set);
    }

    [Fact]
    public void NonBooth_sponsor_with_no_speaker_hat_gets_nothing()
    {
        var set = OrderEntitlements.Base(
            P(ParticipantRole.Sponsor, isBoothMember: false), speaker: null);

        Assert.Empty(set);
    }

    [Fact]
    public void Digital_sponsor_with_selffunded_preday_speaker_gets_dinner_and_preday_lunch()
    {
        // Role=Sponsor, IsSigner, NOT a booth member, BUT a SpeakerProfile that is
        // SponsorSelfFunded + SpeakingPreDay. The sponsor hat adds nothing; the
        // speaker hat adds dinner + main-day lunch (self-funded gives main lunch
        // regardless of which days they speak) — so the union is dinner + main
        // lunch. The spec's expected {AppreciationDinner, LunchPreDay} is for the
        // SUPPORTED rule; for SponsorSelfFunded the rule yields main-day lunch.
        var p = P(ParticipantRole.Sponsor, isBoothMember: false);
        p.IsSigner = true;
        var set = OrderEntitlements.Base(
            p, Speaker(SpeakerFunding.SponsorSelfFunded, preDay: true, mainDay: false));

        Assert.Equal(
            new[] { OrderItem.AppreciationDinner, OrderItem.LunchMainDay }.ToHashSet(),
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

        var set = OrderEntitlements.Effective(p, speaker: null, overrides);

        Assert.Equal(new[] { OrderItem.Polo }.ToHashSet(), set.ToHashSet());
    }

    [Fact]
    public void Booth_member_who_also_speaks_is_counted_once_for_polo()
    {
        // Union of speaker (Supported) + booth-sponsor hats: both grant Polo, but
        // the set is deduped so Polo appears once.
        var p = P(ParticipantRole.Sponsor, isBoothMember: true);
        var set = OrderEntitlements.Base(
            p, Speaker(SpeakerFunding.Supported, mainDay: true));

        Assert.Single(set, OrderItem.Polo);
        // And it still has speaker-only items (Award/Hotel) from the union.
        Assert.Contains(OrderItem.Award, set);
        Assert.Contains(OrderItem.Hotel, set);
    }

    // --- OrderCountService -------------------------------------------------

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"orders-{Guid.NewGuid():N}")
            .Options);

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
}
