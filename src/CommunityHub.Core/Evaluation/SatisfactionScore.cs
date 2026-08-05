namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §743 Part 2 — THE satisfaction metric. Authoritative: any component that displays a satisfaction
/// figure must compute it here, and no component may invent a variant.
/// </summary>
/// <remarks>
/// <para>Three figures are published together and none is meaningful alone: the
/// <b>satisfaction score</b> (the headline index), the <b>positive share</b>, and the
/// <b>response count</b> — a score without its sample size is not interpretable.</para>
/// </remarks>
public static class SatisfactionScore
{
    /// <summary>
    /// §5.1 — a score is computed only from this many responses upward. FLAT for every session type
    /// and length, deliberately: a lower bar for short sessions would make their scores least
    /// reliable precisely where they are already least reliable.
    /// </summary>
    /// <remarks>
    /// Below it a single response moves the result by more than ten points and one contrarian press
    /// swings a session from "strong" to "mixed". Publishing that is misleading, not merely noisy —
    /// so the score is <b>null</b>, and short or sparsely attended sessions will often show nothing.
    /// That consequence is accepted (§743 item 19).
    /// </remarks>
    public const int MinimumResponses = 10;

    /// <summary>The two profiles. §4.1: a tenant selects between them; custom weights are NOT offered,
    /// because arbitrary weights would end cross-tenant comparability and make every support
    /// conversation about a score begin by establishing which arithmetic produced it.</summary>
    public const string ProfileLinear = "linear";
    public const string ProfilePolarity = "polarity";

    /// <summary>
    /// The weight of one rating under a profile.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Linear is written as 200/3 and 100/3, never 66.67 and 33.33.</b> §9.1 is explicit: the
    /// rounded literals introduce a small systematic DOWNWARD bias that grows with response volume.
    /// The division happens at full precision and the result is rounded exactly once, at the end.
    /// </remarks>
    public static double WeightOf(int rating, string profile = ProfileLinear) =>
        string.Equals(profile, ProfilePolarity, StringComparison.OrdinalIgnoreCase)
            ? rating switch { 4 => 100d, 3 => 75d, 2 => 25d, 1 => 0d, _ => 0d }
            : rating switch { 4 => 100d, 3 => 200d / 3d, 2 => 100d / 3d, 1 => 0d, _ => 0d };

    /// <summary>The four counters. Ratings outside 1–4 are not representable, by construction.</summary>
    public sealed record Distribution(int Four, int Three, int Two, int One)
    {
        public int Total => Four + Three + Two + One;

        public static Distribution From(IEnumerable<int> ratings)
        {
            int f = 0, t = 0, tw = 0, o = 0;
            foreach (var r in ratings)
            {
                switch (r)
                {
                    case 4: f++; break;
                    case 3: t++; break;
                    case 2: tw++; break;
                    case 1: o++; break;
                }
            }
            return new Distribution(f, t, tw, o);
        }

        public int CountOf(int rating) => rating switch
        {
            4 => Four, 3 => Three, 2 => Two, 1 => One, _ => 0,
        };
    }

    /// <summary>
    /// The §10 payload for one scope. <see cref="Score"/> and <see cref="PositiveShare"/> are NULL
    /// below the threshold — never 0 and never omitted.
    /// </summary>
    public sealed record Result(
        int Responses, double? Score, double? PositiveShare, string? Band,
        string WeightProfile, Distribution Distribution)
    {
        /// <summary>True when the sample is too small to publish a score.</summary>
        public bool BelowThreshold => Responses < MinimumResponses;
    }

