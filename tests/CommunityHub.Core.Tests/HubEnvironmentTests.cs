using CommunityHub.Core.Diagnostics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §702 / §703 — the DEV/PROD label behind the alert-subject tag and the Settings badge.
///
/// <para>The test that matters is <see cref="The_trap_that_made_this_class_necessary"/>: the
/// obvious implementation (<c>IHostEnvironment.EnvironmentName</c>) returns "Production" in BOTH
/// editions, so it would label every DEV alert [PROD]. These pin the rules that avoid that.</para>
/// </summary>
public sealed class HubEnvironmentTests
{
    [Theory]
    // The explicit setting wins, in the spellings an operator might actually type.
    [InlineData("PROD", null, "PROD")]
    [InlineData("prod", null, "PROD")]
    [InlineData("Production", null, "PROD")]
    [InlineData("DEV", null, "DEV")]
    [InlineData("dev", null, "DEV")]
    [InlineData("Development", null, "DEV")]
    // No setting ⇒ fall back to the App Service site name (present on web AND Functions hosts).
    [InlineData(null, "communityhub-web-prodx1", "PROD")]
    [InlineData(null, "communityhub-fn-prodx1", "PROD")]
    [InlineData(null, "communityhub-web-devx1", "DEV")]
    [InlineData(null, "communityhub-fn-devx1", "DEV")]
    // The real staging slot name must still read PROD — it is the prod app.
    [InlineData(null, "communityhub-web-prodx1__staging", "PROD")]
    // Blank/whitespace is not a value.
    [InlineData("", "communityhub-fn-devx1", "DEV")]
    [InlineData("   ", "communityhub-web-prodx1", "PROD")]
    public void Resolves_the_environment_label(string? configured, string? site, string expected) =>
        Assert.Equal(expected, HubEnvironment.Resolve(configured, site));

    /// <summary>
    /// 🔒 Nothing to go on ⇒ say UNKNOWN. Never guess. An alert that names the wrong environment
    /// sends someone to the wrong place — §701 is exactly that cost, paid for real.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "")]
    [InlineData(null, "some-unrelated-host")]
    public void Falls_back_to_UNKNOWN_rather_than_guessing(string? configured, string? site) =>
        Assert.Equal("UNKNOWN", HubEnvironment.Resolve(configured, site));

    /// <summary>
    /// The setting outranks the site name — that is the whole point of having an explicit one,
    /// and it is how a future host whose name encodes nothing can still be labelled correctly.
    /// </summary>
    [Fact]
    public void An_explicit_label_beats_the_site_name()
    {
        Assert.Equal("DEV", HubEnvironment.Resolve("DEV", "communityhub-web-prodx1"));
        Assert.Equal("PROD", HubEnvironment.Resolve("PROD", "communityhub-fn-devx1"));
    }

    /// <summary>
    /// An unrecognised explicit value is surfaced as typed (upper-cased), NOT silently mapped to
    /// DEV or PROD. A typo must be visible in the subject line, not disguised as a real answer.
    /// </summary>
    [Fact]
    public void An_unrecognised_label_is_surfaced_not_mapped() =>
        Assert.Equal("STAGING2", HubEnvironment.Resolve("staging2", "communityhub-web-prodx1"));

    /// <summary>
    /// 🔒 THE REGRESSION THIS CLASS EXISTS FOR. Both live editions report
    /// <c>ASPNETCORE_ENVIRONMENT=Production</c> (the DEV Functions app sets nothing at all, which
    /// also defaults to Production). Anyone tempted to "simplify" this back to the host environment
    /// name should read this test: the two editions are INDISTINGUISHABLE by that value, and the
    /// site name is what separates them.
    /// </summary>
    [Fact]
    public void The_trap_that_made_this_class_necessary()
    {
        const string whatBothEditionsReport = "Production";

        // Fed the host's environment name, dev and prod are the same string — no tag could work.
        Assert.Equal(
            HubEnvironment.Resolve(whatBothEditionsReport, null),
            HubEnvironment.Resolve(whatBothEditionsReport, null));

        // Fed what this class actually uses, they separate cleanly.
        Assert.NotEqual(
            HubEnvironment.Resolve(null, "communityhub-fn-devx1"),
            HubEnvironment.Resolve(null, "communityhub-fn-prodx1"));
    }

    [Fact]
    public void Subject_tag_is_bracketed()
    {
        Assert.Equal("[DEV]", new HubEnvironment("DEV", null).SubjectTag);
        Assert.Equal("[PROD]", new HubEnvironment(null, "communityhub-web-prodx1").SubjectTag);
        Assert.Equal("[UNKNOWN]", new HubEnvironment(null, null).SubjectTag);
        Assert.False(new HubEnvironment(null, null).IsKnown);
        Assert.True(new HubEnvironment("PROD", null).IsKnown);
    }
}
