using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using Xunit;

namespace CommunityHub.Core.Tests.Entitlements;

/// <summary>
/// §1296 — the PURE hotel-coverage rule: how many nights Experts Live Denmark pays, and how many
/// the person pays themselves (operator 2026-09-22).
/// </summary>
/// <remarks>
/// <para>🔑 <b>The arithmetic is the contract</b>, because five surfaces render it — the hotel form,
/// the person's organizer page, the hotel-assignments list, the per-hotel Excel and the confirmation
/// mail. Pinning it here is what lets those five be thin.</para>
///
/// <para>Replaces <c>OrderEntitlementsTests.Funded_hotel_nights_follow_category_rules</c>, which
/// pinned the retired speaker-only rule.</para>
/// </remarks>
public sealed class HotelCoverageTests
{
    private static SpeakerDays Days(bool preDay = false, bool mainDay = false) => new(preDay, mainDay);

    // -----------------------------------------------------------------
    //  The defaults the operator settled on 2026-09-22
    // -----------------------------------------------------------------

    [Fact]
    public void Default_standards_match_what_was_agreed()
    {
        Assert.Equal(HotelCoverageRule.OfNights(2), HotelCoverage.DefaultFor(HotelCoverageClass.SpeakerMasterClass));
        // "speaker pre-day only we cover 2 nights" — deliberately the same as the master class.
        Assert.Equal(HotelCoverageRule.OfNights(2), HotelCoverage.DefaultFor(HotelCoverageClass.SpeakerPreDayOnly));
        Assert.Equal(HotelCoverageRule.OfNights(1), HotelCoverage.DefaultFor(HotelCoverageClass.SpeakerMainOnly));
        // "guest speakers we pay for (max 6 days)".
        Assert.Equal(HotelCoverageRule.OfNights(6), HotelCoverage.DefaultFor(HotelCoverageClass.SpeakerGuest));
        Assert.Equal(HotelCoverageMode.Nothing, HotelCoverage.DefaultFor(HotelCoverageClass.SpeakerSponsor).Mode);
        Assert.Equal(HotelCoverageRule.OfNights(1), HotelCoverage.DefaultFor(HotelCoverageClass.Volunteer));
        // "event partners we cover all nights they choose, media the same, organizers the same".
        Assert.Equal(HotelCoverageMode.WholeStay, HotelCoverage.DefaultFor(HotelCoverageClass.Organizer).Mode);
        Assert.Equal(HotelCoverageMode.WholeStay, HotelCoverage.DefaultFor(HotelCoverageClass.Media).Mode);
        Assert.Equal(HotelCoverageMode.WholeStay, HotelCoverage.DefaultFor(HotelCoverageClass.EventPartner).Mode);
    }

    // -----------------------------------------------------------------
    //  Classification
    // -----------------------------------------------------------------

    [Fact]
    public void Speakers_are_classified_by_the_days_they_present()
    {
        Assert.Equal(HotelCoverageClass.SpeakerMasterClass,
            HotelCoverage.ClassFor(ParticipantRole.Speaker, SpeakerCategory.Community, Days(preDay: true, mainDay: true)));
        Assert.Equal(HotelCoverageClass.SpeakerPreDayOnly,
            HotelCoverage.ClassFor(ParticipantRole.Speaker, SpeakerCategory.Community, Days(preDay: true)));
        Assert.Equal(HotelCoverageClass.SpeakerMainOnly,
            HotelCoverage.ClassFor(ParticipantRole.Speaker, SpeakerCategory.Community, Days(mainDay: true)));

        // Category beats the schedule for Guest + Sponsor — their coverage is not about which days
        // they happen to present.
        Assert.Equal(HotelCoverageClass.SpeakerGuest,
            HotelCoverage.ClassFor(ParticipantRole.Speaker, SpeakerCategory.Guest, Days(preDay: true, mainDay: true)));
        Assert.Equal(HotelCoverageClass.SpeakerSponsor,
            HotelCoverage.ClassFor(ParticipantRole.Speaker, SpeakerCategory.Sponsor, Days(mainDay: true)));
    }

    /// <summary>
    /// A speaker with no linked sessions is treated as MAIN-DAY, not as "nothing". An unfinished
    /// agenda is not a decision to stop paying for somebody's room, and the organizer page shows
    /// which class it used.
    /// </summary>
    [Fact]
    public void Community_speaker_with_no_sessions_falls_back_to_main_day()
    {
        Assert.Equal(HotelCoverageClass.SpeakerMainOnly,
            HotelCoverage.ClassFor(ParticipantRole.Speaker, SpeakerCategory.Community, SpeakerDays.None));
    }

    [Fact]
    public void Crew_roles_have_their_own_classes_and_the_rest_have_none()
    {
        Assert.Equal(HotelCoverageClass.Volunteer, HotelCoverage.ClassFor(ParticipantRole.Volunteer, null, SpeakerDays.None));
        Assert.Equal(HotelCoverageClass.Organizer, HotelCoverage.ClassFor(ParticipantRole.Organizer, null, SpeakerDays.None));
        Assert.Equal(HotelCoverageClass.Media, HotelCoverage.ClassFor(ParticipantRole.Media, null, SpeakerDays.None));
        Assert.Equal(HotelCoverageClass.EventPartner, HotelCoverage.ClassFor(ParticipantRole.EventPartner, null, SpeakerDays.None));

        // Attendees and sponsors arrange their own accommodation; an UNCATEGORIZED speaker is
        // excluded from every tally until an organizer categorizes them (§299 6.1).
        Assert.Null(HotelCoverage.ClassFor(ParticipantRole.Attendee, null, SpeakerDays.None));
        Assert.Null(HotelCoverage.ClassFor(ParticipantRole.Sponsor, null, SpeakerDays.None));
        Assert.Null(HotelCoverage.ClassFor(ParticipantRole.Speaker, null, Days(mainDay: true)));
    }

