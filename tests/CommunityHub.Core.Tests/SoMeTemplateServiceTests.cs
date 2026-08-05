using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §824.2C — the per-edition post-template editor's storage rules.
/// </summary>
/// <remarks>
/// 🔒 The design under test: <b>shipped default in code, only the DIVERGENCE in the database.</b> No
/// row means the catalog default is live, so improving a default reaches every edition that has not
/// deliberately overridden it — and reset is a DELETE, not a copy of today's default frozen into a
/// row.
/// </remarks>
public sealed class SoMeTemplateServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"some-tpl-{Guid.NewGuid():N}").Options);

    private static SoMeTemplateService Svc(CommunityHubDbContext db) => new(db, new FixedClock(Now));

    private static async Task SeedAsync(CommunityHubDbContext db)
    {
        db.Events.AddRange(
            new Event { Id = EventId, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
                        StartDate = new DateOnly(2027,2,9), EndDate = new DateOnly(2027,2,10), IsActive = true },
            new Event { Id = OtherEventId, Code = "OTHER", CommunityName = "Other", DisplayName = "Other",
                        StartDate = new DateOnly(2027,5,1), EndDate = new DateOnly(2027,5,2) });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task All_five_types_list_in_his_order_with_the_shipped_wording()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var list = await Svc(db).ListAsync(EventId);

        Assert.Equal(5, list.Count);
        Assert.Equal(
            new[] { SoMeTemplateKind.SpeakerTracks, SoMeTemplateKind.Session,
                    SoMeTemplateKind.SponsorCategory, SoMeTemplateKind.Sponsor, SoMeTemplateKind.EventPost },
            list.Select(t => t.Kind));
        Assert.All(list, t => Assert.False(t.IsOverridden));
        Assert.All(list, t => Assert.Empty(t.UnknownPlaceholders));
    }

    [Fact]
    public async Task An_edit_is_stored_and_becomes_the_body_in_force()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = Svc(db);

        await svc.SaveAsync(EventId, SoMeTemplateKind.Sponsor, "✨ {SponsorName} ✨\n{EventTags}", "org@x.dk");

        Assert.Equal("✨ {SponsorName} ✨\n{EventTags}", await svc.BodyAsync(EventId, SoMeTemplateKind.Sponsor));
        var row = (await svc.ListAsync(EventId)).Single(t => t.Kind == SoMeTemplateKind.Sponsor);
        Assert.True(row.IsOverridden);
        Assert.Equal("org@x.dk", row.UpdatedByEmail);
        // The other four are untouched — an edit is per type, not per page.
        Assert.All((await svc.ListAsync(EventId)).Where(t => t.Kind != SoMeTemplateKind.Sponsor),
            t => Assert.False(t.IsOverridden));
    }

    [Fact]
    public async Task Saving_the_shipped_wording_back_removes_the_override()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = Svc(db);

        await svc.SaveAsync(EventId, SoMeTemplateKind.Session, "something of my own", "org@x.dk");
        Assert.Equal(1, await db.SoMeTemplates.CountAsync());

        // 🔒 Back to the default ⇒ no row. Storing a duplicate would silently stop this edition
        // tracking improvements to the shipped wording, for no benefit anyone could see.
        await svc.SaveAsync(
            EventId, SoMeTemplateKind.Session,
            SoMeTemplateCatalog.DefaultBody(SoMeTemplateKind.Session), "org@x.dk");

        Assert.Equal(0, await db.SoMeTemplates.CountAsync());
        Assert.False((await svc.ListAsync(EventId)).Single(t => t.Kind == SoMeTemplateKind.Session).IsOverridden);
    }

    [Fact]
    public async Task Reset_deletes_the_override_and_is_honest_when_there_was_none()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = Svc(db);
        await svc.SaveAsync(EventId, SoMeTemplateKind.SpeakerTracks, "mine", "org@x.dk");

        Assert.True(await svc.ResetAsync(EventId, SoMeTemplateKind.SpeakerTracks));
        Assert.Equal(
            SoMeTemplateCatalog.DefaultBody(SoMeTemplateKind.SpeakerTracks),
            await svc.BodyAsync(EventId, SoMeTemplateKind.SpeakerTracks));

        // Says "nothing changed" rather than reporting a success that did not happen.
        Assert.False(await svc.ResetAsync(EventId, SoMeTemplateKind.SpeakerTracks));
    }

    [Fact]
    public async Task A_second_save_updates_the_same_row_rather_than_adding_another()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = Svc(db);

        await svc.SaveAsync(EventId, SoMeTemplateKind.EventPost, "first", "a@x.dk");
        await svc.SaveAsync(EventId, SoMeTemplateKind.EventPost, "second", "b@x.dk");

        // Without this, "which template is live" becomes a matter of insertion order.
        Assert.Equal(1, await db.SoMeTemplates.CountAsync());
        Assert.Equal("second", await svc.BodyAsync(EventId, SoMeTemplateKind.EventPost));
    }

    [Fact]
    public async Task An_edit_belongs_to_its_own_edition()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = Svc(db);

        await svc.SaveAsync(EventId, SoMeTemplateKind.Sponsor, "ELDK27 wording", "org@x.dk");

        Assert.Equal(
            SoMeTemplateCatalog.DefaultBody(SoMeTemplateKind.Sponsor),
            await svc.BodyAsync(OtherEventId, SoMeTemplateKind.Sponsor));
    }

    [Fact]
    public async Task A_typo_is_saved_and_reported_rather_than_rejected()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = Svc(db);

        // ⚠️ He may be mid-edit. A page that refuses to keep his work because a token is half-typed
        // loses the work; the warning is what makes the typo findable.
        var unknown = await svc.SaveAsync(
            EventId, SoMeTemplateKind.Sponsor, "Hello {SponsorNmae} {EventTags}", "org@x.dk");

        Assert.Equal(new[] { "{SponsorNmae}" }, unknown);
        Assert.Equal("Hello {SponsorNmae} {EventTags}", await svc.BodyAsync(EventId, SoMeTemplateKind.Sponsor));
        Assert.Equal(
            new[] { "{SponsorNmae}" },
            (await svc.ListAsync(EventId)).Single(t => t.Kind == SoMeTemplateKind.Sponsor).UnknownPlaceholders);
    }

    [Fact]
    public async Task Emojis_and_blank_lines_are_stored_exactly_as_typed()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = Svc(db);

        // The blank line between blocks IS the layout on LinkedIn, and emoji are ordinary text.
        const string body = "✨ Hello ✨\n\n🚀 {EventSystemUrl}\n\n{EventTags}";
        await svc.SaveAsync(EventId, SoMeTemplateKind.EventPost, body, "org@x.dk");

        Assert.Equal(body, await svc.BodyAsync(EventId, SoMeTemplateKind.EventPost));
    }

    [Fact]
    public void The_preview_uses_obviously_fake_names()
    {
        // A preview carrying a REAL sponsor's name is one screenshot away from looking like a post
        // that already went out.
        var preview = SoMeTemplateService.PreviewWithSamples(
            SoMeTemplateCatalog.DefaultBody(SoMeTemplateKind.Sponsor));

        Assert.Contains("Contoso", preview);
        Assert.DoesNotContain("{", preview);
    }
}
