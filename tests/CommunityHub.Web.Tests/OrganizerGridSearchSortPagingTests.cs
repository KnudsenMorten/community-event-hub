using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Resources;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Search / sort / pagination + bulk for the high-traffic organizer grids
/// (REQUIREMENTS §21). Drives the real page-models over a fake organizer session
/// and asserts the SERVER-SIDE behaviour: the grid filters in the database,
/// orders by the chosen column, returns only the requested page (Attendees can be
/// many pages), and the existing bulk operations still apply to the selection.
/// FAKE names only.
/// </summary>
public sealed class OrganizerGridSearchSortPagingTests
{
    private const int EventId = 42;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"grids-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-15T10:00:00Z");
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static DefaultHttpContext OrganizerContext()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "1"),
            new(ClaimTypes.Email, "org@example.test"),
            new(ClaimTypes.Name, "Olive Organizer"),
            new(ClaimTypes.Role, ParticipantRole.Organizer.ToString()),
            new("EventId", EventId.ToString()),
        };
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
        };
    }

    private static ParticipantsModel NewParticipants(CommunityHubDbContext db, DefaultHttpContext http, TimeProvider clock)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new ParticipantsModel(
            db, accessor, new ParticipantBulkOperationService(db),
            new ParticipantDeletionService(db, clock),
            new ParticipantSearchService(db),
            new ImpersonationAuditService(db, clock), clock)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static SpeakersModel NewSpeakers(CommunityHubDbContext db, DefaultHttpContext http, TimeProvider clock)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new SpeakersModel(db, accessor, clock,
            new CommunityHub.Core.Organizer.SpeakerDeletionService(db))
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static AttendeesModel NewAttendees(CommunityHubDbContext db, DefaultHttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new AttendeesModel(db, accessor)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static Participant MakeParticipant(string fullName, string email,
        ParticipantRole role = ParticipantRole.Attendee, bool active = true) => new()
    {
        EventId = EventId, FullName = fullName, Email = email, Role = role,
        IsActive = active, LifecycleState = ParticipantLifecycleState.Active,
    };

    // ---- Participants: search ------------------------------------------------

    [Fact]
    public async Task Participants_search_filters_by_name_or_email_server_side()
    {
        using var db = NewDb();
        db.Participants.AddRange(
            MakeParticipant("Alice Anderson", "alice@example.test"),
            MakeParticipant("Bob Brown", "bob@example.test"),
            MakeParticipant("Carol Clark", "carol-alice@example.test"));   // matches "alice" via email
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewParticipants(db, http, new FixedClock());
        model.ActiveFilter = "all";
        model.Search = "alice";

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(2, model.Participants.Count);
        Assert.All(model.Participants, p =>
            Assert.True(p.FullName.Contains("Alice") || p.Email.Contains("alice")));
    }

    // ---- Participants: sort --------------------------------------------------

    [Fact]
    public async Task Participants_sort_by_name_ascending_then_descending()
    {
        using var db = NewDb();
        db.Participants.AddRange(
            MakeParticipant("Charlie", "c@example.test"),
            MakeParticipant("Alice", "a@example.test"),
            MakeParticipant("Bob", "b@example.test"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewParticipants(db, http, new FixedClock());
        model.ActiveFilter = "all";
        model.Sort = "name";
        model.Desc = false;
        await model.OnGetAsync(CancellationToken.None);
        Assert.Equal(new[] { "Alice", "Bob", "Charlie" },
            model.Participants.Select(p => p.FullName).ToArray());

        model.Desc = true;
        await model.OnGetAsync(CancellationToken.None);
        Assert.Equal(new[] { "Charlie", "Bob", "Alice" },
            model.Participants.Select(p => p.FullName).ToArray());
    }

    // ---- Participants: paging ------------------------------------------------

    [Fact]
    public async Task Participants_paging_returns_only_one_page_and_reports_total()
    {
        using var db = NewDb();
        for (var i = 0; i < 60; i++)
            db.Participants.Add(MakeParticipant($"P{i:D2}", $"p{i:D2}@example.test"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewParticipants(db, http, new FixedClock());
        model.ActiveFilter = "all";
        model.PageNo = 2;
        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(GridPaging.DefaultPageSize, model.Participants.Count);   // not all 60
        Assert.Equal(60, model.Paging.TotalItems);
        Assert.Equal(2, model.Paging.Page);
        Assert.True(model.Paging.TotalPages >= 3);
        // page 2 of a name-sorted list starts at P25
        Assert.Equal("P25", model.Participants.First().FullName);
    }

    // ---- Participants: bulk still works on the selection ---------------------

    [Fact]
    public async Task Participants_bulk_deactivate_applies_to_selected_only()
    {
        using var db = NewDb();
        var a = MakeParticipant("Bulk A", "ba@example.test");
        var b = MakeParticipant("Bulk B", "bb@example.test");
        var c = MakeParticipant("Bulk C", "bc@example.test");
        db.Participants.AddRange(a, b, c);
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewParticipants(db, http, new FixedClock());
        model.SelectedIds = new List<int> { a.Id, c.Id };

        var result = await model.OnPostBulkDeactivateAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.False((await db.Participants.FindAsync(a.Id))!.IsActive);
        Assert.True((await db.Participants.FindAsync(b.Id))!.IsActive);   // not selected
        Assert.False((await db.Participants.FindAsync(c.Id))!.IsActive);
    }

    [Fact]
    public async Task Participants_bulk_with_empty_selection_is_a_safe_noop_with_message()
    {
        using var db = NewDb();
        var a = MakeParticipant("Keep Me", "keep@example.test");
        db.Participants.Add(a);
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewParticipants(db, http, new FixedClock());
        model.SelectedIds = new List<int>();   // confirm modal blocks this in the UI; server is the backstop

        var result = await model.OnPostBulkDeactivateAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.True((await db.Participants.FindAsync(a.Id))!.IsActive);    // nothing changed
        Assert.Contains("Pick at least one", redirect.RouteValues!["Msg"]!.ToString());
    }

    // ---- Speakers: search + sort + paging + bulk -----------------------------

    private static Participant MakeSpeaker(string fullName, string email) =>
        MakeParticipant(fullName, email, ParticipantRole.Speaker);

    [Fact]
    public async Task Speakers_search_and_sort_are_server_side()
    {
        using var db = NewDb();
        db.Participants.AddRange(
            MakeSpeaker("Zoe Speaker", "zoe@example.test"),
            MakeSpeaker("Amy Speaker", "amy@example.test"),
            MakeSpeaker("Other Person", "other@example.test"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewSpeakers(db, http, new FixedClock());
        // Case matches the stored value: the InMemory provider's Contains is
        // ordinal/case-sensitive (production SQL Server is collation-insensitive,
        // so this is stricter than prod, never looser).
        model.Search = "Speaker";
        model.Sort = "name";
        model.Desc = false;
        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(new[] { "Amy Speaker", "Zoe Speaker" },
            model.Rows.Select(r => r.Name).ToArray());
    }

    [Fact]
    public async Task Speakers_paging_returns_one_page()
    {
        using var db = NewDb();
        for (var i = 0; i < 40; i++)
            db.Participants.Add(MakeSpeaker($"Spk{i:D2}", $"spk{i:D2}@example.test"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewSpeakers(db, http, new FixedClock());
        model.PageNo = 1;
        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(GridPaging.DefaultPageSize, model.Rows.Count);
        Assert.Equal(40, model.Paging.TotalItems);
        Assert.True(model.Paging.HasNext);
    }

    [Fact]
    public async Task Speakers_bulk_selected_sets_flag_on_selection_only()
    {
        using var db = NewDb();
        var s1 = MakeSpeaker("Flag One", "f1@example.test");
        var s2 = MakeSpeaker("Flag Two", "f2@example.test");
        db.Participants.AddRange(s1, s2);
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewSpeakers(db, http, new FixedClock());
        model.SelectedIds = new[] { s1.Id };
        model.FieldToSet = "preday";
        model.TargetValue = true;

        await model.OnPostBulkSelectedAsync(CancellationToken.None);

        var p1 = await db.SpeakerProfiles.FirstOrDefaultAsync(p => p.ParticipantId == s1.Id);
        var p2 = await db.SpeakerProfiles.FirstOrDefaultAsync(p => p.ParticipantId == s2.Id);
        Assert.True(p1!.SpeakingPreDay);
        Assert.True(p2 is null || !p2.SpeakingPreDay);   // untouched speaker
    }

    // ---- Attendees: search + sort + paging (read-only grid) ------------------

    private static Attendee MakeAttendee(string first, string last, string email,
        TicketStatus ticket = TicketStatus.Other) => new()
    {
        EventId = EventId, FirstName = first, LastName = last, Email = email,
        TicketStatus = ticket, LastSyncedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Attendees_paging_does_not_assume_one_page()
    {
        using var db = NewDb();
        for (var i = 0; i < 70; i++)
            db.Attendees.Add(MakeAttendee($"First{i:D2}", $"Last{i:D2}", $"att{i:D2}@example.test"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewAttendees(db, http);
        model.PageNo = 1;
        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(GridPaging.DefaultPageSize, model.Attendees.Count);   // not 70
        Assert.Equal(70, model.Paging.TotalItems);
        Assert.True(model.Paging.TotalPages >= 3);
        Assert.True(model.Paging.HasNext);
    }

    [Fact]
    public async Task Attendees_sort_by_name_ascending_then_descending()
    {
        using var db = NewDb();
        db.Attendees.AddRange(
            MakeAttendee("First", "Charlie", "c@example.test"),
            MakeAttendee("First", "Alpha", "a@example.test"),
            MakeAttendee("First", "Bravo", "b@example.test"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewAttendees(db, http);
        model.Sort = "name";   // LastName is the name sort key for attendees

        model.Desc = false;
        await model.OnGetAsync(CancellationToken.None);
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" },
            model.Attendees.Select(a => a.LastName).ToArray());

        model.Desc = true;
        await model.OnGetAsync(CancellationToken.None);
        Assert.Equal(new[] { "Charlie", "Bravo", "Alpha" },
            model.Attendees.Select(a => a.LastName).ToArray());
    }

    [Fact]
    public async Task Attendees_sort_direction_reverses_results()
    {
        // The descending order must be the exact reverse of the ascending order
        // for the same column (proves the Desc flag wires through end-to-end).
        using var db = NewDb();
        db.Attendees.AddRange(
            MakeAttendee("First", "Zeta", "z@example.test"),
            MakeAttendee("First", "Mu", "m@example.test"),
            MakeAttendee("First", "Beta", "x@example.test"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var asc = NewAttendees(db, http);
        asc.Sort = "name"; asc.Desc = false;
        await asc.OnGetAsync(CancellationToken.None);

        var desc = NewAttendees(db, http);
        desc.Sort = "name"; desc.Desc = true;
        await desc.OnGetAsync(CancellationToken.None);

        Assert.Equal(
            asc.Attendees.Select(a => a.LastName).Reverse().ToArray(),
            desc.Attendees.Select(a => a.LastName).ToArray());
    }

    [Fact]
    public async Task Attendees_search_matches_name_or_email()
    {
        using var db = NewDb();
        db.Attendees.AddRange(
            MakeAttendee("Find", "Me", "find@example.test"),
            MakeAttendee("Skip", "Other", "skip@example.test"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewAttendees(db, http);
        model.Search = "Find";
        await model.OnGetAsync(CancellationToken.None);

        Assert.Single(model.Attendees);
        Assert.Equal("find@example.test", model.Attendees[0].Email);
    }

    // ---- Sessions: search + sort + paging (read-only grid) -------------------
    // The Sessions page-model takes several services in its constructor, but only
    // its LoadAsync (search/sort/page) runs on the GET path under test, so the
    // unused mgmt/mail/sync services are passed as null.

    private static IStringLocalizer<SharedResource> Loc()
    {
        var options = Options.Create(new LocalizationOptions { ResourcesPath = "" });
        var factory = new ResourceManagerStringLocalizerFactory(options, NullLoggerFactory.Instance);
        return new StringLocalizer<SharedResource>(factory);
    }

    private static SessionsModel NewSessions(CommunityHubDbContext db, DefaultHttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new SessionsModel(db, accessor, null!, null!, null!, null!, null!, null!, null!,
            new FixedClock(), Loc())
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static Session MakeSession(string title, string? room = null,
        SessionType type = SessionType.CommunityTechSession,
        SessionLength length = SessionLength.SixtyMin) => new()
    {
        EventId = EventId, Title = title, Room = room, Type = type, Length = length,
        SessionizeId = Guid.NewGuid().ToString("N"),
    };

    [Fact]
    public async Task Sessions_search_filters_by_title_or_room_server_side()
    {
        using var db = NewDb();
        db.Sessions.AddRange(
            MakeSession("Kubernetes deep dive", room: "Room A"),
            MakeSession("Welcome keynote", room: "Main hall"),
            MakeSession("Networking break", room: "Room A"));   // matches "Room A"
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewSessions(db, http);
        model.Search = "Room A";
        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(2, model.Rows.Count);
        Assert.All(model.Rows, r => Assert.Equal("Room A", r.Room));
    }

    [Fact]
    public async Task Sessions_sort_by_title_ascending_then_descending()
    {
        using var db = NewDb();
        db.Sessions.AddRange(
            MakeSession("Charlie talk"),
            MakeSession("Alpha talk"),
            MakeSession("Bravo talk"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewSessions(db, http);
        model.Sort = "title";

        model.Desc = false;
        await model.OnGetAsync(CancellationToken.None);
        Assert.Equal(new[] { "Alpha talk", "Bravo talk", "Charlie talk" },
            model.Rows.Select(r => r.Title).ToArray());

        model.Desc = true;
        await model.OnGetAsync(CancellationToken.None);
        Assert.Equal(new[] { "Charlie talk", "Bravo talk", "Alpha talk" },
            model.Rows.Select(r => r.Title).ToArray());
    }

    [Fact]
    public async Task Sessions_paging_returns_one_page_and_reports_total()
    {
        using var db = NewDb();
        for (var i = 0; i < 60; i++)
            db.Sessions.Add(MakeSession($"S{i:D2}"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewSessions(db, http);
        model.PageNo = 2;
        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(GridPaging.DefaultPageSize, model.Rows.Count);   // not all 60
        Assert.Equal(60, model.Paging.TotalItems);
        Assert.Equal(2, model.Paging.Page);
        Assert.True(model.Paging.TotalPages >= 3);
        Assert.Equal("S25", model.Rows.First().Title);                // page 2 of a title sort
    }

    [Fact]
    public async Task Sessions_service_sessions_are_excluded_from_the_grid()
    {
        using var db = NewDb();
        var real = MakeSession("Real session");
        var service = MakeSession("Service session");
        service.IsServiceSession = true;
        db.Sessions.AddRange(real, service);
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewSessions(db, http);
        await model.OnGetAsync(CancellationToken.None);

        Assert.Single(model.Rows);
        Assert.Equal("Real session", model.Rows[0].Title);
    }

    // ---- Leads: search + sort + paging ---------------------------------------

    private static CommunityHub.Pages.Organizer.SponsorAdmin.LeadsModel NewLeads(
        CommunityHubDbContext db, DefaultHttpContext http, TimeProvider clock)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        // The key/token/sync/email services are only used by the POST handlers
        // (issue/revoke/reply/sync) — never by the GET grid path under test —
        // and no sponsor-role participants are seeded, so the sponsor-key loop
        // never invokes them. Pass null for those.
        return new CommunityHub.Pages.Organizer.SponsorAdmin.LeadsModel(
            db, accessor, null!, null!, null!, null!, clock)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static SponsorLead MakeLead(string fullName, string email, string companyId,
        SponsorLeadStatus status = SponsorLeadStatus.Open, DateTimeOffset? captured = null) => new()
    {
        EventId = EventId, FullName = fullName, Email = email, SponsorCompanyId = companyId,
        Company = companyId, Status = status,
        CapturedAt = captured ?? DateTimeOffset.Parse("2026-06-01T10:00:00Z"),
        LastSyncedAt = DateTimeOffset.Parse("2026-06-15T10:00:00Z"),
    };

    [Fact]
    public async Task Leads_search_filters_by_name_email_or_company()
    {
        using var db = NewDb();
        db.SponsorLeads.AddRange(
            MakeLead("Alice Lead", "alice@acme.test", "acme"),
            MakeLead("Bob Lead", "bob@globex.test", "globex"),
            MakeLead("Carol Lead", "carol@acme.test", "acme"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewLeads(db, http, new FixedClock());
        model.Search = "acme";
        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(2, model.Leads.Count);
        Assert.All(model.Leads, l => Assert.Equal("acme", l.SponsorCompanyId));
    }

    [Fact]
    public async Task Leads_default_sort_is_captured_newest_first()
    {
        using var db = NewDb();
        db.SponsorLeads.AddRange(
            MakeLead("Oldest", "o@x.test", "acme", captured: DateTimeOffset.Parse("2026-06-01T10:00:00Z")),
            MakeLead("Newest", "n@x.test", "acme", captured: DateTimeOffset.Parse("2026-06-10T10:00:00Z")),
            MakeLead("Middle", "m@x.test", "acme", captured: DateTimeOffset.Parse("2026-06-05T10:00:00Z")));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewLeads(db, http, new FixedClock());
        await model.OnGetAsync(CancellationToken.None);   // default Sort=captured, Desc=true

        Assert.Equal(new[] { "Newest", "Middle", "Oldest" },
            model.Leads.Select(l => l.FullName).ToArray());
    }

    [Fact]
    public async Task Leads_sort_by_name_ascending()
    {
        using var db = NewDb();
        db.SponsorLeads.AddRange(
            MakeLead("Charlie", "c@x.test", "acme"),
            MakeLead("Alpha", "a@x.test", "acme"),
            MakeLead("Bravo", "b@x.test", "acme"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewLeads(db, http, new FixedClock());
        model.Sort = "name"; model.Desc = false;
        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" },
            model.Leads.Select(l => l.FullName).ToArray());
    }

    [Fact]
    public async Task Leads_paging_returns_one_page_and_reports_total()
    {
        using var db = NewDb();
        for (var i = 0; i < 55; i++)
            db.SponsorLeads.Add(MakeLead($"Lead{i:D2}", $"l{i:D2}@x.test", "acme",
                captured: DateTimeOffset.Parse("2026-06-01T10:00:00Z").AddMinutes(i)));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewLeads(db, http, new FixedClock());
        model.PageNo = 1;
        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(GridPaging.DefaultPageSize, model.Leads.Count);   // not all 55
        Assert.Equal(55, model.Paging.TotalItems);
        Assert.True(model.Paging.TotalPages >= 3);
        Assert.True(model.Paging.HasNext);
    }

    [Fact]
    public async Task Leads_ignored_and_junk_hidden_by_default_shown_with_toggle()
    {
        using var db = NewDb();
        db.SponsorLeads.AddRange(
            MakeLead("Open one", "o@x.test", "acme", SponsorLeadStatus.Open),
            MakeLead("Junked", "j@x.test", "acme", SponsorLeadStatus.Junk),
            MakeLead("Ignored", "i@x.test", "acme", SponsorLeadStatus.Ignore));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var hidden = NewLeads(db, http, new FixedClock());
        await hidden.OnGetAsync(CancellationToken.None);
        Assert.Single(hidden.Leads);   // only the Open row by default

        var shown = NewLeads(db, http, new FixedClock());
        shown.ShowHidden = true;
        await shown.OnGetAsync(CancellationToken.None);
        Assert.Equal(3, shown.Leads.Count);
    }

    // ---- Sponsors: search + sort + paging (company-grouped grid) -------------

    private static SponsorsModel NewSponsors(CommunityHubDbContext db, DefaultHttpContext http, TimeProvider clock)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new SponsorsModel(db, accessor, clock,
            new CommunityHub.Core.Organizer.SponsorInfoDeletionService(db))
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static Participant MakeSponsor(string fullName, string email, string companyId) =>
        new()
        {
            EventId = EventId, FullName = fullName, Email = email,
            Role = ParticipantRole.Sponsor, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
            SponsorCompanyId = companyId,
        };

    [Fact]
    public async Task Sponsors_groups_by_company_and_pages_the_company_rows()
    {
        using var db = NewDb();
        for (var i = 0; i < 40; i++)
            db.Participants.Add(MakeSponsor($"Contact {i:D2}", $"c{i:D2}@x.test", $"company-{i:D2}"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewSponsors(db, http, new FixedClock());
        model.PageNo = 1;
        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(GridPaging.DefaultPageSize, model.Companies.Count);   // one page, not 40
        Assert.Equal(40, model.Paging.TotalItems);
        Assert.True(model.Paging.HasNext);
        Assert.Equal(40, model.CompanyCount);   // header stat counts ALL companies
    }

    [Fact]
    public async Task Sponsors_search_matches_company_id_or_contact()
    {
        using var db = NewDb();
        db.Participants.AddRange(
            MakeSponsor("Anna Sponsor", "anna@acme.test", "acme"),
            MakeSponsor("Ben Sponsor", "ben@globex.test", "globex"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();

        var byCompany = NewSponsors(db, http, new FixedClock());
        byCompany.Search = "acme";
        await byCompany.OnGetAsync(CancellationToken.None);
        Assert.Single(byCompany.Companies);
        Assert.Equal("acme", byCompany.Companies[0].CompanyId);

        var byContact = NewSponsors(db, http, new FixedClock());
        byContact.Search = "Ben";
        await byContact.OnGetAsync(CancellationToken.None);
        Assert.Single(byContact.Companies);
        Assert.Equal("globex", byContact.Companies[0].CompanyId);
    }

    [Fact]
    public async Task Sponsors_sort_by_company_ascending_then_descending()
    {
        using var db = NewDb();
        db.Participants.AddRange(
            MakeSponsor("X", "x@x.test", "charlie-co"),
            MakeSponsor("Y", "y@x.test", "alpha-co"),
            MakeSponsor("Z", "z@x.test", "bravo-co"));
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewSponsors(db, http, new FixedClock());
        model.Sort = "company";

        model.Desc = false;
        await model.OnGetAsync(CancellationToken.None);
        Assert.Equal(new[] { "alpha-co", "bravo-co", "charlie-co" },
            model.Companies.Select(c => c.CompanyId).ToArray());

        model.Desc = true;
        await model.OnGetAsync(CancellationToken.None);
        Assert.Equal(new[] { "charlie-co", "bravo-co", "alpha-co" },
            model.Companies.Select(c => c.CompanyId).ToArray());
    }

    [Fact]
    public async Task Sponsors_sort_by_overdue_count_descending()
    {
        using var db = NewDb();
        db.Participants.AddRange(
            MakeSponsor("A", "a@x.test", "low-co"),
            MakeSponsor("B", "b@x.test", "high-co"));
        // high-co has 2 overdue tasks; low-co has none.
        var pastDue = new DateOnly(2026, 1, 1);
        db.Tasks.AddRange(
            new ParticipantTask { EventId = EventId, Title = "t1", SponsorCompanyId = "high-co", State = TaskState.Open, DueDate = pastDue },
            new ParticipantTask { EventId = EventId, Title = "t2", SponsorCompanyId = "high-co", State = TaskState.Open, DueDate = pastDue },
            new ParticipantTask { EventId = EventId, Title = "t3", SponsorCompanyId = "low-co", State = TaskState.Done, DueDate = pastDue });
        await db.SaveChangesAsync();

        var http = OrganizerContext();
        var model = NewSponsors(db, http, new FixedClock());
        model.Sort = "overdue"; model.Desc = true;
        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal("high-co", model.Companies.First().CompanyId);
        Assert.Equal(2, model.Companies.First().Overdue);
    }
}
