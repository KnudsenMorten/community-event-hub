using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Entitlements;

/// <summary>
/// §1296 — the groups a person's hotel coverage is decided by. One class, one standard, set per
/// edition in <c>/Organizer/Settings/HotelNights</c>.
/// </summary>
/// <remarks>
/// <para>🔑 <b>Speakers split by WHICH DAYS THEY PRESENT, not by what they are called.</b> The days
/// are derived from linked sessions (<see cref="SpeakerDayScope"/>), never from a stored flag, so a
/// speaker who gains a master class simply changes class and their coverage follows without anybody
/// editing them.</para>
///
/// <para>The values are persisted as ints on <c>HotelCoverageStandard.Class</c>, so <b>never reuse a
/// number</b> — a retired class leaves its slot vacant.</para>
/// </remarks>
public enum HotelCoverageClass
{
    /// <summary>Speaker presenting on the pre-day AND the main day (the master-class case).</summary>
    SpeakerMasterClass = 0,

    /// <summary>Speaker presenting on the pre-day only.</summary>
    SpeakerPreDayOnly = 1,

    /// <summary>Speaker presenting on the main day only.</summary>
    SpeakerMainOnly = 2,

    /// <summary>A guest speaker ELDK hires on individual terms.</summary>
    SpeakerGuest = 3,

    /// <summary>A sponsor-brought speaker — the sponsor pays.</summary>
    SpeakerSponsor = 4,

    Volunteer = 5,
    Organizer = 6,
    Media = 7,
    EventPartner = 8,
}

/// <summary>What a class's standard actually grants.</summary>
public enum HotelCoverageMode
{
    /// <summary>A fixed number of nights (<see cref="HotelCoverageRule.Nights"/>).</summary>
    Nights = 0,

    /// <summary>Every night they book — the uncapped classes.</summary>
    WholeStay = 1,

    /// <summary>Nothing is covered; the person pays their own room.</summary>
    Nothing = 2,
}

/// <summary>One class's standard: the "included nights per rule".</summary>
/// <param name="Nights">Only meaningful for <see cref="HotelCoverageMode.Nights"/>.</param>
public readonly record struct HotelCoverageRule(HotelCoverageMode Mode, int Nights)
{
    public static HotelCoverageRule OfNights(int nights) =>
        new(HotelCoverageMode.Nights, nights < 0 ? 0 : nights);

    public static readonly HotelCoverageRule WholeStay = new(HotelCoverageMode.WholeStay, 0);
    public static readonly HotelCoverageRule Nothing = new(HotelCoverageMode.Nothing, 0);
}

/// <summary>
/// The resolved answer for ONE person: how many of their booked nights ELDK pays, and how many they
/// pay themselves. Every surface — the hotel form, the person's organizer page, the hotel
/// assignments list, the hotel Excel and the confirmation mail — renders from this one record.
/// </summary>
/// <param name="Booked">Nights they actually booked (0 when they need no room / have no dates).</param>
/// <param name="StandardNights">
/// What the class standard alone granted, AFTER clamping to the booking — so the read-out can show
/// the arithmetic (<c>standard N + extra M = covered X</c>) rather than only the total. Null for a
/// whole-stay class, where "the standard" is not a number.
/// </param>
/// <param name="Extra">The person's extra nights as entered (before clamping).</param>
/// <param name="Covered">Nights ELDK pays. Never more than <paramref name="Booked"/>.</param>
public readonly record struct HotelCoverageResult(
    int Booked, HotelCoverageMode Mode, int? StandardNights, int Extra, int Covered)
{
    /// <summary>Nights the person pays themselves, directly to the hotel.</summary>
    public int OwnExpense => Booked - Covered;

    /// <summary>True when they booked more than ELDK covers — the case the form must warn about.</summary>
    public bool HasOwnExpense => OwnExpense > 0;

    /// <summary>True when every booked night is covered (including a zero-night booking).</summary>
    public bool FullyCovered => OwnExpense == 0;
}

