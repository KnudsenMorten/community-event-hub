using CommunityHub.Core.Evaluation;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §743 Part 2 — the satisfaction metric. Part 2 is BINDING on anything that computes or displays a
/// score, so these tests are written against its stated numbers rather than against the
/// implementation: if the two ever disagree, the document wins and this suite is what says so.
/// </summary>
public sealed class SatisfactionScoreTests
{
    private static SatisfactionScore.Distribution D(int four, int three, int two, int one) =>
        new(four, three, two, one);

    // =====================================================================
    //  §10.1 — the brief's own WORKED EXAMPLE. A precise oracle.
    // =====================================================================

    [Fact]
    public void The_briefs_worked_example_reproduces_exactly()
    {
        // 20 responses: 9 dark green, 8 light green, 2 yellow, 1 red.
        // Points = 900 + 533.3 + 66.7 + 0 = 1500.0 → 1500/20 = 75.0 → band Strong.
        // Cross-check via POMP: mean 3.25 → (3.25−1)/3×100 = 75.0.
        var r = SatisfactionScore.Compute(D(9, 8, 2, 1));

        Assert.Equal(20, r.Responses);
        Assert.Equal(75.0, r.Score);
        Assert.Equal(85.0, r.PositiveShare);      // 17 of 20
        Assert.Equal("Happy", r.Band);            // §752.7 — bands use the BUTTON words now
        Assert.Equal("linear", r.WeightProfile);
    }

    [Fact]
    public void The_score_equals_the_POMP_form_of_the_mean()
    {
        // §4: linear IS the normalised-mean construction, (mean − 1) / 3 × 100. Any drift between
        // the weighted-sum implementation and this identity is a bug in one of them.
        var dist = D(5, 12, 7, 3);
        var ratings = Enumerable.Repeat(4, 5)
            .Concat(Enumerable.Repeat(3, 12))
            .Concat(Enumerable.Repeat(2, 7))
            .Concat(Enumerable.Repeat(1, 3))
            .ToList();

        var expected = Math.Round((ratings.Average() - 1) / 3 * 100, 1, MidpointRounding.AwayFromZero);

        Assert.Equal(expected, SatisfactionScore.Compute(dist).Score);
    }

    [Fact]
    public void The_linear_weights_are_full_precision_thirds_not_rounded_literals()
    {
        // 🔒 §9.1 — hard-coding 66.67 / 33.33 introduces a small systematic DOWNWARD bias that grows
        // with volume. 300 light-green responses must score exactly 66.7, not 66.67-rounded-down
        // arithmetic drifting below it.
        var r = SatisfactionScore.Compute(D(0, 300, 0, 0));

        Assert.Equal(66.7, r.Score);
        Assert.Equal(200d / 3d, SatisfactionScore.WeightOf(3));
        Assert.Equal(100d / 3d, SatisfactionScore.WeightOf(2));
    }

    // =====================================================================
    //  §5.1 — the threshold
    // =====================================================================

    [Fact]
    public void Below_ten_responses_the_score_is_NULL_never_zero()
    {
        // 🔥 The single most consequential rule here. Zero (or an omitted field) makes a consuming
        // chart plot a real zero, rendering a decent session as a catastrophe.
        var r = SatisfactionScore.Compute(D(2, 1, 1, 0));   // 4 responses

        Assert.Equal(4, r.Responses);
        Assert.Null(r.Score);
        Assert.Null(r.PositiveShare);
        Assert.Null(r.Band);
        Assert.True(r.BelowThreshold);

        // ...but the distribution IS still populated, so the UI can render progress toward 10
        // rather than a blank or a provisional number.
        Assert.Equal(2, r.Distribution.Four);
        Assert.Equal(4, r.Distribution.Total);
    }

    [Fact]
    public void Exactly_ten_responses_DOES_produce_a_score()
    {
        var r = SatisfactionScore.Compute(D(10, 0, 0, 0));

        Assert.False(r.BelowThreshold);
        Assert.Equal(100.0, r.Score);
    }

    [Fact]
    public void The_threshold_is_flat_at_ten_for_every_session_type_and_length()
    {
        // §743 item 19 — deliberately NOT scaled by session length. Pinned so "short sessions get a
        // lower bar" cannot be reintroduced as a kindness; it would make the least reliable scores
        // the most likely to be published.
        Assert.Equal(10, SatisfactionScore.MinimumResponses);
    }

    // =====================================================================
    //  §7 — bands
    // =====================================================================