    /// <summary>
    /// Compute the three figures for one set of ratings.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Below the threshold the score is NULL, not 0.</b> Both alternatives — zero, or omitting
    /// the field — eventually cause a consuming chart to plot a real zero, rendering a decent
    /// session as a catastrophe. The distribution and the count are still populated, so a caller can
    /// render "4 of 10 responses needed" rather than a blank.
    ///
    /// <para>§5.2 rounding: accumulate at full precision and round ONCE, here, to one decimal.
    /// Rounding during accumulation compounds error across large response sets.</para>
    /// </remarks>
    /// <param name="requireMinimum">
    /// §754.1 — <c>false</c> computes a score however few responses there are. <b>Only the EVENT-wide
    /// pooled figure passes false</b>; every per-session path leaves it true.
    /// </param>
    public static Result Compute(
        Distribution d, string profile = ProfileLinear, bool requireMinimum = true)
    {
        var total = d.Total;

        // 🔑 §754.1 (operator 2026-08-01: *"drop this requirement - the event score is null below 10
        // responses"*). The floor is a §5.1 decision about an INDIVIDUAL SESSION: below ten, one
        // contrarian press moves the result by more than ten points, so publishing it misleads. That
        // reasoning does not survive aggregation — the event figure pools every response in the
        // building, and on the morning of day one "no score yet" on a 15-screen wall is worse than a
        // provisional one that is honestly labelled with its response count.
        //
        // 🔒 A PARAMETER, not a lowered constant. Changing MinimumResponses would have silently moved
        // every session score, the speaker report, the public scoreboard and the report threshold
        // copy at the same time — one edit, four surfaces, no test naming the connection.
        if (requireMinimum && total < MinimumResponses)
        {
            return new Result(total, null, null, null, profile, d);
        }

        // Nothing at all is still nothing: a score from zero responses would be a fabricated 0.
        if (total == 0)
        {
            return new Result(0, null, null, null, profile, d);
        }

        var points =
            WeightOf(4, profile) * d.Four +
            WeightOf(3, profile) * d.Three +
            WeightOf(2, profile) * d.Two +
            WeightOf(1, profile) * d.One;

        var score = Math.Round(points / total, 1, MidpointRounding.AwayFromZero);

        // §3.2 — how many left satisfied, discarding intensity. Named positiveShare rather than
        // "satisfaction rate" on purpose: two metrics distinguished only by "score" vs "rate" get
        // conflated by users.
        var positive = Math.Round((d.Four + d.Three) * 100d / total, 1, MidpointRounding.AwayFromZero);

        return new Result(total, score, positive, BandFor(score), profile, d);
    }

    /// <summary>Convenience over a raw rating sequence.</summary>
    public static Result Compute(IEnumerable<int> ratings, string profile = ProfileLinear) =>
        Compute(Distribution.From(ratings), profile);

    /// <summary>
    /// §7 — the interpretation band, so nobody is left inferring meaning from a bare number.
    /// </summary>
    /// <remarks>
    /// Calibrated for LINEAR, and the boundaries are not arbitrary: an all-light-green session
    /// (nobody dissatisfied at all) scores 66.67, so the "Strong" floor sits at 60, comfortably
    /// below it — a session with no negative feedback cannot land in a middling band. "Very strong"
    /// starts at 80 because a 50/50 split of dark and light green scores 83.3, so the top band means
    /// real enthusiasm rather than the mere absence of complaint.
    ///
    /// <para>The labels are DESCRIPTIVE, not a verdict. The earlier set (Excellent / Good / Needs
    /// attention / Poor) reads as judgement, which is intolerable on a public scoreboard and venue
    /// signage where a speaker's peers and audience will read it — and "needs attention" instructs
    /// the reader to act, which an aggregate satisfaction figure does not license on its own.</para>
    ///
    /// <para>🔑 <b>§752.7 — the labels are now the BUTTON words</b> (operator 2026-08-01:
    /// <i>"i dont understand the word strong"</i> → <i>"can we use other wordings like Happy, Very
    /// happy"</i>). This supersedes the brief's §7 set (Very strong / Strong / Mixed / Weak), which
    /// was descriptive but introduced a second vocabulary: a reader had to learn "strong" on top of
    /// the four words the buttons had already taught them. Reusing the button words means the report
    /// asks nobody to learn anything — the scale a person pressed is the scale their session is
    /// described on.</para>
    ///
    /// <para>🔒 <b>The risk this creates, and the mitigation.</b> A band label now collides with a
    /// BUTTON label: a session can be banded "Happy" while only four people pressed Happy. That is
    /// exactly the class of confusion the brief's §3.2 warns about for positiveShare vs
    /// satisfactionScore. Mitigated by never printing the band bare — every surface shows it with its
    /// RANGE ("Happy (60–79 of 100)"), which a button count can never carry.</para>
    ///
    /// <para>⚠️ This string travels in the §10 API payload and onto the C9 scoreboard and C11 signage
    /// when those are built. All three must show the same word for the same score.</para>
    /// </remarks>
    public static string? BandFor(double? score) => score switch
    {
        null => null,
        >= 80 => "Very happy",
        >= 60 => "Happy",
        >= 40 => "Somewhat unhappy",
        _ => "Unhappy",
    };

