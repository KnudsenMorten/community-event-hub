using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §824.10 — the OAuth scopes the LinkedIn connect asks to have approved.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: <i>"add the read scope, then i will do the connect"</i>. Two WRITE
/// scopes can post but cannot look anything up, so a sponsor's organization id — what
/// <c>{SponsorLinkedInUrl}</c> needs to become a real company mention (§824.3) — was unreachable by
/// construction.</para>
///
/// <para>🔒 The list is CONFIGURATION, and these tests exist to keep it that way. LinkedIn refuses
/// the entire consent screen when the app lacks product access for a single requested scope, so the
/// recovery path — put the two write scopes back via <c>LinkedIn__Scopes</c> — must work without a
/// redeploy, in the middle of him trying to connect.</para>
/// </remarks>
public sealed class LinkedInScopeTests
{
    [Fact]
    public void The_default_asks_for_the_two_write_scopes_and_the_two_read_scopes()
    {
        var scopes = new LinkedInOptions().ScopeList;

        // The write scopes: posting. Removing either breaks publishing outright.
        Assert.Contains("w_member_social", scopes);
        Assert.Contains("w_organization_social", scopes);
        // The read scopes: the company-id lookup behind {SponsorLinkedInUrl}.
        Assert.Contains("r_organization_admin", scopes);
        Assert.Contains("r_organization_social", scopes);
        Assert.Equal(4, scopes.Count);
    }

    [Fact]
    public void Posting_scopes_survive_the_fallback_he_would_reach_for()
    {
        // ⚠️ The documented recovery when the consent screen errors. It must leave posting intact —
        // the reads are an enhancement to tagging, never a prerequisite for publishing.
        var opts = new LinkedInOptions { Scopes = "w_member_social w_organization_social" };

        Assert.Equal(new[] { "w_member_social", "w_organization_social" }, opts.ScopeList);
    }

    [Theory]
    [InlineData("  w_member_social   w_organization_social  ")]
    [InlineData("w_member_social\tw_organization_social")]
    public void Sloppy_spacing_in_an_app_setting_does_not_produce_a_broken_scope(string configured)
    {
        // A scope list is typed into the Azure portal by hand under time pressure. An empty or
        // whitespace-padded entry would be sent to LinkedIn verbatim and rejected as an unknown
        // scope — failing the connect for a reason that looks nothing like a stray space.
        var scopes = new LinkedInOptions { Scopes = configured }.ScopeList;

        Assert.All(scopes, s => Assert.Equal(s.Trim(), s));
        Assert.All(scopes, s => Assert.NotEqual(string.Empty, s));
    }

    [Fact]
    public void An_empty_setting_yields_no_scopes_rather_than_one_blank_one()
    {
        Assert.Empty(new LinkedInOptions { Scopes = "" }.ScopeList);
        Assert.Empty(new LinkedInOptions { Scopes = "   " }.ScopeList);
    }
}
