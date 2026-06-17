using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Sponsors;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="SponsorLeadScoreExplainer"/> — the pure, single
/// source of truth for the sponsor-lead AI-screen score (REQUIREMENTS §21
/// organizer "explain AI scores"). Pins the factor breakdown, the
/// base/raw/final arithmetic, the verdict labels, AND the critical contract that
/// the live <see cref="SponsorLeadScreeningService"/> persists EXACTLY the score
/// the explainer reports (so the badge and the "why" can never drift). No DB /
/// clock / I/O. FAKE lead values only.
/// </summary>
public sealed class SponsorLeadScoreExplainerTests
{
    private static SponsorLead Lead(
        string? name = null, string? email = null,
        string? company = null, string? phone = null) => new()
    {
        FullName = name ?? string.Empty,
        Email    = email ?? string.Empty,
        Company  = company ?? string.Empty,
        Phone    = phone ?? string.Empty,
    };

    [Fact]
    public void A_complete_legit_lead_scores_every_positive_factor()
    {
        var b = SponsorLeadScoreExplainer.Compute(
            Lead("Jane Prospect", "jane@acme.test", "Acme A/S", "+45 11 22 33 44"));

        Assert.Equal(50, b.BaseScore);
        Assert.Equal("looks-legit", b.Label);
        Assert.False(b.LooksTest);
        // 50 + 20 + 10 + 15 + 5 = 100
        Assert.Equal(100, b.RawTotal);
        Assert.Equal(100, b.FinalScore);

        Assert.Collection(b.Factors,
            f => { Assert.Equal(SponsorLeadScoreExplainer.ReasonHasEmail, f.ReasonKey);   Assert.Equal(20, f.Points); },
            f => { Assert.Equal(SponsorLeadScoreExplainer.ReasonHasName, f.ReasonKey);     Assert.Equal(10, f.Points); },
            f => { Assert.Equal(SponsorLeadScoreExplainer.ReasonHasCompany, f.ReasonKey);  Assert.Equal(15, f.Points); },
            f => { Assert.Equal(SponsorLeadScoreExplainer.ReasonHasPhone, f.ReasonKey);    Assert.Equal(5, f.Points); });
    }

    [Fact]
    public void Sum_of_base_plus_factors_equals_raw_total()
    {
        // The breakdown must be arithmetically honest: base + every factor delta
        // reconstructs the raw (pre-clamp) total exactly. Use a phone-only lead so
        // the negative "unreachable" factor is NOT present but a missing-name path is.
        var b = SponsorLeadScoreExplainer.Compute(Lead(phone: "12345678"));

        var reconstructed = b.BaseScore + b.Factors.Sum(f => f.Points);
        Assert.Equal(b.RawTotal, reconstructed);
    }

    [Fact]
    public void An_unreachable_lead_takes_the_minus35_factor_and_is_labelled()
    {
        // No email, no phone -> the -35 unreachable penalty applies.
        var b = SponsorLeadScoreExplainer.Compute(Lead("No Contact"));

        Assert.Equal("unreachable", b.Label);
        Assert.Contains(b.Factors, f => f.ReasonKey == SponsorLeadScoreExplainer.ReasonUnreachable && f.Points == -35);
        // 50 + 10 (name) - 35 = 25
        Assert.Equal(25, b.RawTotal);
        Assert.Equal(25, b.FinalScore);
        Assert.False(b.LooksTest);
    }

    [Fact]
    public void A_lead_without_a_name_is_incomplete()
    {
        // Has email (reachable) but no usable name -> "incomplete".
        var b = SponsorLeadScoreExplainer.Compute(Lead(name: "Jo", email: "jo@acme.test"));

        Assert.Equal("incomplete", b.Label);
        Assert.DoesNotContain(b.Factors, f => f.ReasonKey == SponsorLeadScoreExplainer.ReasonHasName);
    }

    [Fact]
    public void A_test_pattern_caps_the_score_at_five_with_a_distinct_factor()
    {
        // "demo" in the name + an @example. address: even a fully-filled lead is
        // capped to 5 by the test-pattern ceiling, surfaced as its own factor.
        var b = SponsorLeadScoreExplainer.Compute(
            Lead("demo person", "x@example.com", "Acme", "12345678"));

        Assert.True(b.LooksTest);
        Assert.Equal("test-entry", b.Label);
        Assert.Equal(5, b.FinalScore);
        // The cap factor brings the running total down to 5.
        var capFactor = Assert.Single(b.Factors, f => f.ReasonKey == SponsorLeadScoreExplainer.ReasonLooksTest);
        Assert.Equal(5, b.RawTotal + capFactor.Points);
        Assert.True(capFactor.Points < 0);
    }

    [Fact]
    public void Final_score_is_clamped_into_zero_to_one_hundred()
    {
        var b = SponsorLeadScoreExplainer.Compute(
            Lead("Jane Prospect", "jane@acme.test", "Acme", "12345678"));
        Assert.InRange(b.FinalScore, 0, 100);

        var worst = SponsorLeadScoreExplainer.Compute(Lead()); // empty: base 50 - 35 = 15
        Assert.InRange(worst.FinalScore, 0, 100);
    }

    [Fact]
    public void Compute_never_mutates_the_lead()
    {
        var lead = Lead("Jane Prospect", "jane@acme.test", "Acme", "12345678");
        lead.AiScreenScore = null;
        lead.AiScreenLabel = null;

        SponsorLeadScoreExplainer.Compute(lead);

        // Pure: the explainer reads, never writes.
        Assert.Null(lead.AiScreenScore);
        Assert.Null(lead.AiScreenLabel);
    }

    [Theory]
    [InlineData("Jane Prospect", "jane@acme.test", "Acme", "12345678")] // looks-legit / 100
    [InlineData("No Contact", "", "", "")]                              // unreachable / 25
    [InlineData("demo", "x@example.com", "", "")]                       // test-entry / 5
    [InlineData("Jo", "jo@acme.test", "", "")]                          // incomplete
    public void Screening_service_persists_exactly_what_the_explainer_reports(
        string name, string email, string company, string phone)
    {
        // The drift guard: the live screen and the explainer must agree on the
        // number AND the label for every shape, because the screen delegates to
        // the explainer. If someone re-forks the math, this fails.
        var lead = Lead(name, email, company, phone);
        var expected = SponsorLeadScoreExplainer.Compute(lead);

        new SponsorLeadScreeningService().Screen(lead, DateTimeOffset.UtcNow);

        Assert.Equal(expected.FinalScore, lead.AiScreenScore);
        Assert.Equal(expected.Label, lead.AiScreenLabel);
    }

    [Fact]
    public void Null_lead_throws()
    {
        Assert.Throws<ArgumentNullException>(() => SponsorLeadScoreExplainer.Compute(null!));
    }
}
