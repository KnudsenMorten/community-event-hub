using CommunityHub.Core.Entitlements;

namespace CommunityHub.Core.Domain;

/// <summary>
/// §1296 — the per-edition "included nights per rule": one row per
/// <see cref="HotelCoverageClass"/>, set by an organizer in
/// <c>/Organizer/Settings/HotelNights</c> (operator 2026-09-22: <i>"we need somewhere in the
/// settings menu where i can define the included nights per rule"</i>).
/// </summary>
/// <remarks>
/// <para>When no row exists for a class the catalog default applies
/// (<see cref="HotelCoverage.DefaultFor"/>), so a fresh edition behaves exactly as the agreed
/// standards describe without seeding anything.</para>
///
/// <para>🔑 This holds the STANDARD only. What one named person gets beyond it is
/// <see cref="HotelCoverageExtra"/> — one field, one place, so two controls can never disagree
/// about the same person's nights.</para>
/// </remarks>
public class HotelCoverageStandard
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The class this standard applies to. Persisted as an int — values are never reused.</summary>
    public HotelCoverageClass Class { get; set; }

    /// <summary>Fixed nights / whole stay / nothing.</summary>
    public HotelCoverageMode Mode { get; set; } = HotelCoverageMode.Nights;

    /// <summary>The night count — only meaningful when <see cref="Mode"/> is Nights.</summary>
    public int Nights { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The organizer who last changed this standard (audit; nullable).</summary>
    public string? UpdatedByEmail { get; set; }

    /// <summary>The stored row as the resolver's pure rule.</summary>
    public HotelCoverageRule ToRule() => Mode switch
    {
        HotelCoverageMode.WholeStay => HotelCoverageRule.WholeStay,
        HotelCoverageMode.Nothing => HotelCoverageRule.Nothing,
        _ => HotelCoverageRule.OfNights(Nights),
    };
}

/// <summary>
/// §1296 — the EXTRA ELDK-covered nights for ONE person, on top of their class standard (operator
/// 2026-09-22: <i>"just extra eldk covered nights outside of included nights - all per the rule"</i>).
/// </summary>
/// <remarks>
/// <para>🔒 <b>Additive only, and deliberately the ONLY per-person field.</b> An absolute override
/// was designed and cut: two fields that can both answer "how many nights?" is one too many, and the
/// second is where they disagree. A consequence worth knowing rather than discovering — nobody can
/// be covered for FEWER nights than their class standard.</para>
///
/// <para>A row exists only for people who were actually granted something, so the absence of a row
/// means "standard, nothing extra" — not "nobody looked at them".</para>
///
/// <para>It carries who/when because this is a money decision: somebody will eventually ask why a
/// hotel bill has three nights on it.</para>
/// </remarks>
public class HotelCoverageExtra
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>Extra nights ELDK covers beyond the class standard. Never negative.</summary>
    public int ExtraNights { get; set; }

    /// <summary>Optional free-text reason ("agreed with organizers — two extra shifts").</summary>
    public string? Note { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The organizer who last set this (audit; nullable).</summary>
    public string? UpdatedByEmail { get; set; }
}