/// <summary>
/// §1296 — the PURE hotel-coverage arithmetic (operator 2026-09-22: <i>"i need a way to define
/// standards for how many nights we pay hotel and what people have to pay themselves"</i>).
/// </summary>
/// <remarks>
/// <para><b>The whole rule, in one line:</b>
/// <c>covered = clamp(standard + extra, 0, booked)</c>, and <c>ownExpense = booked − covered</c>.</para>
///
/// <para>🔑 <b>COUNTS, NEVER DATES.</b> Operator 2026-09-22: <i>"hotel doesnt need to know which
/// dates, only count."</i> An earlier draft had a covered-date window; it would have had to be
/// chosen, explained to a hotel and re-derived every time somebody moved their check-in. Do not
/// reintroduce dates without him asking.</para>
///
/// <para>🔑 <b>The clamp IS the cap.</b> Guest speakers are <i>"max 6 days"</i>, which needs no
/// special mode: a 6-night standard gives a guest who books 3 nights 3 covered, and one who books 8
/// nights 6 covered + 2 own. "Cover their stay, up to 6" and "a 6-night standard" are the same rule.</para>
/// </remarks>
public static class HotelCoverage
{
    /// <summary>
    /// The catalog defaults — what an edition gets before an organizer touches
    /// <c>/Organizer/Settings/HotelNights</c>. Settled with the operator 2026-09-22.
    /// </summary>
    public static readonly IReadOnlyDictionary<HotelCoverageClass, HotelCoverageRule> Defaults =
        new Dictionary<HotelCoverageClass, HotelCoverageRule>
        {
            // "2 nights for master class" — deliberately the same as pre-day-only.
            [HotelCoverageClass.SpeakerMasterClass] = HotelCoverageRule.OfNights(2),
            [HotelCoverageClass.SpeakerPreDayOnly]  = HotelCoverageRule.OfNights(2),
            [HotelCoverageClass.SpeakerMainOnly]    = HotelCoverageRule.OfNights(1),
            // "guest speakers we pay for (max 6 days)" — the clamp supplies the cap.
            [HotelCoverageClass.SpeakerGuest]       = HotelCoverageRule.OfNights(6),
            // The sponsor pays for their own speaker.
            [HotelCoverageClass.SpeakerSponsor]     = HotelCoverageRule.Nothing,
            // "volunteer is individually, some we cover 1 and some we cover 2 and some we cover
            // more" — 1 is the floor everyone starts on; the extra-nights field carries the rest.
            [HotelCoverageClass.Volunteer]          = HotelCoverageRule.OfNights(1),
            // "event partners we cover all nights they choose, media the same, organizers the same".
            [HotelCoverageClass.Organizer]          = HotelCoverageRule.WholeStay,
            [HotelCoverageClass.Media]              = HotelCoverageRule.WholeStay,
            [HotelCoverageClass.EventPartner]       = HotelCoverageRule.WholeStay,
        };

    /// <summary>All classes in display order, for the settings page.</summary>
    public static readonly IReadOnlyList<HotelCoverageClass> All = new[]
    {
        HotelCoverageClass.SpeakerMasterClass,
        HotelCoverageClass.SpeakerPreDayOnly,
        HotelCoverageClass.SpeakerMainOnly,
        HotelCoverageClass.SpeakerGuest,
        HotelCoverageClass.SpeakerSponsor,
        HotelCoverageClass.Volunteer,
        HotelCoverageClass.Organizer,
        HotelCoverageClass.Media,
        HotelCoverageClass.EventPartner,
    };

    /// <summary>The standard for a class when the edition has stored no row.</summary>
    public static HotelCoverageRule DefaultFor(HotelCoverageClass cls) =>
        Defaults.TryGetValue(cls, out var r) ? r : HotelCoverageRule.Nothing;

    /// <summary>
    /// Which coverage class a person falls in, or <c>null</c> when hotel coverage does not apply to
    /// them at all (attendees and sponsors arrange their own accommodation; an UNCATEGORIZED speaker
    /// is excluded from every tally until an organizer categorizes them, exactly as §299 6.1 has it).
    /// </summary>
    /// <param name="speakerCategory">The speaker profile's category, or null when they have no speaker hat.</param>
    /// <param name="days">Their derived presenting days — only consulted for a Community speaker.</param>
    public static HotelCoverageClass? ClassFor(
        ParticipantRole role, SpeakerCategory? speakerCategory, SpeakerDays days)
    {
        // The SPEAKER hat decides first: a speaker who is also a volunteer is covered as a speaker,
        // because that is the hat the nights were promised for. (There is no MasterclassSpeaker
        // ROLE — it was dropped and the pre-day/main-day distinction is derived from the schedule,
        // which is exactly what the three Community classes below read.)
        if (role == ParticipantRole.Speaker)
        {
            return speakerCategory switch
            {
                SpeakerCategory.Guest => HotelCoverageClass.SpeakerGuest,
                SpeakerCategory.Sponsor => HotelCoverageClass.SpeakerSponsor,
                SpeakerCategory.Community => days switch
                {
                    { PresentsPreDay: true, PresentsMainDay: true } => HotelCoverageClass.SpeakerMasterClass,
                    { PresentsPreDay: true } => HotelCoverageClass.SpeakerPreDayOnly,
                    { PresentsMainDay: true } => HotelCoverageClass.SpeakerMainOnly,
                    // No linked sessions yet: treat as the main-day case rather than as "nothing".
                    // A speaker with no schedule is an unfinished agenda, not a decision to stop
                    // paying for their room — and the organizer page shows the class it used.
                    _ => HotelCoverageClass.SpeakerMainOnly,
                },
                // Uncategorized (null): no class, so no coverage claim is made about them.
                _ => null,
            };
        }

        return role switch
        {
            ParticipantRole.Volunteer => HotelCoverageClass.Volunteer,
            ParticipantRole.Organizer => HotelCoverageClass.Organizer,
            ParticipantRole.Media => HotelCoverageClass.Media,
            ParticipantRole.EventPartner => HotelCoverageClass.EventPartner,
            _ => null,
        };
    }

