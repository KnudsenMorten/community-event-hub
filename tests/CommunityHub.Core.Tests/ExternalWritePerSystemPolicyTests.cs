using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1037 — DEV'S OUTBOUND POLICY, PER SYSTEM.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10: <i>"dev must newer WRITE to zoho, but it is allowed to read from zoho.
/// dev is allowed to readwrite to erp. dev is allowed to read from webshop. dev is allowed to have
/// test webpages so i can verify signage. dev is not allowed to publish on linkedin. dev is allowed
/// to import from sessionize. dev is allowed to write to sharepoint (as it has its own separate
/// path). as a general rule, importing into ceh is 100% fine - but sending data out from dev is
/// controlled"</i>.</para>
///
/// <para>🔑 The general rule was already §340-H's rule — <i>"Reads are never gated"</i>. What was
/// missing is that the environment ceiling was ONE boolean for every system, so DEV could not be
/// "no Zoho, but yes ERP and SharePoint": it was all or nothing, and the answer had to be nothing.</para>
/// </remarks>
public sealed class ExternalWritePerSystemPolicyTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"extwrites-{Guid.NewGuid():N}").Options);

    /// <summary>DEV as he specified it: Zoho and LinkedIn off, ERP and SharePoint on.</summary>
    private static ExternalWriteOptions DevPolicy() => new()
    {
        AllowExternalWrites = false,     // the default for anything not named
        ExternalWrites = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Zoho"] = false,
            ["LinkedIn"] = false,
            ["Erp"] = true,
            ["SharePoint"] = true,
        },
    };

    private static ExternalWriteGuard Guard(CommunityHubDbContext db, ExternalWriteOptions o) =>
        new(db, o);

    [Fact]
    public async Task Dev_never_writes_to_zoho()
    {
        using var db = NewDb();
        Assert.False(await Guard(db, DevPolicy())
            .AllowAsync(ExternalSystems.Zoho, "CreateExhibitorAsync"));
    }

    [Fact]
    public async Task Dev_never_publishes_to_linkedin()
    {
        using var db = NewDb();
        Assert.False(await Guard(db, DevPolicy())
            .AllowAsync(ExternalSystems.LinkedIn, "PublishAsync"));
    }

    /// <summary>
    /// 🔴 The half that was impossible before: DEV writing to ERP while still blocked from Zoho.
    /// </summary>
    [Fact]
    public async Task Dev_may_write_to_erp_and_sharepoint()
    {
        using var db = NewDb();
        var guard = Guard(db, DevPolicy());

        Assert.True(await guard.AllowAsync(ExternalSystems.Erp, "CreateCustomerAsync"));
        Assert.True(await guard.AllowAsync(ExternalSystems.SharePoint, "StoreAsync"));
    }

    /// <summary>
    /// 🔴 §1119 — …but DEV still may not CREATE AN INVOICE, and that is a narrower ceiling than
    /// <see cref="ExternalSystems.Erp"/>.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-21, for the eighth time: <i>"no erp create of invoice from dev. this
    /// can only happend on prod"</i>. The 2026-08-10 grant above — <i>"dev is allowed to readwrite to
    /// erp"</i> — is still right for customers, contacts and orders; only invoices are
    /// production-only, and one boolean could not say both.</para>
    ///
    /// <para>🔑 It works with NO app setting: <c>ErpInvoiceCreate</c> is unlisted, so it falls back to
    /// <c>AllowExternalWrites</c> — false on DEV, true on PROD. ⚠️ Which means this test is also the
    /// guard on that fallback: give <c>ErpInvoiceCreate</c> a key of its own in DEV's config and the
    /// policy inverts silently.</para>
    /// </remarks>
    [Fact]
    public async Task Dev_may_write_to_erp_but_may_never_create_an_invoice()
    {
        using var db = NewDb();
        var guard = Guard(db, DevPolicy());

        Assert.True(await guard.AllowAsync(ExternalSystems.Erp, "CreateCustomerAsync"));
        Assert.False(await guard.AllowAsync(
            ExternalSystems.ErpInvoiceCreate, "CreateDraftInvoiceAsync"));

        // PROD writes everywhere from its single default — invoicing included, or nobody gets billed.
        var prod = Guard(db, new ExternalWriteOptions { AllowExternalWrites = true });
        Assert.True(await prod.AllowAsync(
            ExternalSystems.ErpInvoiceCreate, "CreateDraftInvoiceAsync"));
    }

    /// <summary>
    /// 🔒 A system nobody listed falls back to the host default — so an unconfigured host still
    /// writes NOTHING, and a new integration is governed the moment it calls the guard.
    /// </summary>
    [Fact]
    public async Task An_unlisted_system_falls_back_to_the_host_default()
    {
        using var db = NewDb();

        Assert.False(await Guard(db, DevPolicy()).AllowAsync("SomeNewThing", "Write"));
        Assert.True(await Guard(db, new ExternalWriteOptions { AllowExternalWrites = true })
            .AllowAsync("SomeNewThing", "Write"));
    }

    /// <summary>PROD keeps writing everywhere without having to enumerate systems.</summary>
    [Fact]
    public async Task Prod_writes_everywhere_from_the_single_default()
    {
        using var db = NewDb();
        var guard = Guard(db, new ExternalWriteOptions { AllowExternalWrites = true });

        foreach (var s in new[]
                 {
                     ExternalSystems.Zoho, ExternalSystems.Erp,
                     ExternalSystems.LinkedIn, ExternalSystems.SharePoint,
                 })
        {
            Assert.True(await guard.AllowAsync(s, "op"), s);
        }
    }

    /// <summary>
    /// 🔒 §612 SURVIVES THE SPLIT, and this is the test that proves it. The organizer override may
    /// only ever RESTRICT: on a host whose config blocks Zoho, no click on the Settings page can
    /// widen it. Operator 2026-07-28: <i>"in dev we want to enforce BLOCK WRITES !!!"</i> — because
    /// otherwise <i>"i will then have 2 set of everything in zoho"</i>.
    /// </summary>
    [Fact]
    public async Task The_organizer_override_can_only_restrict_never_widen()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = 1, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = 1, FeatureKey = ExternalWriteGuard.OverrideKey, Enabled = true,
        });
        await db.SaveChangesAsync();

        // Override ON + Zoho blocked by config ⇒ still blocked.
        Assert.False(await Guard(db, DevPolicy()).AllowAsync(ExternalSystems.Zoho, "op"));
    }

    [Fact]
    public async Task An_organizer_switching_writes_off_stops_even_an_allowed_system()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = 1, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = 1, FeatureKey = ExternalWriteGuard.OverrideKey, Enabled = false,
        });
        await db.SaveChangesAsync();

        Assert.False(await Guard(db, DevPolicy()).AllowAsync(ExternalSystems.Erp, "op"));
    }

    /// <summary>
    /// ⚠️ The banner must not LIE. The old one said "no outbound writes to Zoho Backstage,
    /// e-conomic, LinkedIn or SharePoint" whenever the default was off — false on a DEV that writes
    /// to two of them, and a banner an operator cannot trust is worse than none.
    /// </summary>
    [Fact]
    public void The_startup_banner_names_each_system()
    {
        var banner = ExternalWriteGuard.StartupBanner(DevPolicy());

        Assert.Contains("Zoho Backstage=BLOCKED", banner);
        Assert.Contains("LinkedIn=BLOCKED", banner);
        Assert.Contains("e-conomic=ALLOWED", banner);
        Assert.Contains("SharePoint=ALLOWED", banner);
        // §1119 — the line that answers "can the app in front of me invoice a customer".
        Assert.Contains("e-conomic invoice creation=BLOCKED", banner);
    }

    /// <summary>
    /// 🔴 THE MAPPING IS PINNED AGAINST THE REAL CALL SITES.
    /// </summary>
    /// <remarks>
    /// A system name is a STRING agreed between a call site and a settings key. Rename either and
    /// the policy silently stops binding — the write is then governed by the host default, which on
    /// DEV means "blocked" (safe) but on PROD means "allowed" (not safe, if the intent was to block).
    /// §767 is the precedent: a guessed convention matched nothing for four production runs. This
    /// reads the actual sources so the agreement cannot drift unnoticed.
    /// </remarks>
    [Fact]
    public void Every_guarded_system_name_matches_a_known_config_key()
    {
        var root = FindRepoRoot();
        var sources = Directory.GetFiles(
            Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);

        var used = sources
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(f), @"AllowAsync\(\s*""([^""]+)""")
                .Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        // The guard is called with literal names somewhere — if this ever finds none, the test has
        // stopped testing anything rather than started passing.
        Assert.NotEmpty(used);

        var known = new[]
        {
            ExternalSystems.Zoho, ExternalSystems.Erp,
            ExternalSystems.LinkedIn, ExternalSystems.SharePoint,
            ExternalSystems.Webshop, ExternalSystems.ErpInvoiceCreate,
        };

        var unknown = used.Where(u => !known.Contains(u)).ToList();
        Assert.True(unknown.Count == 0,
            "These system names are passed to the external-write guard but are not in "
            + "ExternalSystems, so `Integrations:ExternalWrites:<key>` cannot govern them and they "
            + "silently fall back to the host default: " + string.Join(", ", unknown));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
