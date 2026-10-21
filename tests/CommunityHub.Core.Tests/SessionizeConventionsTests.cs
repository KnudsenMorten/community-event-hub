using System.Net;
using System.Net.Http;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1240 — the two ELDK27 Sessionize conventions: a <c>[NoImport]</c> title (never a CEH session
/// at all), and the <c>Common</c> "track" that is really the per-session common-for-all-tracks flag.
/// Plus the guard that <c>[Placeholder]</c> is NOT a rule.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-20: *"sponsor sessions which must not be imported into ceh, as they will
/// come from ceh already"* · *"i have created Common in the event track, but dont create that in
/// zoho. these sessions should be tagged as common session like welcome keynote and closing
/// keynote"* · *"i have changed my mind rgr [Placeholder]. i want it to show on zoho, so change
/// logic and remove that gate"*.</para>
///
/// <para>🔑 <b>Why these are worth a test rather than a comment.</b> Each failure is IRREVERSIBLE
/// and SILENT. The Backstage sessions API has no update and no delete, so a track created once
/// cannot be removed through the API at all — and nothing goes red, because the push reports a
/// successful create. The no-import rule is the mirror image: getting it backwards deletes the
/// sponsor's real session instead of the planner's stand-in.</para>
/// </remarks>
public sealed class SessionizeConventionsTests
{
    private const int EventId = 1;

    // ---- shared plumbing (mirrors SessionBackstagePushServiceTests) -------------------

    private sealed record Recorded(HttpMethod Method, string Url, string Body);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<Recorded> Calls { get; } = new();
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;
        public FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
            => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add(new Recorded(request.Method, request.RequestUri!.ToString(), body));
            return _respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static (ZohoClient Zoho, FakeHandler Handler) NewZoho(string? tracksJson = null)
    {
        HttpResponseMessage Dispatch(HttpRequestMessage req, string body)
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/tracks"))
                return Json(HttpStatusCode.OK,
                    tracksJson ?? "{\"tracks\":[{\"track_id\":\"track-sec\",\"name\":\"Security\"}]}");
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/halls"))
                return Json(HttpStatusCode.OK, "{\"halls\":[]}");
            return Json(HttpStatusCode.OK, "{\"id\":\"new-id\"}");
        }
        var handler = new FakeHandler(Dispatch);
        return (new ZohoClient(new HttpClient(handler),
            new ZohoOptions { BackstagePortalId = "P", BackstageEventId = "E" }), handler);
    }

    private static SessionBackstagePushService NewPush(CommunityHubDbContext db, ZohoClient zoho) =>
        new(db, zoho, new ZohoOptions(), tokenOverride: _ => Task.FromResult<string?>("tok"));

    private static async Task SeedEditionAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 10), EndDate = new DateOnly(2027, 2, 10),
            PreDayDate = new DateOnly(2027, 2, 9),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A Sessionize session row as the v2 parser produces one.</summary>
    private static SessionizeSession Src(string id, string title, string? track) =>
        new(id, title, "About " + title, "Room-A1",
            track,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero),
            IsServiceSession: false, SpeakerIds: Array.Empty<string>(),
            Category: "Technical Session (60 min)", Level: "Expert (400)", LengthMinutes: 60);

    private static Task<SessionImportResult> ImportAsync(
        CommunityHubDbContext db, params SessionizeSession[] sessions) =>
        new SessionImportService(db, ScenarioFixture.Clock,
                deletions: new CommunityHub.Core.Organizer.SessionDeletionService(db))
            .ImportSessionsAsync(
                EventId, sessions, Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());


    // ---- the [NoImport] rule ---------------------------------------------------------

    [Theory]
    [InlineData("[NoImport] Sponsor Session ELDK27 - 07:20-08:10")]
    [InlineData("[noimport] sponsor session")]
    public async Task A_no_import_row_never_becomes_a_ceh_session(string title)
    {
        // Operator 2026-09-20: the sponsor sessions exist in the Sessionize PLANNER to hold the
        // slot; the real session is created on the CEH side by the sponsor. Importing the planner
        // row would put two sessions in the same slot, and he will never rename it.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);

        var r = await ImportAsync(db,
            Src("sz-plan", title, "Security"),
            Src("sz-real", "A real talk", "Security"));

        Assert.Equal(1, r.Created);
        Assert.Equal(1, r.Skipped);
        Assert.Equal("A real talk", db.Sessions.Single().Title);
    }

    [Theory]
    [InlineData("Sponsor Session ELDK27 - 10:00-11:00 [NoImport]")]            // his postfix style
    [InlineData("Sponsor Session ELDK27 [NoImport] [Placeholder]")]            // both markers
    public async Task The_marker_is_matched_anywhere_in_the_title_not_as_a_prefix(string title)
    {
        // 🔒 He moved the markers to the END of the title on 2026-09-20
        // ("Identity \"black belt\" session [Placeholder]"). A prefix rule would have silently
        // stopped matching the moment he did — and silently importing a planner slot is exactly the
        // duplicate this rule exists to prevent.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);

        await ImportAsync(db, Src("sz-1", title, "Security"));

        Assert.Empty(db.Sessions);
    }

    [Fact]
    public async Task A_placeholder_title_is_NOT_a_rule_and_publishes_like_any_other_session()
    {
        // 🛑 REGRESSION GUARD, not a feature test. A `[Placeholder]` gate existed for part of
        // 2026-09-20 and he reversed it: *"i have changed my mind rgr [Placeholder]. i want it to
        // show on zoho, so change logic and remove that gate"*. The live titles still carry the
        // word, so the next reader will be tempted to "restore" the missing gate.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);
        await ImportAsync(db, Src("sz-1", "Identity \"black belt\" session [Placeholder]", "Security"));

        var (zoho, _) = NewZoho();
        var r = await NewPush(db, zoho).RunAsync(EventId);

        Assert.Equal(1, r.Created);
        Assert.Equal("new-id", db.Sessions.Single().BackstageSessionId);
    }

    [Fact]
    public async Task Marking_an_ALREADY_IMPORTED_row_no_import_removes_the_ceh_duplicate()
    {
        // Operator 2026-09-20 chose automatic removal over a warning.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);
        await ImportAsync(db, Src("sz-1", "Sponsor Session ELDK27 - 10:00-11:00", "Security"));
        Assert.Single(db.Sessions);

        var r = await ImportAsync(db, Src("sz-1", "Sponsor Session ELDK27 - 10:00-11:00 [NoImport]", "Security"));

        Assert.Empty(db.Sessions);
        Assert.Contains(r.Warnings, w => w.Contains("Removed session"));
    }

    [Fact]
    public async Task Attendee_input_still_blocks_the_removal_and_is_reported()
    {
        // 🔴 The delete runs through `SessionDeletionService`, so the §327d blockers hold. Deleting
        // a session that carries attendee questions would destroy what an attendee typed — an
        // import must never do that silently, whatever a title says.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);
        await ImportAsync(db, Src("sz-1", "Sponsor Session 10:00-11:00", "Security"));
        var session = db.Sessions.Single();
        db.SessionQuestions.Add(new SessionQuestion
        {
            SessionId = session.Id, EventId = EventId, QuestionText = "Will this be recorded?",
        });
        await db.SaveChangesAsync();

        var r = await ImportAsync(db, Src("sz-1", "[NoImport] Sponsor Session 10:00-11:00", "Security"));

        Assert.Single(db.Sessions);                     // survived
        Assert.Contains(r.Warnings, w => w.Contains("was NOT removed"));
    }

    [Fact]
    public async Task A_hub_added_session_is_never_removed_by_the_rule()
    {
        // The sponsor's REAL session is hub-added. It must be untouchable by anything a Sessionize
        // title says — that is the whole point of the rule, and getting it backwards would delete
        // exactly the row he is protecting.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);
        db.Sessions.Add(new Session
        {
            EventId = EventId, SessionizeId = "sz-1", Title = "Sponsor session (the real one)",
            IsHubAdded = true,
        });
        await db.SaveChangesAsync();

        await ImportAsync(db, Src("sz-1", "[NoImport] Sponsor Session ELDK27", "Security"));

        Assert.Equal("Sponsor session (the real one)", db.Sessions.Single().Title);
    }

    // ---- §1241: the speaker's confirmation --------------------------------------------

    [Fact]
    public async Task An_unconfirmed_session_is_not_imported()
    {
        // Operator 2026-09-20: *"importer should only import if IsConfirmed = true"* — the red
        // "not confirmed" badge on the Sessionize grid. 18 of the 76 ELDK27 sessions on the day.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);

        var r = await ImportAsync(db,
            Src("sz-yes", "Confirmed talk", "Security"),
            Src("sz-no", "Awaiting the speaker", "Security") with { IsConfirmed = false });

        Assert.Equal(1, r.Created);
        Assert.Equal(1, r.Skipped);
        Assert.Equal("Confirmed talk", db.Sessions.Single().Title);
        Assert.Contains(r.Warnings, w => w.Contains("has not confirmed"));
    }

    [Fact]
    public async Task Confirming_in_sessionize_imports_the_session_by_itself()
    {
        // 🔑 An import gate needs no release switch — the flag flipping IS the release.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);

        await ImportAsync(db, Src("sz-1", "A talk", "Security") with { IsConfirmed = false });
        Assert.Empty(db.Sessions);

        await ImportAsync(db, Src("sz-1", "A talk", "Security"));
        Assert.Equal("A talk", db.Sessions.Single().Title);
    }

    [Fact]
    public async Task An_ALREADY_IMPORTED_session_that_goes_unconfirmed_is_left_alone_and_reported()
    {
        // ⚠️ Deliberately UNLIKE [NoImport], which removes. An unconfirmed session is a REAL
        // session in a transient state — possibly already pushed to Backstage, possibly already
        // carrying attendee questions — and the flag can flip back. Deleting on an un-confirm
        // would destroy live work; the organizer decides instead.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);
        await ImportAsync(db, Src("sz-1", "A talk", "Security"));
        var id = db.Sessions.Single().Id;

        var r = await ImportAsync(db, Src("sz-1", "A talk", "Security") with { IsConfirmed = false });

        Assert.Equal(id, db.Sessions.Single().Id);          // survived
        Assert.Contains(r.Warnings, w => w.Contains("ALREADY EXISTS in CEH"));
    }

    [Fact]
    public void A_payload_with_no_isConfirmed_key_imports_rather_than_blocking_everything()
    {
        // 🔒 §326ax's lesson, one field along: an ABSENT signal must never read as a NEGATIVE one.
        // The importer refuses unconfirmed sessions, so a view that does not carry the key would
        // otherwise import NOTHING AT ALL — silently, with no failure anywhere.
        var withKey = SessionizeApiClient.ParseSessions(
            """{"sessions":[{"id":"1","title":"T","isConfirmed":false}]}""");
        Assert.False(withKey.Sessions.Single().IsConfirmed);

        var withoutKey = SessionizeApiClient.ParseSessions(
            """{"sessions":[{"id":"1","title":"T"}]}""");
        Assert.True(withoutKey.Sessions.Single().IsConfirmed);
    }

    [Fact]
    public void The_live_payload_shape_is_parsed_the_way_the_sessions_grid_shows_it()
    {
        // Guards the exact key name against the real v2 shape — a typo here would read every
        // session as "no key present" and the gate would quietly never fire.
        var r = SessionizeApiClient.ParseSessions("""
            {"sessions":[
              {"id":"1","title":"Confirmed","status":"Accepted","isInformed":true,"isConfirmed":true},
              {"id":"2","title":"Not confirmed","status":"Accepted","isInformed":true,"isConfirmed":false}
            ]}
            """);

        Assert.True(r.Sessions.Single(s => s.SessionizeId == "1").IsConfirmed);
        Assert.False(r.Sessions.Single(s => s.SessionizeId == "2").IsConfirmed);
    }

    // ---- the "Common" track rule -----------------------------------------------------

    [Fact]
    public async Task The_sessionize_Common_track_becomes_the_common_for_all_tracks_flag()
    {
        // Operator 2026-09-20: *"we already have a common setting on per session level which is
        // equivalent to track = Common in sessionize"*. So it maps onto the EXISTING §1011 flag and
        // CEH stores NO track — storing the literal would make the push create it in Zoho.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);

        await ImportAsync(db,
            Src("sz-key", "Keynote: TBD", "Common"),
            Src("sz-clo", "ELDK27 Closing", "common"),          // case must not matter
            Src("sz-com", "Panel: Women in Tech", "Community")); // NOT the marker — a real track

        var keynote = db.Sessions.Single(s => s.SessionizeId == "sz-key");
        Assert.True(keynote.IsCommonForAllTracks);
        Assert.Null(keynote.Track);

        var closing = db.Sessions.Single(s => s.SessionizeId == "sz-clo");
        Assert.True(closing.IsCommonForAllTracks);
        Assert.Null(closing.Track);

        // 🔒 "Community" is a REAL track he has already created in Zoho — one letter apart from the
        // marker, and swallowing it would quietly strip a track off the public agenda.
        var community = db.Sessions.Single(s => s.SessionizeId == "sz-com");
        Assert.False(community.IsCommonForAllTracks);
        Assert.Equal("Community", community.Track);
    }

    [Fact]
    public async Task A_common_session_IS_created_under_the_configured_fallback_track()
    {
        // 🔴 §1251. Operator 2026-09-20: *"you must create it to link the zoho id to ceh. that will
        // not work if i create it manually"* — the deciding argument, and it is about the LINK. A
        // hand-made Backstage session never returns its id, so the two are never linked and every
        // later check has nothing to hold.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);
        await ImportAsync(db, Src("sz-key", "Keynote [Placeholder]", "Common"));

        var (zoho, handler) = NewZoho();   // the fake exposes one track: "Security"
        var opts = new ZohoOptions { CommonSessionFallbackTrack = "Security" };
        var push = new SessionBackstagePushService(
            db, zoho, opts, tokenOverride: _ => Task.FromResult<string?>("tok"));

        var r = await push.RunAsync(EventId);

        Assert.Equal(1, r.Created);
        // 🔑 The whole point: the Backstage id is now STORED, so the session is linked.
        Assert.Equal("new-id", db.Sessions.Single().BackstageSessionId);
        // It went over with the fallback track, and no track was invented.
        var post = Assert.Single(handler.Calls,
            c => c.Method == HttpMethod.Post && c.Url.Contains("/sessions"));
        Assert.Contains("track-sec", post.Body);
        Assert.DoesNotContain(handler.Calls,
            c => c.Method == HttpMethod.Post && c.Url.Contains("/tracks"));
    }

    [Fact]
    public void The_create_mail_tells_him_to_clear_the_placeholder_track()
    {
        // ⚠️ The session is WRONG IN PUBLIC until he acts, so this line must read as an
        // instruction with its consequence, not as one more "the API could not send this" entry.
        var common = new Session { Title = "Keynote", IsCommonForAllTracks = true };

        var gaps = SessionBackstagePushService.DescribeCreateGaps(
            common, venueId: "hall", speakerEmails: new[] { "a@b.c" }, trackSent: true);

        Assert.Contains("CLEAR the Track field NOW", gaps);
        Assert.Contains("public agenda", gaps);
    }

    [Fact]
    public async Task With_NO_fallback_configured_a_common_session_is_skipped_not_failed()
    {
        // 🔴 §1248 — PROVEN on PROD 2026-09-20: a track-less create is refused with
        // HTTP 400 "`track` is a required parameter". §1240d had removed this skip arguing the
        // requirement was never verified; trying it verified it, at the cost of one failed pass
        // that wrote nothing. The original behaviour was right and this pins it.
        //
        // 🔑 SKIPPED, not FAILED: no CEH edit could ever clear the failure, so a failure line
        // would arrive on every pass for ever — the §594 failure the operator actually reported
        // ("still getting this").
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);
        await ImportAsync(db, Src("sz-key", "ELDK27 Closing", "Common"));

        var (zoho, handler) = NewZoho();
        var r = await NewPush(db, zoho).RunAsync(EventId);

        Assert.Equal(0, r.Created);
        Assert.Equal(0, r.Failed);
        Assert.Equal(1, r.Skipped);
        Assert.Contains(r.Items, i => i.Error!.Contains("Create this one by hand"));
        // Nothing was written to Zoho — no session, and certainly no "Common" track.
        Assert.DoesNotContain(handler.Calls, c => c.Method != HttpMethod.Get);
    }

    [Fact(Skip = "§1248 — superseded: the live API refuses a track-less create (HTTP 400). Kept as "
               + "the record of what was tried and why, since the reasoning for trying it was sound "
               + "even though the answer went the other way.")]
    public async Task A_common_session_IS_created_in_zoho_with_NO_track_at_all()
    {
        // 🔴 §1240d. Operator 2026-09-20: *"these 2 should be created as well, why would you skip
        // them"* · *"but they are both common"*. Being common is exactly WHY they are created
        // track-less: §1011 live-verified that Backstage shows a common session as `track: null`,
        // and 16 of 25 live sessions are in that state. The "create needs a track" rule was his own
        // §569 sentence, never a verified API rejection — and `BuildSessionPayload` omits the key
        // entirely when the id is null, so the payload is well-formed without it.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);
        await ImportAsync(db, Src("sz-key", "ELDK27 Closing", "Common"));

        var (zoho, handler) = NewZoho();
        var r = await NewPush(db, zoho).RunAsync(EventId);

        Assert.Equal(1, r.Created);
        Assert.Equal(0, r.Failed);
        Assert.Equal(0, r.Skipped);
        Assert.Equal("new-id", db.Sessions.Single().BackstageSessionId);

        // The create payload carries NO `track` key — and no track was created to carry.
        var post = Assert.Single(handler.Calls,
            c => c.Method == HttpMethod.Post && c.Url.Contains("/sessions"));
        Assert.DoesNotContain("\"track\"", post.Body);
        Assert.DoesNotContain(handler.Calls,
            c => c.Method == HttpMethod.Post && c.Url.Contains("/tracks"));
    }

    [Fact]
    public async Task An_ordinary_session_with_no_track_still_fails_rather_than_going_over_bare()
    {
        // 🔒 The bare create is for COMMON sessions only. A missing track on an ordinary session is
        // a real data gap, and publishing it track-less would hide that on an API that cannot undo.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);
        await ImportAsync(db, Src("sz-1", "A talk with no track", null));

        var (zoho, handler) = NewZoho();
        var r = await NewPush(db, zoho).RunAsync(EventId);

        Assert.Equal(0, r.Created);
        Assert.Equal(1, r.Failed);
        Assert.DoesNotContain(handler.Calls, c => c.Method != HttpMethod.Get);
    }

    [Fact]
    public void The_create_mail_does_not_tell_him_to_clear_a_track_that_was_never_sent()
    {
        // §594 — an instruction already satisfied before he reads it teaches him to skim the list.
        // §1011's "CLEAR the Track field" line is right only when a track actually went over.
        var common = new Session { Title = "ELDK27 Closing", IsCommonForAllTracks = true };

        var bare = SessionBackstagePushService.DescribeCreateGaps(
            common, venueId: "hall", speakerEmails: new[] { "a@b.c" }, trackSent: false);
        Assert.DoesNotContain("CLEAR the Track field", bare);

        var withTrack = SessionBackstagePushService.DescribeCreateGaps(
            common, venueId: "hall", speakerEmails: new[] { "a@b.c" }, trackSent: true);
        Assert.Contains("CLEAR the Track field", withTrack);
    }

    [Fact]
    public async Task A_hand_typed_Common_track_still_never_reaches_zoho()
    {
        // Belt and braces: the importer can no longer produce this, but the organizer session
        // editor accepts any track string, and create-if-missing would write it to the live agenda.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);
        db.Sessions.Add(new Session
        {
            EventId = EventId, SessionizeId = "hub-1", Title = "Hub session", Track = "Common",
            StartsAt = new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync();

        var (zoho, handler) = NewZoho();
        var r = await NewPush(db, zoho).RunAsync(EventId);

        Assert.Equal(0, r.Created);
        Assert.DoesNotContain(handler.Calls,
            c => c.Method == HttpMethod.Post && c.Url.Contains("/tracks"));
    }

    [Fact]
    public async Task Sessionize_moving_a_live_session_to_Common_reports_the_tick_box_not_a_track()
    {
        // CEH owns the track on an existing row (§999) — so this is report-only. The line must name
        // the tick box: typing "Common" into the Track field is the one action that must not happen.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);
        await ImportAsync(db, Src("sz-1", "Keynote", "Security"));

        var r = await ImportAsync(db, Src("sz-1", "Keynote", "Common"));

        var track = Assert.Single(r.DeviationsOrEmpty, d => d.Field == "Track");
        Assert.Equal("Security", track.InCeh);
        Assert.Contains("Common for all tracks", track.InSessionize);
        Assert.Equal("Security", db.Sessions.Single().Track);   // never overwritten
    }

    // ---- the level that is not a difficulty ------------------------------------------

    [Fact]
    public async Task The_panel_level_reaches_the_zoho_tag_line_verbatim()
    {
        // Operator 2026-09-20: *"Session Level General Discussions (Panels) should be included in
        // zoho tags if sessionize level is General Discussions (Panels)"*. It already does — the
        // level VALUE passes through verbatim behind the colon-less §594b prefix. This test is what
        // keeps that true, since the label carries no "(NNN)" code and so takes a different path
        // through every OTHER level rule in the pipeline.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db);
        await ImportAsync(db, Src("sz-1", "Panel: Women in Tech", "Community")
            with { Level = "General Discussions (Panels)", Tags = "AI, Microsoft Copilot" });

        var session = db.Sessions.Single();
        Assert.Equal("General Discussions (Panels)", session.Level);
        Assert.Null(session.LevelCode);     // no numeric code exists, and none is invented

        var tags = ZohoFieldMap.SessionTags(session);
        Assert.Contains("Session Level General Discussions (Panels)", tags);
        Assert.Contains("Session Language English", tags);
        Assert.Contains("AI", tags);
        Assert.Contains("Microsoft Copilot", tags);
    }
}
