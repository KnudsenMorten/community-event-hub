using CommunityHub.Core.Evaluation;
using Xunit;

using Inputs = CommunityHub.Core.Evaluation.EvaluationReportBuilder.VersionInputs;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §747 C8 — the report VERSION identifier.
/// </summary>
/// <remarks>
/// The brief's requirement is precise: <i>"Report versions are superseded when a session is
/// recomputed after late data, so consumers must be able to detect that a newer version exists."</i>
/// That is one property in two halves, and both halves are failure modes:
/// <list type="bullet">
///   <item>a version that DOESN'T change when the figures do ⇒ a consumer archives a stale report
///   as final, which is serious because the PDF becomes the permanent record once raw responses are
///   purged at 12 months;</item>
///   <item>a version that DOES change when nothing has ⇒ every poll looks like new data, and the
///   consumer either re-downloads forever or stops trusting the signal.</item>
/// </list>
/// Each test below pins one or the other.
/// </remarks>
public sealed class EvaluationReportVersionTests
{
    private static readonly DateTimeOffset T0 = new(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);

    private static Inputs Baseline() => new(
        SessionResponses: 12,
        SessionLatestReceived: T0,
        EventResponses: 400,
        EventLatestReceived: T0.AddMinutes(5),
        SessionUpdatedAt: T0.AddDays(-1));

    // ---- stability -------------------------------------------------------------------------

    /// <summary>The same inputs must always give the same version, or polling is meaningless.</summary>
    [Fact]
    public void Same_inputs_give_the_same_version()
    {
        Assert.Equal(
            EvaluationReportBuilder.DeriveVersion(Baseline()),
            EvaluationReportBuilder.DeriveVersion(Baseline()));
    }

    /// <summary>
    /// 🔒 <b>The version must NOT contain "now".</b> This is the trap §744 called out: a timestamp of
    /// generation time changes on every request, so a consumer would be told the report changed each
    /// time it asked. The derivation takes no clock at all — this test pins that by deriving twice
    /// across real elapsed time.
    /// </summary>
    [Fact]
    public void Version_does_not_move_with_wall_clock_time()
    {
        var first = EvaluationReportBuilder.DeriveVersion(Baseline());
        Thread.Sleep(15);
        var second = EvaluationReportBuilder.DeriveVersion(Baseline());
        Assert.Equal(first, second);
    }

    /// <summary>An identity must not vary with the server's time zone.</summary>
    [Fact]
    public void Equivalent_instants_in_different_offsets_are_one_version()
    {
        var utc = Baseline() with { SessionLatestReceived = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero) };
        var cet = Baseline() with { SessionLatestReceived = new DateTimeOffset(2027, 2, 9, 11, 0, 0, TimeSpan.FromHours(1)) };

        Assert.Equal(
            EvaluationReportBuilder.DeriveVersion(utc),
            EvaluationReportBuilder.DeriveVersion(cet));
    }

    // ---- sensitivity -----------------------------------------------------------------------

    /// <summary>A new press in this session changes the figures, so it must change the version.</summary>
    [Fact]
    public void A_new_response_in_the_session_changes_the_version()
    {
        var after = Baseline() with
        {
            SessionResponses = 13,
            SessionLatestReceived = T0.AddMinutes(30),
            EventResponses = 401,
            EventLatestReceived = T0.AddMinutes(30),
        };

        Assert.NotEqual(
            EvaluationReportBuilder.DeriveVersion(Baseline()),
            EvaluationReportBuilder.DeriveVersion(after));
    }

    /// <summary>
    /// 🔑 A press in ANOTHER room changes this document too — the report prints the event POOLED
    /// figure beside the session's, so the page genuinely differs. Not over-signalling.
    /// </summary>
    [Fact]
    public void A_response_in_another_session_changes_the_version()
    {
        var after = Baseline() with
        {
            EventResponses = 401,
            EventLatestReceived = T0.AddMinutes(30),
        };

        Assert.NotEqual(
            EvaluationReportBuilder.DeriveVersion(Baseline()),
            EvaluationReportBuilder.DeriveVersion(after));
    }

    /// <summary>
    /// 🔑 A C3 sync can retitle a session or move it to another room with NO new response at all,
    /// and the report's header would then be stale. Response counts alone would miss this entirely.
    /// </summary>
    [Fact]
    public void A_session_metadata_change_changes_the_version()
    {
        var after = Baseline() with { SessionUpdatedAt = T0.AddHours(2) };

        Assert.NotEqual(
            EvaluationReportBuilder.DeriveVersion(Baseline()),
            EvaluationReportBuilder.DeriveVersion(after));
    }

    /// <summary>
    /// 🔒 <b>Count alone is not enough.</b> The 12-month purge, and any future correction row, can
    /// change the SET of responses without changing its size. The latest received timestamp is what
    /// catches that.
    /// </summary>
    [Fact]
    public void Same_count_but_different_latest_received_is_a_new_version()
    {
        var after = Baseline() with { SessionLatestReceived = T0.AddMinutes(1) };

        Assert.NotEqual(
            EvaluationReportBuilder.DeriveVersion(Baseline()),
            EvaluationReportBuilder.DeriveVersion(after));
    }

    /// <summary>
    /// A session with no responses yet must still have a stable version, and must be distinguishable
    /// from one whose only response arrived at the epoch — hence "-" rather than a default date.
    /// </summary>
    [Fact]
    public void No_responses_is_a_distinct_stable_version()
    {
        var empty = Baseline() with { SessionResponses = 0, SessionLatestReceived = null };
        var epoch = Baseline() with
        {
            SessionResponses = 0,
            SessionLatestReceived = DateTimeOffset.UnixEpoch,
        };

        Assert.Equal(
            EvaluationReportBuilder.DeriveVersion(empty),
            EvaluationReportBuilder.DeriveVersion(empty));
        Assert.NotEqual(
            EvaluationReportBuilder.DeriveVersion(empty),
            EvaluationReportBuilder.DeriveVersion(epoch));
    }

    // ---- shape -----------------------------------------------------------------------------

    /// <summary>
    /// 🔒 Opaque and safe to put in an ETag: hex only, so it needs no quoting or escaping, and it
    /// leaks nothing — the raw material includes the event's total response count, which a consumer
    /// entitled to ONE session has no business reading.
    /// </summary>
    [Fact]
    public void Version_is_an_opaque_hex_token()
    {
        var v = EvaluationReportBuilder.DeriveVersion(Baseline());

        Assert.Equal(32, v.Length);
        Assert.All(v, c => Assert.True(
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'), $"unexpected character '{c}'"));
        Assert.DoesNotContain("400", v, StringComparison.Ordinal);   // the event total, not leaked verbatim
    }
}
