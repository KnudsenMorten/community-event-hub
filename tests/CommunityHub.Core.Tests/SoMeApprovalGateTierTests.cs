using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §865.4 — a TIER post (Type 3) is a sponsor post too, and the readiness guard never ran on it.
/// </summary>
/// <remarks>
/// 🔴 <b>These are regression tests for a defect that reached PROD.</b> Operator 2026-08-05:
/// <i>"guards are not working - it still shows posts shich are NOT ready"</i>, on post #540 ("Our
/// Silver sponsors") whose three sponsors had all failed to deliver their social-media text — while
/// the page said "Nothing is hidden" beside it.
///
/// <para>The cause was not a weak rule but a rule that never ran: <c>SponsorCompanyIdOf</c>
/// recognised only <c>TemplateKind.Sponsor</c>, so a tier post — which has no single company id —
/// returned null and passed every check.</para>
/// </remarks>
public class SoMeApprovalGateTierTests
{
    private static SoMePost TierPost(SponsorPackage tier) => new()
    {
        TemplateKind = SoMeTemplateKind.SponsorCategory,
        SubjectKey = $"tier:{tier}",
    };

    [Fact]
    public void Tier_post_is_blocked_when_its_tier_owes_social_text()
    {
        // The exact shape of #540: Silver, with sponsors still owing their text.
        var blocked = SoMeApprovalGate.IsBlockedBy(
            TierPost(SponsorPackage.Silver),
            blockedCompanyIds: [],
            blockedTiers: [SponsorPackage.Silver]);

        Assert.True(blocked);
    }

    [Fact]
    public void Tier_post_is_allowed_when_its_own_tier_is_ready()
    {
        // 🔒 Another tier being blocked must NOT block this one — over-blocking would stall the
        // campaign, which §850 explicitly refuses ("it is ok, that it is planned").
        var blocked = SoMeApprovalGate.IsBlockedBy(
            TierPost(SponsorPackage.Gold),
            blockedCompanyIds: [],
            blockedTiers: [SponsorPackage.Silver]);

        Assert.False(blocked);
    }

    [Fact]
    public void The_original_defect_company_ids_alone_never_blocked_a_tier_post()
    {
        // 🔴 THE BUG, pinned: with only the company set — which is all the old signature had — a
        // tier post could never be blocked, no matter how many of its sponsors owed their text.
        var blocked = SoMeApprovalGate.IsBlockedBy(
            TierPost(SponsorPackage.Silver),
            blockedCompanyIds: ["apento", "system-center-dudes", "test-silver"],
            blockedTiers: []);

        Assert.False(blocked);
    }

    [Fact]
    public void A_single_sponsor_post_still_blocks_on_its_own_company()
    {
        // The Type 4 path must keep working — the fix added a case, it did not replace one.
        var post = new SoMePost
        {
            TemplateKind = SoMeTemplateKind.Sponsor,
            SubjectKey = "sponsor:apento",
        };

        Assert.True(SoMeApprovalGate.IsBlockedBy(post, ["apento"], []));
        Assert.False(SoMeApprovalGate.IsBlockedBy(post, ["someone-else"], []));
    }

    [Fact]
    public void Non_sponsor_types_are_never_blocked_by_sponsor_readiness()
    {
        // §850: a track, session or event post has no sponsor deliverable to wait for, and gating
        // them would stall the campaign for no reason.
        foreach (var kind in new[]
                 {
                     SoMeTemplateKind.SpeakerTracks, SoMeTemplateKind.Session,
                     SoMeTemplateKind.EventPost,
                 })
        {
            var post = new SoMePost { TemplateKind = kind, SubjectKey = "event:whatever" };
            Assert.False(SoMeApprovalGate.IsBlockedBy(post, ["apento"], [SponsorPackage.Silver]));
        }
    }

    [Fact]
    public void A_tier_post_with_an_unreadable_subject_key_is_not_blocked()
    {
        // Fail OPEN here on purpose: a malformed key is a data oddity, and blocking approval on a
        // puzzle would be worse than showing it (§850 — tell him what to fix, never merely refuse).
        var post = new SoMePost
        {
            TemplateKind = SoMeTemplateKind.SponsorCategory,
            SubjectKey = "tier:NotARealTier",
        };

        Assert.False(SoMeApprovalGate.IsBlockedBy(post, [], [SponsorPackage.Silver]));
    }
}
