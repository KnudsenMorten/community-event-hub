using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

// Operator directive 2026-06-16: CEH must NEVER mail anyone outside the
// configured allowlist, in dev OR prod. The allowlist is the SOLE gate (the
// dev redirect is turned off). An empty/unconfigured allowlist must FAIL CLOSED
// (send to nobody), so a wiped Email__OnlySendTo can never leak to real
// speakers / sponsors / volunteers. These tests lock that contract.
public class EmailAllowlistFailClosedTests
{
    private static EmailOptions Opts(string onlySendTo, string redirectAllTo = "")
        => new EmailOptions { OnlySendTo = onlySendTo, RedirectAllTo = redirectAllTo };

    [Fact]
    public void EmptyAllowlist_FailsClosed_DropsEveryone()
    {
        var (_, allowed) = BrevoEmailSender.ResolveDelivery(Opts(""), "speaker@theircompany.com");
        Assert.False(allowed); // empty allowlist => send to NOBODY (was send-all = the leak)
    }

    [Fact]
    public void EmptyAllowlist_FailsClosed_EvenForOperator()
    {
        var (_, allowed) = BrevoEmailSender.ResolveDelivery(Opts("   "), "mok@expertslive.dk");
        Assert.False(allowed);
    }

    [Fact]
    public void RealSpeaker_NotOnAllowlist_IsDropped()
    {
        var opts = Opts("@expertslive.dk, mok@2linkit.net");
        Assert.False(BrevoEmailSender.ResolveDelivery(opts, "speaker@sessionize-import.com").allowed);
        Assert.False(BrevoEmailSender.ResolveDelivery(opts, "sponsor@bigcorp.example").allowed);
    }

    [Fact]
    public void AllowlistedRecipients_GetThrough()
    {
        var opts = Opts("@expertslive.dk, mok@2linkit.net");
        Assert.True(BrevoEmailSender.ResolveDelivery(opts, "kea@expertslive.dk").allowed);   // @domain wildcard
        Assert.True(BrevoEmailSender.ResolveDelivery(opts, "MOK@2linkit.net").allowed);       // exact, case-insensitive
    }

    [Fact]
    public void RedirectOff_DeliversToRealAddress_StillAllowlistGated()
    {
        // Redirect is OFF (empty) in both envs now: actualTo == the real address,
        // and the allowlist still decides whether it sends.
        var opts = Opts("@expertslive.dk");
        var (actualTo, allowed) = BrevoEmailSender.ResolveDelivery(opts, "kea@expertslive.dk");
        Assert.Equal("kea@expertslive.dk", actualTo);
        Assert.True(allowed);
    }
}
