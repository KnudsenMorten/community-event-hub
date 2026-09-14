using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Participants;
using CommunityHub.Forms;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1085 — the ONE participant-status page: organizer-gated, filtered, paginated, and cached for
/// <see cref="ParticipantStatusModel.CacheTtl"/> so paging a board does not rebuild 140 wizards.
/// FAKE names only.
/// </summary>
public sealed class ParticipantStatusPageTests
{
    private const int EventId = 71;
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private sealed class NoEconomicClient : IEconomicContactAdminClient
    {
        public bool CanWrite => false;
        public Task<System.Collections.Generic.IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
            string? search, int? customerGroup = null, System.Threading.CancellationToken ct = default) =>
            Task.FromResult<System.Collections.Generic.IReadOnlyList<EconomicCustomerRow>>(Array.Empty<EconomicCustomerRow>());
        public Task<System.Collections.Generic.IReadOnlyList<EconomicContactRow>> ListContactsAsync(
            int customerNumber, System.Threading.CancellationToken ct = default) =>
            Task.FromResult<System.Collections.Generic.IReadOnlyList<EconomicContactRow>>(Array.Empty<EconomicContactRow>());
        public Task<int> CreateContactAsync(int c, EconomicContactInput i, System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UpdateContactAsync(int c, int n, EconomicContactInput i, System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteContactAsync(int c, int n, System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"pstatus-page-{Guid.NewGuid():N}").Options);

    private static ClaimsPrincipal Session(Participant p) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new Claim(ClaimTypes.Email, p.Email),
            new Claim(ClaimTypes.Name, p.FullName),
            new Claim(ClaimTypes.Role, p.Role.ToString()),
            new Claim("EventId", p.EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>
    /// The REAL board over the REAL wizard services — only the e-conomic seam is faked. The page's
    /// job is to filter, page and cache what the board says, so faking the board would test nothing.
    /// </summary>
    private static ParticipantStatusBoardBuilder NewBuilder(CommunityHubDbContext db)
    {
        var cmOptions = new CompanyManagerOptions();
        var sponsor = new SponsorWizardService(
            db, new CompanyManagerClient(new System.Net.Http.HttpClient(), cmOptions), cmOptions,
            new EconomicContactAdminService(new NoEconomicClient()),
            NullLogger<SponsorWizardService>.Instance);
        var reader = new WizardProgressReader(
            db, new SpeakerWizardService(db), new RoleWizardService(db),
            new AttendeeWizardService(db), sponsor);
        return new ParticipantStatusBoardBuilder(db, reader, new FixedClock());
    }

    private static ParticipantStatusModel NewModel(
        CommunityHubDbContext db, HttpContext http, IMemoryCache? cache = null) =>
        new(new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http)),
            NewBuilder(db),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            new FixedClock(),
            NullLogger<ParticipantStatusModel>.Instance,
            // §1211 — the coordinator addresses behind the sponsor rows.
            db)
        {
            PageContext = new PageContext { HttpContext = http },
        };

    private static async Task<Participant> SeedAsync(CommunityHubDbContext db, int people = 3)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "PS27", CommunityName = "C", DisplayName = "PS 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();

        var org = new Participant
        {
            EventId = EventId, FullName = "Olivia Organizer", Email = "olivia@example.test",
            Role = ParticipantRole.Organizer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(org);

        for (var i = 0; i < people; i++)
        {
            db.Participants.Add(new Participant
            {
                EventId = EventId, FullName = $"Person {i:D3}", Email = $"p{i}@example.test",
                Role = ParticipantRole.Volunteer, IsActive = true,
                LifecycleState = ParticipantLifecycleState.Active,
            });
        }
        await db.SaveChangesAsync();
        return org;
    }

