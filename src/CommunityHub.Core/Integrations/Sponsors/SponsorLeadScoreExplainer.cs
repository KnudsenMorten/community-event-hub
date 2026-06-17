using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations.Sponsors;

/// <summary>
/// One contributing factor in a lead's heuristic AI-screen score: a stable
/// resource key (the organizer-facing reason, localised in the UI) and the
/// signed point delta it added to (or subtracted from) the running total.
/// </summary>
/// <param name="ReasonKey">
/// Stable key for the human-readable reason — the UI resolves it through the
/// shared localizer (<c>LeadScore.*</c>), so the breakdown reads the same way
/// in English and Danish. Never a pre-formatted sentence.
/// </param>
/// <param name="Points">Signed delta this factor applied (+/-).</param>
public sealed record SponsorLeadScoreFactor(string ReasonKey, int Points);

/// <summary>
/// The full, transparent decomposition of a lead's AI-screen score: the
/// starting baseline, every factor that moved it, the raw (pre-clamp) total,
/// the final clamped 0..100 score, and the verdict label. This is what the
/// organizer grid renders under "why this score", turning the opaque badge
/// (REQUIREMENTS §21 organizer "[L] explain AI scores") into an auditable list.
/// </summary>
public sealed record SponsorLeadScoreBreakdown(
    int BaseScore,
    IReadOnlyList<SponsorLeadScoreFactor> Factors,
    int RawTotal,
    int FinalScore,
    string Label,
    bool LooksTest);

/// <summary>
/// Pure, deterministic decomposition of the sponsor-lead heuristic screen — the
/// SINGLE source of truth for the scoring math. <see cref="SponsorLeadScreeningService"/>
/// delegates to <see cref="Compute"/> so the score it persists and the breakdown
/// the organizer sees can never drift apart. No DB / clock / I/O.
///
/// Why a separate explainer: the badge on the leads grid showed only a number +
/// a one-word label, leaving organizers (and the future model-based screen that
/// learns from their overrides) with no insight into WHY a lead scored the way
/// it did. <see cref="Compute"/> reconstructs the exact same arithmetic the
/// screen runs, but returns each contribution as a labelled, signed factor.
/// </summary>
public static class SponsorLeadScoreExplainer
{
    /// <summary>The neutral starting point every lead begins from.</summary>
    public const int BaseScore = 50;

    private static readonly string[] TestPatterns =
        { "test", "asdf", "qwerty", "demo", "example", "xxx" };

    // Stable reason keys — resolved through the shared localizer in the UI.
    public const string ReasonHasEmail     = "LeadScore.HasEmail";
    public const string ReasonHasName      = "LeadScore.HasName";
    public const string ReasonHasCompany   = "LeadScore.HasCompany";
    public const string ReasonHasPhone     = "LeadScore.HasPhone";
    public const string ReasonUnreachable  = "LeadScore.Unreachable";
    public const string ReasonLooksTest    = "LeadScore.LooksTest";

    /// <summary>
    /// Re-derive the full score breakdown for a lead. Pure — reads only the
    /// lead's content fields; never mutates the lead and never persists.
    /// </summary>
    public static SponsorLeadScoreBreakdown Compute(SponsorLead lead)
    {
        ArgumentNullException.ThrowIfNull(lead);

        var email = (lead.Email ?? string.Empty).Trim();
        var name  = (lead.FullName ?? string.Empty).Trim();

        var hasEmail   = email.Contains('@') && email.Contains('.');
        var hasName    = name.Length >= 3;
        var hasCompany = !string.IsNullOrWhiteSpace(lead.Company);
        var hasPhone   = !string.IsNullOrWhiteSpace(lead.Phone);
        var looksTest  = TestPatterns.Any(p =>
            name.Contains(p, StringComparison.OrdinalIgnoreCase)
            || email.StartsWith(p, StringComparison.OrdinalIgnoreCase)
            || email.Contains("@example.", StringComparison.OrdinalIgnoreCase));

        var factors = new List<SponsorLeadScoreFactor>();
        var raw = BaseScore;

        if (hasEmail)   { factors.Add(new(ReasonHasEmail,   +20)); raw += 20; }
        if (hasName)    { factors.Add(new(ReasonHasName,    +10)); raw += 10; }
        if (hasCompany) { factors.Add(new(ReasonHasCompany, +15)); raw += 15; }
        if (hasPhone)   { factors.Add(new(ReasonHasPhone,    +5)); raw +=  5; }
        if (!hasEmail && !hasPhone) { factors.Add(new(ReasonUnreachable, -35)); raw -= 35; }

        // The test-pattern cap is a ceiling, not an additive delta, so it is
        // surfaced as a distinct factor (it overrides the running total) rather
        // than folded into the +/- list.
        var capped = raw;
        if (looksTest)
        {
            capped = Math.Min(raw, 5);
            factors.Add(new(ReasonLooksTest, capped - raw));
        }

        var final = Math.Clamp(capped, 0, 100);

        string label;
        if (looksTest)                   label = "test-entry";
        else if (!hasEmail && !hasPhone) label = "unreachable";
        else if (!hasName)               label = "incomplete";
        else                             label = "looks-legit";

        return new SponsorLeadScoreBreakdown(BaseScore, factors, raw, final, label, looksTest);
    }
}
