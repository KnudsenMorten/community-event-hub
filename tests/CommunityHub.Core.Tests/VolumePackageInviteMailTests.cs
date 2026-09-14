using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1077 stage 3 — the invitation: the FIRST mail in this feature that reaches somebody outside the
/// organizing team.
/// </summary>
/// <remarks>
/// <para>🔴 <b>This is the line the operator's opening instruction was drawn around</b> — stages 1
/// and 2 stayed inside the building (a page, and a mail to <c>info@</c>); this one lands in a
/// customer's inbox. So the tests are mostly about when it must NOT send.</para>
///
/// <para>🔒 It goes through the ORDINARY participant mail path, not <c>EngineAlertSender</c>'s
/// ring-exempt channel. That is deliberate: the rings and the kill switch exist to stop mail
/// reaching real people before we mean it, and this is exactly such a person. A "convenient" switch
/// to the exempt sender would silently remove the protection the rings were built for.</para>
/// </remarks>
public sealed class VolumePackageInviteMailTests
{
    private const int EventId = 42;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vp-invite-{Guid.NewGuid():N}").Options);

    private static (VolumePackageInviteMailService Svc, CapturingEmailSender Mail, VolumePackageWizardService Wizard)
        New(CommunityHubDbContext db)
    {
        var mail = new CapturingEmailSender();
        var wizard = new VolumePackageWizardService(
            db, new NullSharePointFileStore(), TestDocLibrary.Resolver());
        return (new VolumePackageInviteMailService(db, wizard, mail, new EmailContextAccessor()),
                mail, wizard);
    }

    private static async Task<VolumePackageCompany> SeedAsync(
        CommunityHubDbContext db, Action<VolumePackageCompany>? tweak = null)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "D", IsActive = true,
        });
        var company = new VolumePackageCompany
        {
            EventId = EventId, CustomName = "Globeteam", Domains = "globeteam.dk",
            ApproverEmail = "bea@globeteam.dk", ApproverName = "Bea Buyer",
            BenefitsApprovedAt = DateTimeOffset.UtcNow, QualifiedNow = true, LastQualifiedCount = 12,
        };
        tweak?.Invoke(company);
        db.VolumePackageCompanies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    [Fact]
    public async Task It_invites_the_approver_and_mints_a_link_if_there_is_none()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var (svc, mail, _) = New(db);

        var result = await svc.SendAsync(company.Id);

        Assert.True(result.Sent);
        var sent = Assert.Single(mail.Messages);
        Assert.Equal("bea@globeteam.dk", sent.To);
        Assert.Contains("Globeteam", sent.Subject);

        var saved = await db.VolumePackageCompanies.SingleAsync(c => c.Id == company.Id);
        Assert.False(string.IsNullOrWhiteSpace(saved.WizardToken));
        Assert.NotNull(saved.WizardInvitedAt);
        // The mail must carry the link it just minted, or it invites somebody to nothing.
        Assert.Contains(saved.WizardToken!, sent.Html);
    }

    /// <summary>
    /// ⚠️ The link IS the credential, so the mail says so. People handle a secret better when they
    /// are told it is one — and this one will be forwarded inside the company by design.
    /// </summary>
    [Fact]
    public async Task The_mail_says_the_link_is_personal_and_offers_the_way_out()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var (svc, mail, _) = New(db);

        await svc.SendAsync(company.Id);

        var html = Assert.Single(mail.Messages).Html;
        Assert.Contains("do not publish it", html);
        Assert.Contains("do not want to participate", html);
    }

    /// <summary>
    /// 🔴 No approver ⇒ no invitation. The difference between "we decided who to write to" and "the
    /// query returned a row" is the whole point of stage 2 having happened first.
    /// </summary>
    [Fact]
    public async Task With_no_approver_it_refuses_and_says_why()
    {
        using var db = NewDb();
        var company = await SeedAsync(db, c => { c.ApproverEmail = null; c.BenefitsApprovedAt = null; });
        var (svc, mail, _) = New(db);

        var result = await svc.SendAsync(company.Id);

        Assert.False(result.Sent);
        Assert.Contains("no approver", result.Problem);
        Assert.Empty(mail.Sent);
    }

    /// <summary>
    /// 🔒 A company that declined is not invited again. Asking a question somebody has already
    /// answered is how a polite feature becomes a nuisance.
    /// </summary>
    [Fact]
    public async Task A_company_that_declined_is_never_invited_again()
    {
        using var db = NewDb();
        var company = await SeedAsync(db, c => c.DeclinedAt = DateTimeOffset.UtcNow);
        var (svc, mail, _) = New(db);

        var result = await svc.SendAsync(company.Id);

        Assert.False(result.Sent);
        Assert.Contains("declined", result.Problem);
        Assert.Empty(mail.Sent);
    }

    /// <summary>
    /// A dead link is replaced rather than mailed. Inviting somebody to a URL that 404s wastes the
    /// one moment of attention the invitation gets.
    /// </summary>
    [Fact]
    public async Task A_revoked_link_is_replaced_before_the_invitation_goes_out()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var (svc, mail, wizard) = New(db);

        var dead = await wizard.IssueTokenAsync(company.Id);
        await wizard.RevokeTokenAsync(company.Id);

        Assert.True((await svc.SendAsync(company.Id)).Sent);

        var saved = await db.VolumePackageCompanies.SingleAsync(c => c.Id == company.Id);
        Assert.NotEqual(dead, saved.WizardToken);
        Assert.Null(saved.WizardTokenRevokedAt);
        Assert.DoesNotContain(dead!, Assert.Single(mail.Messages).Html);
    }

    /// <summary>
    /// 🔒 <b>Ring-governed, not ring-exempt.</b> The context this send carries is what the transport
    /// gate reads; asserting it here is what stops a later "it was being dropped, so I used the
    /// alert sender" from quietly bypassing the rings for a mail to a customer.
    /// </summary>
    [Fact]
    public async Task It_sends_as_ordinary_participant_mail_never_ring_exempt()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var accessor = new EmailContextAccessor();
        // A local sender, so the shared CapturingEmailSender keeps its shape for every other test:
        // this one has to observe the CONTEXT at the moment of sending, which is what the transport
        // ring-gate reads.
        var mail = new ContextObservingSender(accessor);

        var svc = new VolumePackageInviteMailService(
            db,
            new VolumePackageWizardService(db, new NullSharePointFileStore(), TestDocLibrary.Resolver()),
            mail, accessor);

        await svc.SendAsync(company.Id);

        Assert.NotNull(mail.Seen);
        Assert.False(mail.Seen!.RingExempt);
        Assert.Equal(VolumePackageInviteMailService.FeatureKey, mail.Seen.FeatureKey);
        Assert.Equal(EventId, mail.Seen.EventId);
    }

    private sealed class ContextObservingSender(IEmailContextAccessor accessor) : IEmailSender
    {
        public EmailContext? Seen { get; private set; }

        public Task SendAsync(string t, string s, string h, CancellationToken ct = default)
        {
            Seen = accessor.Current;
            return Task.CompletedTask;
        }

        public Task SendAsync(string t, string s, string h, IReadOnlyCollection<string>? cc,
            CancellationToken ct = default) => SendAsync(t, s, h, ct);
        public Task SendAsync(string t, string s, string h, string text,
            CancellationToken ct = default) => SendAsync(t, s, h, ct);
        public Task SendWithIcsAsync(string t, string s, string h, string ics, string fn,
            CancellationToken ct = default) => SendAsync(t, s, h, ct);
        public Task SendWithAttachmentsAsync(string t, string s, string h,
            IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => SendAsync(t, s, h, ct);
    }
}
