using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using CommunityHub.Core.Integrations;
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
///   • setting the §299 6.1 Category (+ Guest hotel nights) persists, clearing
///     re-marks the speaker uncategorized, and nights are cleared for non-Guest;
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
        return new SpeakerReviewModel(db, accessor, TimeProvider.System)
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
        SpeakerCategory? category = SpeakerCategory.Community,
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
            EventId = eventId, ParticipantId = p.Id, Category = category,
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
    public async Task Setting_category_persists_and_uncategorized_rows_are_flagged()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var sam = await AddSpeakerAsync(db, seed.Event.Id, "Sam", "sam@example.test",
            category: null);   // fresh import = uncategorized

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };

        // The review row surfaces the uncategorized state (null Category).
        var m0 = NewModel(db, http);
        await m0.OnGetAsync(default);
        Assert.Null(Assert.Single(m0.Speakers).Category);

        var result = await NewModel(db, http)
            .OnPostCategoryAsync(sam.Id, SpeakerCategory.Sponsor, guestFundedNights: null, default);

        Assert.IsType<RedirectToPageResult>(result);
        var profile = await db.SpeakerProfiles.FirstAsync(s => s.ParticipantId == sam.Id);
        Assert.Equal(SpeakerCategory.Sponsor, profile.Category);
    }

    [Fact]
    public async Task Guest_category_stores_organizer_entered_nights_and_clearing_resets_them()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var gwen = await AddSpeakerAsync(db, seed.Event.Id, "Gwen Guest", "gwen@example.test",
            category: null);

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };

        // Guest + organizer-entered ELDK-funded nights save in one post (§299 6.3).
        await NewModel(db, http)
            .OnPostCategoryAsync(gwen.Id, SpeakerCategory.Guest, guestFundedNights: 3, default);
        var profile = await db.SpeakerProfiles.FirstAsync(s => s.ParticipantId == gwen.Id);
        Assert.Equal(SpeakerCategory.Guest, profile.Category);
        Assert.Equal(3, profile.GuestFundedNights);

        // Switching away from Guest clears the stale nights value.
        await NewModel(db, http)
            .OnPostCategoryAsync(gwen.Id, SpeakerCategory.Community, guestFundedNights: 3, default);
        profile = await db.SpeakerProfiles.FirstAsync(s => s.ParticipantId == gwen.Id);
        Assert.Equal(SpeakerCategory.Community, profile.Category);
        Assert.Null(profile.GuestFundedNights);

        // Clearing the category ("(not set)") marks the speaker uncategorized again.
        await NewModel(db, http)
            .OnPostCategoryAsync(gwen.Id, category: null, guestFundedNights: null, default);
        profile = await db.SpeakerProfiles.FirstAsync(s => s.ParticipantId == gwen.Id);
        Assert.Null(profile.Category);
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
    public async Task BoothMember_toggle_persists_for_an_exhibitor_company()
    {
        // §299 7.4: the toggle now requires the company to actually HAVE a booth
        // (SponsorPackage >= Gold) — seeded here so the happy path still round-trips.
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var sponsor = new Participant
        {
            EventId = seed.Event.Id, FullName = "Sandra Sponsor", Email = "sandra@example.test",
            Role = ParticipantRole.Sponsor, IsActive = true, IsBoothMember = false,
            SponsorCompanyId = "co-booth",
        };
        db.Participants.Add(sponsor);
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = seed.Event.Id, SponsorCompanyId = "co-booth",
            SponsorPackage = SponsorPackage.Gold, Tier = BoothTier.Gold,
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };
        var model = NewModel(db, http);

        await model.OnPostBoothMemberAsync(sponsor.Id, isBoothMember: true, default);
        Assert.True((await db.Participants.FirstAsync(p => p.Id == sponsor.Id)).IsBoothMember);

        await model.OnPostBoothMemberAsync(sponsor.Id, isBoothMember: false, default);
        Assert.False((await db.Participants.FirstAsync(p => p.Id == sponsor.Id)).IsBoothMember);
    }

    [Fact]
    public async Task BoothMember_toggle_is_refused_for_a_digital_only_sponsor()
    {
        // §299 7.4 hard constraint: a non-exhibitor (no-booth) sponsor cannot have a booth
        // member — the assignment is refused at POST time, not just hidden in the GUI.
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var sponsor = new Participant
        {
            EventId = seed.Event.Id, FullName = "Dana Digital", Email = "dana@example.test",
            Role = ParticipantRole.Sponsor, IsActive = true, IsBoothMember = false,
            SponsorCompanyId = "co-digital",
        };
        db.Participants.Add(sponsor);
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = seed.Event.Id, SponsorCompanyId = "co-digital",
            SponsorPackage = SponsorPackage.Silver, Tier = BoothTier.None,
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };
        var model = NewModel(db, http);

        await model.OnPostBoothMemberAsync(sponsor.Id, isBoothMember: true, default);
        Assert.False((await db.Participants.FirstAsync(p => p.Id == sponsor.Id)).IsBoothMember);

        // A sponsor contact with NO company/SponsorInfo at all is refused too (no booth).
        var orphan = new Participant
        {
            EventId = seed.Event.Id, FullName = "Nora NoCompany", Email = "nora@example.test",
            Role = ParticipantRole.Sponsor, IsActive = true, IsBoothMember = false,
        };
        db.Participants.Add(orphan);
        await db.SaveChangesAsync();
        await model.OnPostBoothMemberAsync(orphan.Id, isBoothMember: true, default);
        Assert.False((await db.Participants.FirstAsync(p => p.Id == orphan.Id)).IsBoothMember);
    }

    [Fact]
    public async Task Counts_reflect_a_force_exclude_override()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var sam = await AddSpeakerAsync(db, seed.Event.Id, "Sam", "sam@example.test",
            category: SpeakerCategory.Community); // Community speaker is entitled to Polo

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };

        // §327j: the page no longer SHOWS the counts — they moved to the logistics pages.
        // What must still hold is that classifying HERE changes them, so the assertion now
        // reads OrderCountService directly: the same authority those pages use, tested
        // without going through a view that no longer displays it.
        var counts = new OrderCountService(db);

        // Baseline: Sam counts toward Polo (organizer also has Polo).
        var poloBefore = (await counts.CountsAsync(seed.Event.Id, default))[OrderItem.Polo];

        // Force-exclude Polo for Sam.
        await NewModel(db, http).OnPostOverrideAsync(sam.Id, OrderItem.Polo, "exclude", default);

        var poloAfter = (await counts.CountsAsync(seed.Event.Id, default))[OrderItem.Polo];

        Assert.Equal(poloBefore - 1, poloAfter);
    }

    [Fact]
    public async Task SamePerson_duplicate_is_not_double_counted()
    {
        using var db = NewDb();
        var seed = await SeedEventAsync(db);
        var primary = await AddSpeakerAsync(db, seed.Event.Id, "Primary", "primary@example.test",
            category: SpeakerCategory.Community);
        var dup = await AddSpeakerAsync(db, seed.Event.Id, "Dup", "dup@example.test",
            category: SpeakerCategory.Community);

        var http = new DefaultHttpContext { User = Session(seed.Organizer) };

        // §327j: counts read from OrderCountService — see the note above.
        var counts = new OrderCountService(db);
        var poloTwoPeople = (await counts.CountsAsync(seed.Event.Id, default))[OrderItem.Polo];

        // Mark dup as the same physical person as primary.
        await NewModel(db, http).OnPostSamePersonAsync(dup.Id, primary.Id, default);

        var poloOnePerson = (await counts.CountsAsync(seed.Event.Id, default))[OrderItem.Polo];

        Assert.Equal(poloTwoPeople - 1, poloOnePerson);
    }
}
