using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1034 — IS THIS COMPANY A SPONSOR? A RECORDED ANSWER, AND A FILTER THAT MAKES IT STICK.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10, on a coupon customer in the Sponsors grid: <i>"this is a coupon
/// customer, and must not be created as sponsor"</i> → <i>"I recommend something like IsSponsor=0/1
/// and isExhibitor=0/1 HasExhibitorSession=0/1. We have the definitions based on the webshop
/// products they buy"</i> → <i>"you must do a thourough audit of the entire code stack for sponsors,
/// as we have many filters/views/looksup etc"</i>.</para>
///
/// <para>🔴 <b>The audit is the reason for the query filter.</b> 45 files and 81 call sites read
/// <c>SponsorInfos</c>, every one written when a row meant a sponsor — the public sponsors page, the
/// SoMe planner that writes posts ABOUT a company, the Zoho provisioner that CREATES records there,
/// the tier graphics, the lunch headcount. Putting non-sponsor rows in that table without a filter
/// would have widened all of them silently. §905 is the same story with the test-data flag
/// (<i>"all have the test flag but some service doesn't handle it"</i>), and it was found in
/// production.</para>
///
/// <para>🔒 So these tests pin BOTH halves: the flag is set from the products, and the filter means
/// an unfiltered consumer cannot see a non-sponsor even if it forgets to ask.</para>
/// </remarks>
public sealed class SponsorIsSponsorFlagTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"sponsorflags-{Guid.NewGuid():N}").Options);

    private static SponsorInfo Company(string id, bool isSponsor, bool isExhibitor = false) =>
        new()
        {
            EventId = EventId, SponsorCompanyId = id, CompanyName = $"Co {id}",
            IsSponsor = isSponsor, IsExhibitor = isExhibitor,
            SponsorPackage = isExhibitor ? SponsorPackage.Gold : SponsorPackage.Silver,
        };

    /// <summary>
    /// 🔴 THE WHOLE POINT: an ordinary query — the shape all 81 call sites use — cannot see a
    /// company that is not a sponsor.
    /// </summary>
    [Fact]
    public async Task An_ordinary_query_never_returns_a_non_sponsor()
    {
        using var db = NewDb();
        db.SponsorInfos.Add(Company("44", isSponsor: true, isExhibitor: true));
        db.SponsorInfos.Add(Company("33", isSponsor: false));    // the coupon customer
        await db.SaveChangesAsync();

        var all = await db.SponsorInfos.Where(s => s.EventId == EventId).ToListAsync();

        Assert.Single(all);
        Assert.Equal("44", all[0].SponsorCompanyId);
    }

    /// <summary>The count the organizer command centre shows must not include coupon customers.</summary>
    [Fact]
    public async Task Aggregates_do_not_count_a_non_sponsor()
    {
        using var db = NewDb();
        db.SponsorInfos.Add(Company("44", isSponsor: true));
        db.SponsorInfos.Add(Company("33", isSponsor: false));
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.SponsorInfos.CountAsync(s => s.EventId == EventId));
    }

    /// <summary>
    /// 🔒 The escape hatch works, and is the ONLY way through — the writer, the deletion/reset
    /// services and the grid's exclusion all depend on being able to see the hidden row.
    /// </summary>
    [Fact]
    public async Task IgnoreQueryFilters_is_how_a_writer_sees_the_hidden_row()
    {
        using var db = NewDb();
        db.SponsorInfos.Add(Company("33", isSponsor: false));
        await db.SaveChangesAsync();

        Assert.Null(await db.SponsorInfos
            .FirstOrDefaultAsync(s => s.SponsorCompanyId == "33"));
        Assert.NotNull(await db.SponsorInfos.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.SponsorCompanyId == "33"));
    }

    /// <summary>
    /// ⚠️ The failure mode the filter exists to prevent, stated as a test: an upsert that does NOT
    /// bypass the filter sees nothing and inserts a SECOND row for a company that already has one.
    /// Every upsert path (§1034 lists six) therefore uses IgnoreQueryFilters.
    /// </summary>
    [Fact]
    public async Task An_upsert_that_forgets_the_bypass_would_duplicate_the_company()
    {
        using var db = NewDb();
        db.SponsorInfos.Add(Company("33", isSponsor: false));
        await db.SaveChangesAsync();

        // The WRONG shape, on purpose — this is what the six upsert sites would do unguarded.
        var found = await db.SponsorInfos.FirstOrDefaultAsync(s => s.SponsorCompanyId == "33");
        Assert.Null(found);

        // The RIGHT shape finds it and updates in place.
        var real = await db.SponsorInfos.IgnoreQueryFilters()
            .FirstAsync(s => s.SponsorCompanyId == "33");
        real.CompanyName = "Renamed";
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.SponsorInfos.IgnoreQueryFilters()
            .CountAsync(s => s.SponsorCompanyId == "33"));
    }

    // ── HasBooth: the ~30 existing consumers pick the flag up for free ────────────────────────

    /// <summary>
    /// 🔑 <c>HasBooth</c> now reads <c>IsExhibitor</c> as well, so the nav, the wizard steps, the
    /// booth tasks and the Zoho exhibitor push follow the new flag without 30 edits.
    /// </summary>
    [Fact]
    public void HasBooth_follows_the_exhibitor_flag()
    {
        Assert.True(new SponsorInfo { IsExhibitor = true, SponsorPackage = SponsorPackage.Silver }.HasBooth);
    }

    /// <summary>
    /// 🔒 The OR is what makes the transition safe: an organizer who bumps the PACKAGE by hand
    /// still gets a booth even though nothing set the flag. This can only widen, never narrow.
    /// </summary>
    [Fact]
    public void HasBooth_still_follows_the_package_on_its_own()
    {
        Assert.True(new SponsorInfo { IsExhibitor = false, SponsorPackage = SponsorPackage.Gold }.HasBooth);
        Assert.False(new SponsorInfo { IsExhibitor = false, SponsorPackage = SponsorPackage.Silver }.HasBooth);
    }

    /// <summary>
    /// A digital-only sponsor is a SPONSOR without being an EXHIBITOR — operator: <i>"any sponsor
    /// buying a product in webshop should be created as sponsor. but only the ones that actually
    /// have booth are considered a exhibitor"</i>.
    /// </summary>
    [Fact]
    public async Task A_digital_only_sponsor_is_a_sponsor_but_not_an_exhibitor()
    {
        using var db = NewDb();
        db.SponsorInfos.Add(Company("42", isSponsor: true, isExhibitor: false));
        await db.SaveChangesAsync();

        var s = await db.SponsorInfos.SingleAsync();
        Assert.True(s.IsSponsor);
        Assert.False(s.IsExhibitor);
        Assert.False(s.HasBooth);
    }
}