    /// <summary>
    /// §752.7 — the band's own RANGE, to be printed beside the band label. Never optional.
    /// </summary>
    /// <remarks>
    /// 🔒 Lives HERE, beside <see cref="BandFor"/>, precisely because the two must never disagree:
    /// the brief now makes a bare band non-conformant, so every surface that renders a band needs
    /// this, and a copy in one renderer would drift the moment a boundary moved.
    ///
    /// <para>🔑 It exists because the operator could not read the band: <i>"i dont understand the
    /// word strong"</i>. The word alone is vocabulary someone has to be taught — and once §752.7
    /// moved the labels onto the BUTTON words, the range also became the only thing distinguishing
    /// the band "Happy" from the count of people who pressed Happy.</para>
    /// </remarks>
    public static string BandRange(double? score) => score switch
    {
        >= 80 => "80–100",
        >= 60 => "60–79",
        >= 40 => "40–59",
        not null => "0–39",
        _ => "",
    };

    // -------------------------------------------------------------------------
    // §6 — AGGREGATION ACROSS SCOPES
    // -------------------------------------------------------------------------

    /// <summary>
    /// POOLED — recompute from the raw responses of every session in scope, as one large sample.
    /// </summary>
    /// <remarks>
    /// Weights by response volume, so a packed keynote influences the event score more than a
    /// sparsely attended breakout. It answers <i>"what was the average attendee's experience?"</i>
    /// and is the correct figure for an event-level headline and for the signage feed.
    ///
    /// <para>🔑 Below-threshold sessions still CONTRIBUTE here: the threshold protects against
    /// small-sample volatility, and an aggregate is not a small sample (§5.1).</para>
    /// </remarks>
    /// <param name="requireMinimum">
    /// §754.1 — defaults to <b>false</b> for the pooled figure: the event-wide score publishes from
    /// the first response. The floor is a per-session protection (§5.1) and does not carry here.
    /// Callers wanting the old behaviour pass true explicitly.
    /// </param>
    public static Result Pooled(
        IEnumerable<Distribution> sessions, string profile = ProfileLinear,
        bool requireMinimum = false)
    {
        int f = 0, t = 0, tw = 0, o = 0;
        foreach (var d in sessions) { f += d.Four; t += d.Three; tw += d.Two; o += d.One; }
        return Compute(new Distribution(f, t, tw, o), profile, requireMinimum);
    }

    /// <summary>
    /// MEAN SESSION — the unweighted mean of the individual session scores, EXCLUDING sessions below
    /// the response threshold.
    /// </summary>
    /// <remarks>
    /// Treats every session equally regardless of attendance. It answers <i>"how good was the
    /// programme?"</i> and is the right figure for comparing tracks or years, where a change in room
    /// sizes should not move the number.
    ///
    /// <para>🔒 <b>This and <see cref="Pooled"/> produce DIFFERENT numbers and must never be
    /// presented interchangeably or under a shared label.</b> Both are legitimate; each has to be
    /// named explicitly wherever it appears. Returns null when no session clears the threshold —
    /// there is nothing to average, which is not the same as zero.</para>
    /// </remarks>
    public static double? MeanSession(IEnumerable<Distribution> sessions, string profile = ProfileLinear)
    {
        var scores = sessions
            .Select(d => Compute(d, profile).Score)
            .Where(s => s is not null)
            .Select(s => s!.Value)
            .ToList();

        return scores.Count == 0
            ? null
            : Math.Round(scores.Average(), 1, MidpointRounding.AwayFromZero);
    }
}
