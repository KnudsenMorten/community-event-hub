using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1221 — a dead extra Zoho sponsor link is removed only after five "not found" reports at
/// least twelve hours apart.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-14: <i>"it must have been failed like 5 times with min 12 hr apart before
/// removing to rule out api issues"</i>. Measured the same day: three companies held a second sponsor
/// id deleted in Backstage, which Zoho answers with 400 "Sponsor not found" — and the same endpoint
/// also answers LIVE records with bursts of bare 400s (§1220).</para>
///
/// <para>FAKE names and ids only. Fully offline.</para>
/// </remarks>
public sealed class ZohoDeadLinkStrikesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
    private const string Dead = "ZSP-DEAD";

    // ---------------------------------------------------------------- the rule itself

    [Fact]
    public void The_fifth_report_twelve_hours_apart_earns_removal_and_not_before()
    {
        var info = new SponsorInfo { SponsorCompanyId = "1" };
        for (var i = 0; i < ZohoDeadLinkStrikes.StrikesToRemove - 1; i++)
            Assert.False(ZohoDeadLinkStrikes.RecordNotFound(info, Dead, T0.AddHours(12 * i)));

        Assert.True(ZohoDeadLinkStrikes.RecordNotFound(info, Dead, T0.AddHours(12 * 4)));
    }

    /// <summary>🔑 The job runs every ten minutes — without the gap, five strikes take under an hour.</summary>
    [Fact]
    public void Reports_inside_the_gap_are_not_counted()
    {
        var info = new SponsorInfo { SponsorCompanyId = "1" };

        // Two full days of ten-minute runs that never cross a 12h boundary from the last COUNTED report
        // would still only count every 12h: 11h59m of reports add nothing.
        for (var m = 0; m < 12 * 60 - 1; m += 10)
            Assert.False(ZohoDeadLinkStrikes.RecordNotFound(info, Dead, T0.AddMinutes(m)));

        Assert.Equal(1, Assert.Single(ZohoDeadLinkStrikes.Read(info)).Strikes);
    }

    [Fact]
    public void Reports_eleven_hours_apart_count_only_every_second_one()
    {
        // Reports at 0, 11, 22, 33 … hours. Counted only when 12h+ since the last COUNTED one:
        // 0, 22, 44, 66, 88 — so the fifth strike lands on the ninth report (88h), not the fifth (44h).
        var info = new SponsorInfo { SponsorCompanyId = "1" };
        for (var i = 0; i < 8; i++)
            Assert.False(ZohoDeadLinkStrikes.RecordNotFound(info, Dead, T0.AddHours(11 * i)));

        Assert.True(ZohoDeadLinkStrikes.RecordNotFound(info, Dead, T0.AddHours(11 * 8)));
    }

    /// <summary>🔒 The strikes must be consecutive evidence: one good read wipes them.</summary>
    [Fact]
    public void A_successful_read_resets_the_count()
    {
        var info = new SponsorInfo { SponsorCompanyId = "1" };
        for (var i = 0; i < 4; i++) ZohoDeadLinkStrikes.RecordNotFound(info, Dead, T0.AddHours(12 * i));

        Assert.True(ZohoDeadLinkStrikes.Clear(info, Dead));
        Assert.Null(info.ZohoDeadLinkStrikesJson);

        Assert.False(ZohoDeadLinkStrikes.RecordNotFound(info, Dead, T0.AddHours(12 * 4)));
    }

    [Fact]
    public void Malformed_json_is_no_strikes_not_a_throw()
    {
        var info = new SponsorInfo { SponsorCompanyId = "1", ZohoDeadLinkStrikesJson = "{ nope" };
        Assert.Empty(ZohoDeadLinkStrikes.Read(info));
        Assert.False(ZohoDeadLinkStrikes.RecordNotFound(info, Dead, T0));
    }

    // ---------------------------------------------------------------- through the real sync

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// Primary record reads fine; the extra answers either Zoho's 400 "Sponsor not found" or a bare
    /// 400 (a burst on a record that exists), depending on <see cref="BareBurst"/>.
    /// </summary>
    private sealed class Zoho : HttpMessageHandler
    {
        public bool BareBurst { get; set; }
        public bool ExtraBack { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/oauth/v2/token", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json("{\"access_token\":\"t\"}"));
            if (request.Method == HttpMethod.Put)
                return Task.FromResult(Json("{\"ok\":true}"));
            if (path.EndsWith("/sponsors/" + Dead, StringComparison.Ordinal))
            {
                if (ExtraBack)
                    return Task.FromResult(Json("{\"sponsor\":{\"company_name\":\"Company One\",\"website_url\":\"\"}}"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        BareBurst ? "{\"status_code\":\"400\"}" : "{\"status_code\":\"400\",\"message\":\"Sponsor not found\"}",
                        Encoding.UTF8, "application/json"),
                });
            }
            if (path.Contains("/sponsors/", StringComparison.Ordinal))
                return Task.FromResult(Json("{\"sponsor\":{\"company_name\":\"Company One\",\"website_url\":\"\"}}"));
            return Task.FromResult(Json("{\"sponsors\":[],\"exhibitors\":[]}"));
        }

        private static HttpResponseMessage Json(string b) =>
            new(HttpStatusCode.OK) { Content = new StringContent(b, Encoding.UTF8, "application/json") };
    }

    private static SponsorZohoSyncService Sync(CommunityHubDbContext db, Zoho zoho, TimeProvider clock)
    {
        var o = new ZohoOptions
        {
            Enabled = true, ApiDomain = "https://stub.local", TokenEndpoint = "https://stub.local/oauth/v2/token",
            BackstagePortalId = "p1", BackstageEventId = "e1", ClientId = "c", ClientSecret = "s", RefreshToken = "r",
        };
        var cm = new CompanyManagerOptions { Enabled = false };
        return new SponsorZohoSyncService(
            new ZohoClient(new HttpClient(zoho), o, NullLogger<ZohoClient>.Instance), db, o,
            new CompanyManagerClient(new HttpClient(zoho), cm), cm,
            NullLogger<SponsorZohoSyncService>.Instance, clock: clock);
    }

    private static async Task<int> SeedAsync(CommunityHubDbContext db)
    {
        var ev = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "DL27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var info = new SponsorInfo { EventId = ev.Id, SponsorCompanyId = "1", ZohoSponsorId = "ZSP-LIVE" };
        SponsorZohoLinks.Write(info, new[]
        {
            new SponsorZohoLink("Gold sponsors", "T-1", "ZSP-LIVE"),
            new SponsorZohoLink("Swag sponsor", "T-2", Dead),
        });
        db.SponsorInfos.Add(info);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static async Task<IReadOnlyList<string>> IdsAsync(CommunityHubDbContext db) =>
        SponsorZohoLinks.AllSponsorIds(await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "1"));

    [Fact]
    public async Task A_dead_extra_link_is_removed_on_the_fifth_report_twelve_hours_apart()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedAsync(db);
        var clock = new MutableClock();
        var zoho = new Zoho();

        for (var i = 0; i < ZohoDeadLinkStrikes.StrikesToRemove - 1; i++)
        {
            clock.Now = T0.AddHours(12 * i);
            await Sync(db, zoho, clock).SyncAsync(eventId, "1", "Company One", notifyZohoChange: false);
            Assert.Contains(Dead, await IdsAsync(db));                 // still held after strike i+1
        }

        clock.Now = T0.AddHours(12 * 4);
        await Sync(db, zoho, clock).SyncAsync(eventId, "1", "Company One", notifyZohoChange: false);

        var ids = await IdsAsync(db);
        Assert.DoesNotContain(Dead, ids);
        // 🔒 The live primary is untouched — the repair removes the dead link and nothing else.
        Assert.Equal("ZSP-LIVE", Assert.Single(ids));
        Assert.Equal("ZSP-LIVE", (await db.SponsorInfos.SingleAsync()).ZohoSponsorId);
        Assert.Null((await db.SponsorInfos.SingleAsync()).ZohoDeadLinkStrikesJson);
    }

    [Fact]
    public async Task Ten_minute_runs_for_a_day_remove_nothing()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedAsync(db);
        var clock = new MutableClock();
        var zoho = new Zoho();

        for (var m = 0; m < 24 * 60; m += 60)   // hourly is enough to prove it; 24 reports, 2 counted
        {
            clock.Now = T0.AddMinutes(m);
            await Sync(db, zoho, clock).SyncAsync(eventId, "1", "Company One", notifyZohoChange: false);
        }

        Assert.Contains(Dead, await IdsAsync(db));
    }

    /// <summary>🔑 The §1220 burst: a bare 400 is not "not found" and must never count.</summary>
    [Fact]
    public async Task A_bare_400_burst_never_counts()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedAsync(db);
        var clock = new MutableClock();
        var zoho = new Zoho { BareBurst = true };

        for (var i = 0; i < 10; i++)
        {
            clock.Now = T0.AddHours(12 * i);
            await Sync(db, zoho, clock).SyncAsync(eventId, "1", "Company One", notifyZohoChange: false);
        }

        Assert.Contains(Dead, await IdsAsync(db));
        Assert.Null((await db.SponsorInfos.SingleAsync()).ZohoDeadLinkStrikesJson);
    }

    [Fact]
    public async Task The_record_coming_back_resets_the_strikes()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedAsync(db);
        var clock = new MutableClock();
        var zoho = new Zoho();

        for (var i = 0; i < 4; i++)
        {
            clock.Now = T0.AddHours(12 * i);
            await Sync(db, zoho, clock).SyncAsync(eventId, "1", "Company One", notifyZohoChange: false);
        }

        zoho.ExtraBack = true;
        clock.Now = T0.AddHours(12 * 4);
        await Sync(db, zoho, clock).SyncAsync(eventId, "1", "Company One", notifyZohoChange: false);

        zoho.ExtraBack = false;
        clock.Now = T0.AddHours(12 * 5);
        await Sync(db, zoho, clock).SyncAsync(eventId, "1", "Company One", notifyZohoChange: false);

        Assert.Contains(Dead, await IdsAsync(db));
        Assert.Equal(1, Assert.Single(ZohoDeadLinkStrikes.Read(await db.SponsorInfos.SingleAsync())).Strikes);
    }
}
