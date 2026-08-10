using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1035 — MAY THIS COMPANY BE PUSHED TO ZOHO BACKSTAGE? Asked in ONE place, so provisioning and
/// sync cannot disagree.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10: <i>"company 100 was a test company. it appears as i dont have a
/// IsTestSponsor so i had to offboard them as sponsor so i didnt get them synced to zoho"</i> and
/// <i>"i didn't have a place to set the flag in the ui"</i>.</para>
///
/// <para>🔴 <b>Both halves of that were true, and the second one made the workaround useless.</b>
/// <see cref="SponsorInfo.IsTestData"/> existed (§905/§909) but was read ONLY by the social-media
/// side — the planner, the graphics builders, the variable resolver. <b>Nothing in the Zoho path
/// looked at it, and nothing looked at <see cref="SponsorStatus.Withdrawn"/> either.</b> So
/// withdrawing a test sponsor to stop it reaching Zoho did not stop it reaching Zoho: the company
/// stayed in scope for `SponsorZohoProvisionService` and `SponsorZohoSyncService`, and its
/// `ZohoSponsorId` was already there to keep updating. The withdraw confirmation even says
/// <i>"Zoho/ERP records were not touched"</i> — which was about the existing record, and read as a
/// promise about the future.</para>
///
/// <para>⚠️ <b>And there was no way to SET the flag.</b> Measured 2026-08-10: no page, no handler and
/// no service in the entire codebase ever wrote <c>SponsorInfo.IsTestData</c> — the one row carrying
/// it can only have been edited straight in the database. A flag nobody can set is not a feature.</para>
///
/// <para>🔒 <b>Withdrawn is included deliberately.</b> He used withdrawal AS the mechanism for "stop
/// syncing this company", and that is the honest reading of offboarding: a company that has left
/// should not keep receiving pushes. ⚠️ This stops FUTURE writes only — it never deletes anything
/// already in Backstage, which stays an organizer's decision there (§334).</para>
/// </remarks>
public static class SponsorZohoScope
{
    /// <summary>
    /// True when CEH may create or update this company in Zoho Backstage.
    /// </summary>
    /// <remarks>
    /// 🔑 Reads the STORED flag only — never a derived "all contacts are test users" rule. A test
    /// company is an explicit statement by an organizer, and §905 already measured why deriving it
    /// is dangerous: <c>2linkIT</c>, a real paying Gold sponsor, carries six test contacts beside
    /// six real ones because it is the operator's own company.
    /// </remarks>
    public static bool MayPushToZoho(SponsorInfo info) =>
        !info.IsTestData && info.Status != SponsorStatus.Withdrawn;

    /// <summary>The reason it was skipped, for the run log — never a silent omission.</summary>
    public static string SkipReason(SponsorInfo info) =>
        info.IsTestData ? "marked as test data"
        : info.Status == SponsorStatus.Withdrawn ? "withdrawn"
        : "not skipped";
}
