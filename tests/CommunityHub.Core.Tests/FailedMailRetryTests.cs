using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §655 — the failed-mail retry job retries ONLY what fixes itself.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-29: <i>"build the retry job. i agree to your recommendation"</i> — only
/// the self-correcting failures, three attempts across about an hour, never a bad address.</para>
///
/// <para>🔒 <b>Re-sending mail is outward-facing.</b> Get the scope wrong and real people get
/// duplicates, or a dead address is hammered until it costs sending reputation. The decision below
/// is therefore a pure function, and these tests are the specification of what it may touch.</para>
/// </remarks>
public class FailedMailRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static bool ShouldRetry(
        string? error, int retryCount = 0, TimeSpan? age = null, DateTimeOffset? lastRetryAt = null,
        int? participantId = 57, string? template = "welcome-speaker", string? category = "welcome",
        bool alreadyDeliveredLater = false) =>
        FailedMailRetryService.ShouldRetry(
            error, retryCount, Now - (age ?? TimeSpan.FromMinutes(30)), lastRetryAt,
            participantId, template, category, Now, alreadyDeliveredLater);

    /// <summary>
    /// 🔒 §655.3 — THE DUPLICATE GUARD, found in production on the very first row this job touched.
    /// </summary>
    /// <remarks>
    /// The in-send retry (<c>SendMaxAttempts</c>) writes a SEPARATE log row per attempt, so a
    /// message that failed once and succeeded a second later leaves BOTH a Failed and a Sent row.
    /// Per Larsen's hotel calendar invite did exactly that — failed 13:42:34, delivered 13:42:34 —
    /// so the "Failed" row he was looking at had in fact reached him. Retrying it would have sent a
    /// duplicate of a message he already had.
    /// </remarks>
    [Fact]
    public void A_failure_ALREADY_SUPERSEDED_by_a_later_success_is_never_retried()
    {
        // Same error that would otherwise be retried — only the delivery evidence differs.
        Assert.True(ShouldRetry("The operation was canceled."));
        Assert.False(ShouldRetry("The operation was canceled.", alreadyDeliveredLater: true));
    }

    // ---------- what it DOES retry ----------

    [Fact]
    public void A_THROTTLED_send_is_retried()
    {
        Assert.True(ShouldRetry("429 Too Many Requests"));
    }

    [Fact]
    public void An_INTERRUPTED_send_is_retried()
    {
        // The shape that lost Per Larsen's hotel calendar invite: the app restarted mid-send, so
        // the message never reached Brevo at all.
        Assert.True(ShouldRetry("The operation was canceled."));
    }

    // ---------- 🔒 what it must NEVER retry ----------

    /// <summary>
    /// The single most important case. §650: retrying a dead address cannot succeed and actively
    /// harms our sending reputation — it needs a corrected address, from a human.
    /// </summary>
    [Fact]
    public void A_BAD_ADDRESS_is_never_retried()
    {
        Assert.False(ShouldRetry("550 5.1.1 Recipient address rejected: User unknown"));
    }

    [Fact]
    public void A_CREDENTIAL_failure_is_never_retried()
    {
        // §635: nothing is going out at all until a human reissues the key. Retrying just burns
        // attempts and hides the real problem.
        Assert.False(ShouldRetry("535 5.7.8 Authentication credentials invalid"));
    }

    [Fact]
    public void A_RECIPIENT_REJECTION_is_never_retried_automatically()
    {
        // Their server accepted the address and refused the message — a full mailbox or a policy
        // rule. That needs a conversation, not a louder retry.
        Assert.False(ShouldRetry("552 5.2.2 Mailbox full"));
    }

    /// <summary>
    /// 🔒 A ring drop is the system OBEYING the operator. Retrying it would resend mail he
    /// deliberately withheld — the single worst thing this job could do.
    /// </summary>
    [Fact]
    public void A_RING_DROPPED_message_is_never_retried()
    {
        Assert.False(ShouldRetry("Ring-dropped (recipient outside the released ring) - not sent."));
    }

    [Fact]
    public void A_SUCCESSFUL_send_is_never_retried()
    {
        Assert.False(ShouldRetry(null));
        Assert.False(ShouldRetry(""));
    }

    // ---------- the budget and the spacing ----------

    [Fact]
    public void The_budget_is_THREE_attempts()
    {
        Assert.True(ShouldRetry("429 throttled", retryCount: 2));
        Assert.False(ShouldRetry("429 throttled", retryCount: 3));
        Assert.Equal(3, FailedMailRetryService.MaxRetries);
    }

    [Fact]
    public void Attempts_are_SPACED_OUT_rather_than_hammered()
    {
        // Retrying instantly just re-hits whatever was throttling us.
        Assert.False(ShouldRetry("429 throttled", lastRetryAt: Now.AddMinutes(-5)));
        Assert.True(ShouldRetry("429 throttled", lastRetryAt: Now.AddMinutes(-21)));
    }

    /// <summary>
    /// Three attempts, twenty minutes apart, is "about an hour" — the shape he agreed to.
    /// </summary>
    [Fact]
    public void Three_attempts_at_that_spacing_span_roughly_an_hour()
    {
        var span = FailedMailRetryService.MinGapBetweenAttempts * FailedMailRetryService.MaxRetries;

        Assert.True(span >= TimeSpan.FromMinutes(50));
        Assert.True(span <= TimeSpan.FromMinutes(70));
    }

    /// <summary>
    /// 🔒 An old failure is HISTORY, not a backlog. Re-sending a three-day-old welcome would
    /// confuse the recipient more than the silence did.
    /// </summary>
    [Fact]
    public void A_failure_older_than_the_window_is_left_alone()
    {
        Assert.True(ShouldRetry("429 throttled", age: TimeSpan.FromHours(23)));
        Assert.False(ShouldRetry("429 throttled", age: TimeSpan.FromHours(25)));
    }

    // ---------- it must know WHO and WHAT to resend ----------

    [Fact]
    public void Without_a_participant_there_is_nobody_to_resend_to()
    {
        Assert.False(ShouldRetry("429 throttled", participantId: null));
        Assert.False(ShouldRetry("429 throttled", participantId: 0));
    }

    /// <summary>
    /// 🔒 §644's lesson: never guess the template. A guessed one sent a real speaker an unrelated
    /// onboarding mail.
    /// </summary>
    [Fact]
    public void Without_a_template_OR_category_it_refuses_rather_than_guessing()
    {
        Assert.False(ShouldRetry("429 throttled", template: null, category: null));
        Assert.False(ShouldRetry("429 throttled", template: "  ", category: "  "));
    }

    [Fact]
    public void The_CATEGORY_stands_in_when_no_template_was_recorded()
    {
        // TemplateName is blank on older rows; Category is always set.
        Assert.True(ShouldRetry("429 throttled", template: null, category: "calendar-invite"));
        Assert.Equal("calendar-invite", FailedMailRetryService.TemplateFor(null, "calendar-invite"));
        Assert.Equal("getstarted-digest", FailedMailRetryService.TemplateFor(null, "getstarted-digest:Speaker"));
    }
}
