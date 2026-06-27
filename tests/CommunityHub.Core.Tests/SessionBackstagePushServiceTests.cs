using System.Net;
using System.Net.Http;
using System.Text.Json;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §57/§58 STAGE 2 (CehToZoho) push engines: create-if-unlinked / update-if-linked sessions
/// and create-once speakers, idempotent by the stored Backstage id, NEVER deleting. Gated
/// per-edition on the session/speaker sync direction == stage 2 (inert at stage 1 + 3). The
/// real <see cref="ZohoClient"/> is exercised over a RECORDING fake HTTP handler so the
/// payload shape + create/update verb are asserted without a live call.
/// </summary>
public sealed class SessionBackstagePushServiceTests
{
    private const int EventId = 1;

    // --- recording fake HTTP handler ---------------------------------------
    private sealed record Recorded(HttpMethod Method, string Url, string Body);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<Recorded> Calls { get; } = new();
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;
        public FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Calls.Add(new Recorded(request.Method, request.RequestUri!.ToString(), body));
            return _respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private const string TracksJson =
        "{\"tracks\":[{\"track_id\":\"track-cloud\",\"name\":\"Cloud\"}]}";

    /// <summary>
    /// Wrap <paramref name="respond"/> so the GET /tracks the push service issues per pass is
    /// auto-answered (one Cloud track), leaving the test lambda to handle the session calls.
    /// </summary>
    private static (ZohoClient Zoho, FakeHandler Handler) NewZoho(
        Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        HttpResponseMessage Dispatch(HttpRequestMessage req, string body)
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/tracks"))
                return Json(HttpStatusCode.OK, TracksJson);
            return respond(req, body);
        }
        var handler = new FakeHandler(Dispatch);
        var opts = new ZohoOptions { BackstagePortalId = "P", BackstageEventId = "E" };
        return (new ZohoClient(new HttpClient(handler), opts), handler);
    }

    private static async Task SeedEditionAsync(
        CommunityHubDbContext db, SessionSyncDirection sessionDir, SessionSyncDirection speakerDir)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 10), EndDate = new DateOnly(2027, 2, 10),
            PreDayDate = new DateOnly(2027, 2, 9),
        });
        db.SessionSourceSettings.Add(new SessionSourceSetting
        {
            EventId = EventId, Source = SessionSourceKinds.Default,
            SyncDirection = sessionDir, SpeakerSyncDirection = speakerDir,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> SeedSessionAsync(
        CommunityHubDbContext db, string title, string? backstageId,
        DateTimeOffset? start, DateTimeOffset? end, string? track = "Cloud")
    {
        var s = new Session
        {
            EventId = EventId, SessionizeId = $"sz-{title}", Title = title, Abstract = "About " + title,
            BackstageSessionId = backstageId, StartsAt = start, EndsAt = end, Track = track,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    private static async Task<int> SeedSpeakerAsync(
        CommunityHubDbContext db, string email, string? backstageId, bool selectedForPublish)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = "Sam Speaker", Role = ParticipantRole.Speaker,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId, ParticipantId = p.Id, FirstName = "Sam", LastName = "Speaker",
            Country = "DK", Tagline = "Tagline", Biography = "Bio", BackstageSpeakerId = backstageId,
            SelectedForPublish = selectedForPublish,
        });
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static SessionBackstagePushService NewSessionSvc(CommunityHubDbContext db, ZohoClient zoho) =>
        new(db, zoho, new ZohoOptions(), tokenOverride: _ => Task.FromResult<string?>("tok"));

    private static SpeakerBackstagePushService NewSpeakerSvc(CommunityHubDbContext db, ZohoClient zoho) =>
        new(db, zoho, new ZohoOptions(), tokenOverride: _ => Task.FromResult<string?>("tok"));

    /// <summary>
    /// A session push service wired to the §59 delta queue (lazy factory), and the queue wired
    /// back to the SAME push service for apply-on-approve. Returns both so a test can drive an
    /// enqueue-on-update then an approve-pushes round-trip.
    /// </summary>
    private static (SessionBackstagePushService Push, SyncDeltaQueueService Queue) NewSessionSvcWithQueue(
        CommunityHubDbContext db, ZohoClient zoho)
    {
        // Break the queue↔push cycle the same way DI does: the push captures the queue lazily.
        SyncDeltaQueueService? queueRef = null;
        var push = new SessionBackstagePushService(
            db, zoho, new ZohoOptions(),
            tokenOverride: _ => Task.FromResult<string?>("tok"),
            queueFactory: () => queueRef!);
        var queue = new SyncDeltaQueueService(db, sessionPush: push);
        queueRef = queue;
        return (push, queue);
    }

    // ===================== payload shape ===================================

    [Fact]
    public void BuildSessionPayload_strips_blanks_and_formats_start_and_duration()
    {
        var start = new DateTimeOffset(2027, 2, 9, 9, 30, 0, TimeSpan.Zero);
        // track carries the resolved track ID (not the name); sessionType is required on create.
        var p = ZohoClient.BuildSessionPayload("  My Talk ", "An abstract", start, 50, "track-123", "PRESENTATION");

        Assert.Equal("My Talk", p["title"]);
        Assert.Equal("An abstract", p["description"]);
        Assert.Equal("track-123", p["track"]);            // the ID, keyed as `track`
        Assert.Equal("PRESENTATION", p["sessionType"]);
        Assert.Equal("2027-02-09T09:30:00Z", p["start_time"]);
        Assert.Equal(50, p["duration"]);

        // Blank description/track/sessionType and a null start/zero duration are omitted.
        var min = ZohoClient.BuildSessionPayload("T", "  ", null, 0, null, null);
        Assert.True(min.ContainsKey("title"));
        Assert.False(min.ContainsKey("description"));
        Assert.False(min.ContainsKey("track"));
        Assert.False(min.ContainsKey("sessionType"));
        Assert.False(min.ContainsKey("start_time"));
        Assert.False(min.ContainsKey("duration"));
    }

    [Theory]
    [InlineData("2027-02-09", 1)] // pre-day  → day 1
    [InlineData("2027-02-10", 2)] // main day → day 2
    public void DayIndex_is_1_based_from_first_agenda_day(string date, int expected)
    {
        var first = new DateOnly(2027, 2, 9); // pre-day anchor
        var start = new DateTimeOffset(DateOnly.Parse(date), new TimeOnly(9, 0), TimeSpan.Zero);
        Assert.Equal(expected, SessionBackstagePushService.DayIndex(first, start));
    }

    [Fact]
    public void DayIndex_falls_back_to_1_when_unknown_or_negative()
    {
        Assert.Equal(1, SessionBackstagePushService.DayIndex(null, DateTimeOffset.UtcNow));
        Assert.Equal(1, SessionBackstagePushService.DayIndex(new DateOnly(2027, 2, 9), null));
        // session dated before the anchor → clamp to day 1
        Assert.Equal(1, SessionBackstagePushService.DayIndex(
            new DateOnly(2027, 2, 9), new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void DurationMinutes_prefers_end_minus_start_then_length_bucket()
    {
        var start = new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(90, SessionBackstagePushService.DurationMinutes(
            new Session { StartsAt = start, EndsAt = start.AddMinutes(90) }));
        // no end → use the Length bucket
        Assert.Equal(480, SessionBackstagePushService.DurationMinutes(
            new Session { Length = SessionLength.FullDay }));
        Assert.Equal(50, SessionBackstagePushService.DurationMinutes(
            new Session { Length = SessionLength.FiftyMin }));
    }

    // ===================== §57 session direction gate ======================

    [Theory]
    [InlineData(SessionSyncDirection.SessionizeToCeh)] // stage 1
    [InlineData(SessionSyncDirection.ZohoToCeh)]        // stage 3
    public async Task Session_push_is_inert_unless_session_direction_is_stage2(SessionSyncDirection dir)
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, sessionDir: dir, speakerDir: SessionSyncDirection.CehToZoho);
        await SeedSessionAsync(db, "Talk A", null,
            new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero));

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"x\"}"));
        var r = await NewSessionSvc(db, zoho).RunAsync(EventId);

        Assert.False(r.DirectionActive);
        Assert.Contains($"stage {(int)dir}", r.InactiveReason);
        Assert.Empty(handler.Calls);                    // inert: not even the track lookup runs
        Assert.Null(db.Sessions.Single().BackstageSessionId);
    }

    /// <summary>The session push calls, excluding the per-pass GET /tracks lookup.</summary>
    private static List<Recorded> SessionCalls(FakeHandler h) =>
        h.Calls.Where(c => c.Url.Contains("/sessions")).ToList();

    [Fact]
    public async Task Session_push_creates_when_unlinked_and_stores_returned_id()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        await SeedSessionAsync(db, "Talk A", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 9, 50, 0, TimeSpan.Zero));

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-new-1\"}"));
        var r = await NewSessionSvc(db, zoho).RunAsync(EventId);

        Assert.True(r.DirectionActive);
        Assert.Equal(1, r.Created);
        Assert.Equal(0, r.Updated);
        Assert.Equal("bs-new-1", db.Sessions.Single().BackstageSessionId);

        // POST to the day-2 sessions endpoint with the stage-2 payload shape.
        var call = Assert.Single(SessionCalls(handler));
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Contains("/sessions?day=2", call.Url);
        using var doc = JsonDocument.Parse(call.Body);
        Assert.Equal("Talk A", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(50, doc.RootElement.GetProperty("duration").GetInt32());
        Assert.Equal("2027-02-10T09:00:00Z", doc.RootElement.GetProperty("start_time").GetString());
        // Track NAME "Cloud" resolved to its Backstage track ID; sessionType is sent.
        Assert.Equal("track-cloud", doc.RootElement.GetProperty("track").GetString());
        Assert.Equal("PRESENTATION", doc.RootElement.GetProperty("sessionType").GetString());
    }

    [Fact]
    public async Task Session_push_updates_when_already_linked_no_duplicate_create()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        await SeedSessionAsync(db, "Talk A", backstageId: "bs-existing",
            new DateTimeOffset(2027, 2, 10, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 12, 0, 0, TimeSpan.Zero));

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"status\":\"success\"}"));
        var r = await NewSessionSvc(db, zoho).RunAsync(EventId);

        Assert.Equal(0, r.Created);
        Assert.Equal(1, r.Updated);
        var call = Assert.Single(SessionCalls(handler));
        Assert.Equal(HttpMethod.Put, call.Method);                // PUT, not POST
        Assert.EndsWith("/sessions/bs-existing", call.Url);       // by id, no ?day=
        Assert.Equal("bs-existing", db.Sessions.Single().BackstageSessionId); // unchanged
    }

    // ============ §59: stage-2 UPDATE via the delta queue ==================

    [Fact]
    public async Task Linked_session_update_is_enqueued_not_pushed_inline_when_queue_wired()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        await SeedSessionAsync(db, "Talk A", backstageId: "bs-existing",
            new DateTimeOffset(2027, 2, 10, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 12, 0, 0, TimeSpan.Zero));

        // Any /sessions PUT/POST during the pass would be an inline push — must NOT happen.
        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"status\":\"success\"}"));
        var (push, queue) = NewSessionSvcWithQueue(db, zoho);

        var r = await push.RunAsync(EventId);

        Assert.True(r.DirectionActive);
        Assert.Equal(0, r.Created);
        Assert.Equal(0, r.Updated);
        Assert.Equal(1, r.Enqueued);                         // routed to the queue, not pushed
        Assert.Empty(SessionCalls(handler));                 // NO inline session call

        // A Pending CehToZoho Session Update delta now exists carrying the current CEH values.
        var pending = Assert.Single(await queue.ListPendingAsync(EventId));
        Assert.Equal(SyncDeltaEntityType.Session, pending.EntityType);
        Assert.Equal(SyncDeltaChangeKind.Update, pending.ChangeKind);
        Assert.Equal(SessionSyncDirection.CehToZoho, pending.Source);
        Assert.Contains(pending.Changes, c => c.Field == SyncDeltaQueueService.FieldTitle && c.NewValue == "Talk A");
    }

    [Fact]
    public async Task Approving_a_linked_session_update_pushes_current_values_to_zoho()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        var sessionId = await SeedSessionAsync(db, "Talk A", backstageId: "bs-existing",
            new DateTimeOffset(2027, 2, 10, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 12, 0, 0, TimeSpan.Zero));

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"status\":\"success\"}"));
        var (push, queue) = NewSessionSvcWithQueue(db, zoho);

        // 1) Pass enqueues (no inline push).
        await push.RunAsync(EventId);
        Assert.Empty(SessionCalls(handler));
        var delta = Assert.Single(await queue.ListPendingAsync(EventId));

        // 2) Approve → the queue's CehToZoho apply arm PUSHES the current CEH values to Zoho.
        var decision = await queue.ApproveAsync(delta.Id, "ops@x.dk");
        Assert.True(decision.Found);
        Assert.True(decision.Applied);

        var applied = await queue.GetAsync(delta.Id);
        Assert.Equal(SyncDeltaStatus.Applied, applied!.Status);

        // Exactly one PUT to the linked session id with the current CEH values.
        var call = Assert.Single(SessionCalls(handler));
        Assert.Equal(HttpMethod.Put, call.Method);
        Assert.EndsWith("/sessions/bs-existing", call.Url);
        using var doc = JsonDocument.Parse(call.Body);
        Assert.Equal("Talk A", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("2027-02-10T11:00:00Z", doc.RootElement.GetProperty("start_time").GetString());
    }

    [Fact]
    public async Task New_unlinked_session_still_pushes_directly_even_with_queue_wired()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        await SeedSessionAsync(db, "Talk A", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 9, 50, 0, TimeSpan.Zero));

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-new-1\"}"));
        var (push, queue) = NewSessionSvcWithQueue(db, zoho);

        var r = await push.RunAsync(EventId);

        // CREATE (new record) is NOT gated by approval — pushed directly (§58 chained-new rule).
        Assert.Equal(1, r.Created);
        Assert.Equal(0, r.Enqueued);
        Assert.Equal("bs-new-1", db.Sessions.Single().BackstageSessionId);
        var call = Assert.Single(SessionCalls(handler));
        Assert.Equal(HttpMethod.Post, call.Method);          // POST create, direct
        Assert.Empty(await queue.ListPendingAsync(EventId)); // nothing enqueued
    }

    [Fact]
    public async Task Session_push_failure_is_counted_and_id_stays_null()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        await SeedSessionAsync(db, "Talk A", null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero));

        var (zoho, _) = NewZoho((_, _) => Json(HttpStatusCode.BadRequest, "{\"message\":\"bad\"}"));
        var r = await NewSessionSvc(db, zoho).RunAsync(EventId);

        Assert.Equal(1, r.Failed);
        Assert.Equal(0, r.Created);
        Assert.Null(db.Sessions.Single().BackstageSessionId);
        var item = Assert.Single(r.Items);
        Assert.Equal(SessionBackstagePushService.PushAction.Failed, item.Action);
        Assert.Contains("400", item.Error);
    }

    // ===================== §58 speaker direction gate ======================

    [Theory]
    [InlineData(SessionSyncDirection.SessionizeToCeh)]
    [InlineData(SessionSyncDirection.ZohoToCeh)]
    public async Task Speaker_push_is_inert_unless_speaker_direction_is_stage2(SessionSyncDirection dir)
    {
        using var db = ScenarioFixture.NewDb();
        // session direction at stage 2 must NOT activate the speaker push (separate setting).
        await SeedEditionAsync(db, sessionDir: SessionSyncDirection.CehToZoho, speakerDir: dir);
        await SeedSpeakerAsync(db, "sam@x.dk", backstageId: null, selectedForPublish: true);

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"x\"}"));
        var r = await NewSpeakerSvc(db, zoho).RunAsync(EventId);

        Assert.False(r.DirectionActive);
        Assert.Contains($"stage {(int)dir}", r.InactiveReason);
        Assert.Empty(handler.Calls);
        Assert.Null(db.SpeakerProfiles.Single().BackstageSpeakerId);
    }

    [Fact]
    public async Task Speaker_push_creates_when_unlinked_stores_id_and_sends_publish_flag()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerAsync(db, "sam@x.dk", backstageId: null, selectedForPublish: true);

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-sp-1\"}"));
        var r = await NewSpeakerSvc(db, zoho).RunAsync(EventId);

        Assert.True(r.DirectionActive);
        Assert.Equal(1, r.Created);
        Assert.Equal("bs-sp-1", db.SpeakerProfiles.Single().BackstageSpeakerId);

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.EndsWith("/speakers", call.Url);
        using var doc = JsonDocument.Parse(call.Body);
        Assert.Equal("sam@x.dk", doc.RootElement.GetProperty("email").GetString());
        Assert.True(doc.RootElement.GetProperty("featured").GetBoolean()); // SelectedForPublish → featured
        Assert.Equal("Sam", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Speaker_push_leaves_already_linked_speaker_untouched_create_only_api()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerAsync(db, "sam@x.dk", backstageId: "bs-already", selectedForPublish: false);

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"should-not-happen\"}"));
        var r = await NewSpeakerSvc(db, zoho).RunAsync(EventId);

        Assert.Equal(0, r.Created);
        Assert.Equal(1, r.AlreadyLinked);
        Assert.Empty(handler.Calls);                                  // no duplicate create
        Assert.Equal("bs-already", db.SpeakerProfiles.Single().BackstageSpeakerId);
    }

    [Fact]
    public async Task Speaker_push_unselected_speaker_is_created_non_featured()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerAsync(db, "sam@x.dk", backstageId: null, selectedForPublish: false);

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-sp-2\"}"));
        await NewSpeakerSvc(db, zoho).RunAsync(EventId);

        using var doc = JsonDocument.Parse(Assert.Single(handler.Calls).Body);
        Assert.False(doc.RootElement.GetProperty("featured").GetBoolean()); // HARD publish gate
    }
}
