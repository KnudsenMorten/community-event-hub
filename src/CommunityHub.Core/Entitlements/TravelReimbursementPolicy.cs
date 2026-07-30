namespace CommunityHub.Core.Entitlements;

/// <summary>
/// §399 — THE rule for who may claim travel reimbursement (operator 2026-07-26: <i>"when a speaker
/// is from Denmark, then the 'travel imbursement' should be disabled as the terms is that no people
/// from denmark get travel reimbursed"</i>).
///
/// <para><b>Why this type exists rather than another private helper.</b> The rule was already
/// implemented — but only inside <c>SpeakerDeadlineSeeder</c>, as a private <c>IsDenmark</c>. So the
/// TASK was correctly withheld from Danish speakers while the NAV ENTRY and the <c>/Forms/Travel</c>
/// PAGE happily offered the claim to them. One rule in three places, enforced in one. Now every
/// caller asks the same method.</para>
///
/// <para><b>A hidden link is not a rule.</b> The page must refuse on its own — someone with the URL,
/// a bookmark, or an old e-mail must meet the same answer as someone reading the menu.</para>
/// </summary>
public static class TravelReimbursementPolicy
{
    /// <summary>
    /// True when a speaker from <paramref name="country"/> may claim travel reimbursement.
    ///
    /// <para><b>Unknown country ⇒ ELIGIBLE.</b> A speaker who has not filled in their country yet
    /// must not silently lose a benefit they may be entitled to; the worst case is that they see a
    /// form they turn out not to need, which is recoverable. Silently withholding it is not.</para>
    /// </summary>
    public static bool IsEligible(string? country) => !IsDenmark(country);

    /// <summary>
    /// Denmark, by the spellings the data actually contains — the ISO code and the English name.
    /// Blank is NOT Denmark (see the unknown-country note above).
    /// </summary>
    public static bool IsDenmark(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return false;
        var c = country.Trim();
        return string.Equals(c, "DK", StringComparison.OrdinalIgnoreCase)
            || string.Equals(c, "Denmark", StringComparison.OrdinalIgnoreCase)
            || string.Equals(c, "Danmark", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The explanation shown wherever the claim is withheld — one wording, one place.</summary>
    public const string NotEligibleMessage =
        "Travel reimbursement is for speakers travelling to Copenhagen from outside Denmark, "
        + "so there is nothing to claim here. Your hotel, dinner and lunch arrangements are "
        + "unaffected — you can review them from Get Started.";
}
