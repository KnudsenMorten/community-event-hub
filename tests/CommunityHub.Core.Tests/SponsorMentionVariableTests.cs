using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §884.3 — the sponsor's own people as template variables, mentioned where LinkedIn allows it.
/// Measured follower rates before building: signers 7/14, coordinators 6/15 — so roughly half of
/// each renders as plain text, and these tests pin that BOTH halves behave.
/// </summary>
public sealed class SponsorMentionVariableTests
{
    private static CommunityHubDbContext NewDb() => new(
        new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"sponsormention-{Guid.NewGuid():N}").Options);

    private const string Company = "acme-ltd";

    private static async Task<int> SeedAsync(
        CommunityHubDbContext db,
        params (string Name, bool Signer, bool Coordinator, string? Urn)[] contacts)
    {
        var ev = new Event { CommunityName = "Demo", Code = "DEMO", DisplayName = "Demo 2027" };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = ev.Id, SponsorCompanyId = Company, CompanyName = "Acme Ltd",
            SponsorPackage = SponsorPackage.Gold,
        });

        foreach (var (name, signer, coordinator, urn) in contacts)
        {
            db.Participants.Add(new Participant
            {
                EventId = ev.Id, FullName = name, IsActive = true,
                Role = ParticipantRole.Sponsor, SponsorCompanyId = Company,
                IsSigner = signer, IsEventCoordinator = coordinator,
                LinkedInPersonUrn = urn,
                LinkedInPersonUrnStatus = urn is null ? "NotAFollower" : "Resolved",
            });
        }

        await db.SaveChangesAsync();
        return ev.Id;
    }

    /// <summary>
    /// Operator 2026-08-06: *"if more signers or coordinators, mention all"*. A company with two
    /// signers must not silently lose one — that is somebody's name missing from their own
    /// announcement.
    /// </summary>
    [Fact]
    public async Task Every_signer_and_every_coordinator_is_listed_not_just_the_first()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db,
            ("Ann Signer", true, false, "urn:li:person:AAA"),
            ("Bo Signer", true, false, "urn:li:person:BBB"),
            ("Cara Coord", false, true, "urn:li:person:CCC"),
            ("Dan Coord", false, true, "urn:li:person:DDD"));

        var v = await new SoMeVariableResolver(db).SponsorValuesAsync(ev, Company);

        Assert.Equal("@[Ann Signer](urn:li:person:AAA) | @[Bo Signer](urn:li:person:BBB)",
            v["SponsorSigner"]);
        Assert.Equal("@[Cara Coord](urn:li:person:CCC) | @[Dan Coord](urn:li:person:DDD)",
            v["SponsorEventCoordinators"]);
    }

    /// <summary>
    /// Half of real sponsor contacts do not follow the page. They must still appear, by name —
    /// a sponsor announcement that omits its own signer would be worse than one that cannot tag him.
    /// </summary>
    [Fact]
    public async Task A_contact_who_does_not_follow_the_page_still_appears_by_full_name()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db,
            ("Ann Signer", true, false, "urn:li:person:AAA"),
            ("Bo Nofollow", true, false, null));

        var v = await new SoMeVariableResolver(db).SponsorValuesAsync(ev, Company);

        Assert.Equal("@[Ann Signer](urn:li:person:AAA) | Bo Nofollow", v["SponsorSigner"]);
    }

    /// <summary>Being tagged twice in one post reads as a mistake, so a dual-role contact appears once.</summary>
    [Fact]
    public async Task A_contact_who_is_both_signer_and_coordinator_is_listed_only_as_signer()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db, ("Ann Both", true, true, "urn:li:person:AAA"));

        var v = await new SoMeVariableResolver(db).SponsorValuesAsync(ev, Company);

        Assert.Equal("@[Ann Both](urn:li:person:AAA)", v["SponsorSigner"]);
        Assert.Null(v["SponsorEventCoordinators"]);
    }

    /// <summary>
    /// 🔒 Empty rather than a placeholder: §824.15's renderer drops a line whose variables all
    /// resolve empty, so a sponsor with no contacts publishes NO "Tag:" line — never a bare label.
    /// </summary>
    [Fact]
    public async Task A_sponsor_with_no_contacts_yields_no_value_so_the_Tag_line_disappears()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db);

        var v = await new SoMeVariableResolver(db).SponsorValuesAsync(ev, Company);

        Assert.Null(v["SponsorSigner"]);
        Assert.Null(v["SponsorEventCoordinators"]);
    }

    /// <summary>An inactive contact is not tagged — they have left the company or the event.</summary>
    [Fact]
    public async Task An_inactive_contact_is_not_mentioned()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db, ("Ann Signer", true, false, "urn:li:person:AAA"));
        var gone = db.Participants.First(p => p.FullName == "Ann Signer");
        gone.IsActive = false;
        await db.SaveChangesAsync();

        var v = await new SoMeVariableResolver(db).SponsorValuesAsync(ev, Company);

        Assert.Null(v["SponsorSigner"]);
    }

    /// <summary>
    /// The template must carry the operator's layout: a BLANK line after the sponsor text, then
    /// "Tag:" with both variables — and both must be tokens the resolver knows, or §824.15 would
    /// publish the literal braces into a live post.
    /// </summary>
    [Fact]
    public void The_sponsor_template_has_a_blank_line_then_a_Tag_line_with_both_variables()
    {
        var body = SoMeTemplateCatalog.DefaultBody(SoMeTemplateKind.Sponsor);

        Assert.Contains("Tag: {SponsorSigner} {SponsorEventCoordinators}", body);
        // A blank line immediately before the Tag line.
        Assert.Contains("\n\nTag: {SponsorSigner}", body.Replace("\r\n", "\n"));
        Assert.Contains("{SponsorSigner}", SoMeTemplateCatalog.KnownVariables);
        Assert.Contains("{SponsorEventCoordinators}", SoMeTemplateCatalog.KnownVariables);
    }
}
