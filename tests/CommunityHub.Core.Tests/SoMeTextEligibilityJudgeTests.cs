using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1060(l) — parsing the model's verdict.
/// </summary>
/// <remarks>
/// <para>🔴 <b>The property every case here defends: "could not ask" and "the answer is no" must stay
/// different values.</b> The judge returns <c>null</c> for an unusable answer and a verdict only when
/// the model actually said something. Collapse the two and an outage, a rate limit or a chatty model
/// reads as <i>"every session is ineligible"</i> — which, with §1060's window dropped, silently stops
/// the entire campaign and looks like nothing is scheduled rather than like a failure. That is
/// §858.16h's lesson (a throttled sweep is not a real negative) applied to a second data source.</para>
/// </remarks>
public sealed class SoMeTextEligibilityJudgeTests
{
    [Theory]
    [InlineData("{\"eligible\":1}")]
    [InlineData("{\"eligible\": 1, \"reason\": \"\"}")]
    [InlineData("{\"eligible\":true}")]
    [InlineData("```json\n{\"eligible\":1}\n```")]        // fenced despite being told not to
    [InlineData("Sure! {\"eligible\":1} — hope that helps")]  // prose either side
    public void An_eligible_verdict_parses(string content)
    {
        var v = SoMeTextEligibilityJudge.Parse(content);
        Assert.NotNull(v);
        Assert.True(v!.Eligible);
    }

    [Fact]
    public void A_refusal_carries_its_reason_so_the_blocker_can_quote_it()
    {
        var v = SoMeTextEligibilityJudge.Parse(
            "{\"eligible\":0,\"reason\":\"says the abstract will follow later\"}");

        Assert.NotNull(v);
        Assert.False(v!.Eligible);
        Assert.Equal("says the abstract will follow later", v.Reason);
    }

    /// <summary>
    /// 🔴 Each of these is a way the model can fail to answer. None may become a 0.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I'm sorry, I can't help with that.")]      // refusal, no JSON
    [InlineData("{\"verdict\":1}")]                          // right shape, wrong key
    [InlineData("{\"eligible\":\"maybe\"}")]                 // unparseable value
    [InlineData("{\"eligible\":1")]                          // truncated — a cut-off response
    public void An_unusable_answer_is_NOT_a_refusal(string? content) =>
        Assert.Null(SoMeTextEligibilityJudge.Parse(content));

    /// <summary>
    /// 🔒 The judge and the rules must agree at the boundary they share, BY CONSTRUCTION: the judge
    /// short-circuits on <see cref="SoMePlaceholderText"/> before spending a call. Pinned because a
    /// later edit could easily make one lenient where the other is strict, and the disagreement
    /// would only ever show up as a session that is eligible on one screen and not on another.
    /// </summary>
    [Fact]
    public void The_two_implementations_agree_on_the_certain_cases()
    {
        Assert.True(SoMePlaceholderText.IsMissingOrPlaceholder("TBD"));
        Assert.True(SoMePlaceholderText.IsMissingOrPlaceholder(null));
        Assert.False(SoMePlaceholderText.IsMissingOrPlaceholder(
            "A practical hour on Kubernetes cost control."));
    }
}
