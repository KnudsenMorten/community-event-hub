using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1115 — the ERP reconcile mail repeats only when the note list CHANGES.
/// </summary>
/// <remarks>
/// <para>⚠️ <b>§1112 turned a latent problem into a live one within ten minutes of deploying.</b>
/// Every note this job produced before was episodic — a rename, a billing update, a company missing
/// contacts that someone then fixed — so "mail whenever there is a note" never repeated for long.
/// The de-sponsored note is the first PERMANENT one: it stands until the operator clears
/// <c>erp_customer_number</c> in Company Manager, a webshop action CEH cannot take. It mailed the
/// same line at 02:46 and again at 02:56, and would have done so every 10 minutes for ever.</para>
///
/// <para>🔑 §1088's lesson: a line that arrives every ten minutes stops being read, and the next
/// REAL item becomes indistinguishable from the noise everyone has learned to ignore.</para>
///
/// <para>FAKE names only.</para>
/// </remarks>
public sealed class ErpReconcileMailRepeatTests
{
    private const int EventId = 91;

    private sealed class CmHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            HttpResponseMessage Json(string b) =>
                new(HttpStatusCode.OK) { Content = new StringContent(b, Encoding.UTF8, "application/json") };

            // ⚠️ BOTH customers need a webshop company. #100 without one produces a permanent
            // "no matching webshop company" note of its own, which would mask the thing under test
            // by keeping the note list non-empty on every single run.
            if (req.Method == HttpMethod.Get && path.EndsWith("/companies"))
                return Task.FromResult(Json(
                    "[{\"id\":1,\"erp_customer_number\":\"100\",\"name\":\"Alpha\",\"default_signer_id\":11,\"event_coordination_default_contact_id\":11},"
                    + "{\"id\":2,\"erp_customer_number\":\"200\",\"name\":\"Beta\",\"default_signer_id\":11,\"event_coordination_default_contact_id\":11}]"));

            if (req.Method == HttpMethod.Get && path.Contains("/companies/") && !path.EndsWith("/users"))
                return Task.FromResult(Json(
                    "{\"id\":1,\"name\":\"Alpha\",\"company_name_public\":\"Alpha\",\"erp_customer_number\":\"100\","
                    + "\"default_signer_id\":11,\"event_coordination_default_contact_id\":11}"));
            // The ERP contact already exists as a linked user, so nothing is created and no note
            // about it is raised — the de-sponsored line is then the only content in the mail.
            if (req.Method == HttpMethod.Get && path.Contains("/companies/") && path.EndsWith("/users"))
                return Task.FromResult(Json(
                    "[{\"user_id\":11,\"email\":\"p100@x.test\",\"full_name\":\"Person 100\",\"display_name\":\"Person 100\"}]"));
            return Task.FromResult(Json("{}"));
        }
    }

    /// <summary>#200 has left group 1 — the permanent note that exposed the repeat.</summary>
    private sealed class StubErp : IEconomicContactAdminClient
    {
        private readonly bool _stillDeSponsored;
        public StubErp(bool stillDeSponsored = true) => _stillDeSponsored = stillDeSponsored;

        public bool CanWrite => true;

        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
            string? search, int? customerGroup = null, CancellationToken ct = default)
        {
            var set = customerGroup is null
                ? new[] { 100, 200 }
                : (_stillDeSponsored ? new[] { 100 } : new[] { 100, 200 });
            return Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(
                set.Select(n => new EconomicCustomerRow(n, $"Customer {n}", null)).ToList());
        }

        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(int c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EconomicContactRow>>(new[]
            {
                new EconomicContactRow(1, $"Person {c}", $"p{c}@x.test", "+45", "Role:1,2"),
            });

        public Task<int> CreateContactAsync(int c, EconomicContactInput i, CancellationToken ct = default) => Task.FromResult(1);
        public Task UpdateContactAsync(int c, int n, EconomicContactInput i, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteContactAsync(int c, int n, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingEmail : IEmailSender
    {
        public List<string> Subjects { get; } = new();
        public List<string> Bodies { get; } = new();

        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
        { Subjects.Add(s); Bodies.Add(h); return Task.CompletedTask; }
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string i, string f, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => SendAsync(to, s, h, ct);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 21, 3, 0, 0, TimeSpan.Zero);
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"erp-repeat-{Guid.NewGuid():N}").Options);

    private static async Task SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "RP27", CommunityName = "C", DisplayName = "RP 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        // The job's own run-state row — where the fingerprint lives.
        db.JobRunStates.Add(new JobRunState { FunctionName = "ErpSyncCustomerContactJob" });
        await db.SaveChangesAsync();
    }

    private static ErpWebshopContactSyncService Sut(
        CommunityHubDbContext db, IEmailSender email, bool stillDeSponsored = true)
    {
        var cm = new CompanyManagerClient(new HttpClient(new CmHandler()), new CompanyManagerOptions
        {
            Enabled = true,
            BaseUrl = "https://cm.test/wp-json/company-manager/v1",
            Username = "u",
            Password = "p",
        });
        var clock = new FixedClock();
        return new ErpWebshopContactSyncService(
            new EconomicContactAdminService(new StubErp(stillDeSponsored)), cm,
            new CompanyManagerOptions { Enabled = true }, email,
            NullLogger<ErpWebshopContactSyncService>.Instance,
            db: db,
            deactivate: new ParticipantDeactivationService(
                db, clock, new CommunityHub.Core.Audit.AuditTrailService(db, clock)));
    }

    [Fact]
    public async Task An_unchanged_note_list_is_NOT_mailed_again()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var email = new CapturingEmail();

        await Sut(db, email).SyncAsync();
        Assert.Single(email.Subjects);          // told once, within ten minutes

        await Sut(db, email).SyncAsync();
        await Sut(db, email).SyncAsync();
        Assert.Single(email.Subjects);          // and then quiet — this is the whole fix
    }

    /// <summary>
    /// 🔒 The other half, and the one that matters more: suppressing a repeat must never suppress
    /// NEWS. A fix that quietens a noisy mail by also eating its new content is worse than the noise.
    /// </summary>
    [Fact]
    public async Task A_CHANGED_note_list_is_always_mailed_again()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var email = new CapturingEmail();

        await Sut(db, email).SyncAsync();
        Assert.Single(email.Subjects);

        // #200 rejoins the sponsor group — the de-sponsored line disappears, so the set differs.
        await Sut(db, email, stillDeSponsored: false).SyncAsync();
        Assert.Equal(2, email.Subjects.Count);

        // …and a repeat of THAT state is silent again.
        await Sut(db, email, stillDeSponsored: false).SyncAsync();
        Assert.Equal(2, email.Subjects.Count);

        // It leaves again — news, and a suppressed repeat here would be the fix eating a real alert.
        await Sut(db, email).SyncAsync();
        Assert.Equal(3, email.Subjects.Count);
    }

    [Fact]
    public async Task The_mail_says_it_only_repeats_when_the_list_changes()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var email = new CapturingEmail();

        await Sut(db, email).SyncAsync();

        // 🔑 Silence now means "unchanged", not "fixed" — so the mail has to say so, or the reader
        // draws the wrong conclusion from not hearing again.
        Assert.Contains("repeats only when the list", Assert.Single(email.Bodies),
            StringComparison.OrdinalIgnoreCase);
    }
}
