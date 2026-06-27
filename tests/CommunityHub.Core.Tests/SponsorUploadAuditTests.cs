using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests the §68 "show already-uploaded files on load" data contract that the
/// Sponsor Company Details page relies on:
///  - <see cref="SponsorUploadAudit"/> persists who/when/what per upload control
///    and the "latest per kind" query returns the NEWEST upload (so a shared team
///    sees the current file + version + uploader on reload);
///  - <see cref="SponsorBoothMaterial.CreatedByEmail"/> records the uploader of a
///    booth video / collateral file.
/// Uses the EF Core InMemory provider so the real DbContext mapping + LINQ run.
/// FAKE names/emails only.
/// </summary>
public sealed class SponsorUploadAuditTests
{
    private const int EventId = 1;
    private const string CompanyId = "777";

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"upload-audit-{Guid.NewGuid():N}")
            .Options);

    private static readonly DateTimeOffset T0 = new(2027, 1, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Latest_per_kind_returns_the_newest_upload_with_uploader_and_version()
    {
        await using var db = NewDb();

        // Two uploads of the "some" logo by different coordinators, plus one "wall".
        db.SponsorUploadAudits.AddRange(
            new SponsorUploadAudit
            {
                EventId = EventId, SponsorCompanyId = CompanyId, Kind = "some",
                FileName = "SoMeBrandingLogo_Acme_v1.png", Version = 1,
                WebUrl = "https://sp/some_v1.png", UploadedByEmail = "ann@acme.test", UploadedAt = T0,
            },
            new SponsorUploadAudit
            {
                EventId = EventId, SponsorCompanyId = CompanyId, Kind = "some",
                FileName = "SoMeBrandingLogo_Acme_v2.png", Version = 2,
                WebUrl = "https://sp/some_v2.png", UploadedByEmail = "ben@acme.test", UploadedAt = T0.AddHours(3),
            },
            new SponsorUploadAudit
            {
                EventId = EventId, SponsorCompanyId = CompanyId, Kind = "wall",
                FileName = "ExhibitorWall_Acme_v1.pdf", Version = 1,
                WebUrl = "https://sp/wall_v1.pdf", UploadedByEmail = "ann@acme.test", UploadedAt = T0.AddHours(1),
            });
        // A different company's row must never bleed in.
        db.SponsorUploadAudits.Add(new SponsorUploadAudit
        {
            EventId = EventId, SponsorCompanyId = "999", Kind = "some",
            FileName = "SoMeBrandingLogo_Other_v9.png", Version = 9,
            UploadedByEmail = "x@other.test", UploadedAt = T0.AddDays(1),
        });
        await db.SaveChangesAsync();

        // Mirror the page's LoadAsync query: latest row per kind for this company.
        var audits = await db.SponsorUploadAudits
            .Where(a => a.EventId == EventId && a.SponsorCompanyId == CompanyId)
            .ToListAsync();
        var latest = audits
            .GroupBy(a => a.Kind)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.UploadedAt).First());

        Assert.Equal(2, latest.Count);                       // some + wall (not the other company)
        Assert.Equal("SoMeBrandingLogo_Acme_v2.png", latest["some"].FileName);
        Assert.Equal(2, latest["some"].Version);             // newest version, not v1
        Assert.Equal("ben@acme.test", latest["some"].UploadedByEmail);
        Assert.Equal("ExhibitorWall_Acme_v1.pdf", latest["wall"].FileName);
        Assert.False(latest.ContainsKey("print"));           // never-used control => no entry
    }

    [Fact]
    public async Task Booth_material_records_the_uploader()
    {
        await using var db = NewDb();
        db.SponsorBoothMaterials.Add(new SponsorBoothMaterial
        {
            EventId = EventId, SponsorCompanyId = CompanyId, Kind = BoothMaterialKind.Collateral,
            Url = "https://sp/brochure.pdf", FileName = "brochure.pdf",
            CreatedByEmail = "ann@acme.test", CreatedAt = T0,
        });
        await db.SaveChangesAsync();

        var m = await db.SponsorBoothMaterials.SingleAsync(
            x => x.EventId == EventId && x.SponsorCompanyId == CompanyId);
        Assert.Equal("ann@acme.test", m.CreatedByEmail);
    }
}