    /// <summary>
    /// Resolve one person: the class standard, plus their extra nights, clamped to what they booked.
    /// </summary>
    /// <param name="rule">The edition's standard for their class.</param>
    /// <param name="extraNights">Their per-person extra nights (negatives are treated as 0).</param>
    /// <param name="bookedNights">
    /// Nights they actually booked. 0 ⇒ nothing is covered and nothing is owed: a person with no
    /// room cannot have a hotel bill, whatever their class says.
    /// </param>
    public static HotelCoverageResult Resolve(
        HotelCoverageRule rule, int extraNights, int bookedNights)
    {
        var booked = bookedNights < 0 ? 0 : bookedNights;
        var extra = extraNights < 0 ? 0 : extraNights;

        if (booked == 0)
            return new HotelCoverageResult(0, rule.Mode, rule.Mode == HotelCoverageMode.Nights ? 0 : null, extra, 0);

        switch (rule.Mode)
        {
            case HotelCoverageMode.WholeStay:
                // Every night, so "extra" has nothing left to add — but it is still reported, so a
                // page never shows a number the operator typed as if it had been discarded.
                return new HotelCoverageResult(booked, rule.Mode, null, extra, booked);

            case HotelCoverageMode.Nothing:
                // 🔒 Extra nights STILL APPLY. "The sponsor pays" is the standard, not a veto: an
                // organizer who types 2 into this person's extra-nights field has decided to cover
                // two nights, and a switch that silently ignored them would be the worst kind of
                // control — one that accepts input and does nothing with it.
                var fromExtraOnly = Math.Min(extra, booked);
                return new HotelCoverageResult(booked, rule.Mode, 0, extra, fromExtraOnly);

            default:
                var standard = Math.Min(rule.Nights, booked);
                var covered = Math.Clamp(rule.Nights + extra, 0, booked);
                return new HotelCoverageResult(booked, rule.Mode, standard, extra, covered);
        }
    }

    /// <summary>
    /// Nights between two dates — the same arithmetic the hotel bills on. 0 when either date is
    /// missing or the range is inverted: an unknown stay is not a guess.
    /// </summary>
    public static int NightsBetween(DateOnly? checkIn, DateOnly? checkOut) =>
        checkIn is null || checkOut is null || checkOut <= checkIn
            ? 0
            : checkOut.Value.DayNumber - checkIn.Value.DayNumber;

    /// <summary>A short label for the organizer surfaces + the settings page.</summary>
    public static string Label(HotelCoverageClass cls) => cls switch
    {
        HotelCoverageClass.SpeakerMasterClass => "Speaker — master class (pre-day + main day)",
        HotelCoverageClass.SpeakerPreDayOnly => "Speaker — pre-day only",
        HotelCoverageClass.SpeakerMainOnly => "Speaker — main day only",
        HotelCoverageClass.SpeakerGuest => "Speaker — guest",
        HotelCoverageClass.SpeakerSponsor => "Speaker — sponsor-brought",
        HotelCoverageClass.Volunteer => "Volunteer",
        HotelCoverageClass.Organizer => "Organizer",
        HotelCoverageClass.Media => "Media team",
        HotelCoverageClass.EventPartner => "Event partner",
        _ => cls.ToString(),
    };

    /// <summary>How a rule reads in one phrase ("2 nights", "whole stay", "not covered").</summary>
    public static string Describe(HotelCoverageRule rule) => rule.Mode switch
    {
        HotelCoverageMode.WholeStay => "whole stay",
        HotelCoverageMode.Nothing => "not covered",
        _ => rule.Nights == 1 ? "1 night" : $"{rule.Nights} nights",
    };
}
