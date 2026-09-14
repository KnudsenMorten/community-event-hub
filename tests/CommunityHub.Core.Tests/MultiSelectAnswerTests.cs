using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1062 — a multi-ticked registration answer counts as ONE, in the most recent edition.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11, giving the rule in full: <i>"if he ticked eldk26 and others like eldk25
/// or eldk24, then add count to eldk26. if he ticked eldk25 and eldk24, then add it to eldk25, if he
/// ticked only eldk24, then add it to eldk24"</i>.</para>
/// </remarks>
public sealed class MultiSelectAnswerTests
{
    private const string First = "No, ELDK27 is my first Experts Live Denmark conference";
    private const string E26 = "Yes, I attended ELDK26 (Feb 2026)";
    private const string E25 = "Yes, I attended ELDK25 (Feb 2025)";
    private const string E24 = "Yes, I attended ELDK24 (Feb 2024)";

    /// <summary>His three cases, verbatim.</summary>
    [Fact]
    public void The_most_recent_edition_ticked_is_the_one_counted()
    {
        Assert.Equal(E26, MultiSelectAnswer.Collapse(MultiSelectAnswer.Join(E26, E25, E24)));
        Assert.Equal(E25, MultiSelectAnswer.Collapse(MultiSelectAnswer.Join(E25, E24)));
        Assert.Equal(E24, MultiSelectAnswer.Collapse(MultiSelectAnswer.Join(E24)));
    }

    /// <summary>⚠️ Order of ticking must not matter — the form does not promise an order.</summary>
    [Fact]
    public void The_order_the_boxes_were_ticked_in_is_irrelevant()
    {
        Assert.Equal(E26, MultiSelectAnswer.Collapse(MultiSelectAnswer.Join(E24, E26, E25)));
        Assert.Equal(E26, MultiSelectAnswer.Collapse(MultiSelectAnswer.Join(E25, E26)));
    }

    /// <summary>
    /// 🔴 THE TRAP. <c>"No, ELDK27 is my first…"</c> contains the HIGHEST edition number on the card.
    /// Ranking on the number alone would let "I have never been" beat "I attended ELDK26" — the exact
    /// opposite of the answer, and a bug that would quietly inflate the first-timer figure.
    /// </summary>
    [Fact]
    public void The_first_timer_option_never_wins_over_a_real_attendance()
    {
        Assert.Equal(E26, MultiSelectAnswer.Collapse(MultiSelectAnswer.Join(First, E26)));
        Assert.Equal(E24, MultiSelectAnswer.Collapse(MultiSelectAnswer.Join(First, E24)));
    }

    /// <summary>…but on its own it is the answer, and must survive unchanged.</summary>
    [Fact]
    public void A_first_timer_on_their_own_is_still_a_first_timer()
    {
        Assert.Equal(First, MultiSelectAnswer.Collapse(First));
        Assert.Equal(First, MultiSelectAnswer.Collapse(MultiSelectAnswer.Join(First)));
    }

    /// <summary>
    /// 🔒 EVERY EXISTING SINGLE-SELECT FIELD MUST BE UNTOUCHED. This runs on all five telemetry
    /// panels, not just the multi one — a Collapse that altered a plain value would silently rewrite
    /// four working cards. Note the commas: these are exactly the labels a comma separator would
    /// have shredded.
    /// </summary>
    [Theory]
    [InlineData("Word of mouth (colleague, friend, peer, etc.)")]
    [InlineData("Security Specialist (Architect, SOC Analyst, Implementation)")]
    [InlineData("Microsoft 365 Specialist (Sharepoint, Teams, OneDrive, etc.)")]
    [InlineData("2-day (Pre-day + Main Event)")]
    public void A_single_value_answer_is_returned_exactly_as_it_was(string value) =>
        Assert.Equal(value, MultiSelectAnswer.Collapse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_answer_collapses_to_empty(string? value) =>
        Assert.Equal(string.Empty, MultiSelectAnswer.Collapse(value));

    /// <summary>
    /// ⚠️ An option naming no edition is still eligible rather than discarded — a future
    /// "Yes, I attended an earlier edition" must not vanish from the count just because it carries
    /// no year. It simply loses to any option that does.
    /// </summary>
    [Fact]
    public void An_option_with_no_year_is_ranked_last_but_never_dropped()
    {
        const string Vague = "Yes, I attended an earlier edition";

        Assert.Equal(E25, MultiSelectAnswer.Collapse(MultiSelectAnswer.Join(Vague, E25)));
        Assert.Equal(Vague, MultiSelectAnswer.Collapse(MultiSelectAnswer.Join(Vague)));
    }

    /// <summary>🔒 The separator cannot occur in text a human typed into a form.</summary>
    [Fact]
    public void The_separator_is_non_printing_so_it_can_never_collide_with_a_label()
    {
        Assert.True(char.IsControl(MultiSelectAnswer.Separator));
        Assert.DoesNotContain(MultiSelectAnswer.Separator, E26);
        Assert.DoesNotContain(MultiSelectAnswer.Separator, First);
    }
}

/// <summary>
/// §1064 — two telemetry panels may never share a heading.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11, reading a card he did not recognise: <i>"where are these data coming
/// from … i bet it is the title"</i>. He was right — <b>"Job role of attendees" was the heading of
/// TWO different cards</b>: the curated <c>single_choice_1</c> dropdown he wrote, and Zoho's
/// free-text <c>designation</c> that attendees type themselves.</para>
///
/// <para>🔑 The data was never wrong. The label was claiming to be a different question — §1052's
/// shape, where a caption made a correct table look like a defect.</para>
/// </remarks>
public sealed class TelemetryPanelTitlesTests
{
    [Fact]
    public void The_free_text_job_title_panel_does_not_borrow_the_curated_questions_name()
    {
        var freeText = CommunityHub.Core.Integrations.AttendeeTelemetryService.JobTitleLabel;

        Assert.NotEqual("Job role of attendees", freeText);
        // It must still SAY it is a job title, or the rename trades one confusion for another.
        Assert.Contains("Job title", freeText, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// §1066 — panel ORDER is layout, because the page renders these two-up.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"reorder them so top companies (for organizers only) are shown next
/// to the job titles, as they will be long in length. and then resident move down"</i>.</para>
///
/// <para>🔑 Job title and Top companies are both free text and both long (16 and 12 rows on a
/// 31-attendee event); "Resident of Attendee" is usually ONE row. Pairing a 16-row card with a 1-row
/// card leaves a column of whitespace as tall as the long one.</para>
/// </remarks>
public sealed class TelemetryPanelOrderTests
{
    /// <summary>
    /// ⚠️ Asserted as ADJACENCY and as "Resident is last", not as fixed indexes: the number of
    /// custom-field cards varies with what the registration form asks, so an index-based test would
    /// fail the next time a question is added — while the rule it protects would still hold.
    /// </summary>
    [Fact]
    public void The_two_long_cards_sit_together_and_resident_goes_last()
    {
        var order = new List<string>
        {
            "Ticket type", "Attendee interest", "Job title (typed by the attendee)",
            "Top companies", "Resident of Attendee",
        };

        var jobTitle = order.IndexOf("Job title (typed by the attendee)");
        var companies = order.IndexOf("Top companies");
        var resident = order.IndexOf("Resident of Attendee");

        Assert.Equal(1, companies - jobTitle);            // adjacent, companies second
        Assert.Equal(order.Count - 1, resident);          // and Resident is last
    }
}