    [Fact]
    public async Task Organizer_sees_the_board()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });

        var result = await model.OnGetAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(model.AccessDenied);
        Assert.Null(model.Error);
        Assert.Equal(4, model.AllRows.Count);       // 3 volunteers + the organizer
    }

    /// <summary>
    /// A non-organizer gets the friendly notice and NO DATA — the same contract as the other
    /// organizer pages. The rows must stay empty: a 200 with a populated board would be the leak.
    /// </summary>
    [Fact]
    public async Task Non_organizer_is_refused_and_sees_no_rows()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var volunteer = await db.Participants.FirstAsync(p => p.Role == ParticipantRole.Volunteer);
        var model = NewModel(db, new DefaultHttpContext { User = Session(volunteer) });

        await model.OnGetAsync();

        Assert.True(model.AccessDenied);
        Assert.Empty(model.AllRows);
        Assert.Empty(model.Rows);
    }

    [Fact]
    public async Task Anonymous_is_sent_to_login()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var model = NewModel(db, new DefaultHttpContext());

        Assert.IsType<RedirectToPageResult>(await model.OnGetAsync());
    }

    /// <summary>The role filter scopes the board — and it is what keeps the build cheap.</summary>
    [Fact]
    public async Task Role_filter_scopes_the_rows()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        model.RoleFilter = ParticipantRole.Volunteer;

        await model.OnGetAsync();

        Assert.Equal(3, model.AllRows.Count);
        Assert.All(model.AllRows, r => Assert.Equal(ParticipantRole.Volunteer, r.Role));
    }

    /// <summary>
    /// 🔑 §1085's performance decision, asserted: a second load of the same scope reads the CACHE.
    /// Without this, paging a 140-person board rebuilds 140 wizards per click — the exact objection
    /// the completion sweep was fixed for.
    /// </summary>
    [Fact]
    public async Task Second_load_of_the_same_scope_is_served_from_cache()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var cache = new MemoryCache(new MemoryCacheOptions());

        var first = NewModel(db, new DefaultHttpContext { User = Session(org) }, cache);
        await first.OnGetAsync();
        Assert.True(first.WasRebuilt);

        var second = NewModel(db, new DefaultHttpContext { User = Session(org) }, cache);
        await second.OnGetAsync();

        Assert.False(second.WasRebuilt);
        Assert.Equal(first.BuiltAt, second.BuiltAt);
        Assert.Equal(first.AllRows.Count, second.AllRows.Count);
    }

    /// <summary>Refresh is the escape hatch — "I just finished that" must be answerable.</summary>
    [Fact]
    public async Task Refresh_rebuilds_even_when_the_cache_is_warm()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var cache = new MemoryCache(new MemoryCacheOptions());

        await NewModel(db, new DefaultHttpContext { User = Session(org) }, cache).OnGetAsync();

        var refreshed = NewModel(db, new DefaultHttpContext { User = Session(org) }, cache);
        await refreshed.OnGetAsync(refresh: true);

        Assert.True(refreshed.WasRebuilt);
    }

    /// <summary>
    /// A board cached for ALL roles must never answer a question about ONE role — the cache key
    /// carries the scope that was built.
    /// </summary>
    [Fact]
    public async Task All_roles_cache_does_not_answer_a_single_role_question()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var cache = new MemoryCache(new MemoryCacheOptions());

        var all = NewModel(db, new DefaultHttpContext { User = Session(org) }, cache);
        await all.OnGetAsync();
        Assert.Equal(4, all.AllRows.Count);

        var speakers = NewModel(db, new DefaultHttpContext { User = Session(org) }, cache);
        speakers.RoleFilter = ParticipantRole.Speaker;
        await speakers.OnGetAsync();

        Assert.True(speakers.WasRebuilt);
        Assert.Empty(speakers.AllRows);
    }

    /// <summary>Pagination: page 2 holds the remainder, and the summary counts the whole scope.</summary>
    [Fact]
    public async Task Pagination_splits_the_matching_rows()
    {
        using var db = NewDb();
        var org = await SeedAsync(db, people: ParticipantStatusModel.PageSize + 5);
        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        model.PageNumber = 2;

        await model.OnGetAsync();

        Assert.Equal(2, model.TotalPages);
        Assert.Equal(6, model.Rows.Count);                                   // 55 + organizer = 56
        Assert.Equal(ParticipantStatusModel.PageSize + 6, model.MatchCount);
    }

    /// <summary>A page number past the end lands on the last page rather than an empty screen.</summary>
    [Fact]
    public async Task Page_number_past_the_end_clamps()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        model.PageNumber = 99;

        await model.OnGetAsync();

        Assert.Equal(1, model.PageNumber);
        Assert.NotEmpty(model.Rows);
    }

    /// <summary>
    /// The completion filter narrows the ROWS but not the SUMMARY: the counts describe the role
    /// scope, so they do not move when the reader flips the filter to look at one slice.
    /// </summary>
    [Fact]
    public async Task Completion_filter_narrows_rows_but_not_the_summary()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        model.StateFilter = ParticipantStatusFilter.Complete;

        await model.OnGetAsync();

        Assert.Equal(4, model.AllRows.Count);
        Assert.Equal(model.AllRows.Count(r => r.IsComplete), model.MatchCount);
        Assert.All(model.Rows, r => Assert.True(r.IsComplete));
    }
}
