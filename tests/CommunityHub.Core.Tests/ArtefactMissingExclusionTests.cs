using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §598 — an artefact a COMPLETE SharePoint read confirmed is GONE must stop counting, so the task
/// reverts to not-done and the reminders resume.
///
/// <para>Operator 2026-07-28: <i>"i have manually deleted these files on sharepoint, but ceh still
/// remember them. it should check if the files actually exist in the sharepoint folder with the
/// name, otherwiswe it should null the value (as it hasn't uploaded)."</i> CEH treated a stored path
/// as proof of upload forever, so the Get Started step reported all three logos uploaded and counted
/// itself DONE with nothing behind it.</para>
///
/// <para>These tests cover the READ side. The write side — only stamping on a listing that actually
/// succeeded — lives in <c>SponsorArtefactVerifier</c>, whose fail-safe matters more than this
/// filter: acting on a failed read would strip every sponsor's artefacts at once.</para>
/// </summary>
public class ArtefactMissingExclusionTests
{
    private const int EventId = 1;
    private const string Company = "12";

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"art-{Guid.NewGuid():N}").Options);

    private static async Task AddAuditAsync(
        CommunityHubDbContext db, string kind, DateTimeOffset? missingAt = null)
    {
        db.SponsorUploadAudits.Add(new SponsorUploadAudit
        {
            EventId = EventId,
            SponsorCompanyId = Company,
            Kind = kind,
            FileName = $"{kind}_file_v1.png",
            UploadedByEmail = "a@b.dk",
            ArtefactMissingAt = missingAt,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_present_artefact_counts()
    {
        using var db = NewDb();
        await AddAuditAsync(db, "wall");

        var kinds = await TaskArtefactRules.UploadedKindsAsync(db, EventId, Company);

        Assert.Contains("wall", kinds);
    }

    [Fact]
    public async Task An_artefact_confirmed_missing_does_NOT_count()
    {
        // THE FIX. Before this the deleted file still read as uploaded and the task stayed done.
        using var db = NewDb();
        await AddAuditAsync(db, "wall", missingAt: DateTimeOffset.UtcNow);

        var kinds = await TaskArtefactRules.UploadedKindsAsync(db, EventId, Company);

        Assert.DoesNotContain("wall", kinds);
    }

    [Fact]
    public async Task A_RE_UPLOAD_restores_completion_even_though_the_old_row_is_still_marked_missing()
    {
        // The audit trail keeps BOTH rows — the deleted upload and the replacement — so the history
        // survives. Completion must follow the live one, not the stamped one.
        using var db = NewDb();
        await AddAuditAsync(db, "wall", missingAt: DateTimeOffset.UtcNow);   // deleted by hand
        await AddAuditAsync(db, "wall");                                     // re-uploaded since

        var kinds = await TaskArtefactRules.UploadedKindsAsync(db, EventId, Company);

        Assert.Contains("wall", kinds);
    }

    [Fact]
    public async Task One_missing_artefact_does_not_hide_the_others()
    {
        using var db = NewDb();
        await AddAuditAsync(db, "wall", missingAt: DateTimeOffset.UtcNow);
        await AddAuditAsync(db, "print");

        var kinds = await TaskArtefactRules.UploadedKindsAsync(db, EventId, Company);

        Assert.DoesNotContain("wall", kinds);
        Assert.Contains("print", kinds);
    }

    [Fact]
    public async Task Another_companys_artefacts_are_never_borrowed()
    {
        using var db = NewDb();
        db.SponsorUploadAudits.Add(new SponsorUploadAudit
        {
            EventId = EventId, SponsorCompanyId = "99", Kind = "wall",
            FileName = "wall_other_v1.eps", UploadedByEmail = "x@y.dk",
        });
        await db.SaveChangesAsync();

        var kinds = await TaskArtefactRules.UploadedKindsAsync(db, EventId, Company);

        Assert.Empty(kinds);
    }
}
