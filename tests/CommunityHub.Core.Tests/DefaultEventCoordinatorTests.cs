using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §597.4 — CEH follows Company Manager's <b>Default Event Coordinator</b>, and only that.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-28: <i>"maybe add the ability to select DEFAULT event coordinator here"</i>
/// → <i>"then you know which will be synced to zoho"</i> → <i>"default comes from CM (default event
/// coordinator)"</i>. Everyone with coordinator status still gets the mails (§597.3); the DEFAULT
/// decides only the Zoho record.</para>
///
/// <para>🔒 <b>Why a POINTER and not "first coordinator in the list".</b> Zoho hard-caps
/// contact-e-mail updates at 3 attempts, and his warning was explicit: <i>"the sponsor object and
/// exhibitor goes into a stale state so we cannot update it anymore and have to delete it, which is
/// a disaster as leads will be lost"</i>. A list-derived contact would churn the e-mail every time a
/// booth member was added or removed and burn the cap on nothing. A single pointer changes only when
/// he changes it.</para>
/// </remarks>
public class DefaultEventCoordinatorTests
{
    // ---------- the case the old fill-blank-only code could never see ----------

    [Fact]
    public void A_CHANGED_default_in_CM_is_followed_even_though_CEH_already_has_a_coordinator()
    {
        // This is §597.4's whole remaining gap: CEH took the coordinator once and then went deaf.
        Assert.True(SponsorZohoSyncService.ShouldReadCoordinator(
            storedPointer: 41, cmPointer: 77, cehCoordinatorEmpty: false));
    }

    [Fact]
    public void The_FIRST_ever_read_is_followed()
    {
        Assert.True(SponsorZohoSyncService.ShouldReadCoordinator(
            storedPointer: null, cmPointer: 77, cehCoordinatorEmpty: true));
    }

    [Fact]
    public void A_company_CEH_has_no_coordinator_for_is_filled_even_when_the_pointer_is_unchanged()
    {
        Assert.True(SponsorZohoSyncService.ShouldReadCoordinator(
            storedPointer: 77, cmPointer: 77, cehCoordinatorEmpty: true));
    }

    // ---------- 🔒 and the three cases where touching it would do harm ----------

    [Fact]
    public void An_UNCHANGED_pointer_does_NOT_overwrite_what_the_hub_holds()
    {
        // "CEH is always the master" for the VALUES — someone may have corrected the name or phone
        // on Company Details, and a routine sync must not silently revert that.
        Assert.False(SponsorZohoSyncService.ShouldReadCoordinator(
            storedPointer: 77, cmPointer: 77, cehCoordinatorEmpty: false));
    }

    [Fact]
    public void An_UNSET_default_in_CM_is_NOT_an_instruction_to_wipe_the_contact()
    {
        // Blanking here would push an EMPTY contact to Zoho and spend one of the 3 capped attempts
        // doing it. Silence from CM means "no opinion", not "remove the contact".
        Assert.False(SponsorZohoSyncService.ShouldReadCoordinator(
            storedPointer: 77, cmPointer: 0, cehCoordinatorEmpty: false));
        Assert.False(SponsorZohoSyncService.ShouldReadCoordinator(
            storedPointer: 77, cmPointer: 0, cehCoordinatorEmpty: true));
    }

    [Fact]
    public void A_negative_or_absent_pointer_is_treated_the_same_as_unset()
    {
        Assert.False(SponsorZohoSyncService.ShouldReadCoordinator(
            storedPointer: null, cmPointer: -1, cehCoordinatorEmpty: true));
    }

    /// <summary>
    /// The property that protects the cap: repeated syncs with nothing changing in CM must decide
    /// "do nothing" every time. A rule that re-read on each pass would re-push the contact and,
    /// through it, keep testing the 3-attempt e-mail limit.
    /// </summary>
    [Fact]
    public void Repeated_syncs_with_no_change_in_CM_stay_idle_forever()
    {
        for (var pass = 0; pass < 50; pass++)
        {
            Assert.False(SponsorZohoSyncService.ShouldReadCoordinator(
                storedPointer: 77, cmPointer: 77, cehCoordinatorEmpty: false));
        }
    }
}
