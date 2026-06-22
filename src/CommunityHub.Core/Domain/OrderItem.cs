namespace CommunityHub.Core.Domain;

/// <summary>
/// A countable thing a participant is (or is not) entitled to receive at the
/// event — the unit the appreciation/logistics tallies count by. The set a
/// person is entitled to is computed (per their hats) by
/// <see cref="Entitlements.OrderEntitlements"/> and counted once per physical
/// person by <see cref="Entitlements.OrderCountService"/>.
/// </summary>
public enum OrderItem
{
    /// <summary>Branded polo shirt.</summary>
    Polo = 0,

    /// <summary>General swag bag / gift.</summary>
    Swag = 1,

    /// <summary>Speaker award / token of appreciation.</summary>
    Award = 2,

    /// <summary>Hotel room.</summary>
    Hotel = 3,

    /// <summary>Travel reimbursement.</summary>
    TravelReimbursement = 4,

    /// <summary>Appreciation dinner seat.</summary>
    AppreciationDinner = 5,

    /// <summary>Lunch on the pre-day.</summary>
    LunchPreDay = 6,

    /// <summary>Lunch on the main conference day.</summary>
    LunchMainDay = 7,
}
