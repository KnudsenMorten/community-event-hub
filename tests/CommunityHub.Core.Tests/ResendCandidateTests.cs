using CommunityHub.Core.Organizer;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §644 — "Resend" must resend THE MAIL THAT FAILED, and say where it is really going.
/// </summary>
/// <remarks>
/// <para><b>What the operator saw.</b> A row read <i>Per Larsen · per@famlarsen.se ·
/// "ELDK27 Hotel Reservation" · calendar-invite · Failed</i>. He pressed Resend and the
/// confirmation said <i>"Resent 'onboarding-getting-started' to per.larsen@microsoft.com"</i> — a
/// different template AND a different address than the row showed.</para>
///
/// <para><b>Both had innocent causes and both were still defects.</b> The template dropdown was
/// HARD-CODED to two options whatever had failed, so a failed calendar-invite could not be resent at
/// all and one click sent a real speaker an unrelated onboarding mail. The address was his CURRENT
/// one (correct — the logged address was historical), but nothing on the page said so.</para>
/// </remarks>
public class ResendCandidateTests
{
    private static ResendCandidate Row(
        string category, string? template = null, string logged = "old@example.com",
        string? current = null) =>
        new(ParticipantId: 57, Name: "Per Larsen", Email: logged, Subject: "ELDK27 Hotel Reservation",
            Category: category, Outcome: CommsOutcome.Failed, At: DateTimeOffset.UtcNow,
            Error: "The operation was canceled.", Template: template, CurrentEmail: current);

    // ---------- resend the thing that failed ----------

    [Fact]
    public void The_template_comes_from_the_mail_that_FAILED()
    {
        Assert.Equal("welcome-speaker", Row("welcome", "welcome-speaker").ResendTemplate);
    }

    [Fact]
    public void When_no_template_was_recorded_the_CATEGORY_is_used()
    {
        // TemplateName is blank on older EmailLog rows; Category is always set. His failed row had
        // exactly this shape — category "calendar-invite", no template.
        Assert.Equal("calendar-invite", Row("calendar-invite").ResendTemplate);
    }

    [Fact]
    public void A_qualified_category_is_reduced_to_the_template_name()
    {
        // Categories carry a suffix, e.g. "getstarted-digest:Speaker".
        Assert.Equal("getstarted-digest", Row("getstarted-digest:Speaker").ResendTemplate);
    }

    /// <summary>
    /// 🔒 If we cannot tell what failed, we must NOT guess. Guessing is precisely what sent a
    /// speaker an onboarding mail he had no reason to receive.
    /// </summary>
    [Fact]
    public void With_nothing_to_go_on_there_is_NO_template_and_the_page_hides_the_button()
    {
        Assert.Null(Row(category: "", template: null).ResendTemplate);
        Assert.Null(Row(category: "   ", template: "  ").ResendTemplate);
    }

    // ---------- say where it is really going ----------

    [Fact]
    public void A_CHANGED_address_is_flagged_so_the_page_can_warn()
    {
        var row = Row("calendar-invite", logged: "per@famlarsen.se",
            current: "per.larsen@microsoft.com");

        Assert.True(row.GoesToADifferentAddress);
    }

    [Fact]
    public void The_SAME_address_raises_no_warning()
    {
        Assert.False(Row("calendar-invite", logged: "per@x.dk", current: "per@x.dk")
            .GoesToADifferentAddress);
        // Case alone is not a difference.
        Assert.False(Row("calendar-invite", logged: "Per@X.dk", current: "per@x.dk")
            .GoesToADifferentAddress);
    }

    [Fact]
    public void An_unknown_current_address_raises_no_warning()
    {
        // Nothing to compare against is not evidence of a change — silence, not a scare.
        Assert.False(Row("calendar-invite", current: null).GoesToADifferentAddress);
    }
}
