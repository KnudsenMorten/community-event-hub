using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for the e-conomic ERP / webshop sponsor slice (REQUIREMENTS
/// §7a): CVR validation, customer/contact sync mapping, and the order currency/
/// FX check + idempotency. Everything runs against the service interfaces with
/// the EF Core InMemory provider — no real e-conomic / webshop / FX calls.
/// </summary>
public sealed class ErpSyncTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"erp-{Guid.NewGuid():N}")
            .Options);

    // --- CVR validation (pure offline modulus-11 gate) ---------------------

    [Theory]
    [InlineData("13585628", true)]   // valid: weighted sum 135, mod 11 = 3, check 8
    [InlineData("DK13585628", true)] // VAT prefix + still valid after normalize
    [InlineData("12345678", false)]  // checksum fails
    [InlineData("1358562", false)]   // wrong length (7)
    [InlineData("135856280", false)] // wrong length (9)
    [InlineData("", false)]          // empty
    [InlineData(null, false)]        // null
    public async Task Cvr_offline_gate_validates_format_and_checksum(string? raw, bool expectValid)
    {
        var validator = new CvrValidator(lookup: null, log: null);
        var result = await validator.ValidateAsync(raw);
        Assert.Equal(expectValid, result.IsValid);
        Assert.False(result.RegistryChecked); // no external lookup wired
    }

    [Fact]
    public async Task Cvr_uses_external_register_when_lookup_is_enabled()
    {
        var lookup = new FakeCvrLookup(canLookup: true, exists: true);
        var validator = new CvrValidator(lookup, NullLogger<CvrValidator>.Instance);

        var result = await validator.ValidateAsync("13585628");

        Assert.True(result.IsValid);
        Assert.True(result.RegistryChecked);
    }

    [Fact]
    public async Task Cvr_format_valid_but_not_in_register_is_invalid()
    {
        var lookup = new FakeCvrLookup(canLookup: true, exists: false);
        var validator = new CvrValidator(lookup, NullLogger<CvrValidator>.Instance);

        var result = await validator.ValidateAsync("13585628");

        Assert.False(result.IsValid);
        Assert.Equal("not-in-register", result.Reason);
        Assert.True(result.RegistryChecked);
    }

    [Fact]
    public async Task Cvr_registry_outage_falls_back_to_offline_pass()
    {
        var lookup = new FakeCvrLookup(canLookup: true, exists: true, throws: true);
        var validator = new CvrValidator(lookup, NullLogger<CvrValidator>.Instance);

        var result = await validator.ValidateAsync("13585628");

        Assert.True(result.IsValid);            // offline gate already passed
        Assert.False(result.RegistryChecked);   // registry did not confirm
    }

    // --- Customer / contact mapping ----------------------------------------

    [Fact]
    public void MapCustomer_resolves_public_name_first()
    {
        var cm = Company(publicName: "2LINKIT", legalName: "Legal Name ApS",
            cvr: "13585628", currency: "DKK");
        var customer = EconomicCustomerSyncService.MapCustomer(
            "42", cm, billingName: "Billing GmbH", contactEmail: "coord@example.test");

        Assert.Equal("2LINKIT", customer.Name);                  // public wins
        Assert.Equal("13585628", customer.CorporateIdentificationNumber);
        Assert.Equal("DKK", customer.Currency);
        Assert.Equal("coord@example.test", customer.Email);
    }

    [Fact]
    public void MapCustomer_falls_back_through_the_chain()
    {
        // No public, no legal, no billing → "Company {id}".
        Assert.Equal("Company 7",
            EconomicCustomerSyncService.MapCustomer("7", cm: null, billingName: null, contactEmail: null).Name);

        // Legal when public blank.
        Assert.Equal("Legal ApS",
            EconomicCustomerSyncService.MapCustomer("7",
                Company(publicName: "", legalName: "Legal ApS"), null, null).Name);

        // Billing when public + legal blank.
        Assert.Equal("Billing GmbH",
            EconomicCustomerSyncService.MapCustomer("7",
                Company(publicName: "", legalName: ""), billingName: "Billing GmbH", null).Name);
    }

    [Fact]
    public void MapContact_derives_role_from_signer_and_coordinator_ids()
    {
        var company = Company(signerUserId: 10, coordinatorUserId: 20);

        Assert.Equal(ErpContactRole.Signer,
            EconomicCustomerSyncService.MapContact("42",
                new CompanyManagerUser(10, "s@x.test", "Signer One", "Signer"), company).Role);
        Assert.Equal(ErpContactRole.EventCoordinator,
            EconomicCustomerSyncService.MapContact("42",
                new CompanyManagerUser(20, "c@x.test", "Coord Two", "Coord"), company).Role);
        Assert.Equal(ErpContactRole.Contact,
            EconomicCustomerSyncService.MapContact("42",
                new CompanyManagerUser(99, "o@x.test", "Other", "Other"), company).Role);
    }

    // --- Customer sync against the interface --------------------------------

    [Fact]
    public async Task SyncCustomer_with_invalid_cvr_blocks_create_and_records_reason()
    {
        using var db = NewDb();
        var svc = NewCustomerSvc(db, new StubErpClient(canWrite: true));

        var customer = EconomicCustomerSyncService.MapCustomer(
            "42", Company(publicName: "Acme", cvr: "12345678"), null, null); // bad checksum

        var result = await svc.SyncCustomerAsync(EventId, customer);

        Assert.Equal(ErpSyncOutcome.Failed, result.Outcome);
        Assert.StartsWith("cvr-invalid", result.Detail);

        var link = await db.ErpCustomerLinks.SingleAsync();
        Assert.False(link.CvrValid);
        Assert.Equal(string.Empty, link.ErpCustomerNumber); // never created
    }

    [Fact]
    public async Task SyncCustomer_records_wouldcreate_when_erp_write_disabled()
    {
        using var db = NewDb();
        var svc = NewCustomerSvc(db, new StubErpClient(canWrite: false)); // TESTMODE-like

        var customer = EconomicCustomerSyncService.MapCustomer(
            "42", Company(publicName: "Acme", cvr: "13585628"), null, null);

        var result = await svc.SyncCustomerAsync(EventId, customer);

        Assert.Equal(ErpSyncOutcome.WouldCreate, result.Outcome);
        var link = await db.ErpCustomerLinks.SingleAsync();
        Assert.True(link.CvrValid);
        Assert.Equal(string.Empty, link.ErpCustomerNumber); // not created, but link recorded
    }

    [Fact]
    public async Task SyncCustomer_creates_then_is_idempotent_on_rerun()
    {
        using var db = NewDb();
        var erp = new StubErpClient(canWrite: true);
        var svc = NewCustomerSvc(db, erp);
        var customer = EconomicCustomerSyncService.MapCustomer(
            "42", Company(publicName: "Acme", cvr: "13585628"), null, null);

        var first = await svc.SyncCustomerAsync(EventId, customer);
        Assert.Equal(ErpSyncOutcome.Created, first.Outcome);
        Assert.Equal("ERP-1", first.ErpCustomerNumber);
        Assert.Equal(1, erp.CreateCustomerCalls);

        // Re-run with a FRESH service (same DB) — the link must drive an Update, not a second Create.
        var svc2 = NewCustomerSvc(db, erp);
        var second = await svc2.SyncCustomerAsync(EventId, customer);
        Assert.Equal(ErpSyncOutcome.Updated, second.Outcome);
        Assert.Equal(1, erp.CreateCustomerCalls);       // still 1 — no duplicate create
        Assert.Equal(1, await db.ErpCustomerLinks.CountAsync());
    }

    [Fact]
    public async Task SyncContact_requires_an_existing_customer_link()
    {
        using var db = NewDb();
        var svc = NewCustomerSvc(db, new StubErpClient(canWrite: true));

        var contact = new ErpContact("42", "c@x.test", "Coord", ErpContactRole.EventCoordinator);
        var result = await svc.SyncContactAsync(EventId, contact);

        Assert.Equal(ErpSyncOutcome.Failed, result.Outcome);
        Assert.Equal("no-erp-customer-link", result.Detail);
    }

    // --- Order creation + currency/FX check ---------------------------------

    [Fact]
    public async Task Currency_check_same_currency_is_rate_one()
    {
        using var db = NewDb();
        var svc = NewOrderSvc(db, new StubErpClient(true), new FakeFx(canQuote: false), baseCurrency: "DKK");

        var check = await svc.CheckCurrencyAsync("DKK");
        Assert.True(check.Ok);
        Assert.Equal(1m, check.Rate);
        Assert.Equal("same-currency", check.Result);
    }

    [Fact]
    public async Task Currency_check_applies_live_fx_when_provider_enabled()
    {
        using var db = NewDb();
        var svc = NewOrderSvc(db, new StubErpClient(true), new FakeFx(canQuote: true, rate: 7.46m), baseCurrency: "DKK");

        var check = await svc.CheckCurrencyAsync("EUR");
        Assert.True(check.Ok);
        Assert.Equal(7.46m, check.Rate);
        Assert.Equal("fx-applied", check.Result);
    }

    [Fact]
    public async Task Currency_check_is_known_currency_gate_without_a_provider()
    {
        using var db = NewDb();
        var svc = NewOrderSvc(db, new StubErpClient(true), new FakeFx(canQuote: false), baseCurrency: "DKK");

        var check = await svc.CheckCurrencyAsync("EUR");
        Assert.True(check.Ok);
        Assert.Null(check.Rate);                 // never fabricates a rate
        Assert.Equal("fx-not-configured", check.Result);
    }

    [Fact]
    public async Task Currency_check_rejects_a_malformed_currency()
    {
        using var db = NewDb();
        var svc = NewOrderSvc(db, new StubErpClient(true), new FakeFx(false), "DKK");

        var check = await svc.CheckCurrencyAsync("Danish Kroner");
        Assert.False(check.Ok);
        Assert.Equal("invalid-currency", check.Result);
    }

    [Fact]
    public async Task CreateOrder_records_fx_and_is_idempotent()
    {
        using var db = NewDb();
        var erp = new StubErpClient(canWrite: true);
        var svc = NewOrderSvc(db, erp, new FakeFx(canQuote: true, rate: 7.46m), baseCurrency: "DKK");

        var woo = new WooOrder(
            OrderId: 10726, Status: "completed", BillingEmail: "b@x.test", BillingCompany: "Acme",
            CompanyId: "42", CreatedAt: DateTimeOffset.UtcNow,
            LineItems: new[] { new WooLineItem(501, "Gold Booth", "Tier Packages With Exhibitor Booth") });

        var first = await svc.CreateOrderFromWebshopAsync(EventId, "ERP-1", woo, "42", "EUR");
        Assert.Equal(ErpSyncOutcome.Created, first.Outcome);
        Assert.Equal(1, erp.CreateOrderCalls);

        var link = await db.ErpOrderLinks.SingleAsync();
        Assert.Equal("EUR", link.Currency);
        Assert.Equal(7.46m, link.FxRateApplied);
        Assert.Equal("fx-applied", link.CurrencyCheckResult);

        // Re-run (fresh service, same DB) → idempotent skip, no second create.
        var svc2 = NewOrderSvc(db, erp, new FakeFx(canQuote: true, rate: 7.46m), "DKK");
        var second = await svc2.CreateOrderFromWebshopAsync(EventId, "ERP-1", woo, "42", "EUR");
        Assert.Equal(ErpSyncOutcome.AlreadyExists, second.Outcome);
        Assert.Equal(1, erp.CreateOrderCalls);
    }

    [Fact]
    public async Task CreateOrder_records_wouldcreate_when_erp_write_disabled()
    {
        using var db = NewDb();
        var svc = NewOrderSvc(db, new StubErpClient(canWrite: false), new FakeFx(false), "DKK");

        var woo = new WooOrder(10727, "completed", "b@x.test", "Acme", "42", DateTimeOffset.UtcNow,
            new[] { new WooLineItem(1, "Item", "Sessions") });

        var result = await svc.CreateOrderFromWebshopAsync(EventId, "ERP-1", woo, "42", "DKK");
        Assert.Equal(ErpSyncOutcome.WouldCreate, result.Outcome);
        Assert.Equal(string.Empty, (await db.ErpOrderLinks.SingleAsync()).ErpOrderNumber);
    }

    // --- helpers ------------------------------------------------------------

    private static EconomicCustomerSyncService NewCustomerSvc(CommunityHubDbContext db, IEconomicErpClient erp) =>
        new(db, erp, new CvrValidator(lookup: null, log: null), TimeProvider.System,
            NullLogger<EconomicCustomerSyncService>.Instance);

    private static EconomicOrderCreationService NewOrderSvc(
        CommunityHubDbContext db, IEconomicErpClient erp, IFxRateProvider fx, string baseCurrency) =>
        new(db, erp, fx, new EconomicErpOptions { BaseCurrency = baseCurrency }, TimeProvider.System,
            NullLogger<EconomicOrderCreationService>.Instance);

    private static CompanyManagerCompany Company(
        string publicName = "Acme", string legalName = "Acme ApS", string cvr = "",
        string currency = "", int signerUserId = 0, int coordinatorUserId = 0) =>
        new(Id: 42, Name: legalName, PublicName: publicName, WebsiteUrl: "", LinkedInUrl: "",
            TwitterUrl: "", DefaultSignerUserId: signerUserId,
            EventCoordinationDefaultContactUserId: coordinatorUserId,
            CorporateIdentificationNumber: cvr, Currency: currency, VatZone: "", ErpCustomerNumber: "");

    private sealed class FakeCvrLookup : IExternalCvrLookup
    {
        private readonly bool _exists;
        private readonly bool _throws;
        public FakeCvrLookup(bool canLookup, bool exists, bool throws = false)
        {
            CanLookup = canLookup;
            _exists = exists;
            _throws = throws;
        }
        public bool CanLookup { get; }
        public Task<bool> ExistsAndActiveAsync(string normalizedCvr, CancellationToken ct) =>
            _throws ? throw new HttpRequestException("register down") : Task.FromResult(_exists);
    }

    private sealed class FakeFx : IFxRateProvider
    {
        private readonly decimal? _rate;
        public FakeFx(bool canQuote, decimal? rate = null) { CanQuote = canQuote; _rate = rate; }
        public bool CanQuote { get; }
        public Task<decimal?> GetRateAsync(string b, string q, CancellationToken ct) =>
            Task.FromResult(string.Equals(b, q, StringComparison.OrdinalIgnoreCase) ? 1m : _rate);
    }

    /// <summary>A controllable in-test ERP client implementing the real seam.</summary>
    private sealed class StubErpClient : IEconomicErpClient
    {
        private int _seq;
        public StubErpClient(bool canWrite) => CanWrite = canWrite;
        public bool CanWrite { get; }
        public int CreateCustomerCalls { get; private set; }
        public int CreateOrderCalls { get; private set; }

        public Task<string?> FindCustomerNumberAsync(ErpCustomer customer, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<string> CreateCustomerAsync(ErpCustomer customer, CancellationToken ct)
        {
            CreateCustomerCalls++;
            return Task.FromResult($"ERP-{++_seq}");
        }
        public Task UpdateCustomerAsync(string n, ErpCustomer c, CancellationToken ct) => Task.CompletedTask;
        public Task CreateOrUpdateContactAsync(string n, ErpContact c, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateOrderAsync(string n, ErpOrder o, CancellationToken ct)
        {
            CreateOrderCalls++;
            return Task.FromResult($"ORD-{++_seq}");
        }
    }
}