    // 🔑 §752.7 — the labels are the BUTTON words now (operator: *"i dont understand the word
    // strong"* → *"can we use other wordings like Happy, Very happy"*). Every boundary case is kept
    // deliberately: relabelling must not quietly move a THRESHOLD. 66.7 — all light green, nobody
    // dissatisfied at all — must still land in the second band rather than a middling one, which is
    // the calibration §7 of the brief exists to argue for.
    [Theory]
    [InlineData(100.0, "Very happy")]
    [InlineData(83.3, "Very happy")]         // a 50/50 dark+light green split
    [InlineData(80.0, "Very happy")]         // boundary, inclusive
    [InlineData(79.9, "Happy")]
    [InlineData(66.7, "Happy")]              // all light green — no negatives at all
    [InlineData(60.0, "Happy")]              // boundary, inclusive
    [InlineData(59.9, "Somewhat unhappy")]
    [InlineData(40.0, "Somewhat unhappy")]   // boundary, inclusive
    [InlineData(39.9, "Unhappy")]
    [InlineData(0.0, "Unhappy")]
    public void Bands_are_calibrated_for_linear(double score, string expected)
    {
        Assert.Equal(expected, SatisfactionScore.BandFor(score));
    }

    [Fact]
    public void An_all_light_green_session_reads_STRONG_not_middling()
    {
        // 🔑 The calibration's whole point: nobody was dissatisfied, so the band must not imply a
        // problem. 66.67 would read as a failing grade if presented as a percentage — which is why
        // §11 also requires the figure be named, stated not to be a percentage, and never carry a
        // % sign. (§752.7 replaced the old "label it an index" mechanism; the duty is unchanged.)
        var r = SatisfactionScore.Compute(D(0, 20, 0, 0));

        Assert.Equal(66.7, r.Score);
        Assert.Equal("Happy", r.Band);
        Assert.Equal(100.0, r.PositiveShare);   // every respondent was positive
    }

    [Fact]
    public void Rating_2_counts_as_NEGATIVE_despite_reading_as_neutral()
    {
        // §2.1 — a four-point forced choice has no midpoint. "Somewhat dissatisfied" is on the
        // negative side for every reporting purpose, so positiveShare must exclude it.
        var r = SatisfactionScore.Compute(D(0, 0, 20, 0));

        Assert.Equal(0.0, r.PositiveShare);
        Assert.Equal(33.3, r.Score);
        Assert.Equal("Unhappy", r.Band);
    }

    // =====================================================================
    //  §4.1 — profiles
    // =====================================================================

    [Fact]
    public void The_polarity_profile_is_available_and_scores_differently()
    {
        var dist = D(9, 8, 2, 1);

        var linear = SatisfactionScore.Compute(dist, SatisfactionScore.ProfileLinear);
        var polarity = SatisfactionScore.Compute(dist, SatisfactionScore.ProfilePolarity);

        Assert.Equal(75.0, linear.Score);
        // polarity: (900 + 600 + 50 + 0)/20 = 77.5 — the wider sentiment gap flatters the result,
        // which is exactly why linear is the default (§4).
        Assert.Equal(77.5, polarity.Score);
        Assert.NotEqual(linear.Score, polarity.Score);
    }

    [Fact]
    public void The_profile_identifier_travels_with_every_result()
    {
        // §4.1/§8 — without it a historical 75.0 is ambiguous between the two models and cannot be
        // recomputed or compared.
        Assert.Equal("polarity",
            SatisfactionScore.Compute(D(9, 8, 2, 1), SatisfactionScore.ProfilePolarity).WeightProfile);
    }

    // =====================================================================
    //  §6 — aggregation
    // =====================================================================

    [Fact]
    public void POOLED_and_MEAN_SESSION_are_different_numbers_and_both_are_right()
    {
        // 🔒 They must never be presented interchangeably. A packed session and a small one:
        // pooled follows the crowd, mean-session treats the two sessions equally.
        var big = D(40, 0, 0, 0);      // 40 responses, all dark green → 100
        var small = D(0, 0, 0, 10);    // 10 responses, all red → 0

        var pooled = SatisfactionScore.Pooled(new[] { big, small });
        var mean = SatisfactionScore.MeanSession(new[] { big, small });

        Assert.Equal(50, pooled.Responses);
        Assert.Equal(80.0, pooled.Score);   // volume-weighted: 4000/50
        Assert.Equal(50.0, mean);           // session-weighted: (100 + 0)/2
        Assert.NotEqual(pooled.Score, mean);
    }

