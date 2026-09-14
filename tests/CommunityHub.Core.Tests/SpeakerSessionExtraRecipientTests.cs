using CommunityHub.Core.Diagnostics;
using CommunityHub.Core.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1121 — the speaker / session organizer to-dos reach the shared inbox AND the named organizers,
/// on ONE mail's <b>To:</b> line; and §1123 — the "add by hand" preamble names the right API.
///
/// <para>Operator 2026-08-25: <i>"add kea@expertslive.dk to reminder emails related to sessions +
/// speakers organizer tasks, so they are sent to both info@expertslive.dk and
/// kea@expertslive.dk"</i> · <i>"include this mail kent.agerlund@twoday.com besides
/// info@expertslive.dk"</i> · <i>"put in to field"</i> · <i>"not cc"</i>.</para>
///
/// <para>🔒 The NEGATIVE cases carry as much weight as the positive ones. He named speakers and
/// sessions; the same notifier also carries exhibitors, sponsors, coupon invoicing and webshop
/// orders, and the same sender carries the weekly volunteer list. If those ever start including the
/// extra recipients, two people begin receiving mail they never asked for — and the way that fails
/// is silently, one area at a time.</para>
/// </summary>
public sealed class SpeakerSessionExtraRecipientTests
{
    private sealed class CapturingSender : IEmailSender
    {
        /// <summary>Every send, as (To-list, subject). One entry per mail — so a length of 2 here
        /// would itself be the bug (§1121 is one mail, not one per recipient).</summary>
        public List<(IReadOnlyList<string> To, string Subject)> Sends { get; } = new();

        /// <summary>CC only ever gets a value if someone routes the extras the wrong way.</summary>
        public List<string> CcSeen { get; } = new();

        public Task SendAsync(string toEmail, string subject, string htmlBody,
            CancellationToken cancellationToken = default)
        {
            Sends.Add((new[] { toEmail }, subject));
            return Task.CompletedTask;
        }

        public Task SendAsync(string toEmail, string subject, string htmlBody,
            IReadOnlyCollection<string>? cc, CancellationToken cancellationToken = default)
        {
            if (cc is not null) CcSeen.AddRange(cc);
            return SendAsync(toEmail, subject, htmlBody, cancellationToken);
        }

        public Task SendToManyAsync(IReadOnlyCollection<string> toEmails, string subject,
            string htmlBody, CancellationToken cancellationToken = default)
        {
            Sends.Add((toEmails.ToList(), subject));
            return Task.CompletedTask;
        }

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

    private static (EngineAlertSender, CapturingSender) NewPair()
    {
        var mail = new CapturingSender();
        return (new EngineAlertSender(
            mail, new EmailContextAccessor(), TimeProvider.System,
            NullLogger<EngineAlertSender>.Instance, new HubEnvironment("PROD", null)), mail);
    }

    // ── The shipped default: both addresses he named, on the To: line ────────────────────

    [Fact]
    public void Default_extra_recipients_are_the_two_he_named()
    {
        var list = new EmailOptions().SpeakerSessionAlsoToList();

        Assert.Equal(
            new[] { "kea@expertslive.dk", "kent.agerlund@twoday.com" },
            list);
    }

    [Theory]
    [InlineData("a@x.dk,b@y.dk", 2)]
    [InlineData("a@x.dk; b@y.dk", 2)]          // semicolon + space
    [InlineData(" a@x.dk , A@X.DK ", 1)]       // trimmed + de-duplicated case-insensitively
    [InlineData("", 0)]                        // empty disables the whole behaviour
    public void The_list_is_parsed_trimmed_and_de_duplicated(string raw, int expected)
    {
        var list = new EmailOptions { SpeakerSessionAlsoTo = raw }.SpeakerSessionAlsoToList();
        Assert.Equal(expected, list.Count);
    }

    // ── ONE mail, several To — never a send each, and never a CC ─────────────────────────

