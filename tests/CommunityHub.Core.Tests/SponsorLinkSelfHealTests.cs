using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §553 — the self-heal rule for a stored Zoho SPONSOR/EXHIBITOR id, which had none at all.
/// </summary>
/// <remarks>
/// <para>Operator, on finding the self-heal implemented differently at each call site:
/// <i>"i told you to include self-heal to detect that this backstage id doesn't exist anymore. we
/// have that multiple places in the code. why don't you make it consitent across any comparison
/// against zoho. i do not accept workarounds manually"</i>.</para>
///
/// <para>§553's audit answered him unevenly: <b>speakers YES, sessions "existed but could never
/// run", sponsors/exhibitors NO SELF-HEAL AT ALL</b>. A sponsor deleted in Zoho left CEH pointing at
/// a dead id forever — every sync updating nothing, reporting success, never re-creating.</para>
///
/// <para>🔒 <b>The rule that keeps a healer from becoming data loss:</b> "not found" and "could not
/// look" are different facts. An outage, an expired token or a missing scope must never read as
/// "everything was deleted".</para>
/// </remarks>
public class SponsorLinkSelfHealTests
{
    // ---------- the ONE case that may clear a link ----------

    [Fact]
    public void A_definite_404_for_THAT_id_is_GONE()
    {
        var r = ExternalLinkProbe.ProbeOne(foundExplicitly: false, notFoundExplicitly: true);

        Assert.True(r.IsGone);
        Assert.False(r.IsUnknown);
    }

    [Fact]
    public void A_record_Zoho_returns_is_EXISTS_and_never_touched()
    {
        var r = ExternalLinkProbe.ProbeOne(foundExplicitly: true, notFoundExplicitly: false);

        Assert.False(r.IsGone);
        Assert.False(r.IsUnknown);
    }

    // ---------- 🔒 and everything that must change NOTHING ----------

    [Fact]
    public void An_INDEFINITE_answer_is_UNKNOWN_not_gone()
    {
        // 401 (dead token), 500, a timeout — each would look like "everything was deleted" to a
        // healer that only knew two states, and it would unlink every sponsor during an outage.
        var r = ExternalLinkProbe.ProbeOne(foundExplicitly: false, notFoundExplicitly: false);

        Assert.True(r.IsUnknown);
        Assert.False(r.IsGone);
    }

    [Fact]
    public void An_UNKNOWN_carries_a_reason_so_it_can_be_logged_loudly_rather_than_swallowed()
    {
        // A bare `catch {}` around the old session self-heal is how nine sessions stayed invisibly
        // unlinked for months: a read failing on EVERY run looked exactly like "nothing to do".
        var r = ExternalLinkProbe.ProbeOne(false, false);

        Assert.False(string.IsNullOrWhiteSpace(r.Detail));
    }

    [Fact]
    public void A_stored_id_that_is_absent_is_not_treated_as_gone()
    {
        // Nothing to heal, and nothing to alarm about.
        var r = ExternalLinkProbe.Probe(liveIds: new HashSet<string> { "a" }, storedId: null);

        Assert.True(r.IsUnknown);
        Assert.False(r.IsGone);
    }

    /// <summary>
    /// 🔒 The list-based probe must not believe an EMPTY live set by default. That is the §553/§555
    /// fail-safe that stopped a 401 being read as "0 sessions" and nearly nulling all nine links.
    /// The per-record 404 used for sponsors is stronger precisely because it has no such risk.
    /// </summary>
    [Fact]
    public void An_EMPTY_live_list_is_not_believed_unless_explicitly_allowed()
    {
        var empty = new HashSet<string>();

        Assert.True(ExternalLinkProbe.Probe(empty, "zoho-1").IsUnknown);
        Assert.True(ExternalLinkProbe.Probe(empty, "zoho-1", allowEmptyLiveSet: true).IsGone);
    }

    [Fact]
    public void A_FAILED_list_read_is_never_gone_however_it_is_configured()
    {
        // null live set == the read failed. Even with allowEmptyLiveSet, this cannot mean "deleted".
        Assert.True(ExternalLinkProbe
            .Probe(null, "zoho-1", readFailure: "401", allowEmptyLiveSet: true).IsUnknown);
    }
}
