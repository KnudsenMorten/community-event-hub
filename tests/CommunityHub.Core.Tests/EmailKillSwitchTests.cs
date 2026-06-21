using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §23 first-class email controls: the GLOBAL OUTBOUND-EMAIL KILL SWITCH. When
/// <see cref="EmailOptions.KillSwitch"/> is on, NOTHING may leave the hub by any
/// path. The send paths and the audit decorator both compute the allow/deny via
/// <see cref="BrevoEmailSender.ResolveDelivery"/>, so locking that gate locks the
/// kill switch everywhere (web + jobs). The allowlist was REMOVED (rings-only,
/// operator 2026-06-19): ResolveDelivery now decides solely on the kill switch,
/// and the redirect rule still computes the actual recipient underneath it.
/// </summary>
public class EmailKillSwitchTests
{
    private static EmailOptions Opts(bool kill, string redirectAllTo = "")
        => new EmailOptions { KillSwitch = kill, RedirectAllTo = redirectAllTo };

    [Fact]
    public void KillSwitch_off_allows_the_send()
    {
        var (_, allowed) = BrevoEmailSender.ResolveDelivery(
            Opts(kill: false), "kea@expertslive.dk");
        Assert.True(allowed); // kill switch off => the (ring-gated) send proceeds
    }

    [Fact]
    public void KillSwitch_on_drops_every_recipient()
    {
        var (_, allowed) = BrevoEmailSender.ResolveDelivery(
            Opts(kill: true), "kea@expertslive.dk");
        Assert.False(allowed); // master switch on => send to NOBODY
    }

    [Fact]
    public void KillSwitch_on_drops_even_an_operator_address()
    {
        var (_, allowed) = BrevoEmailSender.ResolveDelivery(
            Opts(kill: true), "mok@expertslive.dk");
        Assert.False(allowed);
    }

    [Fact]
    public void KillSwitch_on_does_not_alter_the_resolved_actual_address()
    {
        // The kill switch only flips "allowed"; the computed actual recipient
        // (for the audit log) is still resolved the same way (redirect off => real).
        var (actualTo, allowed) = BrevoEmailSender.ResolveDelivery(
            Opts(kill: true), "kea@expertslive.dk");
        Assert.Equal("kea@expertslive.dk", actualTo);
        Assert.False(allowed);
    }
}
