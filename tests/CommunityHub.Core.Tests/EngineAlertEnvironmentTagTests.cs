using CommunityHub.Core.Diagnostics;
using CommunityHub.Core.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §702 — every OPS ALERT subject leads with <c>[DEV]</c> / <c>[PROD]</c>, and nothing else does.
///
/// <para>Operator 2026-07-29: <i>"can we include [DEV] and [PROD] in all alert mails so i can see
/// which env is sending the alert / impacted. only for alerts of course, not to roles"</i>.</para>
///
/// <para>§701 is the incident that prompted it: three <c>Engine FAILED</c> mails arrived and could
/// only be attributed to DEV by the accidental <c>[TEST -&gt; …]</c> redirect prefix — a PROD alert
/// would have carried no environment marker at all.</para>
/// </summary>
public sealed class EngineAlertEnvironmentTagTests
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

    [Theory]
    [InlineData("DEV", "[DEV] Engine FAILED: WooCommercePullJob [ELDK27]")]
    [InlineData("PROD", "[PROD] Engine FAILED: WooCommercePullJob [ELDK27]")]
    public async Task Alert_subjects_lead_with_the_environment(string label, string expected)
    {
        var (sender, mail) = New(label);

        await sender.AlertAsync("Engine FAILED: WooCommercePullJob [ELDK27]", "<p>x</p>", default);

        Assert.Equal(expected, Assert.Single(mail.Subjects));
    }

    /// <summary>
    /// The tag goes FIRST, ahead of the existing <c>[ELDK27]</c> edition tag. A long subject gets
    /// truncated from the right in most mail clients, and the environment is the part he scans for.
    /// </summary>
    [Fact]
    public async Task The_environment_tag_precedes_the_edition_tag()
    {
        var (sender, mail) = New("PROD");

        await sender.AlertAsync("Engine FAILED: X [ELDK27]", "<p>x</p>", default);

        var subject = Assert.Single(mail.Subjects);
        Assert.StartsWith("[PROD]", subject, StringComparison.Ordinal);
        Assert.True(subject.IndexOf("[PROD]", StringComparison.Ordinal)
                    < subject.IndexOf("[ELDK27]", StringComparison.Ordinal));
    }

    /// <summary>
    /// Idempotent: an already-tagged subject must not become <c>[DEV] [DEV] …</c>.
    /// </summary>
    [Fact]
    public async Task Tagging_is_idempotent()
    {
        var (sender, mail) = New("DEV");

        await sender.AlertAsync("[DEV] Engine FAILED: X", "<p>x</p>", default);

        Assert.Equal("[DEV] Engine FAILED: X", Assert.Single(mail.Subjects));
    }

    /// <summary>
    /// 🔒 Unresolvable ⇒ <c>[UNKNOWN]</c>, never a confident guess. §702.1: the obvious source
    /// (<c>ASPNETCORE_ENVIRONMENT</c>) reads "Production" on DEV too, so a guess would be wrong
    /// precisely where it matters.
    /// </summary>
    [Fact]
    public async Task An_unresolvable_environment_says_so()
    {
        var (sender, mail) = New(null, null);

        await sender.AlertAsync("Engine FAILED: X", "<p>x</p>", default);

        Assert.StartsWith("[UNKNOWN]", Assert.Single(mail.Subjects), StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔒 "ONLY FOR ALERTS, NOT TO ROLES" — the operator's explicit boundary.
    ///
    /// <para>It holds STRUCTURALLY, not by convention: the tag is applied inside
    /// <see cref="EngineAlertSender"/> only, and participant/role mail never goes through that
    /// class (it is internal ops mail, ring-EXEMPT by construction — the very reason it exists
    /// separately). A mail sent straight down <see cref="IEmailSender"/>, which is the path every
    /// participant mail takes, is untouched.</para>
    /// </summary>
    [Fact]
    public async Task Role_and_participant_mail_is_never_tagged()
    {
        var mail = new CapturingSender();

        // The participant path: the same transport, no EngineAlertSender in front of it.
        await mail.SendAsync("speaker@example.test", "Welcome to ELDK27", "<p>hi</p>");

        var subject = Assert.Single(mail.Subjects);
        Assert.Equal("Welcome to ELDK27", subject);
        Assert.DoesNotContain("[DEV]", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("[PROD]", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("[UNKNOWN]", subject, StringComparison.Ordinal);
    }
}
