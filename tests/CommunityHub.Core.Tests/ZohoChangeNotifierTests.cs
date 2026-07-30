using CommunityHub.Core.Email;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// RULE (operator 2026-07-23): every CEH-made Zoho Backstage write must send a change
/// mail to info@expertslive.dk (the operator must publish/delete manually in Backstage
/// — API writes are not auto-published). `ZohoChangeNotifier` rides the ring-exempt
/// `EngineAlertSender`. Proves: subject/body format (count, area, publish/delete
/// wording, HTML-encoded change lines, Backstage admin link); an EMPTY batch sends
/// NOTHING; a batch is ONE mail (never per item); consecutive batches are UNTHROTTLED
/// (each real change batch arrives); and delivery failure never throws.
/// </summary>
public sealed class ZohoChangeNotifierTests
{
    private static (ZohoChangeNotifier Notifier, CapturingEmailSender Sender) NewNotifier()
    {
        var sender = new CapturingEmailSender();
        var alerts = new EngineAlertSender(
            sender, new EmailContextAccessor(), TimeProvider.System,
            NullLogger<EngineAlertSender>.Instance);
        return (new ZohoChangeNotifier(alerts, NullLogger<ZohoChangeNotifier>.Instance), sender);
    }

    [Fact]
    public void Build_formats_subject_and_body()
    {
        var (subject, html) = ZohoChangeNotifier.Build(
            "Agenda / sessions",
            new[] { "Created session 'Azure Master Class' (Backstage id bs-1)", "Updated session 'Talk B' (Backstage id bs-2)" });

        Assert.Equal("[CEH→Zoho] Agenda / sessions: 2 change(s) — publish/delete may be needed", subject);
        // Intro explains WHY the operator gets the mail (manual publish/delete in Backstage).
        Assert.Contains("not auto-published", html);
        Assert.Contains("publish", html);
        Assert.Contains("delete", html);
        // The change list + the Backstage admin link line.
        Assert.Contains("Created session &#39;Azure Master Class&#39; (Backstage id bs-1)", html);
        Assert.Contains("Updated session &#39;Talk B&#39; (Backstage id bs-2)", html);
        Assert.Contains("https://backstage.zoho.eu/", html);
    }

    [Fact]
    public void Build_renders_multiline_entries_as_line_breaks_for_easy_pasting()
    {
        // §322m (operator): the mail is the operator's COPY-PASTE source for the GuiOnly
        // session fields — a label line followed by a paste block on its OWN line.
        var (_, html) = ZohoChangeNotifier.Build(
            "Agenda / sessions",
            new[] { "Session Description is empty — paste the full text below:\nThis is the full abstract.\n\nSecond paragraph." });

        Assert.Contains("paste the full text below:<br/>This is the full abstract.", html);
        Assert.Contains("<br/><br/>Second paragraph.", html);
        Assert.DoesNotContain("\n", html.Substring(html.IndexOf("<ul>", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Empty_change_list_sends_nothing()
    {
        var (notifier, sender) = NewNotifier();

        await notifier.NotifyAsync("Speakers", Array.Empty<string>(), CancellationToken.None);

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task A_batch_is_one_mail_to_the_ops_mailbox_listing_every_change()
    {
        var (notifier, sender) = NewNotifier();

        await notifier.NotifyAsync("Sponsors / exhibitors",
            new[] { "Created sponsor 'Fabrikam'", "Created exhibitor 'Fabrikam' (Zoho id z-9)" },
            CancellationToken.None);

        var (to, subject, html, _) = Assert.Single(sender.Messages);
        // §493 — system alerts land in the single operator mailbox, not the shared info@ inbox.
        Assert.Equal(ZohoChangeNotifier.Recipient, to);
        Assert.Contains("Sponsors / exhibitors: 2 change(s)", subject);
        Assert.Contains("Created sponsor &#39;Fabrikam&#39;", html);
        Assert.Contains("Created exhibitor &#39;Fabrikam&#39; (Zoho id z-9)", html);
    }

    [Fact]
    public async Task Consecutive_batches_are_not_throttled_each_arrives()
    {
        // Deliberately NO throttle key: unlike engine failure alerts, every real change
        // batch must reach the operator (each needs its own publish/delete action).
        var (notifier, sender) = NewNotifier();

        await notifier.NotifyAsync("Speakers", new[] { "Created speaker 'A' (a@x.dk)" }, CancellationToken.None);
        await notifier.NotifyAsync("Speakers", new[] { "Created speaker 'B' (b@x.dk)" }, CancellationToken.None);

        Assert.Equal(2, sender.Sent.Count);
        Assert.All(sender.Sent, s => Assert.Equal(ZohoChangeNotifier.Recipient, s.To));
    }

    [Fact]
    public async Task Delivery_failure_never_throws()
    {
        // Mirrors the EngineAlertSender guarantee: a mail failure must not break the
        // engine that just pushed to Zoho.
        var alerts = new EngineAlertSender(
            new ThrowingEmailSender(), new EmailContextAccessor(), TimeProvider.System,
            NullLogger<EngineAlertSender>.Instance);
        var notifier = new ZohoChangeNotifier(alerts, NullLogger<ZohoChangeNotifier>.Instance);

        // Must complete without throwing.
        await notifier.NotifyAsync("Agenda / sessions", new[] { "Created session 'X'" }, CancellationToken.None);
    }

    /// <summary>An IEmailSender whose every send hard-fails.</summary>
    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
            => throw new InvalidOperationException("SMTP down");
        public Task SendAsync(string toEmail, string subject, string htmlBody,
            IReadOnlyCollection<string>? cc, CancellationToken ct = default)
            => throw new InvalidOperationException("SMTP down");
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody,
            CancellationToken ct = default)
            => throw new InvalidOperationException("SMTP down");
        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody,
            string icsContent, string icsFileName, CancellationToken ct = default)
            => throw new InvalidOperationException("SMTP down");
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default)
            => throw new InvalidOperationException("SMTP down");
    }
}
