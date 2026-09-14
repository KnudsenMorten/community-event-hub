using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1110 / §1111 — the claim invite's URL, and the tick that used to veto the button that sends it.
/// </summary>
/// <remarks>
/// <para>🔴 <b>§1110 — the link went out relative.</b> Operator 2026-08-21: *"url looks wrong here …
/// correct url format -&gt; https://eldk27.expertslive.dk#/buyTickets?promoCode=…"*. The page asked DI
/// for a bare <see cref="EventEditionConfig"/>, which NOTHING registers — only the loader is. So the
/// optional constructor parameter defaulted to null on every request, the ticket base resolved to
/// <c>""</c>, and two real partners were mailed the bare fragment <c>#/buyTickets?promoCode=…</c>.</para>
///
/// <para>🔑 <b>The composer was never wrong</b> — <c>CouponClaimInviteComposerTests</c> asserted the
/// exact right URL and passed throughout, because it passed a base in. The defect lived entirely in
/// the wiring, which is why the guard here is a PAGE test: it exercises the seam the unit test
/// cannot see.</para>
///
/// <para>🔴 <b>§1111 — two consents for one act.</b> The <c>SendClaimInviteMail</c> tick's only
/// effect was to make "Send the claim link now" answer *"No mail was sent"*. Operator 2026-08-21:
/// *"i dont get the point of having this tick if there is also a button"*.</para>
///
/// <para>FAKE names and a temp config file only; nothing here reads the shipped edition JSON.</para>
/// </remarks>
public sealed class CouponClaimLinkAndSwitchTests : IDisposable
{
    private const int EventId = 71;
    private const string Coupon = "PARTNER-LINK";
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch (IOException) { /* best effort */ }
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context)
            => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    /// <summary>Local capture — <c>CapturingEmailSender</c> lives in the Core test project.</summary>
    private sealed class CaptureSender : IEmailSender
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();

        public Task SendAsync(string to, string subject, string html, CancellationToken ct = default)
        { Sent.Add((to, subject, html)); return Task.CompletedTask; }

        public Task SendAsync(
            string to, string subject, string html,
            IReadOnlyCollection<string>? cc, CancellationToken ct = default)
        { Sent.Add((to, subject, html)); return Task.CompletedTask; }

        public Task SendAsync(
            string to, string subject, string html, string text, CancellationToken ct = default)
        { Sent.Add((to, subject, html)); return Task.CompletedTask; }

        public Task SendWithIcsAsync(
            string to, string subject, string html, string ics, string icsName,
            CancellationToken ct = default)
        { Sent.Add((to, subject, html)); return Task.CompletedTask; }

        public Task SendWithAttachmentsAsync(
            string to, string subject, string html,
            IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default)
        { Sent.Add((to, subject, html)); return Task.CompletedTask; }
    }

    /// <summary>Customer 4242 with one contact who HAS an e-mail, so nothing else blocks the send.</summary>
    private sealed class FakeContactClient : IEconomicContactAdminClient
    {
        public bool CanWrite => true;

        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
            string? search, int? customerGroup = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(new[]
            {
                new EconomicCustomerRow(4242, "Partner A/S", null),
            });

        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(
            int customerNumber, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EconomicContactRow>>(
                customerNumber == 4242
                    ? new[] { new EconomicContactRow(11, "Rita Requester", "rita@example.test", null) }
                    : Array.Empty<EconomicContactRow>());

        public Task<int> CreateContactAsync(int c, EconomicContactInput i, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UpdateContactAsync(int c, int n, EconomicContactInput i, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteContactAsync(int c, int n, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"coupon-link-{Guid.NewGuid():N}").Options);

    private static ClaimsPrincipal Session(Participant p) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new Claim(ClaimTypes.Email, p.Email),
            new Claim(ClaimTypes.Name, p.FullName),
            new Claim(ClaimTypes.Role, p.Role.ToString()),
            new Claim("EventId", p.EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme));

    private static async Task<(Participant Organizer, CouponInvoicingSetting Rule)> SeedAsync(
        CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "CL27", CommunityName = "C", DisplayName = "CL 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });

        var org = new Participant
        {
            EventId = EventId, FullName = "Olivia Organizer", Email = "olivia@example.test",
            Role = ParticipantRole.Organizer, IsActive = true,
        };
        db.Participants.Add(org);

        var rule = new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = Coupon,
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = 4242, RequesterContactNumber = 11,
            // 🔴 §1111 — deliberately FALSE. Before the fix this alone stopped the button.
            SendClaimInviteMail = false,
        };
        db.CouponInvoicingSettings.Add(rule);
        await db.SaveChangesAsync();
        return (org, rule);
    }

    /// <summary>An edition config on disk with (or without) a public ticket URL.</summary>
    private string WriteConfig(string? ticketUrl)
    {
        var path = Path.Combine(Path.GetTempPath(), $"event-cl27-{Guid.NewGuid():N}.json");
        var ticket = ticketUrl is null
            ? "{ \"enabled\": true }"
            : $"{{ \"enabled\": true, \"ticketUrl\": \"{ticketUrl}\" }}";
        File.WriteAllText(path, $"{{ \"ticketSale\": {ticket} }}");
        _tempFiles.Add(path);
        return path;
    }

    private CouponInvoicingModel NewModel(
        CommunityHubDbContext db, HttpContext http, string? ticketUrl,
        IEmailSender? sender = null)
        => new(new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http)),
            db, new FixedClock(),
            economic: new FakeContactClient(),
            emailSender: sender,
            emailContext: sender is null ? null : new EmailContextAccessor(),
            editionConfigLoader: new EventEditionConfigLoader(),
            eventConfigOptions: new EventConfigOptions { EventConfigPath = WriteConfig(ticketUrl) })
        {
            PageContext = new PageContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NullTempDataProvider()),
        };

    // ---------------------------------------------------------------------
    //  §1110 — the claim URL
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Claim_invite_quotes_the_ABSOLUTE_ticket_url_from_the_edition_config()
    {
        using var db = NewDb();
        var (org, rule) = await SeedAsync(db);
        var model = NewModel(db, new DefaultHttpContext { User = Session(org) },
            ticketUrl: "https://tickets.example.test");
        model.InviteSettingId = rule.Id;

        await model.OnPostPreviewInviteAsync(default);

        var preview = Assert.IsType<CouponInvoicingModel.InvitePreview>(model.Invite);
        Assert.Null(preview.Blocker);
        // 🔑 The whole point: the host is PRESENT. The old wiring produced "#/buyTickets?…", which
        // resolves against whatever page the reader happens to have open.
        Assert.Contains($"https://tickets.example.test#/buyTickets?promoCode={Coupon}", preview.Html);
        Assert.DoesNotContain("\">#/buyTickets", preview.Html);
    }

    [Fact]
    public async Task A_missing_ticket_url_BLOCKS_the_invite_rather_than_mailing_a_bare_fragment()
    {
        using var db = NewDb();
        var (org, rule) = await SeedAsync(db);
        var sender = new CaptureSender();
        var model = NewModel(db, new DefaultHttpContext { User = Session(org) },
            ticketUrl: null, sender: sender);
        model.InviteSettingId = rule.Id;

        await model.OnPostPreviewInviteAsync(default);

        var preview = Assert.IsType<CouponInvoicingModel.InvitePreview>(model.Invite);
        Assert.NotNull(preview.Blocker);
        Assert.Contains("ticket URL", preview.Blocker!);

        // 🔒 And the SEND path honours the same blocker — a preview that refuses is worth nothing if
        // the button behind it does not.
        model.InviteSettingId = rule.Id;
        await model.OnPostSendInviteAsync(default);
        Assert.Empty(sender.Sent);
        Assert.Null((await db.CouponInvoicingSettings.SingleAsync()).ClaimInviteSentAt);
    }

    // ---------------------------------------------------------------------
    //  §1111 — the retired tick
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Confirming_the_code_is_live_MAILS_the_partner_even_though_the_old_tick_is_off()
    {
        using var db = NewDb();
        var (org, rule) = await SeedAsync(db);
        Assert.False(rule.SendClaimInviteMail);   // the tick §1111 retired

        var sender = new CaptureSender();
        var model = NewModel(db, new DefaultHttpContext { User = Session(org) },
            ticketUrl: "https://tickets.example.test", sender: sender);
        model.InviteSettingId = rule.Id;

        await model.OnPostConfirmCodeLiveAsync(default);

        // Pressing the button IS the consent. It used to answer "No mail was sent".
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("rita@example.test", sent.To);
        var saved = await db.CouponInvoicingSettings.SingleAsync();
        Assert.NotNull(saved.ClaimInviteSentAt);
        Assert.Equal(Now, saved.BackstageCodeConfirmedAt);
    }

    /// <summary>
    /// 🔴 The DEFECT CLASS, guarded directly: a page may not ask DI for a type DI does not have.
    /// </summary>
    /// <remarks>
    /// <c>EventEditionConfig</c> is never registered — <c>Program.cs</c> registers the LOADER. An
    /// optional constructor parameter of an unregistered type does not fail loudly; it silently
    /// arrives null, which is how a blank ticket base reached two paying partners. Any page needing
    /// edition config must take <see cref="EventEditionConfigLoader"/> plus
    /// <see cref="EventConfigOptions"/>, exactly as every other page already does.
    /// </remarks>
    [Fact]
    public void No_page_model_asks_DI_for_the_unregistered_EventEditionConfig()
    {
        var offenders = typeof(CouponInvoicingModel).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(PageModel).IsAssignableFrom(t))
            .SelectMany(t => t.GetConstructors().Select(c => (Type: t, Ctor: c)))
            .Where(x => x.Ctor.GetParameters()
                .Any(p => p.ParameterType == typeof(EventEditionConfig)))
            .Select(x => x.Type.FullName!)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These page models inject EventEditionConfig, which nothing registers — they will get "
            + "null on every request. Take EventEditionConfigLoader + EventConfigOptions instead: "
            + string.Join(", ", offenders));
    }
}