    [Fact]
    public void Below_threshold_sessions_still_feed_the_POOLED_figure()
    {
        // §5.1 — the threshold guards against small-sample volatility; an aggregate is not a small
        // sample. Their raw responses are real feedback and must not vanish from the event total.
        var scored = D(10, 0, 0, 0);   // 10 → has a score
        var tiny = D(0, 0, 0, 3);      // 3 → no score of its own

        var pooled = SatisfactionScore.Pooled(new[] { scored, tiny });

        Assert.Equal(13, pooled.Responses);          // all 13 counted
        Assert.Equal(76.9, pooled.Score);            // 1000/13
    }

    [Fact]
    public void Below_threshold_sessions_are_EXCLUDED_from_the_mean_session_figure()
    {
        var scored = D(10, 0, 0, 0);
        var tiny = D(0, 0, 0, 3);

        // Only the session that cleared the threshold is averaged — including the other would let a
        // 3-response session move the programme-level number as much as a full room.
        Assert.Equal(100.0, SatisfactionScore.MeanSession(new[] { scored, tiny }));
    }

    [Fact]
    public void Mean_session_is_NULL_when_nothing_clears_the_threshold()
    {
        // Not zero: there is nothing to average, which is a different statement.
        Assert.Null(SatisfactionScore.MeanSession(new[] { D(1, 1, 0, 0), D(0, 0, 1, 0) }));
    }

    [Fact]
    public void An_empty_scope_reports_no_responses_and_no_score()
    {
        var r = SatisfactionScore.Pooled(Array.Empty<SatisfactionScore.Distribution>());

        Assert.Equal(0, r.Responses);
        Assert.Null(r.Score);
        Assert.True(r.BelowThreshold);
    }

    // =====================================================================
    //  §754.1 — the EVENT-wide figure has no response floor
    // =====================================================================

    /// <summary>
    /// Operator 2026-08-01: <i>"drop this requirement - the event score is null below 10
    /// responses"</i>, for the C11 signage feedback view.
    /// </summary>
    /// <remarks>
    /// 🔑 The §5.1 floor protects an INDIVIDUAL SESSION, where below ten responses one contrarian
    /// press moves the result by more than ten points. That reasoning does not survive aggregation:
    /// the event figure pools every response in the building, and on the morning of day one a blank
    /// on a 15-screen wall is worse than a provisional number shown with its response count.
    /// </remarks>
    [Fact]
    public void The_pooled_event_score_publishes_from_the_very_first_response()
    {
        var r = SatisfactionScore.Pooled(new[] { D(1, 0, 0, 0) });   // ONE dark-green press

        Assert.Equal(1, r.Responses);
        Assert.Equal(100.0, r.Score);
        Assert.Equal("Very happy", r.Band);
        // BelowThreshold still reports the FACT (1 < 10) — it is the caller's cue to show the count
        // alongside. What changed is that a score now exists to show beside it.
        Assert.True(r.BelowThreshold);
    }

    /// <summary>
    /// 🔒 THE GUARD THAT MATTERS: dropping the floor for the event must not drop it for a session.
    /// Had this been done by lowering <c>MinimumResponses</c>, every session score, the speaker
    /// report and the public scoreboard would have changed silently at the same time.
    /// </summary>
    [Fact]
    public void A_single_session_still_has_no_score_below_the_threshold()
    {
        var session = SatisfactionScore.Compute(D(1, 0, 0, 0));

        Assert.Equal(1, session.Responses);
        Assert.Null(session.Score);
        Assert.Null(session.Band);
        Assert.Equal(10, SatisfactionScore.MinimumResponses);   // the constant is untouched
    }

    /// <summary>Zero responses is still no score — a computed 0 would be a fabricated result.</summary>
    [Fact]
    public void Zero_responses_still_yields_no_event_score()
    {
        var r = SatisfactionScore.Pooled(new[] { D(0, 0, 0, 0) });

        Assert.Equal(0, r.Responses);
        Assert.Null(r.Score);
    }

    /// <summary>
    /// The pooled figure sums across sessions that individually clear nothing — which is exactly the
    /// day-one signage case: twelve rooms with a handful of presses each, no session scoreable yet.
    /// </summary>
    [Fact]
    public void Sessions_that_individually_score_nothing_still_pool_into_an_event_figure()
    {
        var pooled = SatisfactionScore.Pooled(new[] { D(2, 1, 0, 0), D(1, 1, 0, 0), D(0, 1, 1, 0) });

        Assert.Equal(7, pooled.Responses);          // 3 + 2 + 2, every one below 10 on its own
        Assert.NotNull(pooled.Score);
        Assert.Null(SatisfactionScore.Compute(D(2, 1, 0, 0)).Score);   // …and each still has none
    }
}
