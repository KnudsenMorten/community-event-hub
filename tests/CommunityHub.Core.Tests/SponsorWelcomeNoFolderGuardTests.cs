using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §816 — a Gold+ booth company must be WELCOMED even though it has no SharePoint upload folder.
/// </summary>
/// <remarks>
/// <para>The welcome used to be held until a <c>SponsorUploadLocation</c> with an edit link existed.
/// §784.14 retired the per-company <c>/Sponsors/Sponsor Upload/</c> tree (operator 2026-08-03:
/// *"retire the old logic and delete the old folders"*), so nothing writes those rows any more —
/// and the guard could never clear.</para>
///
/// <para>🔴 <b>Measured on a live sponsor before this was fixed:</b> Glueckkanja (Platinum, booth
/// E-2) was pulled every 15 minutes, task-seeded, given a coordinator and provisioned in Zoho, and
/// its welcome was silently withheld. Every job reported success. The companies that passed the
/// guard held LEGACY rows from when the old method ran, which is why the first sponsor onboarded
/// after the retirement was the first to be blocked.</para>
///
/// <para>🔒 This test exists so the guard cannot come back by reflex. If a future upload model needs
/// preparing before a welcome, gate on THAT model's readiness — never on a row a retired service
/// used to write.</para>
/// </remarks>
public sealed class SponsorWelcomeNoFolderGuardTests
{
    private const int EventId = 1;
    private const string CompanyId = "26";

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"sponsor-welcome-{Guid.NewGuid():N}").Options);

    private static async Task SeedAsync(CommunityHubDbContext db, SponsorPackage package)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "SW27", CommunityName = "C", DisplayName = "SW 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = CompanyId, CompanyName = "Example Platinum A/S",
            SponsorPackage = package,
        });
        await db.SaveChangesAsync();
        // 🔒 Deliberately NO SponsorUploadLocation row — that is the whole point: none is created
        // any more, so a booth company legitimately has zero.
    }

    /// <summary>
    /// 🔴 THE REGRESSION THIS PREVENTS: a Platinum booth company with no upload folder must NOT be
    /// reported as blocked. Before §816 this returned <c>Blocked: true</c> for ever.
    /// </summary>
    [Fact]
    public async Task A_booth_company_without_an_upload_folder_is_not_blocked()
    {
        using var db = NewDb();
        await SeedAsync(db, SponsorPackage.Platinum);

        Assert.Empty(await db.SponsorUploadLocations.ToListAsync());

        var result = await NewService(db).SendForCompanyAsync(EventId, CompanyId);

        Assert.False(result.Blocked);
        Assert.Null(result.Reason);
    }

    /// <summary>The same for the tier that first hit it — Gold is where the old guard started.</summary>
    [Fact]
    public async Task A_gold_company_without_an_upload_folder_is_not_blocked()
    {
        using var db = NewDb();
        await SeedAsync(db, SponsorPackage.Gold);

        var result = await NewService(db).SendForCompanyAsync(EventId, CompanyId);

        Assert.False(result.Blocked);
    }

    /// <summary>
    /// ⚠️ A company with NO coordinators still is not "blocked" — it simply has nobody to write to.
    /// The two states were conflated by the old guard's return shape and must stay distinct: one is
    /// "the system is not ready", the other is "this company has no contact yet".
    /// </summary>
    [Fact]
    public async Task No_coordinators_is_reported_as_zero_recipients_not_as_blocked()
    {
        using var db = NewDb();
        await SeedAsync(db, SponsorPackage.Platinum);

        var result = await NewService(db).SendForCompanyAsync(EventId, CompanyId);

        Assert.False(result.Blocked);
        Assert.Equal(0, result.CoordinatorsResolved);
        Assert.Equal(0, result.Sent);
    }

    private static SponsorWelcomeEmailService NewService(CommunityHubDbContext db) =>
        new(db,
            new CommunityHub.Core.Email.SponsorRecipientResolver(db),
            welcome: null!);
}
