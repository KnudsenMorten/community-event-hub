using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Sponsors;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// 🔴 §1236 — WHICH COMPANY A SPONSOR CONTACT IS ACTING FOR.
///
/// <para>Stage 2 of §1235: a contact who serves several sponsor companies (a group's shared
/// coordinator) picks one in the portal. This value is what the whole sponsor portal authorizes
/// against — tasks, leads, booth, uploads — so the rule that matters is that the COOKIE IS NEVER
/// TRUSTED: it proposes, and every read re-checks it against the contact's real links.</para>
/// </summary>
public sealed class SponsorActingCompanyTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"acting-{Guid.NewGuid():N}").Options);

    private sealed class FakeHttp : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = new DefaultHttpContext();
        public void SetCookie(string value) =>
            HttpContext!.Request.Headers["Cookie"] =
                $"{SponsorActingCompanyService.CookieName}={value}";
    }

    /// <summary>One participant, plus the companies they are linked to.</summary>
    private static async Task<(CommunityHubDbContext Db, Participant Me)> SeedAsync(
        string primaryCompanyId, params string[] alsoLinked)
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "D", Code = "ELDK27", IsActive = true,
        });
        var me = new Participant
        {
            EventId = EventId, Email = "cmo@group.test", FullName = "Global CMO",
            Role = ParticipantRole.Sponsor, SponsorCompanyId = primaryCompanyId, IsActive = true,
        };
        db.Participants.Add(me);
        await db.SaveChangesAsync();

        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = primaryCompanyId, CompanyName = "Group DK", IsSponsor = true,
        });
        db.SponsorCompanyContacts.Add(new SponsorCompanyContact
        {
            EventId = EventId, ParticipantId = me.Id, SponsorCompanyId = primaryCompanyId, IsPrimary = true,
        });
        foreach (var id in alsoLinked)
        {
            db.SponsorInfos.Add(new SponsorInfo
            {
                EventId = EventId, SponsorCompanyId = id, CompanyName = $"Group {id}", IsSponsor = true,
            });
            db.SponsorCompanyContacts.Add(new SponsorCompanyContact
            {
                EventId = EventId, ParticipantId = me.Id, SponsorCompanyId = id,
            });
        }
        await db.SaveChangesAsync();
        return (db, me);
    }

    [Fact]
    public async Task With_no_choice_the_primary_company_is_the_acting_one()
    {
        var (db, me) = await SeedAsync("1", "2");
        using var _ = db;
        var svc = new SponsorActingCompanyService(db, new FakeHttp());

        Assert.Equal("1", await svc.CurrentAsync(me.Id, EventId));
    }

    [Fact]
    public async Task A_chosen_company_the_contact_is_linked_to_is_honoured()
    {
        var (db, me) = await SeedAsync("1", "2");
        using var _ = db;
        var http = new FakeHttp();
        http.SetCookie("2");
        var svc = new SponsorActingCompanyService(db, http);

        Assert.Equal("2", await svc.CurrentAsync(me.Id, EventId));
    }

    /// <summary>
    /// 🔴 THE SECURITY RULE. A cookie naming a company the contact does not serve must not scope the
    /// portal to it — otherwise editing one cookie value would expose another sponsor's tasks, leads
    /// and uploads. It falls back to their own company rather than failing the page.
    /// </summary>
    [Theory]
    [InlineData("999")]            // a company that exists for someone else
    [InlineData("' OR 1=1 --")]    // junk
    [InlineData("")]
    public async Task A_company_the_contact_does_not_serve_is_refused(string tampered)
    {
        var (db, me) = await SeedAsync("1", "2");
        using var _ = db;
        var http = new FakeHttp();
        http.SetCookie(tampered);
        var svc = new SponsorActingCompanyService(db, http);

        Assert.Equal("1", await svc.CurrentAsync(me.Id, EventId));
    }

    /// <summary>…and the same check guards the WRITE, so nothing is even recorded.</summary>
    [Fact]
    public async Task Selecting_a_company_the_contact_does_not_serve_writes_nothing()
    {
        var (db, me) = await SeedAsync("1", "2");
        using var _ = db;
        var http = new FakeHttp();
        var svc = new SponsorActingCompanyService(db, http);

        Assert.False(await svc.TrySelectAsync(me.Id, EventId, "999"));
        Assert.DoesNotContain(
            http.HttpContext!.Response.Headers.SetCookie.ToString() ?? string.Empty,
            new[] { "999" });

        Assert.True(await svc.TrySelectAsync(me.Id, EventId, "2"));
    }

    /// <summary>
    /// ⚠️ A ONE-COMPANY CONTACT IS UNAFFECTED: a single option means no switcher is rendered, and the
    /// answer is the same value the portal used before §1236.
    /// </summary>
    [Fact]
    public async Task A_single_company_contact_has_one_option_and_the_old_answer()
    {
        var (db, me) = await SeedAsync("1");
        using var _ = db;
        var svc = new SponsorActingCompanyService(db, new FakeHttp());

        var options = await svc.OptionsAsync(me.Id, EventId);
        Assert.Single(options);
        Assert.True(options[0].IsPrimary);
        Assert.Equal("1", await svc.CurrentAsync(me.Id, EventId));
    }

    [Fact]
    public async Task Options_are_named_and_list_the_primary_first()
    {
        var (db, me) = await SeedAsync("1", "2");
        using var _ = db;
        var svc = new SponsorActingCompanyService(db, new FakeHttp());

        var options = await svc.OptionsAsync(me.Id, EventId);

        Assert.Equal(2, options.Count);
        Assert.True(options[0].IsPrimary);
        Assert.Equal("Group DK", options[0].DisplayName);
        Assert.Equal("Group 2", options[1].DisplayName);
    }

    /// <summary>
    /// 🔴 §1236 — NO PORTAL SURFACE MAY RESOLVE THE COMPANY BY ITSELF AGAIN.
    ///
    /// <para>The switcher only works because every sponsor-facing read asks the ONE service. An inline
    /// <c>Select(p =&gt; p.SponsorCompanyId)</c> would silently pin that page to the contact's PRIMARY
    /// company — the page would keep working, show the wrong company's data after a switch, and
    /// nothing would fail. That is exactly how the 13 copies accumulated in the first place.</para>
    /// </summary>
    [Fact]
    public void No_sponsor_portal_file_resolves_the_company_inline()
    {
        var web = FindDir("src", "CommunityHub");
        var files = Directory.GetFiles(Path.Combine(web, "Pages", "Sponsor"), "*.cshtml.cs")
            .Append(Path.Combine(web, "Uploads", "DirectUploadEndpoints.cs"))
            .ToList();

        var offenders = files
            .Where(f => File.ReadAllText(f).Contains(".Select(p => p.SponsorCompanyId)", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These sponsor-portal files resolve the company inline instead of asking "
            + $"SponsorActingCompanyService: {string.Join(", ", offenders)}");
    }

    private static string FindDir(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(Path.Combine(parts));
    }

    /// <summary>A contact with no company at all (not yet linked) resolves to null, not a crash.</summary>
    [Fact]
    public async Task A_contact_with_no_company_resolves_to_null()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "D", Code = "ELDK27", IsActive = true,
        });
        var me = new Participant
        {
            EventId = EventId, Email = "new@group.test", FullName = "Unlinked",
            Role = ParticipantRole.Sponsor, IsActive = true,
        };
        db.Participants.Add(me);
        await db.SaveChangesAsync();

        var svc = new SponsorActingCompanyService(db, new FakeHttp());

        Assert.Null(await svc.CurrentAsync(me.Id, EventId));
        Assert.Empty(await svc.OptionsAsync(me.Id, EventId));
    }
}
