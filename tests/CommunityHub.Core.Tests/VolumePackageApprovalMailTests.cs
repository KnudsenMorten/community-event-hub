using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1077 stage 2 — the ONE mail this feature sends: <i>"mail an organizer to approve"</i> when a
/// company reaches ten attendees.
/// </summary>
/// <remarks>
/// <para>🔴 <b>What these tests are really guarding is a silence.</b> Stage 1's contract was "computes
/// and displays, sends nothing", and stage 2 breaks it deliberately in exactly one direction: a mail
/// to the ORGANIZER mailbox about a purchaser. The company itself — attendee, buyer, coordinator —
/// is still never written to, and that is the line stage 3 needs its own conversation to cross.</para>
///
/// <para>⚠️ The dedupe test is the one that keeps the mailbox usable. The job runs daily; a company
/// nobody has got round to approving would otherwise be asked about every single morning, which is
/// how a shared inbox learns to ignore us.</para>
/// </remarks>
public sealed class VolumePackageApprovalMailTests
{
    private const int EventId = 42;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vp-mail-{Guid.NewGuid():N}").Options);

    private static (VolumePackageApprovalMailService Svc, CapturingEmailSender Mail) New(
        CommunityHubDbContext db)
    {
        var mail = new CapturingEmailSender();
        var alerts = new EngineAlertSender(
            mail, new EmailContextAccessor(), TimeProvider.System,
            NullLogger<EngineAlertSender>.Instance);
        var sweep = new VolumePackageSweep(db, new VolumePackageQualificationService(db));
        return (new VolumePackageApprovalMailService(db, sweep, alerts), mail);
    }