    [Fact]
    public async Task Extras_ride_the_To_line_of_a_single_mail()
    {
        var (alerts, mail) = NewPair();

        await alerts.AlertAsync(
            "ACTION: 1 speaker(s) held", "<p>x</p>", CancellationToken.None,
            recipient: "info@expertslive.dk",
            alsoTo: new[] { "kea@expertslive.dk", "kent.agerlund@twoday.com" });

        // 🔑 ONE mail. Two sends would give two threads, in which neither reader can see that the
        // other already did the job — the exact duplication the shared inbox exists to prevent.
        var send = Assert.Single(mail.Sends);
        Assert.Equal(
            new[] { "info@expertslive.dk", "kea@expertslive.dk", "kent.agerlund@twoday.com" },
            send.To);

        // 🔒 "not cc" (operator, in those words). These people own the job; a CC reads as FYI.
        Assert.Empty(mail.CcSeen);
    }

    [Fact]
    public async Task The_shared_inbox_stays_first_on_the_line()
    {
        var (alerts, mail) = NewPair();

        await alerts.AlertAsync(
            "s", "<p>x</p>", CancellationToken.None,
            recipient: "info@expertslive.dk",
            alsoTo: new[] { "kea@expertslive.dk" });

        Assert.Equal("info@expertslive.dk", Assert.Single(mail.Sends).To[0]);
    }

    [Fact]
    public async Task An_extra_that_repeats_the_primary_is_not_addressed_twice()
    {
        var (alerts, mail) = NewPair();

        await alerts.AlertAsync(
            "s", "<p>x</p>", CancellationToken.None,
            recipient: "info@expertslive.dk",
            alsoTo: new[] { "INFO@expertslive.dk", "kea@expertslive.dk" });

        Assert.Equal(
            new[] { "info@expertslive.dk", "kea@expertslive.dk" },
            Assert.Single(mail.Sends).To);
    }

    [Fact]
    public async Task With_no_extras_the_original_single_recipient_path_is_used()
    {
        var (alerts, mail) = NewPair();

        await alerts.AlertAsync("s", "<p>x</p>", CancellationToken.None, recipient: "mok@expertslive.dk");

        var send = Assert.Single(mail.Sends);
        Assert.Equal(new[] { "mok@expertslive.dk" }, send.To);
    }

    // ── Which areas qualify — and, just as importantly, which do not ─────────────────────

    [Theory]
    [InlineData("Speakers")]
    [InlineData("Speakers — details missing in Backstage")]
    [InlineData("Agenda / sessions")]
    public void Speaker_and_session_areas_get_the_extra_recipients(string area) =>
        Assert.True(ZohoChangeNotifier.IsSpeakerOrSessionArea(area));

    [Theory]
    [InlineData("Exhibitor profiles")]
    [InlineData("Sponsors / exhibitors")]
    [InlineData("Coupon invoicing")]
    [InlineData("Webshop orders")]
    [InlineData("")]
    [InlineData(null)]
    public void Every_other_area_does_not(string? area) =>
        Assert.False(ZohoChangeNotifier.IsSpeakerOrSessionArea(area));

    [Fact]
    public void A_future_Speakers_qualifier_is_included_by_default()
    {
        // 🔑 The area strings visibly grow qualifiers ("Speakers — details missing in Backstage").
        // Exact matching would mean the next variant silently loses its recipients with nothing
        // failing anywhere; a prefix rule errs toward what whoever adds it would expect.
        Assert.True(ZohoChangeNotifier.IsSpeakerOrSessionArea("Speakers — held in the Zoho queue"));
    }

    // ── §1123 — the "add by hand" preamble must not blame the speakers API ───────────────

    [Fact]
    public void The_create_only_claim_is_made_only_for_speakers()
    {
        var (_, html) = ZohoChangeNotifier.Build(
            "Speakers", new[] { "line" }, manualOnly: true);

        Assert.Contains("create-only", html);
    }

