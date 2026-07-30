using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §656 — a failure that was already superseded by a successful send is NOT undelivered.
/// </summary>
/// <remarks>
/// <para><b>Found in production (§655.3).</b> <c>SendMaxAttempts</c> retries inside a single send and
/// writes a SEPARATE log row per attempt, so a message that fails once and succeeds a second later
/// leaves BOTH a Failed row and a Sent row. Per Larsen's hotel calendar invite failed at 13:42:34 and
/// was delivered at 13:42:34 — the "Failed" row the operator was asking about had reached him.</para>
///
/// <para>Two surfaces depend on this and must agree: the retry job (which would otherwise send a
/// DUPLICATE) and the Comms page (which would otherwise report a delivered message as undelivered).
/// The rule lives in one place because §637 is what happens when the same judgement is written twice
/// and only one copy gets fixed.</para>
/// </remarks>
public class SupersededSendTests
{
    private static readonly DateTimeOffset Failed = new(2026, 7, 28, 13, 42, 34, TimeSpan.Zero);

    private static SupersededSendDetector.Delivery Sent(
        int pid, string category, DateTimeOffset at) => new(pid, category, at);

    /// <summary>
    /// The exact production case: the successful retry landed in the SAME SECOND as the failure.
    /// A strictly-after comparison would have missed it entirely.
    /// </summary>
    [Fact]
    public void A_success_in_the_SAME_SECOND_supersedes_the_failure()
    {
        var delivered = new[] { Sent(57, "calendar-invite", Failed) };

        Assert.True(SupersededSendDetector.IsSuperseded(57, "calendar-invite", Failed, delivered));
    }

    [Fact]
    public void A_success_LATER_supersedes_the_failure()
    {
        var delivered = new[] { Sent(57, "calendar-invite", Failed.AddMinutes(2)) };

        Assert.True(SupersededSendDetector.IsSuperseded(57, "calendar-invite", Failed, delivered));
    }

    // ---------- 🔒 and everything that must NOT count as delivered ----------

    [Fact]
    public void A_success_BEFORE_the_failure_does_not_supersede_it()
    {
        // An earlier delivery of the same kind is a different message — last week's welcome does
        // not mean today's failed one arrived.
        var delivered = new[] { Sent(57, "calendar-invite", Failed.AddMinutes(-5)) };

        Assert.False(SupersededSendDetector.IsSuperseded(57, "calendar-invite", Failed, delivered));
    }

    [Fact]
    public void A_success_to_a_DIFFERENT_person_does_not_supersede_it()
    {
        var delivered = new[] { Sent(99, "calendar-invite", Failed.AddMinutes(1)) };

        Assert.False(SupersededSendDetector.IsSuperseded(57, "calendar-invite", Failed, delivered));
    }

    [Fact]
    public void A_DIFFERENT_message_to_the_same_person_does_not_supersede_it()
    {
        // Receiving a welcome does not mean the hotel invite arrived.
        var delivered = new[] { Sent(57, "welcome", Failed.AddMinutes(1)) };

        Assert.False(SupersededSendDetector.IsSuperseded(57, "calendar-invite", Failed, delivered));
    }

    [Fact]
    public void With_no_deliveries_at_all_nothing_is_superseded()
    {
        Assert.False(SupersededSendDetector.IsSuperseded(
            57, "calendar-invite", Failed, Array.Empty<SupersededSendDetector.Delivery>()));
    }

    [Fact]
    public void Without_a_participant_the_question_cannot_be_answered()
    {
        // No person means no way to match a delivery — and guessing "delivered" would HIDE a real
        // failure, which is the dangerous direction to be wrong in.
        var delivered = new[] { Sent(57, "calendar-invite", Failed) };

        Assert.False(SupersededSendDetector.IsSuperseded(null, "calendar-invite", Failed, delivered));
        Assert.False(SupersededSendDetector.IsSuperseded(0, "calendar-invite", Failed, delivered));
    }

    [Fact]
    public void Category_matching_ignores_case()
    {
        var delivered = new[] { Sent(57, "Calendar-Invite", Failed) };

        Assert.True(SupersededSendDetector.IsSuperseded(57, "calendar-invite", Failed, delivered));
    }
}
