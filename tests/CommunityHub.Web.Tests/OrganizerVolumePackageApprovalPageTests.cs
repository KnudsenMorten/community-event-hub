using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1077 stage 2 on the organizer page — approve, overrule, withdraw, and ask again.
/// </summary>
/// <remarks>
/// <para>🔴 <b>The kill-switch test is the one that matters.</b> A "send" button that works while
/// the feature is switched off is not a kill switch; the operator turning the feature off means
/// "this feature sends nothing", not "nothing sends unless somebody clicks". The switch defaults
/// OFF (§1077: outbound mail is the part he wants to approve first), so the DEFAULT state of the
/// page is one where the button refuses — and the page says so rather than failing quietly.</para>
///
/// <para>🔒 Approving records a DECISION and contacts nobody. Writing to the approver is stage 3.</para>
/// </remarks>
public sealed class OrganizerVolumePackageApprovalPageTests
{
    private const int EventId = 42;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vp-page-{Guid.NewGuid():N}").Options);

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static DefaultHttpContext OrganizerContext() =>
        new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "1"),
                new Claim(ClaimTypes.Email, "org@example.test"),
                new Claim(ClaimTypes.Name, "Olive Organizer"),
                new Claim(ClaimTypes.Role, ParticipantRole.Organizer.ToString()),
                new Claim("EventId", EventId.ToString()),
            }, CookieAuthenticationDefaults.AuthenticationScheme)),
        };

    private static (VolumePackageModel Page, CapturingSender Mail) NewPage(
        CommunityHubDbContext db, DefaultHttpContext http)
    {
        var mail = new CapturingSender();
        var qualify = new VolumePackageQualificationService(db);
        var sweep = new VolumePackageSweep(db, qualify);
        var alerts = new EngineAlertSender(
            mail, new EmailContextAccessor(), TimeProvider.System,
            NullLogger<EngineAlertSender>.Instance);
        var approvals = new VolumePackageApprovalMailService(db, sweep, alerts);
        var wizard = new VolumePackageWizardService(
            db, new CommunityHub.Core.Integrations.Graphics.NullSharePointFileStore(),
            TestDocLibrary.Resolver());
        var invites = new VolumePackageInviteMailService(
            db, wizard, mail, new EmailContextAccessor());

        var page = new VolumePackageModel(
            db, new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http)),
            qualify, sweep, approvals, wizard, invites,
            // §1077.9 — the page can also send the post-event thank-you. These tests are about
            // approval and the invitation, so it is supplied with a config path that does not exist:
            // the service then falls back to an empty post-event block rather than throwing.
            new VolumePackagePostEventMailService(
                db, mail, new EmailContextAccessor(), new EventEditionConfigLoader(),
                new EventConfigOptions()),
            new FeatureGateService(db), TimeProvider.System)
        {
            PageContext = new PageContext { HttpContext = http },
        };
        return (page, mail);
    }

    /// <summary>Minimal capture — the Web test project has no shared fake for IEmailSender.</summary>
    private sealed class CapturingSender : IEmailSender
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();

        public Task SendAsync(string to, string subject, string html, CancellationToken ct = default)
        {
            Sent.Add((to, subject, html));
            return Task.CompletedTask;
        }

        public Task SendAsync(string to, string subject, string html,
            IReadOnlyCollection<string>? cc, CancellationToken ct = default) => SendAsync(to, subject, html, ct);

        public Task SendAsync(string to, string subject, string html, string text,
            CancellationToken ct = default) => SendAsync(to, subject, html, ct);

        public Task SendWithIcsAsync(string to, string subject, string html, string ics, string fn,
            CancellationToken ct = default) => SendAsync(to, subject, html, ct);

        public Task SendWithAttachmentsAsync(string to, string subject, string html,
            IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => SendAsync(to, subject, html, ct);
    }

    private static async Task<CommunityHubDbContext> SeedAsync(int attendees = 10)
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "D", IsActive = true,
        });
        db.Participants.Add(new Participant
        {
            Id = 1, EventId = EventId, FullName = "Olive Organizer", Email = "org@example.test",
            Role = ParticipantRole.Organizer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        });
        db.VolumePackageCompanies.Add(new VolumePackageCompany
        {
            EventId = EventId, CustomName = "Globeteam", Domains = "globeteam.dk",
        });
        for (var i = 1; i <= attendees; i++)
        {
            db.Attendees.Add(new Attendee
            {
                EventId = EventId, Email = $"p{i}@globeteam.dk", OrderId = "O-1",
                BackstageTicketId = Guid.NewGuid().ToString("N"), MirrorState = MirrorState.Active,
            });
        }
        db.Orders.Add(new Order
        {
            EventId = EventId, BackstageOrderId = "O-1", BuyerEmail = "bea@globeteam.dk",
            BuyerName = "Bea Buyer", MirrorState = MirrorState.Active,
        });
        await db.SaveChangesAsync();
        await new VolumePackageSweep(db, new VolumePackageQualificationService(db)).RunAsync(EventId);
        return db;
    }

    [Fact]
    public async Task Approving_records_who_we_deal_with_and_who_decided_it()
    {
        using var db = await SeedAsync();
        var (page, mail) = NewPage(db, OrganizerContext());

        page.ApproveId = (await db.VolumePackageCompanies.SingleAsync()).Id;
        page.ApproverEmail = "bea@globeteam.dk";
        page.ApproverName = "Bea Buyer";
        await page.OnPostApproveAsync(CancellationToken.None);

        var c = await db.VolumePackageCompanies.SingleAsync();
        Assert.Equal("bea@globeteam.dk", c.ApproverEmail);
        Assert.NotNull(c.BenefitsApprovedAt);
        Assert.Equal("org@example.test", c.BenefitsApprovedByEmail);
        // 🔒 Approving is a decision, not an outbound action.
        Assert.Empty(mail.Sent);
    }

    /// <summary>
    /// The suggestion is a SUGGESTION. Typing somebody else must win — <i>"organizer may
    /// overrule"</i>.
    /// </summary>
    [Fact]
    public async Task An_organizer_can_overrule_the_suggested_purchaser()
    {
        using var db = await SeedAsync();
        var (page, _) = NewPage(db, OrganizerContext());

        page.ApproveId = (await db.VolumePackageCompanies.SingleAsync()).Id;
        page.ApproverEmail = "someone.else@globeteam.dk";
        await page.OnPostApproveAsync(CancellationToken.None);

        Assert.Equal("someone.else@globeteam.dk",
            (await db.VolumePackageCompanies.SingleAsync()).ApproverEmail);
    }

    /// <summary>
    /// 🔴 An approval with no address names nobody: it would look settled on the page while stage 3
    /// has no one to write to.
    /// </summary>
    [Fact]
    public async Task An_approval_without_an_address_is_refused()
    {
        using var db = await SeedAsync();
        var (page, _) = NewPage(db, OrganizerContext());

        page.ApproveId = (await db.VolumePackageCompanies.SingleAsync()).Id;
        page.ApproverEmail = "   ";
        await page.OnPostApproveAsync(CancellationToken.None);

        Assert.Null((await db.VolumePackageCompanies.SingleAsync()).BenefitsApprovedAt);
        Assert.NotNull(page.Error);
    }

    /// <summary>
    /// Withdrawing reopens the decision and KEEPS the contact — who we found is the answer to a
    /// different question than the one being reopened.
    /// </summary>
    [Fact]
    public async Task Withdrawing_clears_the_approval_but_keeps_the_contact()
    {
        using var db = await SeedAsync();
        var (page, _) = NewPage(db, OrganizerContext());
        var id = (await db.VolumePackageCompanies.SingleAsync()).Id;

        page.ApproveId = id;
        page.ApproverEmail = "bea@globeteam.dk";
        await page.OnPostApproveAsync(CancellationToken.None);
        await page.OnPostWithdrawAsync(id, CancellationToken.None);

        var c = await db.VolumePackageCompanies.SingleAsync();
        Assert.Null(c.BenefitsApprovedAt);
        Assert.Null(c.BenefitsApprovedByEmail);
        Assert.Equal("bea@globeteam.dk", c.ApproverEmail);
    }

    /// <summary>
    /// 🔴 The switch is OFF by default, and the button obeys it — a kill switch a button can walk
    /// past is not a kill switch. The page says why instead of appearing to have worked.
    /// </summary>
    [Fact]
    public async Task Ask_now_sends_nothing_while_the_feature_is_switched_off()
    {
        using var db = await SeedAsync();
        var (page, mail) = NewPage(db, OrganizerContext());
        var id = (await db.VolumePackageCompanies.SingleAsync()).Id;

        await page.OnPostAskAsync(id, CancellationToken.None);

        Assert.Empty(mail.Sent);
        Assert.NotNull(page.Error);
        Assert.Null((await db.VolumePackageCompanies.SingleAsync()).ApprovalRequestedAt);
        Assert.False(page.ApprovalMailEnabled);
    }

    /// <summary>
    /// 🔴 §1077 stage 3 — the INVITATION is the one mail that reaches the company, and its switch
    /// is separate from the approval mail's on purpose: "tell info@ that somebody qualifies" and
    /// "write to a customer" are different decisions, and one switch would make the second an
    /// invisible consequence of the first.
    /// </summary>
    [Fact]
    public async Task The_invitation_sends_nothing_while_its_own_switch_is_off()
    {
        using var db = await SeedAsync();
        // The APPROVAL mail is on — this must not carry the invitation with it.
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = EventId, FeatureKey = VolumePackageApprovalMailService.FeatureKey, Enabled = true,
        });
        await db.SaveChangesAsync();

        var (page, mail) = NewPage(db, OrganizerContext());
        var id = (await db.VolumePackageCompanies.SingleAsync()).Id;

        await page.OnPostInviteAsync(id, CancellationToken.None);

        Assert.Empty(mail.Sent);
        Assert.NotNull(page.Error);
        Assert.Contains("switched off", page.Error);
        Assert.Null((await db.VolumePackageCompanies.SingleAsync()).WizardInvitedAt);
        Assert.False(page.InviteMailEnabled);
    }

    /// <summary>
    /// A link can be created and withdrawn without any mail at all — an organizer who wants to send
    /// it in their own words never has to turn the invitation feature on.
    /// </summary>
    [Fact]
    public async Task A_link_can_be_issued_and_withdrawn_with_no_mail()
    {
        using var db = await SeedAsync();
        var (page, mail) = NewPage(db, OrganizerContext());
        var id = (await db.VolumePackageCompanies.SingleAsync()).Id;

        await page.OnPostIssueLinkAsync(id, CancellationToken.None);
        var issued = await db.VolumePackageCompanies.SingleAsync();
        Assert.False(string.IsNullOrWhiteSpace(issued.WizardToken));
        Assert.True(issued.WizardTokenIsActive(DateTimeOffset.UtcNow));

        await page.OnPostRevokeLinkAsync(id, CancellationToken.None);
        var revoked = await db.VolumePackageCompanies.SingleAsync();
        Assert.False(revoked.WizardTokenIsActive(DateTimeOffset.UtcNow));
        // 🔑 The token is KEPT: "revoked on the 3rd" is a different fact from "there was never one".
        Assert.False(string.IsNullOrWhiteSpace(revoked.WizardToken));

        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Ask_now_asks_the_organizer_mailbox_when_the_feature_is_on()
    {
        using var db = await SeedAsync();
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = EventId, FeatureKey = VolumePackageApprovalMailService.FeatureKey, Enabled = true,
        });
        await db.SaveChangesAsync();

        var (page, mail) = NewPage(db, OrganizerContext());
        var id = (await db.VolumePackageCompanies.SingleAsync()).Id;

        await page.OnPostAskAsync(id, CancellationToken.None);

        var sent = Assert.Single(mail.Sent);
        Assert.Equal(VolumePackageApprovalMailService.Recipient, sent.To);
        Assert.Contains("Globeteam", sent.Subject);
        Assert.NotNull((await db.VolumePackageCompanies.SingleAsync()).ApprovalRequestedAt);
        Assert.True(page.ApprovalMailEnabled);
    }
}
