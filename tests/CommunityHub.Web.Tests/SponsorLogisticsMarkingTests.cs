using CommunityHub.Pages.Sponsor;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Sponsor logistics box-marking resolution (REQUIREMENTS §174). The marking lines used
/// to read a literal "&lt;your company&gt;" placeholder and the DSV freight block had no
/// marking at all. These cover the pure resolvers the page model delegates to:
///   • §174a — the company token is the resolved public/legal name, degrading to a soft
///     "your company" (never the "&lt;your company&gt;" placeholder).
///   • §174b — the DSV line reads "&lt;edition&gt; / Booth &lt;number&gt; / &lt;company&gt;",
///     degrading the booth to "Booth (TBA)" when unassigned. FAKE data only.
/// </summary>
public sealed class SponsorLogisticsMarkingTests
{
    [Fact]
    public void Company_token_uses_resolved_name_and_never_renders_the_placeholder()
    {
        Assert.Equal("Acme A/S", LogisticsModel.ResolveBoxMarkingCompany("Acme A/S"));
        // Fail-soft: a missing/blank name degrades to a neutral token, not the placeholder.
        Assert.Equal("your company", LogisticsModel.ResolveBoxMarkingCompany(null));
        Assert.Equal("your company", LogisticsModel.ResolveBoxMarkingCompany("   "));
        Assert.DoesNotContain("<your company>", LogisticsModel.ResolveBoxMarkingCompany(null));
    }

    [Fact]
    public void Dsv_marking_includes_edition_booth_and_company()
    {
        var marking = LogisticsModel.ResolveDsvBoxMarking("ELDK27", "B-12", "Acme A/S");
        Assert.Equal("ELDK27 / Booth B-12 / Acme A/S", marking);
    }

    [Fact]
    public void Dsv_marking_degrades_to_booth_tba_when_no_booth_assigned()
    {
        Assert.Equal("ELDK27 / Booth (TBA) / Acme A/S",
            LogisticsModel.ResolveDsvBoxMarking("ELDK27", null, "Acme A/S"));
        Assert.Equal("ELDK27 / Booth (TBA) / your company",
            LogisticsModel.ResolveDsvBoxMarking("ELDK27", "  ", null));
    }
}
