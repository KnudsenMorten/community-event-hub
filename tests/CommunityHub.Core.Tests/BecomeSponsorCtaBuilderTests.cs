using CommunityHub.Core.Sponsors;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="BecomeSponsorCtaBuilder"/> — the pure builder for
/// the public "become a sponsor" CTA (REQUIREMENTS §21). Pins the precedence
/// (external URL over email), the mailto subject building + event-name substitution,
/// and the "nothing configured ⇒ no CTA" contract so the public sponsors page never
/// renders a dead link. No DB / clock / I/O. FAKE contact values only.
/// </summary>
public sealed class BecomeSponsorCtaBuilderTests
{
    [Fact]
    public void Null_options_yields_no_cta()
    {
        Assert.Null(BecomeSponsorCtaBuilder.Build(null, "Test Community 2027"));
    }

    [Fact]
    public void Nothing_configured_yields_no_cta()
    {
        var opts = new BecomeSponsorOptions(); // both contact fields blank
        Assert.Null(BecomeSponsorCtaBuilder.Build(opts, "Test Community 2027"));
    }

    [Fact]
    public void Blank_or_whitespace_contacts_yield_no_cta()
    {
        var opts = new BecomeSponsorOptions { ContactEmail = "   ", ContactUrl = "  " };
        Assert.Null(BecomeSponsorCtaBuilder.Build(opts, "Test Community 2027"));
    }

    [Fact]
    public void Email_only_builds_a_mailto_with_event_stamped_subject()
    {
        var opts = new BecomeSponsorOptions { ContactEmail = "sponsor@fake.test" };

        var cta = BecomeSponsorCtaBuilder.Build(opts, "Test Community 2027");

        Assert.NotNull(cta);
        Assert.False(cta!.IsExternal); // mailto is not "external/new tab"
        Assert.StartsWith("mailto:sponsor@fake.test?subject=", cta.Href);
        // Default subject carries the event name (URL-encoded).
        Assert.Contains(Uri.EscapeDataString("Sponsorship enquiry — Test Community 2027"), cta.Href);
    }

    [Fact]
    public void Custom_subject_format_substitutes_the_event_name()
    {
        var opts = new BecomeSponsorOptions
        {
            ContactEmail = "sponsor@fake.test",
            EmailSubjectFormat = "Sponsor {0} please",
        };

        var cta = BecomeSponsorCtaBuilder.Build(opts, "FakeFest");

        Assert.NotNull(cta);
        Assert.Contains(Uri.EscapeDataString("Sponsor FakeFest please"), cta!.Href);
    }

    [Fact]
    public void Null_event_name_still_builds_a_valid_mailto()
    {
        var opts = new BecomeSponsorOptions { ContactEmail = "sponsor@fake.test" };

        var cta = BecomeSponsorCtaBuilder.Build(opts, eventDisplayName: null);

        Assert.NotNull(cta);
        Assert.StartsWith("mailto:sponsor@fake.test", cta!.Href);
    }

    [Fact]
    public void Contact_url_wins_over_email_and_opens_external()
    {
        var opts = new BecomeSponsorOptions
        {
            ContactEmail = "sponsor@fake.test",
            ContactUrl = "https://example.test/become-a-sponsor",
        };

        var cta = BecomeSponsorCtaBuilder.Build(opts, "Test Community 2027");

        Assert.NotNull(cta);
        Assert.True(cta!.IsExternal);
        Assert.Equal("https://example.test/become-a-sponsor", cta.Href);
        Assert.DoesNotContain("mailto:", cta.Href);
    }

    [Fact]
    public void Values_are_trimmed()
    {
        var opts = new BecomeSponsorOptions { ContactUrl = "  https://example.test/sponsor  " };

        var cta = BecomeSponsorCtaBuilder.Build(opts, "Test Community 2027");

        Assert.NotNull(cta);
        Assert.Equal("https://example.test/sponsor", cta!.Href);
    }
}
