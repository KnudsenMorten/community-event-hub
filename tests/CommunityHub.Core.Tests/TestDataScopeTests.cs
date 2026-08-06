using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §905 — deciding which sponsor company is TEST DATA, which is what keeps a fixture off the
/// company page.
/// </summary>
/// <remarks>
/// 🔴 The case that drives the whole design is <see cref="A_company_with_real_contacts_is_real"/>:
/// on PROD the operator's own company is a paying Gold sponsor carrying SIX test contacts next to
/// six real ones. The obvious rule — "has a test contact" — would have removed a real sponsor from
/// the campaign and from the tier graphics.
/// </remarks>
public sealed class TestDataScopeTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"test-scope-{Guid.NewGuid():N}").Options);

    private static void AddContact(CommunityHubDbContext db, string companyId, bool isTest) =>
        db.Participants.Add(new Participant
        {
            EventId = EventId,
            Email = $"{Guid.NewGuid():N}@example.test",
            FullName = isTest ? "Test Person" : "Real Person",
            SponsorCompanyId = companyId,
            IsTestUser = isTest,
            IsActive = true,
        });

    [Fact]
    public async Task A_company_whose_every_contact_is_a_test_user_is_test()
    {
        using var db = NewDb();
        AddContact(db, "co-test", isTest: true);
        AddContact(db, "co-test", isTest: true);
        await db.SaveChangesAsync();

        var test = await TestDataScope.TestSponsorCompanyIdsAsync(db, EventId);

        Assert.Contains("co-test", test);
    }

    /// <summary>🔴 The real-world case — see the class remarks.</summary>
    [Fact]
    public async Task A_company_with_real_contacts_is_real()
    {
        using var db = NewDb();
        // Six and six, exactly as measured on PROD for the operator's own company.
        for (var i = 0; i < 6; i++) AddContact(db, "co-12", isTest: true);
        for (var i = 0; i < 6; i++) AddContact(db, "co-12", isTest: false);
        await db.SaveChangesAsync();

        var test = await TestDataScope.TestSponsorCompanyIdsAsync(db, EventId);

        Assert.DoesNotContain("co-12", test);
    }

    /// <summary>
    /// ⚠️ "All of them are test" is vacuously TRUE over an empty set — which would drop every
    /// sponsor who has signed but not yet been onboarded.
    /// </summary>
    [Fact]
    public async Task A_company_with_no_contacts_at_all_is_not_test()
    {
        using var db = NewDb();
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "co-new", CompanyName = "Just Signed A/S",
        });
        await db.SaveChangesAsync();

        var test = await TestDataScope.TestSponsorCompanyIdsAsync(db, EventId);

        Assert.Empty(test);
    }

    /// <summary>
    /// §905 — the flag he sets WINS over the contact rule. This is the case the derived rule cannot
    /// express: a company with real contacts on it that he nevertheless wants treated as a fixture.
    /// </summary>
    [Fact]
    public async Task The_explicit_flag_marks_a_company_test_even_with_real_contacts()
    {
        using var db = NewDb();
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "co-demo", CompanyName = "Demo Corp",
            IsTestData = true,
        });
        AddContact(db, "co-demo", isTest: false);
        await db.SaveChangesAsync();

        var test = await TestDataScope.TestSponsorCompanyIdsAsync(db, EventId);

        Assert.Contains("co-demo", test);
    }

    /// <summary>🔒 Default false — an ordinary sponsor is real without anyone setting anything.</summary>
    [Fact]
    public async Task An_unflagged_company_with_real_contacts_stays_real()
    {
        using var db = NewDb();
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "co-real", CompanyName = "Real A/S",
        });
        AddContact(db, "co-real", isTest: false);
        await db.SaveChangesAsync();

        Assert.Empty(await TestDataScope.TestSponsorCompanyIdsAsync(db, EventId));
    }

    [Fact]
    public async Task Contacts_of_another_edition_do_not_decide_this_one()
    {
        using var db = NewDb();
        AddContact(db, "co-x", isTest: true);
        // The same company, with a REAL contact, on a different edition.
        db.Participants.Add(new Participant
        {
            EventId = EventId + 1, Email = "real@other.test", FullName = "Real",
            SponsorCompanyId = "co-x", IsTestUser = false, IsActive = true,
        });
        await db.SaveChangesAsync();

        var test = await TestDataScope.TestSponsorCompanyIdsAsync(db, EventId);

        // Edition-scoped: co-x is test HERE, whatever it looks like next year.
        Assert.Contains("co-x", test);
    }
}
