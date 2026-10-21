using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §1354 — responses split by channel and capture policy, so organisers can see what each policy yields
/// (operator 2026-10-19: *"link different capture feedback-policy per device so we can test what works"*).
/// </summary>
/// <remarks>
/// Per row: how many responses, the average rating, and how often the extra questions were actually
/// answered — the follow-up rate counts a response that picked either "what was good" or "what could
/// improve"; the free-text rate one with a comment. A rate is null where the policy did not ask it.
/// Responses from before §1354 (no recorded policy) form their own row, so old data never pollutes a policy.
/// </remarks>
public static class EvaluationPolicyComparison
{
    public sealed record Row(
        string Channel,
        FeedbackCapturePolicy? Policy,
        int Responses,
        double AverageRating,
        double? FollowUpRate,
        double? FreeTextRate)
    {
        public string PolicyLabel => Policy is { } p ? FeedbackCapturePolicies.Label(p) : "Before policies (not recorded)";

        /// <summary>§1355 — the rating-button symbols the responses were given with (null = not recorded).</summary>
        public RatingSymbols? Symbols { get; init; }
        public string SymbolsLabel => Symbols is { } s ? RatingSymbolChoices.Label(s) : "—";
    }

    public static async Task<IReadOnlyList<Row>> BuildAsync(CommunityHubDbContext db, int eventId, CancellationToken ct = default)
    {
        var rows = await db.EvaluationResponses.AsNoTracking()
            .Where(r => r.EventId == eventId)
            .Select(r => new
            {
                r.Source,
                r.CapturePolicy,
                r.RatingSymbols,
                r.Rating,
                FollowUp = r.PositivePoint != null || r.ImprovementPoint != null,
                Text = r.FreeText != null && r.FreeText != "",
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => (Channel: ChannelOf(r.Source), r.CapturePolicy, r.RatingSymbols))
            .Select(g =>
            {
                var n = g.Count();
                var p = g.Key.CapturePolicy;
                double? Rate(Func<bool> asked, int hits) => asked() ? (double)hits / n : null;
                return new Row(
                    g.Key.Channel, p, n, g.Average(r => r.Rating),
                    Rate(() => p is null || FeedbackCapturePolicies.HasFollowUps(p.Value), g.Count(r => r.FollowUp)),
                    Rate(() => p is null || FeedbackCapturePolicies.HasFreeText(p.Value), g.Count(r => r.Text)))
                {
                    Symbols = g.Key.RatingSymbols,
                };
            })
            .OrderBy(r => r.Channel)
            .ThenBy(r => r.Policy is null ? 1 : 0)
            .ThenBy(r => r.Policy)
            .ThenBy(r => r.Symbols)
            .ToList();
    }

    private static string ChannelOf(string? source) => source switch
    {
        Domain.Evaluation.EvaluationResponseSources.Qr => "QR code",
        Domain.Evaluation.EvaluationResponseSources.Kiosk => "Kiosk",
        _ => "Box",
    };
}
