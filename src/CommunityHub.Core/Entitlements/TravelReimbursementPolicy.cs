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

    /// <summary>
    /// §1054 — the task/source-key prefix of the travel claim, so Core can RECOGNISE that task
    /// without referencing the web project.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Must stay equal to <c>TravelFormService.SubmitInvoiceTaskKey</c></b>, which lives in the
    /// web assembly and cannot be referenced from here. A rename on either side unbinds the
    /// reminder gate SILENTLY — the mail would simply start going out again — so
    /// <c>TravelTaskKeyPrefixMatchesTests</c> pins the two strings against each other. Same failure
    /// shape as §1037's system names, and §767's guessed convention.
    /// </remarks>
    public const string TaskKeyPrefix = "travel:submit-ticket-invoice";

    /// <summary>
    /// §1054 — may we MAIL this speaker about their travel claim yet?
    /// </summary>
    /// <remarks>
    /// <para><b>False while the country is unknown.</b> §143 deliberately treats a blank country as
    /// non-Denmark so the TASK is offered rather than silently withheld — that protection stays.
    /// This governs only the E-MAIL, because a task can be removed when the speaker answers and a
    /// sent mail cannot: a Danish speaker received "Submit travel reimbursement" before ever being
    /// asked where they live (operator 2026-08-10).</para>
    ///
    /// <para>🔑 Once a country IS set, Denmark is already filtered upstream by the deadline's
    /// <c>nonDenmarkOnly</c> flag, so anything that reaches the mail gate with a country is
    /// legitimately non-Danish. This method therefore asks only "do we know yet?".</para>
    /// </remarks>
    public static bool MayEmailClaim(string? country) => !string.IsNullOrWhiteSpace(country);

    /// <summary>The explanation shown wherever the claim is withheld — one wording, one place.</summary>
    public const string NotEligibleMessage =
        "Travel reimbursement is for speakers travelling to Copenhagen from outside Denmark, "
        + "so there is nothing to claim here. Your hotel, dinner and lunch arrangements are "
        + "unaffected — you can review them from Get Started.";
}
