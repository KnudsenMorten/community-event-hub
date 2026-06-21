using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Email;

/// <summary>
/// What a test-send to an arbitrary address would actually do, decided BEFORE the
/// mail is handed to the sender. The organizer Email Center can test-send a
/// rendered template to ANY address they type (not just themselves), but the
/// outbound path is kill-switch-gated and DEV-redirected — so a naive "send" could
/// silently go nowhere. This planner gives the organizer an HONEST answer up
/// front: the address is malformed, or the send would be dropped by the global
/// kill switch, or it would be redirected to the dev mailbox, or it lands as typed.
/// (The per-recipient RING gate is applied by the sender at send time against the
/// recipient's participant record; the planner models the address-level outcome.)
/// </summary>
public enum EmailTestSendOutcome
{
    /// <summary>The typed address is empty or not a valid email — nothing is sent.</summary>
    InvalidAddress,

    /// <summary>
    /// The global outbound-email kill switch is ON, so the central sender would
    /// drop EVERY mail. Reported as a no-op, never a success.
    /// </summary>
    DroppedByKillSwitch,

    /// <summary>
    /// The send is allowed, but a DEV redirect is in force so it will actually
    /// land in the redirect mailbox (not the typed address). The subject is
    /// prefixed with the original target by the sender.
    /// </summary>
    WouldRedirect,

    /// <summary>The send is allowed and lands at the typed address as-is.</summary>
    WouldDeliver,
}

/// <summary>
/// The honest, structured plan for a single arbitrary-address test-send. Carries
/// the facts (the typed target, the address it would ACTUALLY reach after the
/// redirect, whether the allowlist lets it through) so a caller or a test can
/// assert the real outcome rather than re-parse a message string.
/// </summary>
public sealed record EmailTestSendPlan
{
    /// <summary>The decided outcome.</summary>
    public required EmailTestSendOutcome Outcome { get; init; }

    /// <summary>The normalized (trimmed) address the organizer typed.</summary>
    public string? TargetAddress { get; init; }

    /// <summary>
    /// The address the mail would ACTUALLY be delivered to after any DEV redirect
    /// (equal to <see cref="TargetAddress"/> when no redirect is in force). Null
    /// for an invalid address.
    /// </summary>
    public string? ActualRecipient { get; init; }

    /// <summary>True only when the send will reach a real mailbox (deliver or redirect).</summary>
    public bool WillSend =>
        Outcome is EmailTestSendOutcome.WouldDeliver or EmailTestSendOutcome.WouldRedirect;
}

/// <summary>
/// Pure decision service (no I/O, no DB, no clock). Given a typed target address
/// and the live <see cref="EmailOptions"/>, it reuses the exact gating logic the
/// real sender uses (<see cref="BrevoEmailSender.ResolveDelivery"/>) so the
/// preview can never disagree with what the sender will do.
/// </summary>
public sealed class EmailTestSendPlanner
{
    private readonly IOptions<EmailOptions> _options;

    public EmailTestSendPlanner(IOptions<EmailOptions> options)
    {
        _options = options;
    }

    /// <summary>Plan a test-send to <paramref name="targetAddress"/>.</summary>
    public EmailTestSendPlan Plan(string? targetAddress)
        => Plan(targetAddress, _options.Value);

    /// <summary>
    /// Pure overload taking the options explicitly — used by tests so both the
    /// allowed and dropped/redirected paths are deterministic without standing up
    /// the host's options pipeline.
    /// </summary>
    public static EmailTestSendPlan Plan(string? targetAddress, EmailOptions options)
    {
        var trimmed = targetAddress?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !IsValidEmail(trimmed))
        {
            return new EmailTestSendPlan
            {
                Outcome = EmailTestSendOutcome.InvalidAddress,
                TargetAddress = trimmed,
                ActualRecipient = null,
            };
        }

        // Same gate the sender applies (redirect first, then the global kill
        // switch on the post-redirect address) — no drift between preview and send.
        var (actualTo, allowed) = BrevoEmailSender.ResolveDelivery(options, trimmed);

        if (!allowed)
        {
            return new EmailTestSendPlan
            {
                Outcome = EmailTestSendOutcome.DroppedByKillSwitch,
                TargetAddress = trimmed,
                ActualRecipient = actualTo,
            };
        }

        var redirected = !string.Equals(actualTo, trimmed, StringComparison.OrdinalIgnoreCase);
        return new EmailTestSendPlan
        {
            Outcome = redirected
                ? EmailTestSendOutcome.WouldRedirect
                : EmailTestSendOutcome.WouldDeliver,
            TargetAddress = trimmed,
            ActualRecipient = actualTo,
        };
    }

    // A conservative format check: a single, .NET-parseable address whose display
    // form equals the input (so "Foo <a@b.com>" or a bare token is rejected — a
    // test-send target must be a plain address).
    private static bool IsValidEmail(string candidate)
    {
        try
        {
            var addr = new MailAddress(candidate);
            return string.Equals(addr.Address, candidate, StringComparison.OrdinalIgnoreCase)
                   && candidate.Contains('@', StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