    [Theory]
    [InlineData("Sponsors / exhibitors")]
    [InlineData("Exhibitor profiles")]
    [InlineData("Coupon invoicing")]
    [InlineData("Agenda / sessions")]
    public void No_other_area_claims_the_speakers_API_or_that_no_update_endpoint_exists(string area)
    {
        var (_, html) = ZohoChangeNotifier.Build(area, new[] { "line" }, manualOnly: true);

        // ⚠️ For exhibitors this sentence was not merely off-topic — it was FALSE.
        // ZohoClient.UpdateExhibitorAsync PUTs website, company overview, short description and
        // (since §1087) the social pages. A wrong explanation teaches him the system cannot do
        // something it does every sync.
        Assert.DoesNotContain("create-only", html);
        Assert.DoesNotContain("speakers API", html);

        // It must still say the plain, certain thing.
        Assert.Contains("make the changes below by hand", html);
    }
}

/// <summary>
/// §1124 — ONE audience for ALL SEVEN organizer pending-task mails about speakers and sessions.
///
/// <para>Operator 2026-08-25, after an audit showed §1121 had reached only 2 of the 7:
/// <i>"all 7 (so include the 5 extra and send to kent. add info@expertslive.dk to the missing one as
/// well and remove mok@expertslive.dk. make it consitent"</i>.</para>
///
/// <para>🔑 <b>The consistency IS the feature.</b> The seven mails had drifted to five different
/// answers for "who should see this" — four constants spelling out <c>info@</c> independently, one
/// falling through to the developer mailbox, and two reached only by §1121. This class pins the
/// single list and the fact that the developer mailbox is not on it.</para>
/// </summary>
public sealed class SpeakerSessionAudienceTests
{
    [Fact]
    public void The_audience_is_the_shared_inbox_first_then_the_named_organizers()
    {
        Assert.Equal(
            new[] { "info@expertslive.dk", "kea@expertslive.dk", "kent.agerlund@twoday.com" },
            new EmailOptions().SpeakerSessionRecipients());
    }

    [Fact]
    public void The_developer_mailbox_is_not_in_the_audience()
    {
        // 🔒 *"remove mok@expertslive.dk"*. §493 reserves it for SYSTEM alerts; a queue of pending
        // approvals is organizer work, and routing it to one person is what made it invisible.
        Assert.DoesNotContain(
            new EmailOptions().SpeakerSessionRecipients(),
            a => a.Contains("mok@", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_blank_shared_inbox_does_not_silently_drop_the_mail()
    {
        // The extras still receive it. Only an empty list overall means nobody is told — and that
        // is a configuration choice, not an accident of one blank field.
        var opts = new EmailOptions { OrganizerInbox = "" };
        Assert.Equal(
            new[] { "kea@expertslive.dk", "kent.agerlund@twoday.com" },
            opts.SpeakerSessionRecipients());
    }

    [Fact]
    public void Clearing_the_extras_returns_the_mails_to_the_shared_inbox_alone()
    {
        var opts = new EmailOptions { SpeakerSessionAlsoTo = "" };
        Assert.Equal(new[] { "info@expertslive.dk" }, opts.SpeakerSessionRecipients());
    }

    [Fact]
    public void An_extra_repeating_the_shared_inbox_is_not_listed_twice()
    {
        var opts = new EmailOptions { SpeakerSessionAlsoTo = "INFO@expertslive.dk, kea@expertslive.dk" };
        Assert.Equal(
            new[] { "info@expertslive.dk", "kea@expertslive.dk" },
            opts.SpeakerSessionRecipients());
    }

    [Fact]
    public void Everything_is_configurable_without_a_code_change()
    {
        var opts = new EmailOptions
        {
            OrganizerInbox = "ops@example.org",
            SpeakerSessionAlsoTo = "a@example.org;b@example.org",
        };
        Assert.Equal(
            new[] { "ops@example.org", "a@example.org", "b@example.org" },
            opts.SpeakerSessionRecipients());
    }
}
