using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1379.8 — "Refresh weekly" WordPress posts: the token renderer (every token, rounding, unknown tokens
/// kept), the data it is fed (test users / test sessions excluded, everyone who speaks counted), and the
/// refresh run (only flagged posts, unchanged hash not re-sent, 7-day cadence, guard / feature off ⇒ no
/// write). Fake data only; the WordPress publisher is a recording fake.
/// </summary>
public sealed class WordPressPostRefreshTests
{
    private const int EventId = 1379;
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- fakes

    private sealed class RecordingPublisher : IWordPressPublisher
    {
        public bool CanWrite { get; set; } = true;
        public bool Succeed { get; set; } = true;
        public List<(long PostId, string Html)> Updates { get; } = new();

        public Task<WordPressPublishResult> CreateDraftAsync(WordPressDraft draft, CancellationToken ct = default) =>
            throw new InvalidOperationException("The refresh must never create a post.");

        public Task<WordPressPublishResult> UpdatePostContentAsync(long postId, string contentHtml, CancellationToken ct = default)
        {
            Updates.Add((postId, contentHtml));
            return Task.FromResult(Succeed
                ? new WordPressPublishResult(true, postId, null, $"Post {postId} updated.")
                : new WordPressPublishResult(false, postId, null, "WordPress returned 500."));
        }
    }

    private static CommunityHubDbContext NewDb()
    {
        var db = new CommunityHubDbContext(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"wp-refresh-{Guid.NewGuid():N}").Options);
        db.Events.Add(new Event
        {
            Id = EventId, Code = "WP27", CommunityName = "C", DisplayName = "WP 2027",
            StartDate = new DateOnly(2027, 6, 9), EndDate = new DateOnly(2027, 6, 10), IsActive = true,
        });
        db.SaveChanges();
        return db;
    }

    private static WordPressPostDataService Data(CommunityHubDbContext db, string hubUrl = "https://hub.example.test") =>
        new(db, Options.Create(new EmailTemplateOptions { HubUrl = hubUrl }));

    private static WordPressPostRefreshService Service(
        CommunityHubDbContext db, RecordingPublisher wp, IExternalWriteGuard? guard = null, FeatureGateService? gate = null) =>
        new(db, Data(db), wp, guard, gate);

    private static WordPressPostData SampleData(
        IReadOnlyList<WordPressPostTrack>? tracks = null,
        IReadOnlyList<WordPressPostCompany>? sponsors = null) =>
        new(SpeakerCount: 92, CountryCount: 14, MvpCount: 52, RdCount: 2, MicrosoftEmployeeCount: 13,
            MicrosoftExpertCount: 8, SessionCount: 74, MasterClassCount: 6, PanelCount: 3, TrackCount: 8,
            SponsorCount: 30, ExhibitorCount: 24,
            SpeakersByTrack: tracks ?? Array.Empty<WordPressPostTrack>(),
            Sponsors: sponsors ?? Array.Empty<WordPressPostCompany>(),
            Exhibitors: Array.Empty<WordPressPostCompany>(),
            MvpOrRdCount: 54, FemaleSpeakerCount: 19, MaleSpeakerCount: 71);

    private static Participant Person(CommunityHubDbContext db, string name, ParticipantRole role,
        bool test = false, bool active = true)
    {
        var p = new Participant
        {
            EventId = EventId, FullName = name, Email = $"{name.Replace(' ', '.').ToLowerInvariant()}@x.test",
            Role = role, IsTestUser = test, IsActive = active,
        };
        db.Participants.Add(p);
        db.SaveChanges();
        return p;
    }

    private static Session NewSession(CommunityHubDbContext db, string title, string? track,
        SessionType type = SessionType.TechnicalSession, bool testSession = false, bool service = false,
        params Participant[] speakers)
    {
        var s = new Session
        {
            EventId = EventId, Title = title, SessionizeId = Guid.NewGuid().ToString("N"), Track = track,
            Type = type, UsedForTesting = testSession, IsServiceSession = service,
        };
        db.Sessions.Add(s);
        db.SaveChanges();
        foreach (var sp in speakers) db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = sp.Id });
        db.SaveChanges();
        return s;
    }

    private static WordPressRefreshPost Post(CommunityHubDbContext db, long postId, string template,
        bool flagged = true, DateTimeOffset? lastRefreshed = null, string? hash = null)
    {
        var row = new WordPressRefreshPost
        {
            EventId = EventId, WordPressPostId = postId, Label = $"Post {postId}", BodyTemplate = template,
            RefreshWeekly = flagged, LastRefreshedAt = lastRefreshed, LastPushedHash = hash,
        };
        db.WordPressRefreshPosts.Add(row);
        db.SaveChanges();
        return row;
    }

    // ---------------------------------------------------------------- renderer

    [Theory]
    [InlineData("{{SpeakerCount}}", "92")]
    [InlineData("{{SpeakerCountRounded}}", "90+")]
    [InlineData("{{CountryCount}}", "14")]
    [InlineData("{{MvpRdCount}}", "54")]
    [InlineData("{{FemaleSpeakerCount}}", "19")]
    [InlineData("{{MaleSpeakerCount}}", "71")]
    [InlineData("{{MvpRdCountPercent}}", "59")]          // 54 / 92 = 58.7 → 59
    [InlineData("{{FemaleSpeakerCountPercent}}", "21")]  // 19 / 92 = 20.65 → 21
    [InlineData("{{MicrosoftCountPercent}}", "14")]      // 13 / 92 = 14.1 → 14
    [InlineData("{{MvpCount}}", "52")]
    [InlineData("{{RdCount}}", "2")]
    [InlineData("{{MicrosoftCount}}", "13")]
    [InlineData("{{MicrosoftExpertCount}}", "8")]
    [InlineData("{{SessionCount}}", "74")]
    [InlineData("{{MasterClassCount}}", "6")]
    [InlineData("{{PanelCount}}", "3")]
    [InlineData("{{TrackCount}}", "8")]
    [InlineData("{{SponsorCount}}", "30")]
    [InlineData("{{ExhibitorCount}}", "24")]
    [InlineData("{{ ExhibitorCount }}", "24")]
    public void Each_count_token_renders_its_value(string template, string expected)
    {
        var r = WordPressPostTokenRenderer.Render($"<p>{template}</p>", SampleData());

        Assert.Equal($"<p>{expected}</p>", r.Html);
        Assert.Empty(r.UnknownTokens);
    }

    [Theory]
    [InlineData(92, "90+")]
    [InlineData(90, "90+")]
    [InlineData(109, "100+")]
    [InlineData(10, "10+")]
    [InlineData(7, "7")]
    [InlineData(0, "0")]
    public void The_rounded_headline_floors_to_tens(int n, string expected) =>
        Assert.Equal(expected, WordPressPostTokenRenderer.Rounded(n));

    [Fact]
    public void An_unknown_token_is_left_as_typed_and_reported_once()
    {
        var r = WordPressPostTokenRenderer.Render("{{SpeakerCount}} {{Speakercount}} and {{Speakercount}}", SampleData());

        Assert.Equal("92 {{Speakercount}} and {{Speakercount}}", r.Html);
        Assert.Equal(new[] { "Speakercount" }, r.UnknownTokens);
    }

    [Fact]
    public void Speakers_by_track_renders_a_heading_and_list_per_track_and_encodes_names()
    {
        var tracks = new[]
        {
            new WordPressPostTrack("Security", new[]
            {
                new WordPressPostSpeaker("Ann <b>A</b>", "https://hub.example.test/Speakers/1"),
                new WordPressPostSpeaker("Bo B", null),
            }),
            new WordPressPostTrack("Empty", Array.Empty<WordPressPostSpeaker>()),
        };

        var html = WordPressPostTokenRenderer.Render("{{SpeakersByTrack}}", SampleData(tracks)).Html;

        Assert.Contains("<h3>Security</h3>", html);
        Assert.Contains("<a href=\"https://hub.example.test/Speakers/1\">Ann &lt;b&gt;A&lt;/b&gt;</a>", html);
        Assert.Contains("<li>Bo B</li>", html);
        Assert.DoesNotContain("Empty", html);
    }

    [Fact]
    public void The_sponsor_list_links_only_an_http_website()
    {
        var sponsors = new[]
        {
            new WordPressPostCompany("Contoso", "https://contoso.example"),
            new WordPressPostCompany("Fabrikam", null),
            new WordPressPostCompany("Evil", "javascript:alert(1)"),
        };

        var html = WordPressPostTokenRenderer.Render("{{SponsorList}}", SampleData(sponsors: sponsors)).Html;

        Assert.Contains("<a href=\"https://contoso.example\"", html);
        Assert.Contains("<li>Fabrikam</li>", html);
        Assert.Contains("<li>Evil</li>", html);
        Assert.DoesNotContain("javascript:", html);
    }

    // ---------------------------------------------------------------- data

    [Fact]
    public async Task The_speaker_count_is_everyone_who_speaks_minus_test_users_and_test_sessions()
    {
        using var db = NewDb();
        var speaker = Person(db, "Speaker One", ParticipantRole.Speaker);           // counted (role)
        Person(db, "Speaker NoSession", ParticipantRole.Speaker);                   // counted (role, no session)
        var organizer = Person(db, "Org Talks", ParticipantRole.Organizer);         // counted (linked)
        var onTestOnly = Person(db, "Org TestOnly", ParticipantRole.Organizer);     // NOT: only on a TEST session
        var testUser = Person(db, "Test Speaker", ParticipantRole.Speaker, test: true); // NOT: test user
        Person(db, "Gone Speaker", ParticipantRole.Speaker, active: false);         // NOT: inactive
        Person(db, "Just Volunteer", ParticipantRole.Volunteer);                    // NOT: no session
        NewSession(db, "Real talk", "Security", speakers: new[] { speaker, organizer, testUser });
        NewSession(db, "Test Master Class", "Security", SessionType.MasterClass, testSession: true, speakers: onTestOnly);

        var data = await Data(db).LoadAsync(EventId);

        Assert.Equal(3, data.SpeakerCount);
    }

    [Fact]
    public async Task Accreditation_and_country_counts_come_from_the_counted_speakers_profiles()
    {
        using var db = NewDb();
        var a = Person(db, "A", ParticipantRole.Speaker);
        var b = Person(db, "B", ParticipantRole.Speaker);
        var c = Person(db, "C", ParticipantRole.Speaker);
        var d = Person(db, "D", ParticipantRole.Speaker);
        var t = Person(db, "T", ParticipantRole.Speaker, test: true);
        db.SpeakerProfiles.AddRange(
            new SpeakerProfile { EventId = EventId, ParticipantId = a.Id, Accreditation = "Microsoft MVP", Country = "Denmark" },
            new SpeakerProfile { EventId = EventId, ParticipantId = b.Id, Accreditation = "Microsoft Regional Director", MvpCategories = "Security", Country = "denmark" },
            new SpeakerProfile { EventId = EventId, ParticipantId = c.Id, Accreditation = "Microsoft Employee", Country = "USA" },
            new SpeakerProfile { EventId = EventId, ParticipantId = d.Id, Accreditation = "Microsoft Expert", BackstageCountry = "Sweden" },
            new SpeakerProfile { EventId = EventId, ParticipantId = t.Id, Accreditation = "Microsoft MVP", Country = "Norway" });
        await db.SaveChangesAsync();

        var data = await Data(db).LoadAsync(EventId);

        Assert.Equal(2, data.MvpCount);            // A by accreditation, B by MVP categories; T is a test user
        Assert.Equal(1, data.RdCount);
        Assert.Equal(1, data.MicrosoftEmployeeCount);
        Assert.Equal(1, data.MicrosoftExpertCount);
        Assert.Equal(3, data.CountryCount);        // Denmark (case-insensitive), USA, Sweden — not Norway
    }

    /// <summary>
    /// 🔴 PROD 2026-11-07: {{MvpCount}} = 9 and {{RdCount}} = 0 against ~55 MVPs and 6 RDs — the speaker form saves
    /// accreditation as a COMMA-SEPARATED list, and the count compared the whole string. Every value counts.
    /// </summary>
    [Fact]
    public async Task Accreditation_counts_read_the_comma_separated_list_the_speaker_form_saves()
    {
        using var db = NewDb();
        var a = Person(db, "A", ParticipantRole.Speaker);
        var b = Person(db, "B", ParticipantRole.Speaker);
        var c = Person(db, "C", ParticipantRole.Speaker);
        var d = Person(db, "D", ParticipantRole.Speaker);
        db.SpeakerProfiles.AddRange(
            new SpeakerProfile { EventId = EventId, ParticipantId = a.Id, Accreditation = "Microsoft Expert, Microsoft MVP, Microsoft MCT", Gender = "Female" },
            new SpeakerProfile { EventId = EventId, ParticipantId = b.Id, Accreditation = "Microsoft Expert, Microsoft MVP, Microsoft Regional Director", Gender = "Male" },
            new SpeakerProfile { EventId = EventId, ParticipantId = c.Id, Accreditation = "Microsoft Employee, Microsoft Expert", Gender = " female " },
            new SpeakerProfile { EventId = EventId, ParticipantId = d.Id, Accreditation = "Microsoft MVP,Microsoft Regional Director", Gender = "Prefer not to say" });
        await db.SaveChangesAsync();

        var data = await Data(db).LoadAsync(EventId);

        Assert.Equal(3, data.MvpCount);                 // A, B, D
        Assert.Equal(2, data.RdCount);                  // B, D
        Assert.Equal(3, data.MvpOrRdCount);             // the MERGED figure: A, B, D — B and D are MVP AND RD, counted once
        Assert.Equal(1, data.MicrosoftEmployeeCount);   // C
        Assert.Equal(3, data.MicrosoftExpertCount);     // A, B, C
        Assert.Equal(2, data.FemaleSpeakerCount);       // A, C (case + spaces tolerated)
        Assert.Equal(1, data.MaleSpeakerCount);         // B; D chose "Prefer not to say"
    }

    /// <summary>
    /// 🔴 Operator 2026-11-07 (Kent Agerlund, Morten Knudsen): "both are and must be included". Organizers who speak on
    /// the Welcome/Closing keynotes — sessions kept out of the SoMe campaign by ExcludeFromSoMeAnnouncements — count as
    /// speakers and in the merged MVP/RD figure; the keynote's Track does NOT put them in that track's line-up; a TEST
    /// session still counts nobody.
    /// </summary>
    [Fact]
    public async Task Organizers_on_announcement_excluded_keynotes_count_but_stay_out_of_track_lists()
    {
        using var db = NewDb();
        var speaker = Person(db, "Track Speaker", ParticipantRole.Speaker);
        var kent = Person(db, "Kent Organizer", ParticipantRole.Organizer);
        var morten = Person(db, "Morten Organizer", ParticipantRole.Organizer);
        var onTestOnly = Person(db, "Test Only", ParticipantRole.Organizer);
        db.SpeakerProfiles.AddRange(
            new SpeakerProfile { EventId = EventId, ParticipantId = kent.Id, Accreditation = "Microsoft Expert, Microsoft MVP, Microsoft Regional Director" },
            new SpeakerProfile { EventId = EventId, ParticipantId = morten.Id, Accreditation = "Microsoft Expert, Microsoft MVP, Microsoft MCT" },
            new SpeakerProfile { EventId = EventId, ParticipantId = onTestOnly.Id, Accreditation = "Microsoft MVP" });
        await db.SaveChangesAsync();
        NewSession(db, "Real talk", "Intune", speakers: speaker);
        var welcome = NewSession(db, "Welcome", "Security", SessionType.Keynote, speakers: new[] { kent, morten });
        welcome.ExcludeFromSoMeAnnouncements = true;
        NewSession(db, "Test Master Class", "Security", SessionType.MasterClass, testSession: true, speakers: onTestOnly);
        await db.SaveChangesAsync();

        var data = await Data(db).LoadAsync(EventId);

        Assert.Equal(3, data.SpeakerCount);                      // the track speaker + Kent + Morten; not the test-only one
        Assert.Equal(2, data.MvpOrRdCount);                      // Kent (MVP + RD, once) + Morten
        Assert.Equal(new[] { "Intune" }, data.SpeakersByTrack.Select(t => t.Track));   // no "Security" from the keynote
    }

    /// <summary>
    /// 🔴 PROD 2026-11-07: Kent Agerlund + Morten Knudsen speak as Ring-1 accounts, which §940 keeps test-flagged —
    /// {{MvpRdCount}} said 57 instead of 59. A Ring-1 test-flagged account on a REAL session counts; a test account on
    /// a real session in any other ring, or a Ring-1 account only on a TEST session, does not.
    /// </summary>
    [Fact]
    public async Task A_ring1_organizer_kept_test_flagged_by_940_counts_when_speaking_on_a_real_session()
    {
        using var db = NewDb();
        var organizer = Person(db, "Ring1 Organizer", ParticipantRole.Speaker, test: true);
        organizer.Ring = CommunityHub.Core.Settings.Ring.Ring1;
        var ring1TestOnly = Person(db, "Ring1 TestOnly", ParticipantRole.Organizer, test: true);
        ring1TestOnly.Ring = CommunityHub.Core.Settings.Ring.Ring1;
        var testAccount = Person(db, "Seeded Test Speaker", ParticipantRole.Organizer, test: true);   // Broad ring
        // As on PROD: the keynote also has organizers who are NOT test-flagged — a session whose speakers are ALL test
        // accounts is excluded by §1178, which is right and not what this test is about.
        var coHost = Person(db, "CoHost Organizer", ParticipantRole.Organizer);
        db.SpeakerProfiles.Add(new SpeakerProfile { EventId = EventId, ParticipantId = organizer.Id, Accreditation = "Microsoft MVP, Microsoft Regional Director" });
        await db.SaveChangesAsync();
        NewSession(db, "Welcome", "Security", SessionType.Keynote, speakers: new[] { organizer, testAccount, coHost });
        NewSession(db, "Test Master Class", "Security", SessionType.MasterClass, testSession: true, speakers: ring1TestOnly);

        var data = await Data(db).LoadAsync(EventId);

        Assert.Equal(2, data.SpeakerCount);     // the Ring-1 organizer + the co-host; not the Broad test account / test-only one
        Assert.Equal(1, data.MvpOrRdCount);
    }

    [Fact]
    public async Task Session_counts_exclude_service_and_test_sessions()
    {
        using var db = NewDb();
        var s = Person(db, "S", ParticipantRole.Speaker);
        NewSession(db, "T1", "Security", speakers: s);
        NewSession(db, "T2", "Azure", speakers: s);
        NewSession(db, "T3", "Azure", speakers: s);
        NewSession(db, "Test Session", "Intune", testSession: true, speakers: s);
        NewSession(db, "Lunch", null, service: true);
        NewSession(db, "MC", "Azure", SessionType.MasterClass, speakers: s);
        NewSession(db, "Panel", null, SessionType.PanelDiscussion, speakers: s);

        var data = await Data(db).LoadAsync(EventId);

        Assert.Equal(3, data.SessionCount);
        Assert.Equal(2, data.TrackCount);          // Security + Azure; Intune only had a test session
        Assert.Equal(1, data.MasterClassCount);
        Assert.Equal(1, data.PanelCount);
    }

    [Fact]
    public async Task Speakers_by_track_follows_the_track_order_and_links_only_published_speakers()
    {
        using var db = NewDb();
        var pub = Person(db, "Zed Published", ParticipantRole.Speaker);
        var unpub = Person(db, "Amy Unpublished", ParticipantRole.Speaker);
        var org = Person(db, "Org Speaker", ParticipantRole.Organizer);
        db.SpeakerProfiles.Add(new SpeakerProfile { EventId = EventId, ParticipantId = pub.Id, SelectedForPublish = true });
        await db.SaveChangesAsync();
        NewSession(db, "Cloud", "Azure", speakers: new[] { pub, org });
        NewSession(db, "Zero trust", "Security", speakers: unpub);

        var data = await Data(db).LoadAsync(EventId);

        Assert.Equal(new[] { "Security", "Azure" }, data.SpeakersByTrack.Select(t => t.Track));
        var azure = data.SpeakersByTrack[1].Speakers;
        Assert.Equal(new[] { "Org Speaker", "Zed Published" }, azure.Select(s => s.Name));
        Assert.Equal($"https://hub.example.test/Speakers/{pub.Id}", azure[1].Url);
        Assert.Null(azure[0].Url);                                   // not a Speaker-role public profile
        Assert.Null(data.SpeakersByTrack[0].Speakers.Single().Url);  // not selected for publish
    }

    [Fact]
    public async Task Sponsors_and_exhibitors_use_the_flags_and_drop_test_and_withdrawn_companies()
    {
        using var db = NewDb();
        db.SponsorInfos.AddRange(
            new SponsorInfo { EventId = EventId, SponsorCompanyId = "1", CompanyName = "Contoso", IsSponsor = true, IsExhibitor = true, SponsorPackage = SponsorPackage.Gold, HasCurrentBoothOrder = true },
            new SponsorInfo { EventId = EventId, SponsorCompanyId = "2", CompanyName = "Fabrikam", IsSponsor = true, IsExhibitor = false },
            new SponsorInfo { EventId = EventId, SponsorCompanyId = "3", CompanyName = "Test Co", IsSponsor = true, IsExhibitor = true, IsTestData = true, SponsorPackage = SponsorPackage.Gold, HasCurrentBoothOrder = true },
            new SponsorInfo { EventId = EventId, SponsorCompanyId = "4", CompanyName = "Gone Ltd", IsSponsor = true, Status = SponsorStatus.Withdrawn });
        await db.SaveChangesAsync();

        var data = await Data(db).LoadAsync(EventId);

        Assert.Equal(new[] { "Contoso", "Fabrikam" }, data.Sponsors.Select(c => c.Name));
        Assert.Equal(new[] { "Contoso" }, data.Exhibitors.Select(c => c.Name));
        Assert.Equal(2, data.SponsorCount);
        Assert.Equal(1, data.ExhibitorCount);
    }

    // ---------------------------------------------------------------- refresh run

    [Fact]
    public async Task An_unflagged_post_is_never_written()
    {
        using var db = NewDb();
        Post(db, 100, "<p>{{SpeakerCount}}</p>", flagged: false);
        var wp = new RecordingPublisher();

        var result = await Service(db, wp).RefreshFlaggedPostsAsync(EventId, Now);
        var now = await Service(db, wp).RefreshNowAsync(EventId, db.WordPressRefreshPosts.Single().Id, Now);

        Assert.Empty(wp.Updates);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, now.Updated);
    }

    [Fact]
    public async Task A_flagged_post_is_rendered_and_updated_in_place_then_not_resent_when_unchanged()
    {
        using var db = NewDb();
        Person(db, "S", ParticipantRole.Speaker);
        var row = Post(db, 200, "<p>{{SpeakerCount}} speakers</p>");
        var wp = new RecordingPublisher();

        var first = await Service(db, wp).RefreshFlaggedPostsAsync(EventId, Now);

        Assert.Equal(1, first.Updated);
        Assert.Equal((200L, "<p>1 speakers</p>"), wp.Updates.Single());
        Assert.Equal(Now, row.LastRefreshedAt);
        Assert.NotNull(row.LastPushedHash);

        // A week later, nothing changed in CEH ⇒ not re-sent.
        var second = await Service(db, wp).RefreshFlaggedPostsAsync(EventId, Now.AddDays(8));

        Assert.Equal(1, second.Unchanged);
        Assert.Single(wp.Updates);
        Assert.Equal(Now.AddDays(8), row.LastRefreshedAt);
    }

    [Fact]
    public async Task A_post_refreshed_within_seven_days_is_not_due_but_refresh_now_ignores_the_cadence()
    {
        using var db = NewDb();
        var row = Post(db, 300, "<p>{{SessionCount}}</p>", lastRefreshed: Now.AddDays(-6), hash: "stale");
        var wp = new RecordingPublisher();

        var scheduled = await Service(db, wp).RefreshFlaggedPostsAsync(EventId, Now);
        Assert.Equal(1, scheduled.NotDue);
        Assert.Empty(wp.Updates);

        var due = await Service(db, wp).RefreshFlaggedPostsAsync(EventId, Now.AddDays(1));
        Assert.Equal(1, due.Updated);

        var manual = await Service(db, wp).RefreshNowAsync(EventId, row.Id, Now.AddDays(1).AddMinutes(1));
        Assert.Equal(1, manual.Unchanged);   // same render ⇒ still not re-sent
        Assert.Single(wp.Updates);
    }

    [Fact]
    public async Task The_write_guard_off_means_no_write()
    {
        using var db = NewDb();
        var row = Post(db, 400, "<p>{{SpeakerCount}}</p>");
        var wp = new RecordingPublisher();
        var guard = new ExternalWriteGuard(db, new ExternalWriteOptions
        {
            AllowExternalWrites = true,
            ExternalWrites = new(StringComparer.OrdinalIgnoreCase) { ["WordPressPostRefresh"] = false },
        });

        var result = await Service(db, wp, guard).RefreshFlaggedPostsAsync(EventId, Now);

        Assert.Empty(wp.Updates);
        Assert.Contains("blocked", result.Message);
        Assert.Null(row.LastRefreshedAt);
        Assert.Equal("WordPressPostRefresh", ExternalSystems.ConfigKeyFor(ExternalSystems.WordPressPostRefresh));
    }

    [Fact]
    public async Task The_feature_switched_off_or_an_unconfigured_connector_means_no_write()
    {
        using var db = NewDb();
        Post(db, 500, "<p>{{SpeakerCount}}</p>");
        db.FeatureSettings.Add(new FeatureSetting { EventId = EventId, FeatureKey = ContentStudioService.FeatureKey, Enabled = false });
        await db.SaveChangesAsync();
        var wp = new RecordingPublisher();

        var off = await Service(db, wp, gate: new FeatureGateService(db)).RefreshFlaggedPostsAsync(EventId, Now);
        var unconfigured = await Service(db, new RecordingPublisher { CanWrite = false }).RefreshFlaggedPostsAsync(EventId, Now);

        Assert.Empty(wp.Updates);
        Assert.Contains("turned off", off.Message);
        Assert.Contains("not configured", unconfigured.Message);
    }

    [Fact]
    public async Task A_failed_write_is_recorded_and_retried_next_run()
    {
        using var db = NewDb();
        var row = Post(db, 600, "<p>{{SpeakerCount}}</p>");
        var wp = new RecordingPublisher { Succeed = false };

        var result = await Service(db, wp).RefreshFlaggedPostsAsync(EventId, Now);

        Assert.Equal(1, result.Failed);
        Assert.Null(row.LastRefreshedAt);
        Assert.Null(row.LastPushedHash);
        Assert.Contains("500", row.LastRefreshResult);
    }

    [Fact]
    public async Task Saving_validates_the_post_id_and_editing_the_template_clears_the_pushed_hash()
    {
        using var db = NewDb();
        var svc = Service(db, new RecordingPublisher());

        Assert.NotNull(await svc.SaveAsync(EventId, null, 0, "x", "t", true));
        Assert.Null(await svc.SaveAsync(EventId, null, 10950, "Sponsors", "<p>{{SponsorCount}}</p>", true));
        Assert.NotNull(await svc.SaveAsync(EventId, null, 10950, "Dup", "t", false));

        var row = db.WordPressRefreshPosts.Single();
        row.LastPushedHash = "abc";
        await db.SaveChangesAsync();
        Assert.Null(await svc.SaveAsync(EventId, row.Id, 10950, "Sponsors", "<p>{{SponsorList}}</p>", true));

        Assert.Null(db.WordPressRefreshPosts.Single().LastPushedHash);
    }
}
