using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1077 stage 3 — the company's own wizard, opened by a token link with no hub account.
/// </summary>
/// <remarks>
/// <para>🔴 <b>This token WRITES, and the §1040 monitor token did not.</b> That page rested on four
/// legs: unguessable, scoped, revocable/expiring, and <b>read-only</b>. The fourth is gone here, so
/// these tests hold the other three hard — and add the one that replaces it: every write lands on
/// the company the token resolves to, and no method takes a company id a caller could substitute.</para>
///
/// <para>⚠️ The refusal tests matter more than the happy path. A revoked link that still opened, or
/// an expired one that still saved, would be a disclosure nobody would notice until it was used.</para>
/// </remarks>
public sealed class VolumePackageWizardTests
{
    private const int EventId = 42;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vp-wizard-{Guid.NewGuid():N}").Options);

    private static VolumePackageWizardService New(CommunityHubDbContext db, DateTimeOffset? now = null) =>
        new(db, new NullSharePointFileStore(), TestDocLibrary.Resolver(),
            new FixedClock(now ?? Now));

    private static async Task<VolumePackageCompany> SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "D", IsActive = true,
        });
        var company = new VolumePackageCompany
        {
            EventId = EventId, CustomName = "Globeteam", Domains = "globeteam.dk",
            ApproverEmail = "bea@globeteam.dk", ApproverName = "Bea Buyer",
        };
        db.VolumePackageCompanies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    // ---- the token -------------------------------------------------------

    [Fact]
    public async Task An_issued_link_opens_the_company_it_was_issued_for()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var svc = New(db);

        var token = await svc.IssueTokenAsync(company.Id, "org@example.test");

        Assert.False(string.IsNullOrWhiteSpace(token));
        var resolved = await svc.ResolveAsync(token);
        Assert.Equal(company.Id, resolved!.Id);
    }

    /// <summary>
    /// 🔒 256 bits from a cryptographic RNG, and never repeated. Guessing is not the threat model —
    /// the URL IS the credential, so it has to be worth trusting.
    /// </summary>
    [Fact]
    public async Task Every_issued_token_is_different_and_long()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var svc = New(db);

        var first = await svc.IssueTokenAsync(company.Id);
        var second = await svc.IssueTokenAsync(company.Id);

        Assert.NotEqual(first, second);
        Assert.True(first!.Length >= 40, $"token looks too short: {first.Length} chars");
    }

    /// <summary>
    /// ⚠️ Re-issuing KILLS the old URL. That is the behaviour you want the moment a link has gone to
    /// the wrong person, and it must be true rather than merely intended.
    /// </summary>
    [Fact]
    public async Task Re_issuing_kills_the_previous_link()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var svc = New(db);

        var old = await svc.IssueTokenAsync(company.Id);
        await svc.IssueTokenAsync(company.Id);

        Assert.Null(await svc.ResolveAsync(old));
    }

    [Fact]
    public async Task A_revoked_link_no_longer_opens()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var svc = New(db);
        var token = await svc.IssueTokenAsync(company.Id);

        Assert.True(await svc.RevokeTokenAsync(company.Id, "org@example.test"));

        Assert.Null(await svc.ResolveAsync(token));
    }

    /// <summary>
    /// 🔑 The half of the safety that does not depend on anyone remembering: a link shared with a
    /// partner, the event ends, and nobody thinks about it again.
    /// </summary>
    [Fact]
    public async Task A_link_stops_working_by_itself_after_its_expiry()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var token = await New(db).IssueTokenAsync(company.Id);

        var afterwards = New(db, VolumePackageWizardService.DefaultExpiry.AddDays(1));
        Assert.Null(await afterwards.ResolveAsync(token));

        // …and still opens the day before, so the expiry is a date and not an off-by-one.
        var before = New(db, VolumePackageWizardService.DefaultExpiry.AddDays(-1));
        Assert.NotNull(await before.ResolveAsync(token));
    }

    /// <summary>An unknown token is simply nothing — the page turns all three refusals into a 404.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    public async Task Rubbish_resolves_to_nothing(string token)
    {
        using var db = NewDb();
        await SeedAsync(db);
        Assert.Null(await New(db).ResolveAsync(token));
    }

    /// <summary>
    /// 🔴 <b>The replacement for the read-only leg.</b> One company's link resolves to that company
    /// and no other — the blast radius of a forwarded URL is one row, by construction.
    /// </summary>
    [Fact]
    public async Task A_link_never_resolves_to_another_company()
    {
        using var db = NewDb();
        var globeteam = await SeedAsync(db);
        var arrow = new VolumePackageCompany
        {
            EventId = EventId, CustomName = "Arrow", Domains = "arrow.com",
        };
        db.VolumePackageCompanies.Add(arrow);
        await db.SaveChangesAsync();

        var svc = New(db);
        var arrowToken = await svc.IssueTokenAsync(arrow.Id);

        var resolved = await svc.ResolveAsync(arrowToken);
        Assert.Equal(arrow.Id, resolved!.Id);
        Assert.NotEqual(globeteam.Id, resolved.Id);
    }

    // ---- the four steps --------------------------------------------------

    [Fact]
    public async Task Participation_records_the_three_choices()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var svc = New(db);

        await svc.SaveParticipationAsync(company, keynote: true, social: false, groupPhoto: true);

        var saved = await db.VolumePackageCompanies.SingleAsync(c => c.Id == company.Id);
        Assert.True(saved.ApprovedKeynoteMention);
        Assert.False(saved.ApprovedSocialMediaAnnouncement);
        Assert.True(saved.ApprovedGroupPhoto);
    }

    /// <summary>
    /// 🔴 Declining clears the approvals as well as setting the date. A row that says "declined"
    /// while three benefits sit ticked is a contradiction somebody will later resolve in the wrong
    /// direction — most likely by putting the company on a keynote slide.
    /// </summary>
    [Fact]
    public async Task Declining_clears_the_benefits_and_finishes_the_wizard()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var svc = New(db);
        await svc.SaveParticipationAsync(company, true, true, true);

        await svc.DeclineAsync(company);

        var saved = await db.VolumePackageCompanies.SingleAsync(c => c.Id == company.Id);
        Assert.NotNull(saved.DeclinedAt);
        Assert.False(saved.ApprovedKeynoteMention);
        Assert.False(saved.ApprovedSocialMediaAnnouncement);
        Assert.False(saved.ApprovedGroupPhoto);
        Assert.NotNull(saved.WizardCompletedAt);
    }

    /// <summary>
    /// A company that changes its mind should not have to ask an organizer to undo a click — and the
    /// reminders (stage 4) resume with the participation.
    /// </summary>
    [Fact]
    public async Task Ticking_something_after_a_decline_un_declines()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var svc = New(db);
        await svc.DeclineAsync(company);

        await svc.SaveParticipationAsync(company, keynote: true, social: false, groupPhoto: false);

        Assert.Null((await db.VolumePackageCompanies.SingleAsync(c => c.Id == company.Id)).DeclinedAt);
    }

    [Fact]
    public async Task The_coordinator_and_linkedin_are_saved_and_trimmed()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var svc = New(db);

        await svc.SaveCoordinatorAsync(company, "  Cara Coordinator  ", " cara@globeteam.dk ", "  ");
        await svc.SaveLinkedInAsync(company, "  https://linkedin.com/company/globeteam  ");

        var saved = await db.VolumePackageCompanies.SingleAsync(c => c.Id == company.Id);
        Assert.Equal("Cara Coordinator", saved.GroupPhotoContactName);
        Assert.Equal("cara@globeteam.dk", saved.GroupPhotoContactEmail);
        // ⚠️ Blank means "not given", not an empty string — a UI that later asks "is this set?"
        // must not be told yes by whitespace.
        Assert.Null(saved.GroupPhotoContactMobile);
        Assert.Equal("https://linkedin.com/company/globeteam", saved.LinkedInUrl);
    }

    // ---- step 3, the logo ------------------------------------------------

    /// <summary>
    /// 🔒 An ALLOWLIST here, the opposite of the media library's denylist (§1078) and for the
    /// opposite reason: this upload arrives from an anonymous token holder and ends on a keynote
    /// slide. "A logo" is a small, known set.
    /// </summary>
    [Theory]
    [InlineData("logo.png", true)]
    [InlineData("logo.svg", true)]
    [InlineData("logo.eps", true)]
    [InlineData("logo.pdf", true)]
    [InlineData("logo.exe", false)]
    [InlineData("logo.zip", false)]
    [InlineData("logo", false)]
    public void Only_logo_shaped_files_are_accepted(string name, bool accepted)
        => Assert.Equal(accepted,
            VolumePackageWizardService.LogoRejectionReason(name, 1024) is null);

    [Fact]
    public void A_logo_larger_than_the_cap_is_refused_with_a_reason()
    {
        var why = VolumePackageWizardService.LogoRejectionReason(
            "logo.png", VolumePackageWizardService.MaxLogoBytes + 1);

        Assert.NotNull(why);
        Assert.Contains("25 MB", why);
    }

    /// <summary>
    /// 🔑 The stored name comes from the COMPANY ID, never from what the uploader called the file.
    /// A re-upload then REPLACES, instead of leaving the design team with logo.png, logo(1).png and
    /// logo-final-v2.png and no way to tell which is current — and the uploader's file name never
    /// becomes a path.
    /// </summary>
    [Fact]
    public async Task A_stored_logo_is_named_after_the_company_not_the_upload()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var store = new RecordingStore();
        var svc = new VolumePackageWizardService(
            db, store, TestDocLibrary.Resolver(), new FixedClock(Now));

        using var bytes = new MemoryStream(new byte[] { 1, 2, 3 });
        Assert.True(await svc.SaveLogoAsync(
            company, VolumePackageLogoKind.Print, "../../My Company LOGO final(2).PNG",
            bytes, 3, "image/png"));

        var (folder, name) = Assert.Single(store.Uploads);
        Assert.Equal($"vp-{company.Id}-print.png", name);
        Assert.EndsWith("Event/GroupPhotos/Print", folder);
        Assert.NotNull((await db.VolumePackageCompanies.SingleAsync(c => c.Id == company.Id)).LogoPrintPath);
    }

    /// <summary>With no wired store the wizard says so rather than pretending the logo arrived.</summary>
    [Fact]
    public async Task With_no_store_a_logo_upload_refuses_instead_of_pretending()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var svc = New(db);   // NullSharePointFileStore ⇒ CanStore false

        Assert.False(svc.CanUploadLogo);
        using var bytes = new MemoryStream(new byte[] { 1 });
        Assert.False(await svc.SaveLogoAsync(
            company, VolumePackageLogoKind.Web, "logo.png", bytes, 1, "image/png"));
        Assert.Null((await db.VolumePackageCompanies.SingleAsync(c => c.Id == company.Id)).LogoWebPath);
    }

    private sealed class RecordingStore : ISharePointFileStore
    {
        public List<(string Folder, string Name)> Uploads { get; } = new();
        public bool CanStore => true;
        public bool CanRead => true;

        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] content, string contentType,
            CancellationToken ct = default)
        {
            Uploads.Add((relativeFolder, fileName));
            return Task.FromResult(new StoredFile($"{relativeFolder}/{fileName}", string.Empty, "id"));
        }

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string f, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>([]);
        public Task<byte[]?> DownloadAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);
        public Task<StoredFile> StoreAsync(string p, byte[] c, string t, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteFromFolderAsync(string f, string n, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
