using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §824.16 — filling a SoMe template's variables from CEH's own data.
/// </summary>
/// <remarks>
/// 🔒 The rule under test throughout: <b>every value degrades to EMPTY rather than to a guess.</b> A
/// missing track, an untagged sponsor, an edition with no tag block — all ordinary. An invented tier
/// or a wrong company name on a post to 400+ followers is worse than a shorter post.
/// </remarks>
public sealed class SoMeVariableResolverTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"some-vars-{Guid.NewGuid():N}").Options);

    private static async Task SeedEditionAsync(CommunityHubDbContext db, bool withSettings = true)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "Experts Live Denmark",
            DisplayName = "Experts Live Denmark 2027", VenueName = "Bella Center, Copenhagen",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        if (withSettings)
        {
            db.SoMeSettings.Add(new SoMeSettings
            {
                EventId = EventId, Enabled = true,
                EventSystemUrl = "https://eldk27.expertslive.dk",
                EventTags = "#ELDK27 #ExpertsLiveDK",
                OrganizerCredits = "Morten Waltorp Knudsen [MVP] | Martin Byskov",
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Edition_values_carry_the_footer_the_dates_and_the_venue()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);

        var v = await new SoMeVariableResolver(db).EditionValuesAsync(EventId);

        Assert.Equal("ELDK27", v["EditionCode"]);
        Assert.Equal("Bella Center, Copenhagen", v["EventVenue"]);
        Assert.Equal("https://eldk27.expertslive.dk", v["EventSystemUrl"]);
        Assert.Equal("#ELDK27 #ExpertsLiveDK", v["EventTags"]);
        Assert.Equal("Morten Waltorp Knudsen [MVP] | Martin Byskov", v["OrganizerLinkedInUrls"]);
        // ⚠️ §904 — this used to read "9+10 February 2027", citing his 2026 phrasing
        // ("24+25th February 2026"). It was a real preference, so it is worth saying why it moved:
        // the ELDK27 deck he wrote himself says "9–10 February 2027" in ALL 21 posts that carry the
        // dates, and those posts now take the string from HERE (§904 tokenised the deck). The newer
        // and far more numerous evidence wins. 🔒 One character in FormatDates puts "+" back.
        Assert.Equal("9–10 February 2027", v["EventDates"]);
    }

    [Fact]
    public async Task An_edition_with_no_SoMe_settings_yields_nulls_not_invented_defaults()
    {
        using var db = NewDb();
        await SeedEditionAsync(db, withSettings: false);

        var v = await new SoMeVariableResolver(db).EditionValuesAsync(EventId);

        // The renderer closes the gaps these leave; a hard-coded fallback URL or tag block would put
        // last edition's hashtags on this edition's posts.
        Assert.Null(v["EventSystemUrl"]);
        Assert.Null(v["EventTags"]);
        Assert.Null(v["OrganizerLinkedInUrls"]);
        Assert.Equal("ELDK27", v["EditionCode"]);   // …but what IS known still resolves.
    }

    [Fact]
    public async Task A_sponsor_we_hold_no_org_id_for_is_named_in_plain_text()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "26", CompanyName = "Glueckkanja",
            SponsorPackage = SponsorPackage.Platinum,
            SocialMediaIntro = "A Microsoft security specialist.",
            WebsiteUrl = "https://www.glueckkanja.com/",
            LinkedInUrl = "https://www.linkedin.com/company/glueckkanja",   // vanity slug, no id
        });
        await db.SaveChangesAsync();

        var v = await new SoMeVariableResolver(db).SponsorValuesAsync(EventId, "26");

        // 🔒 Exactly how the ELDK26 posts read. An untagged sponsor produces the post he has already
        // published dozens of times, not a visibly degraded one.
        Assert.Equal("Glueckkanja", v["SponsorLinkedInUrl"]);
        Assert.Equal("Platinum", v["SponsorTier"]);
        Assert.Equal("#Glueckkanja", v["SponsorHashtag"]);
        Assert.Equal("glueckkanja.com", v["SponsorWebsite"]);
        Assert.Equal("A Microsoft security specialist.", v["SponsorSocialMediaCompanyDescription"]);
    }

    [Fact]
    public async Task A_sponsor_with_a_stored_org_id_becomes_a_real_mention()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "26", CompanyName = "Glueckkanja",
            SponsorPackage = SponsorPackage.Platinum,
            LinkedInOrganizationId = "12345",
            LinkedInUrl = "https://www.linkedin.com/company/glueckkanja",
        });
        await db.SaveChangesAsync();

        var v = await new SoMeVariableResolver(db).SponsorValuesAsync(EventId, "26");

        Assert.Equal("@[Glueckkanja](urn:li:organization:12345)", v["SponsorLinkedInUrl"]);
    }

    [Fact]
    public async Task A_track_lists_its_speakers_and_an_empty_track_lists_nobody()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);
        var a = new Participant { EventId = EventId, Email = "a@x.dk", FullName = "Anna Speaker", IsActive = true };
        var b = new Participant { EventId = EventId, Email = "b@x.dk", FullName = "Bo Speaker", IsActive = true };
        db.Participants.AddRange(a, b);
        await db.SaveChangesAsync();
        var s = new Session { EventId = EventId, SessionizeId = "s1", Title = "AI deep dive", Track = "AI" };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        db.SessionSpeakers.AddRange(
            new SessionSpeaker { SessionId = s.Id, ParticipantId = a.Id },
            new SessionSpeaker { SessionId = s.Id, ParticipantId = b.Id });
        await db.SaveChangesAsync();

        var svc = new SoMeVariableResolver(db);

        var ai = await svc.TrackValuesAsync(EventId, "AI");
        Assert.Equal("AI", ai["TrackName"]);
        Assert.Equal("Anna Speaker | Bo Speaker", ai["SpeakerNames"]);

        // A track nobody is on yet: the line simply does not print, and the post is still correct.
        var empty = await svc.TrackValuesAsync(EventId, "Security");
        Assert.Null(empty["SpeakerNames"]);
    }

    [Fact]
    public async Task A_tier_lists_its_companies_and_tags_only_the_ones_we_can_tag()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);
        db.SponsorInfos.AddRange(
            new SponsorInfo { EventId = EventId, SponsorCompanyId = "1", CompanyName = "Alpha",
                              SponsorPackage = SponsorPackage.Gold, LinkedInOrganizationId = "111" },
            new SponsorInfo { EventId = EventId, SponsorCompanyId = "2", CompanyName = "Beta",
                              SponsorPackage = SponsorPackage.Gold },
            new SponsorInfo { EventId = EventId, SponsorCompanyId = "3", CompanyName = "Gamma",
                              SponsorPackage = SponsorPackage.Silver });
        await db.SaveChangesAsync();

        var v = await new SoMeVariableResolver(db).SponsorTierValuesAsync(EventId, SponsorPackage.Gold);

        Assert.Equal("Gold", v["SponsorTier"]);
        // Mixed on purpose: tagging is per sponsor, so one post can carry both forms.
        Assert.Equal("@[Alpha](urn:li:organization:111) | Beta", v["SponsorList"]);
        Assert.DoesNotContain("Gamma", v["SponsorList"]!);   // a different tier
    }

    [Fact]
    public async Task An_unknown_sponsor_resolves_to_nothing_rather_than_throwing()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);

        // A post composed for a sponsor that has since been removed must not take the run down.
        var v = await new SoMeVariableResolver(db).SponsorValuesAsync(EventId, "does-not-exist");

        Assert.Empty(v);
    }

    [Theory]
    // §904 — an EN DASH. This asserted "9+10 February 2027" under a test name saying dates read the
    // way a person writes them, which is the one thing a plus sign does not do.
    [InlineData(2027, 2, 9, 2027, 2, 10, "9–10 February 2027")]
    [InlineData(2027, 2, 9, 2027, 2, 9, "9 February 2027")]
    [InlineData(2027, 1, 31, 2027, 2, 1, "31 January - 1 February 2027")]
    public void Dates_read_the_way_a_person_writes_them(
        int y1, int m1, int d1, int y2, int m2, int d2, string expected)
    {
        // Invariant culture on purpose — the post is English whatever the server's locale, and a
        // Danish month name in an English sentence is what makes an automated post look automated.
        Assert.Equal(expected,
            SoMeVariableResolver.FormatDates(new DateOnly(y1, m1, d1), new DateOnly(y2, m2, d2)));
    }

    [Theory]
    [InlineData("https://www.truesec.com/", "truesec.com")]
    [InlineData("http://truesec.com", "truesec.com")]
    [InlineData(null, null)]
    public void The_website_prints_as_a_domain_not_a_url(string? input, string? expected)
        => Assert.Equal(expected, SoMeVariableResolver.TrimScheme(input));

    [Theory]
    [InlineData("Truesec", "#Truesec")]
    [InlineData("System Center Dudes", "#SystemCenterDudes")]
    [InlineData("2linkIT ApS", "#2linkITApS")]
    [InlineData("   ", null)]
    public void The_sponsor_hashtag_drops_everything_that_would_break_it(string name, string? expected)
        => Assert.Equal(expected, SoMeVariableResolver.Hashtag(name));
}
