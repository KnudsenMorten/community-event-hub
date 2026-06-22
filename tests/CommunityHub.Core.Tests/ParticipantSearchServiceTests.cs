using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="ParticipantSearchService"/> — the single
/// authority for organizer participant search / filter / sort and the global
/// "find a person fast" box (REQUIREMENTS §20 Organizer). EF Core InMemory
/// provider; the service is pure + read-only, so no clock/email collaborators.
///
/// Covers: free-text on name AND email, status filter (lifecycle-correct Active /
/// Inactive / All), role + persona filtering, each sort column + direction,
/// event scoping, the request parser, and the global-search limit/clamp.
/// </summary>
public sealed class ParticipantSearchServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"search-{Guid.NewGuid():N}")
            .Options);

    private static async Task SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(Evt(EventId, "T27"));
        db.Events.Add(Evt(OtherEventId, "T26"));

        // EventId people: a mix of roles + statuses.
        db.Participants.AddRange(
            Person(EventId, "Alice Andersen", "alice@example.test",
                ParticipantRole.Speaker, active: true, life: ParticipantLifecycleState.Active),
            Person(EventId, "Bob Berg", "bob@example.test",
                ParticipantRole.Volunteer, active: true, life: ParticipantLifecycleState.Active),
            // Withdrawn (IsActive=false) → NOT lifecycle-active.
            Person(EventId, "Carol Carlsen", "carol@example.test",
                ParticipantRole.Speaker, active: false, life: ParticipantLifecycleState.Active),
            // Not yet activated (lifecycle Preselected) → NOT lifecycle-active.
            Person(EventId, "Dave Dahl", "dave@example.test",
                ParticipantRole.Speaker, active: true, life: ParticipantLifecycleState.Preselected),
            Person(EventId, "Eve Eriksen", "eve@example.test",
                ParticipantRole.Media, active: true, life: ParticipantLifecycleState.Active),
            // Different edition — must never leak into EventId queries.
            Person(OtherEventId, "Alice Other", "alice@other.test",
                ParticipantRole.Speaker, active: true, life: ParticipantLifecycleState.Active));

        await db.SaveChangesAsync();
    }

    private static Event Evt(int id, string code) => new()
    {
        Id = id, Code = code, CommunityName = "T", DisplayName = "T 2027",
        StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        IsActive = true,
    };

    private static Participant Person(
        int eventId, string name, string email, ParticipantRole role,
        bool active, ParticipantLifecycleState life) => new()
    {
        EventId = eventId, FullName = name, Email = email, Role = role,
        IsActive = active, LifecycleState = life,
    };

    private static async Task<List<Participant>> RunAsync(
        CommunityHubDbContext db, ParticipantSearchRequest req) =>
        await new ParticipantSearchService(db).Query(EventId, req).ToListAsync();

    // ---- Parse ------------------------------------------------------------

    [Fact]
    public void Parse_normalizes_loose_values()
    {
        var req = ParticipantSearchService.Parse(
            "  bob  ", role: null, persona: null, "INACTIVE", "  ", "EMAIL", descending: true);

        Assert.Equal("bob", req.Text);
        Assert.Null(req.SponsorCompanyId);
        Assert.Equal(ParticipantStatusFilter.Inactive, req.Status);
        Assert.Equal(ParticipantSortColumn.Email, req.Sort);
        Assert.True(req.Descending);
    }

    [Fact]
    public void Parse_falls_back_to_active_name_for_unknown_inputs()
    {
        var req = ParticipantSearchService.Parse(
            "", null, null, "bogus", null, "bogus", descending: false);

        Assert.Null(req.Text);
        Assert.Equal(ParticipantStatusFilter.Active, req.Status);
        Assert.Equal(ParticipantSortColumn.Name, req.Sort);
    }

    // ---- Free-text --------------------------------------------------------

    [Fact]
    public async Task FreeText_matches_name_and_email_case_sensitivity_aside()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var byName = await RunAsync(db, Req(text: "Berg", status: ParticipantStatusFilter.All));
        Assert.Single(byName);
        Assert.Equal("Bob Berg", byName[0].FullName);

        var byEmail = await RunAsync(db, Req(text: "carol@example", status: ParticipantStatusFilter.All));
        Assert.Single(byEmail);
        Assert.Equal("Carol Carlsen", byEmail[0].FullName);
    }

    // ---- Status filter (lifecycle-correct) --------------------------------

    [Fact]
    public async Task Active_filter_uses_lifecycle_correct_rule()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var active = await RunAsync(db, Req(status: ParticipantStatusFilter.Active));

        // Alice, Bob, Eve are IsActive AND lifecycle Active. Carol (withdrawn)
        // and Dave (Preselected) are excluded.
        Assert.Equal(new[] { "Alice Andersen", "Bob Berg", "Eve Eriksen" },
            active.Select(p => p.FullName).ToArray());
    }

    [Fact]
    public async Task Inactive_filter_returns_the_non_active_complement()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var inactive = await RunAsync(db, Req(status: ParticipantStatusFilter.Inactive));

        Assert.Equal(new[] { "Carol Carlsen", "Dave Dahl" },
            inactive.OrderBy(p => p.FullName).Select(p => p.FullName).ToArray());
    }

    [Fact]
    public async Task All_filter_returns_everyone_in_the_edition_only()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var all = await RunAsync(db, Req(status: ParticipantStatusFilter.All));

        Assert.Equal(5, all.Count);                              // 5 in EventId
        Assert.DoesNotContain(all, p => p.Email == "alice@other.test"); // not the other edition
    }

    // ---- Role + persona ---------------------------------------------------

    [Fact]
    public async Task Role_filter_restricts_to_one_role()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var speakers = await RunAsync(db,
            Req(role: ParticipantRole.Speaker, status: ParticipantStatusFilter.All));

        // Dave (formerly MasterclassSpeaker) is now a plain Speaker too.
        Assert.Equal(new[] { "Alice Andersen", "Carol Carlsen", "Dave Dahl" },
            speakers.OrderBy(p => p.FullName).Select(p => p.FullName).ToArray());
    }

    [Fact]
    public async Task Persona_filter_collapses_related_roles()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // Speaker persona = Speaker → Alice, Carol, Dave.
        var speakerPersona = await RunAsync(db,
            Req(persona: PersonaGroup.Speaker, status: ParticipantStatusFilter.All));
        Assert.Equal(new[] { "Alice Andersen", "Carol Carlsen", "Dave Dahl" },
            speakerPersona.OrderBy(p => p.FullName).Select(p => p.FullName).ToArray());

        // Media-team persona = Media → Eve only.
        var media = await RunAsync(db,
            Req(persona: PersonaGroup.MediaTeam, status: ParticipantStatusFilter.All));
        Assert.Equal(new[] { "Eve Eriksen" }, media.Select(p => p.FullName).ToArray());
    }

    // ---- Sort -------------------------------------------------------------

    [Fact]
    public async Task Sort_by_name_ascending_and_descending()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var asc = await RunAsync(db, Req(status: ParticipantStatusFilter.All));
        Assert.Equal("Alice Andersen", asc.First().FullName);

        var desc = await RunAsync(db,
            Req(status: ParticipantStatusFilter.All, sort: ParticipantSortColumn.Name, descending: true));
        Assert.Equal("Eve Eriksen", desc.First().FullName);
    }

    [Fact]
    public async Task Sort_by_email_and_persona()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var byEmail = await RunAsync(db,
            Req(status: ParticipantStatusFilter.All, sort: ParticipantSortColumn.Email));
        Assert.Equal("alice@example.test", byEmail.First().Email);

        var byPersona = await RunAsync(db,
            Req(status: ParticipantStatusFilter.All, sort: ParticipantSortColumn.Persona));
        // Role enum order: Speaker(1) < Volunteer(3) < Media(6).
        Assert.Equal(ParticipantRole.Speaker, byPersona.First().Role);
    }

    // ---- Global search ----------------------------------------------------

    [Fact]
    public async Task GlobalSearch_blank_returns_nothing()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = new ParticipantSearchService(db);

        Assert.Empty(await svc.GlobalSearchAsync(EventId, "   "));
        Assert.Empty(await svc.GlobalSearchAsync(EventId, null));
    }

    [Fact]
    public async Task GlobalSearch_matches_name_or_email_and_is_event_scoped()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = new ParticipantSearchService(db);

        var hits = await svc.GlobalSearchAsync(EventId, "alice");

        // Only the EventId Alice — never the other-edition Alice.
        Assert.Single(hits);
        Assert.Equal("alice@example.test", hits[0].Email);
        Assert.True(hits[0].IsActive);
    }

    [Fact]
    public async Task GlobalSearch_includes_inactive_people_flagged_correctly()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = new ParticipantSearchService(db);

        var hits = await svc.GlobalSearchAsync(EventId, "carol");

        Assert.Single(hits);
        Assert.False(hits[0].IsActive);    // Carol is withdrawn → flagged inactive
    }

    [Fact]
    public async Task GlobalSearch_clamps_the_limit()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = new ParticipantSearchService(db);

        // "example" matches all 5 EventId rows; ask for 2 → get 2, name-ordered.
        var hits = await svc.GlobalSearchAsync(EventId, "example", limit: 2);

        Assert.Equal(2, hits.Count);
        Assert.Equal("Alice Andersen", hits[0].FullName);
        Assert.Equal("Bob Berg", hits[1].FullName);
    }

    // ---- Helper -----------------------------------------------------------

    private static ParticipantSearchRequest Req(
        string? text = null,
        ParticipantRole? role = null,
        PersonaGroup? persona = null,
        ParticipantStatusFilter status = ParticipantStatusFilter.Active,
        string? sponsorCompanyId = null,
        ParticipantSortColumn sort = ParticipantSortColumn.Name,
        bool descending = false) =>
        new(text, role, persona, status, sponsorCompanyId, sort, descending);
}