    /// <summary>
    /// Seeds one company plus <paramref name="attendees"/> people on its domain, already swept — so
    /// <c>QualifiedNow</c>/<c>LastQualifiedCount</c> hold what the daily job would have written.
    /// </summary>
    private static async Task<CommunityHubDbContext> SeedAsync(
        int attendees = 10, string? buyerEmail = null, Action<VolumePackageCompany>? tweak = null)
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "D", IsActive = true,
        });

        var company = new VolumePackageCompany
        {
            EventId = EventId, CustomName = "Globeteam", Domains = "globeteam.dk",
        };
        tweak?.Invoke(company);
        db.VolumePackageCompanies.Add(company);

        for (var i = 1; i <= attendees; i++)
        {
            db.Attendees.Add(new Attendee
            {
                EventId = EventId, Email = $"p{i}@globeteam.dk",
                OrderId = buyerEmail is null ? string.Empty : "O-1",
                BackstageTicketId = Guid.NewGuid().ToString("N"),
                MirrorState = MirrorState.Active,
            });
        }

        if (buyerEmail is not null)
        {
            db.Orders.Add(new Order
            {
                EventId = EventId, BackstageOrderId = "O-1", BuyerEmail = buyerEmail,
                BuyerName = "Bea Buyer", MirrorState = MirrorState.Active,
            });
        }

        await db.SaveChangesAsync();

        // The state the daily job leaves behind — the mail reads it rather than recomputing.
        await new VolumePackageSweep(db, new VolumePackageQualificationService(db)).RunAsync(EventId);
        return db;
    }

    [Fact]
    public async Task A_qualifying_company_is_asked_about_and_the_mail_names_the_suggested_approver()
    {
        using var db = await SeedAsync(buyerEmail: "bea@globeteam.dk");
        var (svc, mail) = New(db);

        var result = await svc.SendPendingAsync(EventId, devSilent: false);

        Assert.True(result.Sent);
        Assert.Equal(1, result.Companies);

        var sent = Assert.Single(mail.Messages);
        Assert.Equal(VolumePackageApprovalMailService.Recipient, sent.To);
        Assert.Contains("Globeteam", sent.Subject);
        Assert.Contains("bea@globeteam.dk", sent.Html);
        Assert.Contains("Bea Buyer", sent.Html);
        // The mail says what it is NOT: nobody at the company has been contacted.
        Assert.Contains("asks the organizers", sent.Html);
    }

    /// <summary>
    /// ⚠️ The dedupe. A daily job plus no durable mark = the same request every morning until
    /// somebody acts, which trains the reader to stop looking.
    /// </summary>
    [Fact]
    public async Task It_asks_once_and_the_next_pass_asks_nothing()
    {
        using var db = await SeedAsync(buyerEmail: "bea@globeteam.dk");
        var (svc, mail) = New(db);

        Assert.True((await svc.SendPendingAsync(EventId, devSilent: false)).Sent);
        Assert.False((await svc.SendPendingAsync(EventId, devSilent: false)).Sent);

        Assert.Single(mail.Messages);
        Assert.NotNull((await db.VolumePackageCompanies.SingleAsync()).ApprovalRequestedAt);
    }

    /// <summary>A company below the threshold is not a question, so it is not asked.</summary>
    [Fact]
    public async Task A_company_that_does_not_qualify_is_never_asked_about()
    {
        using var db = await SeedAsync(attendees: 9);
        var (svc, mail) = New(db);

        Assert.False((await svc.SendPendingAsync(EventId, devSilent: false)).Sent);
        Assert.Empty(mail.Sent);
    }

    /// <summary>
    /// 🔒 Already settled — approved or declined — is not a question either. Asking again would
    /// invite a second, contradictory answer to a decision somebody already made.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_settled_company_is_never_asked_about(bool approved, bool declined)
    {
        using var db = await SeedAsync(tweak: c =>
        {
            if (approved) { c.BenefitsApprovedAt = DateTimeOffset.UtcNow; c.ApproverEmail = "a@globeteam.dk"; }
            if (declined) c.DeclinedAt = DateTimeOffset.UtcNow;
        });
        var (svc, mail) = New(db);

        Assert.False((await svc.SendPendingAsync(EventId, devSilent: false)).Sent);
        Assert.Empty(mail.Sent);
    }

    /// <summary>
    /// ⚠️ The honest null, carried into the mail. Ten freelancers on a shared domain have NO
    /// purchaser; naming one would put a stranger in front of an organizer as a recommendation.
    /// </summary>
    [Fact]
    public async Task With_no_purchaser_the_mail_says_so_rather_than_naming_somebody()
    {
        using var db = await SeedAsync();          // no order at all
        var (svc, mail) = New(db);

        await svc.SendPendingAsync(EventId, devSilent: false);

        var html = Assert.Single(mail.Messages).Html;
        Assert.Contains("No suggested approver", html);
        Assert.Contains("Pick the contact by hand", html);
    }

    /// <summary>
    /// 🔑 One mail, every waiting company inside it. Four mails on one morning is four chances to
    /// action three of them.
    /// </summary>
    [Fact]
    public async Task Several_waiting_companies_arrive_as_one_mail()
    {
        using var db = await SeedAsync();
        db.VolumePackageCompanies.Add(new VolumePackageCompany
        {
            EventId = EventId, CustomName = "Arrow", Domains = "arrow.com",
        });
        for (var i = 1; i <= 11; i++)
        {
            db.Attendees.Add(new Attendee
            {
                EventId = EventId, Email = $"a{i}@arrow.com", BackstageTicketId = Guid.NewGuid().ToString("N"),
                MirrorState = MirrorState.Active,
            });
        }
        await db.SaveChangesAsync();
        await new VolumePackageSweep(db, new VolumePackageQualificationService(db)).RunAsync(EventId);

        var (svc, mail) = New(db);
        var result = await svc.SendPendingAsync(EventId, devSilent: false);

        Assert.Equal(2, result.Companies);
        var sent = Assert.Single(mail.Messages);
        Assert.Contains("2 companies", sent.Subject);
        Assert.Contains("Globeteam", sent.Html);
        Assert.Contains("Arrow", sent.Html);
    }

    /// <summary>
    /// The organizer's deliberate "ask again": it ignores the stamp, because a human clicking the
    /// button has decided the mailbox needs it a second time.
    /// </summary>
    [Fact]
    public async Task Asking_again_for_one_company_ignores_the_stamp()
    {
        using var db = await SeedAsync(buyerEmail: "bea@globeteam.dk");
        var (svc, mail) = New(db);
        await svc.SendPendingAsync(EventId, devSilent: false);

        var id = (await db.VolumePackageCompanies.SingleAsync()).Id;
        Assert.True((await svc.SendForCompanyAsync(id)).Sent);

        Assert.Equal(2, mail.Messages.Count);
    }

    /// <summary>
    /// 🔒 …but "ask again" still refuses a settled company. The button is a re-send, not an override
    /// of what the request means.
    /// </summary>
    [Fact]
    public async Task Asking_again_still_refuses_an_approved_company()
    {
        using var db = await SeedAsync(tweak: c =>
        {
            c.BenefitsApprovedAt = DateTimeOffset.UtcNow;
            c.ApproverEmail = "a@globeteam.dk";
        });
        var (svc, mail) = New(db);

        var id = (await db.VolumePackageCompanies.SingleAsync()).Id;

        Assert.False((await svc.SendForCompanyAsync(id)).Sent);
        Assert.Empty(mail.Sent);
    }
}
