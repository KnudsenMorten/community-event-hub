using CommunityHub.Core.Diagnostics;
using CommunityHub.Core.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §752.9 — an alert may declare itself DEV-SILENT, and the decision is enforced in one place.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why this exists.</b> Operator 2026-08-01: <i>"i still get alerts from dev env which I
/// thought we disabled"</i>. §716 had silenced the <c>Engine INACTIVE</c> family by guarding a single
/// call site. That fix WORKED — the family stopped on 31 Jul and has not returned — but it could not
/// generalise, and two other families kept arriving from DEV ("Background jobs: N look asleep",
/// "Stage-2 CEH→Zoho push: failures"). A per-call-site guard only ever fixes the sites someone
/// remembered to visit, so the operator reasonably read the first fix as "DEV alerts are off".</para>
///
/// <para>🔒 <b>Suppression is OPT-IN and that is the important half.</b> Defaulting to silence on DEV
/// would quietly cover every alert nobody has reviewed — including the ones §716 deliberately KEPT,
/// such as an engine that actually threw. These tests pin both directions: a flagged alert is silent
/// on DEV, and an unflagged one is not.</para>
///
/// <para>🔒 <b>UNKNOWN still alerts.</b> Silence is granted only to a positively-identified DEV.
/// Guessing "probably dev" from an unrecognised host is how a real production alert goes missing —
/// the same rule §702.1 applies to the environment TAG, applied here to suppression.</para>
/// </remarks>
public sealed class EngineAlertDevSilenceTests
{
    private sealed class CapturingSender : IEmailSender
    {
        public List<string> Subjects { get; } = new();

        public Task SendAsync(string toEmail, string subject, string htmlBody,
            CancellationToken cancellationToken = default)
        {
            Subjects.Add(subject);
            return Task.CompletedTask;
        }

        public Task SendAsync(string toEmail, string subject, string htmlBody,
            IReadOnlyCollection<string>? cc, CancellationToken cancellationToken = default) =>
            SendAsync(toEmail, subject, htmlBody, cancellationToken);

        public Task SendAsync(string toEmail, string subject, string htmlBody,
            string textBody, CancellationToken cancellationToken = default) =>
            SendAsync(toEmail, subject, htmlBody, cancellationToken);

        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody,
            string icsContent, string icsFileName, CancellationToken cancellationToken = default) =>
            SendAsync(toEmail, subject, htmlBody, cancellationToken);

        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments, CancellationToken cancellationToken = default) =>
            SendAsync(toEmail, subject, htmlBody, cancellationToken);
    }

    private static (EngineAlertSender Sender, CapturingSender Mail) New(string? label, string? site = null)
    {
        var mail = new CapturingSender();
        return (new EngineAlertSender(
            mail, new EmailContextAccessor(), TimeProvider.System,
            NullLogger<EngineAlertSender>.Instance, new HubEnvironment(label, site)), mail);
    }

    /// <summary>The thing he asked for: these stop arriving from DEV.</summary>
    [Fact]
    public async Task A_dev_silent_alert_is_not_sent_on_DEV()
    {
        var (sender, mail) = New("DEV");

        await sender.AlertAsync("Background jobs: 4 look asleep [ELDK27]", "<p>x</p>", default,
            devSilent: true);

        Assert.Empty(mail.Subjects);
    }

    /// <summary>
    /// 🔒 The same alert is still NEWS in production — suppression must not follow the code to PROD.
    /// </summary>
    [Fact]
    public async Task The_same_alert_still_fires_on_PROD()
    {
        var (sender, mail) = New("PROD");

        await sender.AlertAsync("Background jobs: 4 look asleep [ELDK27]", "<p>x</p>", default,
            devSilent: true);

        Assert.Equal("[PROD] Background jobs: 4 look asleep [ELDK27]", Assert.Single(mail.Subjects));
    }

    /// <summary>
    /// 🔒 THE OVER-SUPPRESSION GUARD. An alert that has not opted in keeps arriving from DEV — which
    /// is what stops this mechanism from quietly becoming "DEV never alerts". §716 kept a DEV engine
    /// that THREW loud on purpose, and that must remain true.
    /// </summary>
    [Fact]
    public async Task An_unflagged_alert_is_untouched_on_DEV()
    {
        var (sender, mail) = New("DEV");

        await sender.AlertAsync("Engine FAILED: WooCommercePullJob [ELDK27]", "<p>x</p>", default);

        Assert.Equal("[DEV] Engine FAILED: WooCommercePullJob [ELDK27]", Assert.Single(mail.Subjects));
    }

    /// <summary>
    /// 🔒 An unrecognised host is NOT treated as DEV. Silence is granted only on positive
    /// identification; a missing environment label must never buy an alert its silence.
    /// </summary>
    [Fact]
    public async Task An_UNKNOWN_host_still_sends_a_dev_silent_alert()
    {
        var (sender, mail) = New(label: null, site: null);

        await sender.AlertAsync("Background jobs: 4 look asleep [ELDK27]", "<p>x</p>", default,
            devSilent: true);

        Assert.Single(mail.Subjects);
    }
}
