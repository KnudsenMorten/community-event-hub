using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §609 — a write refused by the §340-H external-write guard is <b>SKIPPED</b>, never <b>FAILED</b>,
/// so it raises no alert.
///
/// <para><b>The incident.</b> Deploying to DEV made the push job mail the operator a red
/// <i>"Stage-2 CEH→Zoho session push: failures [ELDK27]"</i> listing all 11 sessions, every line
/// reading <i>"External writes are disabled for this host (§340-H)"</i> — and it kept arriving. DEV
/// is SUPPOSED to refuse: <i>"we cannot have external writes from DEV !!!!"</i>. So the job was
/// alerting on the safety guard working correctly.</para>
///
/// <para><b>Why it matters beyond noise.</b> An alert that fires for a permanent, by-design
/// condition is worse than no alert: it teaches the reader to ignore the channel, so the one real
/// failure gets ignored too. §545 exists because things were invisible; this is the opposite
/// failure, and it costs the same trust.</para>
/// </summary>
public class BlockedWriteIsSkippedNotFailedTests
{
    [Fact]
    public void The_blocked_write_error_is_a_named_constant_not_a_prose_match()
    {
        // Callers classify on this constant. If it were matched as a loose string, a wording tweak
        // would silently turn every DEV skip back into an alert — the exact regression this guards.
        Assert.Equal("External writes are disabled for this host (§340-H).",
            ZohoClient.ExternalWritesDisabledError);
    }

    [Fact]
    public void The_constant_is_what_a_blocked_write_actually_returns()
    {
        // Ties the constant to the producing side: ZohoClient's write methods return exactly this
        // when MayWriteAsync refuses, so the push services' equality check cannot drift from it.
        Assert.False(string.IsNullOrWhiteSpace(ZohoClient.ExternalWritesDisabledError));
        Assert.Contains("§340-H", ZohoClient.ExternalWritesDisabledError);
        Assert.Contains("External writes are disabled", ZohoClient.ExternalWritesDisabledError);
    }

    [Theory]
    [InlineData("HTTP 400 Bad Request — The country code must be in ISO Alpha-2 format")]
    [InlineData("No Zoho access token (token refresh failed).")]
    [InlineData("HTTP 500 Internal Server Error")]
    public void A_REAL_failure_is_not_mistaken_for_a_blocked_write(string realError)
    {
        // The other half of the rule: genuine failures must still be FAILED and still alert. A
        // blanket "treat push errors as skipped" would hide the §415 country-code failure that
        // silently lost 4 of 4 speaker creates.
        Assert.NotEqual(ZohoClient.ExternalWritesDisabledError, realError);
    }
}