    // -----------------------------------------------------------------
    //  The arithmetic
    // -----------------------------------------------------------------

    [Fact]
    public void Covered_is_the_standard_plus_extra_and_the_rest_is_their_own_expense()
    {
        var r = HotelCoverage.Resolve(HotelCoverageRule.OfNights(1), extraNights: 2, bookedNights: 4);

        Assert.Equal(4, r.Booked);
        Assert.Equal(1, r.StandardNights);
        Assert.Equal(2, r.Extra);
        Assert.Equal(3, r.Covered);
        Assert.Equal(1, r.OwnExpense);
        Assert.True(r.HasOwnExpense);
    }

    /// <summary>
    /// 🔑 "Max 6 days" needed no special mode: the clamp IS the cap. This is the case that made the
    /// whole-stay-with-a-limit idea unnecessary.
    /// </summary>
    [Theory]
    [InlineData(3, 3, 0)]    // guest books 3 of their 6 → 3 covered, nothing owed
    [InlineData(6, 6, 0)]    // exactly the cap
    [InlineData(8, 6, 2)]    // over the cap → they pay the difference
    public void A_night_standard_caps_at_what_they_actually_booked(int booked, int covered, int own)
    {
        var r = HotelCoverage.Resolve(HotelCoverageRule.OfNights(6), extraNights: 0, bookedNights: booked);
        Assert.Equal(covered, r.Covered);
        Assert.Equal(own, r.OwnExpense);
    }

    [Fact]
    public void Whole_stay_covers_every_booked_night_however_many()
    {
        var r = HotelCoverage.Resolve(HotelCoverageRule.WholeStay, extraNights: 0, bookedNights: 9);
        Assert.Equal(9, r.Covered);
        Assert.Equal(0, r.OwnExpense);
        Assert.True(r.FullyCovered);
        // "The standard" is not a number for a whole-stay class, and the record says so rather than
        // inventing one for the read-out.
        Assert.Null(r.StandardNights);
    }

    /// <summary>
    /// "Nothing" is a STANDARD, not a veto: an organizer who types extra nights for a sponsor-brought
    /// speaker has decided to cover them, and a control that accepted the input and discarded it
    /// would be the worst kind.
    /// </summary>
    [Fact]
    public void Extra_nights_still_apply_to_a_not_covered_class()
    {
        var none = HotelCoverage.Resolve(HotelCoverageRule.Nothing, extraNights: 0, bookedNights: 3);
        Assert.Equal(0, none.Covered);
        Assert.Equal(3, none.OwnExpense);

        var granted = HotelCoverage.Resolve(HotelCoverageRule.Nothing, extraNights: 2, bookedNights: 3);
        Assert.Equal(2, granted.Covered);
        Assert.Equal(1, granted.OwnExpense);
    }

    [Fact]
    public void No_booking_means_nothing_covered_and_nothing_owed()
    {
        var r = HotelCoverage.Resolve(HotelCoverageRule.OfNights(2), extraNights: 5, bookedNights: 0);
        Assert.Equal(0, r.Booked);
        Assert.Equal(0, r.Covered);
        Assert.Equal(0, r.OwnExpense);
        Assert.True(r.FullyCovered);   // a person with no room cannot have a hotel bill
    }

    [Fact]
    public void Negative_input_is_treated_as_zero_rather_than_flipping_the_sums()
    {
        var r = HotelCoverage.Resolve(HotelCoverageRule.OfNights(1), extraNights: -5, bookedNights: 2);
        Assert.Equal(1, r.Covered);
        Assert.Equal(1, r.OwnExpense);
    }

    // -----------------------------------------------------------------
    //  Nights between dates — the number that becomes money
    // -----------------------------------------------------------------

    [Fact]
    public void Nights_between_counts_nights_not_days_and_never_guesses()
    {
        Assert.Equal(1, HotelCoverage.NightsBetween(new DateOnly(2027, 2, 9), new DateOnly(2027, 2, 10)));
        Assert.Equal(6, HotelCoverage.NightsBetween(new DateOnly(2027, 2, 5), new DateOnly(2027, 2, 11)));

        // Missing or inverted ⇒ 0. An unknown stay stands out as zero rather than being invented.
        Assert.Equal(0, HotelCoverage.NightsBetween(null, new DateOnly(2027, 2, 10)));
        Assert.Equal(0, HotelCoverage.NightsBetween(new DateOnly(2027, 2, 9), null));
        Assert.Equal(0, HotelCoverage.NightsBetween(new DateOnly(2027, 2, 10), new DateOnly(2027, 2, 9)));
        Assert.Equal(0, HotelCoverage.NightsBetween(new DateOnly(2027, 2, 9), new DateOnly(2027, 2, 9)));
    }

    [Fact]
    public void Describe_reads_as_a_phrase_including_the_singular()
    {
        Assert.Equal("1 night", HotelCoverage.Describe(HotelCoverageRule.OfNights(1)));
        Assert.Equal("2 nights", HotelCoverage.Describe(HotelCoverageRule.OfNights(2)));
        Assert.Equal("whole stay", HotelCoverage.Describe(HotelCoverageRule.WholeStay));
        Assert.Equal("not covered", HotelCoverage.Describe(HotelCoverageRule.Nothing));
    }
}
