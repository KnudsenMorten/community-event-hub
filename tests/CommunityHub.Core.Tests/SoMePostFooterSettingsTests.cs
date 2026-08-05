using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §824.19 — the three per-edition post-footer values, and the guard that stops them being wiped.
/// </summary>
/// <remarks>
/// 🔒 The behaviour worth pinning is the <b>opt-in</b>: <c>SaveAsync</c> already had six
/// unconditional fields, and adding three more as plain optional parameters would mean any existing
/// caller that saves the LinkedIn wiring silently blanks the tag block it knows nothing about. The
/// flag makes "I own this copy" something a caller states rather than something it does by accident.
/// </remarks>
public sealed class SoMePostFooterSettingsTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"some-footer-{Guid.NewGuid():N}").Options);

    private static SoMeSettingsService Svc(CommunityHubDbContext db) => new(db, new FixedClock(Now));

    private static Task SaveWiringOnlyAsync(SoMeSettingsService svc) =>
        svc.SaveAsync(EventId, enabled: true, companyPageUrlOrOrgId: "98360537",
            speakerPreAlertOrganizerEmail: null, notificationEmails: null,
            notifyOnPublish: true, byEmail: "org@x.dk");

    [Fact]
    public async Task The_three_values_are_stored_when_the_caller_says_it_owns_them()
    {
        using var db = NewDb();
        var svc = Svc(db);

        await svc.SaveAsync(EventId, true, "98360537", null, null, true, "org@x.dk", default,
            eventSystemUrl: "https://eldk27.expertslive.dk",
            eventTags: "#ELDK27 #ExpertsLiveDK",
            organizerCredits: "Morten Waltorp Knudsen [MVP] | Martin Byskov",
            updatePostCopy: true);

        var row = await svc.GetOrDefaultAsync(EventId);
        Assert.Equal("https://eldk27.expertslive.dk", row.EventSystemUrl);
        Assert.Equal("#ELDK27 #ExpertsLiveDK", row.EventTags);
        Assert.Equal("Morten Waltorp Knudsen [MVP] | Martin Byskov", row.OrganizerCredits);
    }

    [Fact]
    public async Task Saving_the_linkedin_wiring_does_NOT_wipe_the_post_footer()
    {
        using var db = NewDb();
        var svc = Svc(db);
        await svc.SaveAsync(EventId, true, "98360537", null, null, true, "org@x.dk", default,
            "https://eldk27.expertslive.dk", "#ELDK27", "Organizer One", updatePostCopy: true);

        // 🔒 THE REGRESSION THIS EXISTS FOR. A caller that knows nothing about post copy saves the
        // wiring; the tag block and credit line must survive untouched.
        await SaveWiringOnlyAsync(svc);

        var row = await svc.GetOrDefaultAsync(EventId);
        Assert.Equal("https://eldk27.expertslive.dk", row.EventSystemUrl);
        Assert.Equal("#ELDK27", row.EventTags);
        Assert.Equal("Organizer One", row.OrganizerCredits);
    }

    [Fact]
    public async Task Clearing_a_field_deliberately_DOES_clear_it()
    {
        using var db = NewDb();
        var svc = Svc(db);
        await svc.SaveAsync(EventId, true, "98360537", null, null, true, "org@x.dk", default,
            "https://eldk27.expertslive.dk", "#ELDK27", "Organizer One", updatePostCopy: true);

        // Blank IS meaningful once the caller owns the copy — it is how he removes the tag block.
        await svc.SaveAsync(EventId, true, "98360537", null, null, true, "org@x.dk", default,
            "https://eldk27.expertslive.dk", "", "Organizer One", updatePostCopy: true);

        Assert.Null((await svc.GetOrDefaultAsync(EventId)).EventTags);
    }

    [Fact]
    public async Task The_resolver_reads_exactly_what_the_settings_page_wrote()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            VenueName = "Bella Center", StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();

        await Svc(db).SaveAsync(EventId, true, "98360537", null, null, true, "org@x.dk", default,
            "https://eldk27.expertslive.dk", "#ELDK27 #ExpertsLiveDK", "Organizer One | Two",
            updatePostCopy: true);

        // End to end: the page writes it, the composer reads it. Without this the two halves could
        // drift on a field name and nothing would fail until a post published with a hole in it.
        var v = await new SoMeVariableResolver(db).EditionValuesAsync(EventId);
        Assert.Equal("https://eldk27.expertslive.dk", v["EventSystemUrl"]);
        Assert.Equal("#ELDK27 #ExpertsLiveDK", v["EventTags"]);
        Assert.Equal("Organizer One | Two", v["OrganizerLinkedInUrls"]);
    }

    [Fact]
    public async Task Whitespace_only_input_counts_as_empty_rather_than_as_a_blank_footer_line()
    {
        using var db = NewDb();
        var svc = Svc(db);

        await svc.SaveAsync(EventId, true, "98360537", null, null, true, "org@x.dk", default,
            "   ", "\t", " ", updatePostCopy: true);

        var row = await svc.GetOrDefaultAsync(EventId);
        // Stored as null, so the renderer treats it as absent and closes the gap — rather than
        // publishing a post whose last line is a lone space.
        Assert.Null(row.EventSystemUrl);
        Assert.Null(row.EventTags);
        Assert.Null(row.OrganizerCredits);
    }
}
