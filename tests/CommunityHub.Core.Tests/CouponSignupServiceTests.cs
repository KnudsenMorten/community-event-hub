using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1382 — the PUBLIC coupon sign-up form: server-side validation, the e-conomic lookup-before-create in customer group 2,
/// the external-write guard that keeps DEV from creating customers in the live ERP, the CEH coupon, and the request mail
/// to the event mailbox. Fake data only; no network.
/// </summary>
public sealed class CouponSignupServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ fakes

    /// <summary>An e-conomic customer client with a fixed directory (VAT → customer number); records every create.</summary>
    private sealed class FakeErp : IEconomicErpClient
    {
        public Dictionary<string, string> ByVat { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ErpCustomer> Created { get; } = new();
        public List<string> Lookups { get; } = new();
        public bool CanWrite { get; set; } = true;

        public Task<string?> FindCustomerNumberAsync(ErpCustomer customer, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> FindCustomerNumberByCvrAsync(string? cvr, CancellationToken ct)
        {
            Lookups.Add(cvr ?? string.Empty);
            return Task.FromResult(cvr is not null && ByVat.TryGetValue(cvr, out var n) ? n : null);
        }

        public Task<string> CreateCustomerAsync(ErpCustomer customer, CancellationToken ct)
        {
            Created.Add(customer);
            return Task.FromResult("9001");
        }

        public Task UpdateCustomerAsync(string erpCustomerNumber, ErpCustomer customer, CancellationToken ct) =>
            throw new InvalidOperationException("The coupon sign-up must never update an existing customer.");
        public Task CreateOrUpdateContactAsync(string erpCustomerNumber, ErpContact contact, CancellationToken ct) =>
            throw new InvalidOperationException("not used");
        public Task<string> CreateOrderAsync(string erpCustomerNumber, ErpOrder order, CancellationToken ct) =>
            throw new InvalidOperationException("not used");
    }

    /// <summary>Records each send with its Reply-To and the ambient e-mail context at send time.</summary>
    private sealed class RecordingSender : IEmailSender
    {
        private readonly IEmailContextAccessor _ctx;
        public RecordingSender(IEmailContextAccessor ctx) => _ctx = ctx;
        public List<(string To, string Subject, string Html, EmailReplyTo? ReplyTo, EmailContext? Context)> Sent { get; } = new();

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
            => SendAsync(toEmail, subject, htmlBody, (EmailReplyTo?)null, cancellationToken);
        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken cancellationToken = default)
            => SendAsync(toEmail, subject, htmlBody, (EmailReplyTo?)null, cancellationToken);
        public Task SendAsync(string toEmail, string subject, string htmlBody, EmailReplyTo? replyTo, CancellationToken cancellationToken = default)
        {
            Sent.Add((toEmail, subject, htmlBody, replyTo, _ctx.Current));
            return Task.CompletedTask;
        }
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default)
            => SendAsync(toEmail, subject, htmlBody, (EmailReplyTo?)null, cancellationToken);
        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody, string icsContent, string icsFileName, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not used");
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not used");
    }

    private sealed record World(CommunityHubDbContext Db, int EventId, CouponSignupService Service, FakeErp Erp, RecordingSender Mail);

    /// <summary>DEV's policy as configured (§1037): ERP writes allowed, everything unlisted blocked.</summary>
    private static ExternalWriteOptions DevPolicy() => new()
    {
        AllowExternalWrites = false,
        ExternalWrites = new(StringComparer.OrdinalIgnoreCase) { ["Zoho"] = false, ["LinkedIn"] = false, ["Erp"] = true, ["SharePoint"] = true },
    };

    private static async Task<World> NewAsync(IExternalWriteGuard? guard = null, FakeErp? erp = null)
    {
        var db = ScenarioFixture.NewDb();
        var ev = new Event { CommunityName = "C", DisplayName = "Community 2027", Code = "C27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var ctx = new EmailContextAccessor();
        var mail = new RecordingSender(ctx);
        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            PrivateTemplateDirectory = Path.Combine(Path.GetTempPath(), $"no-overlay-{Guid.NewGuid():N}"),
            HubUrl = "https://hub.example.test",
        }));
        erp ??= new FakeErp();
        var svc = new CouponSignupService(db, guard ?? new AllowAllExternalWrites(), mail, templates, new FixedClock(Now), erp, ctx);
        return new World(db, ev.Id, svc, erp, mail);
    }

    private static CouponSignupInput Valid(
        CouponSignupOrderType order = CouponSignupOrderType.AdHoc, string vat = "DK12345678",
        string? invoiceEmail = "invoice@contoso.example", string? ean = null, int? split = null, string email = "jane@contoso.example") =>
        new(order, CouponSignupTicketType.TwoDay, 10, vat, "Contoso ApS", "Testvej 1", "1234", "Testby", "DK",
            "Jane Tester", email, "+45 12 34 56 78", invoiceEmail, ean, split, "PO-42");

    // ------------------------------------------------------------------ validation

    [Fact]
    public void A_complete_request_is_valid_and_normalised()
    {
        var (problems, v) = CouponSignupService.Validate(Valid(vat: "dk 1234 5678") with { RequesterEmail = " Jane@Contoso.Example " });

        Assert.Empty(problems);
        Assert.NotNull(v);
        Assert.Equal("DK12345678", v!.VatNumber);
        Assert.Equal("jane@contoso.example", v.RequesterEmail);
        Assert.Equal(0, v.SplitPercent);     // empty split = the company pays 100 %
        // A bare national number takes the chosen country's prefix.
        Assert.Equal("DK12345678", CouponSignupService.NormalizeVat("12345678", "DK"));
    }

    [Fact]
    public void Every_mandatory_field_is_required()
    {
        var (problems, v) = CouponSignupService.Validate(new CouponSignupInput(
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));

        Assert.Null(v);
        foreach (var expected in new[]
                 {
                     "order type", "ticket type", "number of tickets", "country", "VAT number", "company name", "address",
                     "postal code", "city", "full name", "e-mail address", "mobile number", "invoice details",
                 })
            Assert.Contains(problems, p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Exactly_one_invoice_detail_is_required()
    {
        Assert.Contains(CouponSignupService.Validate(Valid(invoiceEmail: null, ean: null)).Problems,
            p => p.Contains("invoice e-mail OR an EAN", StringComparison.Ordinal));
        Assert.Contains(CouponSignupService.Validate(Valid(invoiceEmail: "invoice@contoso.example", ean: "5790000123456")).Problems,
            p => p.Contains("not both", StringComparison.Ordinal));
        Assert.Empty(CouponSignupService.Validate(Valid(invoiceEmail: "invoice@contoso.example", ean: null)).Problems);
        Assert.Empty(CouponSignupService.Validate(Valid(invoiceEmail: null, ean: "5790000123456")).Problems);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(50, true)]
    [InlineData(99, true)]
    [InlineData(100, false)]   // the attendee would pay everything — no coupon needed
    [InlineData(101, false)]
    public void Split_percent_must_be_within_0_to_100_and_below_100(int split, bool ok) =>
        Assert.Equal(ok, CouponSignupService.Validate(Valid(split: split)).Problems.Count == 0);

    [Theory]
    [InlineData("5790000123456", true)]
    [InlineData("579 0000 123456", true)]   // spaces are stripped
    [InlineData("579000012345", false)]     // 12 digits
    [InlineData("57900001234567", false)]   // 14 digits
    [InlineData("57900001234AB", false)]
    public void An_EAN_number_is_exactly_13_digits(string ean, bool ok) =>
        Assert.Equal(ok, CouponSignupService.Validate(Valid(invoiceEmail: null, ean: ean)).Problems.Count == 0);

    [Fact]
    public void VAT_email_and_phone_formats_are_checked_server_side()
    {
        Assert.Contains(CouponSignupService.Validate(Valid(vat: "DK1234")).Problems, p => p.Contains("VAT number does not look right"));
        Assert.Contains(CouponSignupService.Validate(Valid(vat: "DK1234567X")).Problems, p => p.Contains("VAT number does not look right"));
        Assert.Empty(CouponSignupService.Validate(Valid(vat: "SE556677889901")).Problems);
        Assert.Contains(CouponSignupService.Validate(Valid(email: "not-an-email")).Problems, p => p.Contains("e-mail address does not look right"));
        Assert.Contains(CouponSignupService.Validate(Valid() with { RequesterMobile = "12" }).Problems, p => p.Contains("mobile number does not look right"));
        Assert.Contains(CouponSignupService.Validate(Valid(invoiceEmail: "invoice@nowhere")).Problems, p => p.Contains("invoice e-mail address does not look right"));
        Assert.Contains(CouponSignupService.Validate(Valid() with { TicketCount = 0 }).Problems, p => p.Contains("number of tickets"));
    }

    // ------------------------------------------------------------------ e-conomic

    [Fact]
    public async Task An_existing_customer_is_found_by_VAT_number_and_never_created_again()
    {
        var erp = new FakeErp();
        erp.ByVat["DK12345678"] = "5001";
        var w = await NewAsync(erp: erp);

        var outcome = await w.Service.SubmitAsync(w.EventId, Valid(), "10.0.0.1");

        Assert.True(outcome.Ok);
        Assert.Equal(new[] { "DK12345678" }, erp.Lookups);
        Assert.Empty(erp.Created);
        var r = await w.Db.CouponSignupRequests.SingleAsync();
        Assert.Equal(5001, r.ErpCustomerNumber);
        Assert.False(r.ErpCustomerCreated);
        Assert.Null(r.ErpError);
        Assert.Equal(5001, (await w.Db.CouponInvoicingSettings.SingleAsync()).ErpCustomerNumber);
    }

    [Fact]
    public async Task A_missing_customer_is_created_in_customer_group_2()
    {
        var w = await NewAsync();

        await w.Service.SubmitAsync(w.EventId, Valid(invoiceEmail: null, ean: "5790000123456"), "10.0.0.1");

        var created = Assert.Single(w.Erp.Created);
        Assert.Equal(2, created.CustomerGroupNumber);
        Assert.Equal(CouponSignupService.ErpCustomerGroup, created.CustomerGroupNumber);
        Assert.Equal("Contoso ApS", created.Name);
        Assert.Equal("DK12345678", created.CorporateIdentificationNumber);
        Assert.Equal("5790000123456", created.Ean);
        Assert.Equal("1", created.VatZone);          // Denmark = domestic zone
        Assert.Equal("Denmark", created.Country);
        var r = await w.Db.CouponSignupRequests.SingleAsync();
        Assert.Equal(9001, r.ErpCustomerNumber);
        Assert.True(r.ErpCustomerCreated);
    }

    [Fact]
    public async Task On_DEV_the_guard_blocks_the_customer_create_but_the_request_coupon_and_mail_survive()
    {
        using var guardDb = ScenarioFixture.NewDb();
        var devGuard = new ExternalWriteGuard(guardDb, DevPolicy());
        var w = await NewAsync(guard: devGuard);

        var outcome = await w.Service.SubmitAsync(w.EventId, Valid(), "10.0.0.1");

        Assert.True(outcome.Ok);
        Assert.Empty(w.Erp.Created);                                      // nothing reached e-conomic
        var r = await w.Db.CouponSignupRequests.SingleAsync();
        Assert.Null(r.ErpCustomerNumber);
        Assert.Contains("ErpCouponCustomerCreate", r.ErpError);
        var coupon = await w.Db.CouponInvoicingSettings.SingleAsync();
        Assert.Null(coupon.ErpCustomerNumber);
        Assert.True(coupon.NeedsAttention);                               // shows up on the organizer page
        var mail = Assert.Single(w.Mail.Sent);
        Assert.Contains("NOT resolved", mail.Html);
    }

    [Fact]
    public async Task The_coupon_ceiling_is_blocked_on_DEV_and_open_on_PROD()
    {
        using var db = ScenarioFixture.NewDb();
        Assert.True(await new ExternalWriteGuard(db, DevPolicy()).AllowAsync(ExternalSystems.Erp, "CreateCustomerAsync"));
        Assert.False(await new ExternalWriteGuard(db, DevPolicy()).AllowAsync(ExternalSystems.ErpCouponCustomerCreate, "CreateCouponCustomer"));
        Assert.True(await new ExternalWriteGuard(db, new ExternalWriteOptions { AllowExternalWrites = true })
            .AllowAsync(ExternalSystems.ErpCouponCustomerCreate, "CreateCouponCustomer"));
        Assert.Equal("ErpCouponCustomerCreate", ExternalSystems.ConfigKeyFor(ExternalSystems.ErpCouponCustomerCreate));
    }

    // ------------------------------------------------------------------ the CEH coupon

    [Fact]
    public async Task An_ad_hoc_request_creates_an_ad_hoc_coupon_capped_at_the_number_of_tickets()
    {
        var w = await NewAsync();

        var outcome = await w.Service.SubmitAsync(w.EventId, Valid(order: CouponSignupOrderType.AdHoc, split: 50), "10.0.0.1");

        var coupon = await w.Db.CouponInvoicingSettings.SingleAsync();
        Assert.Equal(CouponBillingType.ClaimableAdHocPaymentByCustomer, coupon.BillingType);
        Assert.Equal(10, coupon.ClaimCapTickets);
        Assert.Equal(50, coupon.InvoicedSharePercent);           // the COMPANY's share
        Assert.Equal(outcome.CouponCode, coupon.CouponName);
        Assert.StartsWith("CONTOSOAPS-2D-", coupon.CouponName);
        Assert.Equal(coupon.Id, (await w.Db.CouponSignupRequests.SingleAsync()).CouponInvoicingSettingId);
    }

    [Theory]
    [InlineData(0, null)]     // the company pays 100 % — null is every coupon's "100 %"
    [InlineData(30, 70)]      // attendee 30 % ⇒ company invoiced 70 %
    public async Task A_prepaid_request_creates_a_prepaid_coupon_without_a_pool(int split, int? companyShare)
    {
        var w = await NewAsync();

        await w.Service.SubmitAsync(w.EventId, Valid(order: CouponSignupOrderType.Prepaid, split: split), "10.0.0.1");

        var coupon = await w.Db.CouponInvoicingSettings.SingleAsync();
        Assert.Equal(CouponBillingType.AllocatedPrepaymentByCustomer, coupon.BillingType);
        Assert.Null(coupon.ClaimCapTickets);
        Assert.Equal(companyShare, coupon.InvoicedSharePercent);
        // 🔒 No pool / purchase: adding tickets to a pool raises the e-conomic invoice — the organizer's act.
        Assert.Empty(await w.Db.CouponPrepaidAllocations.ToListAsync());
        Assert.Empty(await w.Db.CouponPrepaidPurchases.ToListAsync());
    }

    [Fact]
    public void The_proposed_code_is_short_ascii_and_unique_by_request()
    {
        Assert.Equal("BLAABAERGR-1D-7", CouponSignupService.ProposedCode("Blåbær Gruppen A/S", CouponSignupTicketType.OneDay, 7));
        Assert.Equal("COUPON-2D-12", CouponSignupService.ProposedCode("!!!", CouponSignupTicketType.TwoDay, 12));
    }

    // ------------------------------------------------------------------ the mail

    [Fact]
    public async Task The_request_is_mailed_to_the_event_mailbox_ring_exempt_with_reply_to_the_requester()
    {
        var w = await NewAsync();

        await w.Service.SubmitAsync(w.EventId, Valid(), "10.0.0.1");

        var mail = Assert.Single(w.Mail.Sent);
        Assert.Equal("info@expertslive.dk", mail.To);
        Assert.Equal(CouponSignupService.RequestMailbox, mail.To);
        Assert.Contains("Contoso ApS", mail.Subject);
        Assert.Contains("10 × 2-day ticket", mail.Subject);
        Assert.Equal("jane@contoso.example", mail.ReplyTo?.Email);
        Assert.NotNull(mail.Context);
        Assert.True(mail.Context!.RingExempt);
        Assert.Equal("coupon-signup-request", mail.Context.TemplateName);
        Assert.Contains("PO-42", mail.Html);
        Assert.Contains("https://hub.example.test/Organizer/CouponInvoicing", mail.Html);
        Assert.NotNull((await w.Db.CouponSignupRequests.SingleAsync()).MailSentAt);
    }

    [Fact]
    public void The_mail_is_registered_as_a_ring_exempt_internal_mailbox_mail()
    {
        const string key = CouponSignupService.MailKey;
        Assert.Equal("coupon-signup-request", key);
        Assert.True(EmailTemplateCatalog.Map.ContainsKey(key));
        Assert.True(EmailTemplateCatalog.IsRingExempt(key));
        Assert.True(EmailTemplateCatalog.IsInternalMailboxMail(key));
        Assert.Equal(EmailAudience.Organizer, EmailTemplateCatalog.AudienceFor(key));
        Assert.Empty(EmailTemplateCatalog.RecipientRolesFor(key));
        var file = File.ReadAllText(Path.Combine(RepoPaths.EmailTemplates(), key + ".html"));
        Assert.StartsWith("Subject: ", file);
    }

    // ------------------------------------------------------------------ abuse

    [Fact]
    public async Task The_anonymous_form_is_rate_limited_per_IP()
    {
        var w = await NewAsync();
        for (var i = 0; i < CouponSignupService.MaxPerIpPerHour; i++)
            Assert.True((await w.Service.SubmitAsync(w.EventId, Valid(email: $"p{i}@contoso.example"), "10.0.0.9")).Ok);

        var blocked = await w.Service.SubmitAsync(w.EventId, Valid(email: "late@contoso.example"), "10.0.0.9");

        Assert.False(blocked.Ok);
        Assert.Contains(blocked.Problems, p => p.Contains("Too many requests"));
        Assert.Equal(CouponSignupService.MaxPerIpPerHour, await w.Db.CouponSignupRequests.CountAsync());
    }

    [Fact]
    public async Task An_invalid_request_stores_nothing_and_sends_nothing()
    {
        var w = await NewAsync();

        var outcome = await w.Service.SubmitAsync(w.EventId, Valid(invoiceEmail: null, ean: null), "10.0.0.1");

        Assert.False(outcome.Ok);
        Assert.Empty(await w.Db.CouponSignupRequests.ToListAsync());
        Assert.Empty(await w.Db.CouponInvoicingSettings.ToListAsync());
        Assert.Empty(w.Erp.Created);
        Assert.Empty(w.Mail.Sent);
    }

    // ------------------------------------------------------------------ the live client payload

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"customerNumber\":9001}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static EconomicErpOptions LiveOptions() => new()
    {
        Enabled = true, ApiBaseUrl = "https://erp.example.test", AppSecretToken = "fake", AgreementGrantToken = "fake",
        CustomerGroupNumber = 1,
    };

    [Fact]
    public async Task The_live_client_sends_customer_group_2_and_the_EAN_and_keeps_the_default_group_otherwise()
    {
        var handler = new CapturingHandler();
        var client = new LiveEconomicErpClient(new HttpClient(handler), LiveOptions(),
            NullLogger<LiveEconomicErpClient>.Instance, new AllowAllExternalWrites());

        var number = await client.CreateCustomerAsync(new ErpCustomer("x", "Contoso ApS", "DK12345678", null, "1", null, null,
            CustomerGroupNumber: 2, Ean: "5790000123456"), CancellationToken.None);
        await client.CreateCustomerAsync(new ErpCustomer("y", "Fabrikam", "DK87654321", null, "1", null, null), CancellationToken.None);

        Assert.Equal("9001", number);
        Assert.Contains("\"customerGroupNumber\":2", handler.Calls[0].Body);
        Assert.Contains("\"ean\":\"5790000123456\"", handler.Calls[0].Body);
        Assert.Contains("\"customerGroupNumber\":1", handler.Calls[1].Body);   // every other caller is unchanged
        Assert.DoesNotContain("\"ean\"", handler.Calls[1].Body);
    }

    [Fact]
    public async Task The_live_client_refuses_the_create_when_the_host_may_not_write_to_economic()
    {
        var handler = new CapturingHandler();
        using var db = ScenarioFixture.NewDb();
        var blocked = new ExternalWriteGuard(db, new ExternalWriteOptions { AllowExternalWrites = false });
        var client = new LiveEconomicErpClient(new HttpClient(handler), LiveOptions(), NullLogger<LiveEconomicErpClient>.Instance, blocked);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateCustomerAsync(
            new ErpCustomer("x", "Contoso ApS", "DK12345678", null, "1", null, null, CustomerGroupNumber: 2), CancellationToken.None));
        Assert.Empty(handler.Calls);
    }
}
