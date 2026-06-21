using CommunityHub.Core.Email;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

// REQUIREMENTS §21 (organizer): the Email Center can test-send a rendered
// template to an ARBITRARY address (not just the signed-in organizer). The
// outbound path is kill-switch-gated and DEV-redirected (the allowlist was
// removed — rings-only, operator 2026-06-19), so a naive send could silently go
// nowhere. EmailTestSendPlanner decides the HONEST outcome up front using the
// SAME gate as the real sender (BrevoEmailSender.ResolveDelivery), so the
// organizer is told "dropped" / "redirected" / "delivered" / "invalid" before
// anything is (or isn't) sent. These tests lock that contract. (The per-recipient
// RING gate is applied by the sender at send time, not by the planner.)
public class EmailTestSendPlannerTests
{
    private static EmailOptions Opts(string redirectAllTo = "", bool killSwitch = false)
        => new EmailOptions { RedirectAllTo = redirectAllTo, KillSwitch = killSwitch };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("missing@")]
    [InlineData("@nodomain")]
    [InlineData("Alex <alex@expertslive.dk>")] // display form rejected — bare address only
    public void Invalid_address_is_reported_and_never_sent(string? address)
    {
        var plan = EmailTestSendPlanner.Plan(address, Opts());

        Assert.Equal(EmailTestSendOutcome.InvalidAddress, plan.Outcome);
        Assert.False(plan.WillSend);
        Assert.Null(plan.ActualRecipient);
    }

    [Fact]
    public void Valid_address_would_deliver_as_typed()
    {
        var plan = EmailTestSendPlanner.Plan("organizer@expertslive.dk", Opts());

        Assert.Equal(EmailTestSendOutcome.WouldDeliver, plan.Outcome);
        Assert.True(plan.WillSend);
        Assert.Equal("organizer@expertslive.dk", plan.TargetAddress);
        Assert.Equal("organizer@expertslive.dk", plan.ActualRecipient);
    }

    [Fact]
    public void Any_external_address_would_deliver_now_that_the_allowlist_is_gone()
    {
        // Pre-rings this was DroppedByAllowlist; rings-only means the planner no
        // longer filters by address — only the kill switch + redirect apply.
        var plan = EmailTestSendPlanner.Plan("speaker@theircompany.com", Opts());

        Assert.Equal(EmailTestSendOutcome.WouldDeliver, plan.Outcome);
        Assert.True(plan.WillSend);
        Assert.Equal("speaker@theircompany.com", plan.TargetAddress);
    }

    [Fact]
    public void Kill_switch_drops_a_valid_address()
    {
        // Mirrors the global kill-switch contract: kill on => send to NOBODY.
        var plan = EmailTestSendPlanner.Plan("organizer@expertslive.dk", Opts(killSwitch: true));

        Assert.Equal(EmailTestSendOutcome.DroppedByKillSwitch, plan.Outcome);
        Assert.False(plan.WillSend);
    }

    [Fact]
    public void Dev_redirect_reports_the_real_redirect_target()
    {
        // With a DEV redirect in force, mail to any address actually lands in the
        // redirect mailbox — the planner says so, and reports BOTH the typed
        // target and where it really goes.
        var opts = Opts(redirectAllTo: "mok@expertslive.dk");
        var plan = EmailTestSendPlanner.Plan("colleague@expertslive.dk", opts);

        Assert.Equal(EmailTestSendOutcome.WouldRedirect, plan.Outcome);
        Assert.True(plan.WillSend);
        Assert.Equal("colleague@expertslive.dk", plan.TargetAddress);
        Assert.Equal("mok@expertslive.dk", plan.ActualRecipient);
    }

    [Fact]
    public void Kill_switch_drops_even_when_a_redirect_is_set()
    {
        var opts = Opts(redirectAllTo: "mok@expertslive.dk", killSwitch: true);
        var plan = EmailTestSendPlanner.Plan("organizer@expertslive.dk", opts);

        Assert.Equal(EmailTestSendOutcome.DroppedByKillSwitch, plan.Outcome);
        Assert.False(plan.WillSend);
    }

    [Fact]
    public void Address_is_trimmed_before_evaluation()
    {
        var plan = EmailTestSendPlanner.Plan("  organizer@expertslive.dk  ", Opts());

        Assert.Equal(EmailTestSendOutcome.WouldDeliver, plan.Outcome);
        Assert.Equal("organizer@expertslive.dk", plan.TargetAddress);
    }

    [Fact]
    public void Instance_reads_injected_options()
    {
        // The DI-registered planner reads IOptions<EmailOptions> — same decision,
        // exercised through the constructor-injected path the web app uses.
        var deliver = new EmailTestSendPlanner(Options.Create(Opts()));
        Assert.Equal(EmailTestSendOutcome.WouldDeliver,
            deliver.Plan("organizer@expertslive.dk").Outcome);

        var killed = new EmailTestSendPlanner(Options.Create(Opts(killSwitch: true)));
        Assert.Equal(EmailTestSendOutcome.DroppedByKillSwitch,
            killed.Plan("organizer@expertslive.dk").Outcome);
    }
}
