using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the organizer Speaker &amp; order review page
/// (<see cref="SpeakerReviewModel"/>). Drives the real page model over a fake
/// HttpContext + in-memory DbContext. Proves:
///   • an organizer sees the speaker list + booth list; a non-organizer is denied;
///   • setting Funding / Days persists;
///   • SamePersonAsId persists, self-reference is rejected, and a chain
///     (linking to a non-primary) is rejected;
///   • an override upsert then clear (default) works;
///   • the IsBoothMember toggle persists;
///   • the counts summary reflects a force-exclude override;
///   • a SamePersonAsId duplicate is not double-counted.
/// FAKE names only.
/// </summary>
public sealed class SpeakerReviewPageTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"speakerreview-{Guid.NewGuid():N}")
            .Options);

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static ClaimsPrincipal Session(Participant p)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        };
        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static SpeakerReviewModel NewModel(CommunityHubDbContext db, HttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new SpeakerReviewModel(db, accessor, new OrderCountService(db), TimeProvider.System)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private sealed record Seed(Event Event, Participant Organizer);

    private static async Task<Seed> SeedEventAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "SPK27", CommunityName = "C", DisplayName = "SPK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var organizer = new Participant
        {
            EventId = evt.Id, FullName = "Olivia Organizer", Email = "olivia@example.test",
            Role = ParticipantRole.Organizer, IsActive = true,
        };
        db.Participants.Add(organizer);
        await db.SaveChangesAsync();
        return new Seed(evt, organizer);
    }

    private static async Task<Participant> AddSpeakerAsync(
        CommunityHubDbContext db, int eventId, string name, string email,
        SpeakerFunding funding = SpeakerFunding.Supported,
        bool preDay = false, bool mainDay = true,
        ParticipantRole role = ParticipantRole.Speaker)
    {
        var p = new Participant
        {
            EventId = eventId, FullName = name, Email = email, Role = role, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = eventId, ParticipantId = p.Id,
            SpeakerFunding = funding, SpeakingPreDay = preDay, SpeakingMainDay = mainDay,
        });
        await db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task OnGet_lists_speakers_and_is_organizer_only()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        await AddSpeakerAsync(db, seed.Event.Id, "Sam Speaker", "sam@example.test");

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.AccessDenied);
        Assert.Null(model.Error);
        Assert.Single(model.Speakers);
        Assert.Contains(model.Speakers, s => s.Email == "sam@example.test");
    }

    [Fact]
    public async Task Non_organizer_role_is_access_denied()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        seed.Organizer.Role = ParticipantRole.Attendee;
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
        Assert.Empty(model.Speakers);
    }

    [Fact]
    public async Task Setting_funding_persists()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var sam = await AddSpeakerAsync(db, seed.Event.Id, "Sam", "sam@example.test",
            funding: SpeakerFunding.Supported);

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };
        var model = NewModel(db, http);

        var result = await model.OnPostFundingAsync(sam.Id, SpeakerFunding.SponsorSelfFunded, default);

        Assert.IsType<RedirectToPageResult>(result);
        var profile = await db.SpeakerProfiles.FirstAsync(s => s.ParticipantId == sam.Id);
        Assert.Equal(SpeakerFunding.SponsorSelfFunded, profile.SpeakerFunding);
    }

    [Fact]
    public async Task Setting_days_persists()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var sam = await AddSpeakerAsync(db, seed.Event.Id, "Sam", "sam@example.test",
            preDay: false, mainDay: false);

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };
        var model = NewModel(db, http);

        var result = await model.OnPostDaysAsync(sam.Id, preDay: true, mainDay: false, default);

        Assert.IsType<RedirectToPageResult>(result);
        var profile = await db.SpeakerProfiles.FirstAsync(s => s.ParticipantId == sam.Id);
        Assert.True(profile.SpeakingPreDay);
        Assert.False(profile.SpeakingMainDay);
    }

    [Fact]
    public async Task SamePerson_link_persists()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var primary = await AddSpeakerAsync(db, seed.Event.Id, "Primary", "primary@example.test");
        var dup = await AddSpeakerAsync(db, seed.Event.Id, "Dup", "dup@example.test");

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };
        var model = NewModel(db, http);

        var result = await model.OnPostSamePersonAsync(dup.Id, primary.Id, default);

        Assert.IsType<RedirectToPageResult>(result);
        var refreshed = await db.Participants.FirstAsync(p => p.Id == dup.Id);
        Assert.Equal(primary.Id, refreshed.SamePersonAsId);
    }

    [Fact]
    public async Task SamePerson_self_reference_is_rejected()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var sam = await AddSpeakerAsync(db, seed.Event.Id, "Sam", "sam@example.test");

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };
        var model = NewModel(db, http);

        await model.OnPostSamePersonAsync(sam.Id, sam.Id, default);

        var refreshed = await db.Participants.FirstAsync(p => p.Id == sam.Id);
        Assert.Null(refreshed.SamePersonAsId);
    }

    [Fact]
    public async Task SamePerson_chain_is_rejected()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var primary = await AddSpeakerAsync(db, seed.Event.Id, "Primary", "primary@example.test");
        var middle = await AddSpeakerAsync(db, seed.Event.Id, "Middle", "middle@example.test");
        var third = await AddSpeakerAsync(db, seed.Event.Id, "Third", "third@example.test");

        // middle is already a duplicate of primary.
        middle.SamePersonAsId = primary.Id;
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };
        var model = NewModel(db, http);

        // linking third -> middle (a non-primary) must be rejected (no chains).
        await model.OnPostSamePersonAsync(third.Id, middle.Id, default);

        var refreshed = await db.Participants.FirstAsync(p => p.Id == third.Id);
        Assert.Null(refreshed.SamePersonAsId);
    }

    [Fact]
    public async Task SamePerson_clear_removes_the_link()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var primary = await AddSpeakerAsync(db, seed.Event.Id, "Primary", "primary@example.test");
        var dup = await AddSpeakerAsync(db, seed.Event.Id, "Dup", "dup@example.test");
        dup.SamePersonAsId = primary.Id;
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };
        var model = NewModel(db, http);

        await model.OnPostClearSamePersonAsync(dup.Id, default);

        var refreshed = await db.Participants.FirstAsync(p => p.Id == dup.Id);
        Assert.Null(refreshed.SamePersonAsId);
    }

    [Fact]
    public async Task Override_upsert_then_clear()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var sam = await AddSpeakerAsync(db, seed.Event.Id, "Sam", "sam@example.test");

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };
        var model = NewModel(db, http);

        // Upsert a force-exclude override.
        await model.OnPostOverrideAsync(sam.Id, OrderItem.Polo, "exclude", default);
        var ov = await db.ParticipantOrderOverrides.FirstAsync(
            o => o.ParticipantId == sam.Id && o.Item == OrderItem.Polo);
        Assert.False(ov.Include);

        // Update the same row to force-include (one row per item).
        await model.OnPostOverrideAsync(sam.Id, OrderItem.Polo, "include", default);
        Assert.Single(db.ParticipantOrderOverrides.Where(
            o => o.ParticipantId == sam.Id && o.Item == OrderItem.Polo));
        ov = await db.ParticipantOrderOverrides.FirstAsync(
            o => o.ParticipantId == sam.Id && o.Item == OrderItem.Polo);
        Assert.True(ov.Include);

        // Clear (default) deletes the row.
        await model.OnPostOverrideAsync(sam.Id, OrderItem.Polo, "default", default);
        Assert.Empty(db.ParticipantOrderOverrides.Where(
            o => o.ParticipantId == sam.Id && o.Item == OrderItem.Polo));
    }

    [Fact]
    public async Task BoothMember_toggle_persists()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var sponsor = new Participant
        {
            EventId = seed.Event.Id, FullName = "Sandra Sponsor", Email = "sandra@example.test",
            Role = ParticipantRole.Sponsor, IsActive = true, IsBoothMember = false,
        };
        db.Participants.Add(sponsor);
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };
        var model = NewModel(db, http);

        await model.OnPostBoothMemberAsync(sponsor.Id, isBoothMember: true, default);
        Assert.True((await db.Participants.FirstAsync(p => p.Id == sponsor.Id)).IsBoothMember);

        await model.OnPostBoothMemberAsync(sponsor.Id, isBoothMember: false, default);
        Assert.False((await db.Participants.FirstAsync(p => p.Id == sponsor.Id)).IsBoothMember);
    }

    [Fact]
    public async Task Counts_reflect_a_force_exclude_override()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var sam = await AddSpeakerAsync(db, seed.Event.Id, "Sam", "sam@example.test",
            funding: SpeakerFunding.Supported); // Supported speaker is entitled to Polo

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };

        // Baseline: Sam counts toward Polo (organizer also has Polo).
        var m1 = NewModel(db, http);
        await m1.OnGetAsync(default);
        var poloBefore = m1.Counts[OrderItem.Polo];

        // Force-exclude Polo for Sam.
        await NewModel(db, http).OnPostOverrideAsync(sam.Id, OrderItem.Polo, "exclude", default);

        var m2 = NewModel(db, http);
        await m2.OnGetAsync(default);
        var poloAfter = m2.Counts[OrderItem.Polo];

        Assert.Equal(poloBefore - 1, poloAfter);
    }

    [Fact]
    public async Task SamePerson_duplicate_is_not_double_counted()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var primary = await AddSpeakerAsync(db, seed.Event.Id, "Primary", "primary@example.test",
            funding: SpeakerFunding.Supported);
        var dup = await AddSpeakerAsync(db, seed.Event.Id, "Dup", "dup@example.test",
            funding: SpeakerFunding.Supported);

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };

        var m1 = NewModel(db, http);
        await m1.OnGetAsync(default);
        var poloTwoPeople = m1.Counts[OrderItem.Polo];

        // Mark dup as the same physical person as primary.
        await NewModel(db, http).OnPostSamePersonAsync(dup.Id, primary.Id, default);

        var m2 = NewModel(db, http);
        await m2.OnGetAsync(default);
        var poloOnePerson = m2.Counts[OrderItem.Polo];

        Assert.Equal(poloTwoPeople - 1, poloOnePerson);
    }
}
