using System.Net;
using System.Net.Http;
using System.Text.Json;
using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Settings;
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
    /// Wrap <paramref name="respond"/> so the GET /tracks and GET /halls lookups the push
    /// service issues are auto-answered (default: one Cloud track, no halls), leaving the
    /// test lambda to handle the session/hall WRITE calls. Override <paramref name="tracksJson"/>
    /// / <paramref name="hallsJson"/> to shape the Zoho side.
    /// </summary>
    private static (ZohoClient Zoho, FakeHandler Handler) NewZoho(
        Func<HttpRequestMessage, string, HttpResponseMessage> respond,
        string? tracksJson = null, string? hallsJson = null)
    {
        HttpResponseMessage Dispatch(HttpRequestMessage req, string body)
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/tracks"))
                return Json(HttpStatusCode.OK, tracksJson ?? TracksJson);
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/halls"))
                return Json(HttpStatusCode.OK, hallsJson ?? "{\"halls\":[]}");
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
        DateTimeOffset? start, DateTimeOffset? end, string? track = "Cloud", string? room = null)
    {
        var s = new Session
        {
            EventId = EventId, SessionizeId = $"sz-{title}", Title = title, Abstract = "About " + title,
            BackstageSessionId = backstageId, StartsAt = start, EndsAt = end, Track = track, Room = room,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    /// <summary>
    /// Link a new speaker participant with <paramref name="email"/> to a session — APPROVED
    /// (active + lifecycle Active + categorized) at <paramref name="ring"/> (Ring1 default),
    /// since the 2026-07-24 incident fix only attaches approved + ring-eligible e-mails.
    /// </summary>
    private static async Task LinkSpeakerAsync(
        CommunityHubDbContext db, int sessionId, string email, Ring ring = Ring.Ring1)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = "S " + email, Role = ParticipantRole.Speaker,
            IsActive = true, LifecycleState = ParticipantLifecycleState.Active, Ring = ring,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId, ParticipantId = p.Id, FirstName = "S", LastName = email,
            Category = SpeakerCategory.Community,
        });
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = sessionId, ParticipantId = p.Id });
        await db.SaveChangesAsync();
    }

    /// <summary>The 2026-07-24 incident-fix gate: a real FeatureGateService over the test db
    /// with the backstage-speaker-sync kill switch ON (its catalog released ring = Ring1).</summary>
    private static async Task<FeatureGateService> NewSpeakerAttachGateAsync(CommunityHubDbContext db)
    {
        if (!db.FeatureSettings.Any(f =>
                f.EventId == EventId && f.FeatureKey == "backstage-speaker-sync"))
        {
            db.FeatureSettings.Add(new FeatureSetting
            {
                EventId = EventId, FeatureKey = "backstage-speaker-sync", Enabled = true,
                ReleasedToRing = Ring.Ring1, ReleasedToRingOverride = Ring.Ring1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        return new FeatureGateService(db);
    }

    /// <summary>A §299.6/b5 room registry built directly from (name, capacity) entries —
    /// capacity null marks an expo location (no hall is ever created for those).</summary>
    private static CommunityHub.Core.Config.RoomRegistryService NewRooms(
        params (string Name, int? Capacity)[] rooms) =>
        new(new CommunityHub.Core.Config.EventEditionConfig
        {
            SessionRooms = rooms.Select(r => new CommunityHub.Core.Config.SessionRoomOption
            {
                Name = r.Name, Capacity = r.Capacity, Expo = r.Capacity is null,
            }).ToList(),
        });

    /// <summary>Seeds a speaker who is APPROVED by default (active + fully activated +
    /// categorized, ring 1) so the stage-2 approval/ring gates pass unless a test
    /// deliberately holds them via the optional parameters.</summary>
    private static async Task<int> SeedSpeakerAsync(
        CommunityHubDbContext db, string email, string? backstageId, bool selectedForPublish,
        bool isActive = true,
        ParticipantLifecycleState lifecycle = ParticipantLifecycleState.Active,
        Ring ring = Ring.Ring1,
        SpeakerCategory? category = SpeakerCategory.Community,
        // §415: Get Started completed by DEFAULT. Every pre-existing test in this file is about
        // some OTHER gate, so they must keep describing a speaker who is ready to push. Pass false
        // to exercise the new hold.
        bool getStartedDone = true)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = "Sam Speaker", Role = ParticipantRole.Speaker,
            IsActive = isActive, LifecycleState = lifecycle, Ring = ring,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId, ParticipantId = p.Id, FirstName = "Sam", LastName = "Speaker",
            Country = "DK", Tagline = "Tagline", Biography = "Bio", BackstageSpeakerId = backstageId,
            SelectedForPublish = selectedForPublish, Category = category,
            BioLastEditedBySpeakerAt = getStartedDone ? DateTimeOffset.UtcNow : null,
        });
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static SessionBackstagePushService NewSessionSvc(
        CommunityHubDbContext db, ZohoClient zoho,
        CommunityHub.Core.Config.RoomRegistryService? rooms = null, ZohoOptions? options = null,
        FeatureGateService? gate = null) =>
        new(db, zoho, options ?? new ZohoOptions(),
            tokenOverride: _ => Task.FromResult<string?>("tok"), rooms: rooms, gate: gate);

    /// <summary>
    /// A session push service wired with the operator-2026-07-23 CEH→Zoho change notifier
    /// over a capturing mail sender, so a test can assert the ops change mail.
    /// </summary>
    private static (SessionBackstagePushService Svc, CapturingEmailSender Mail) NewSessionSvcWithChangeMail(
        CommunityHubDbContext db, ZohoClient zoho)
    {
        var mail = new CapturingEmailSender();
        var alerts = new CommunityHub.Core.Email.EngineAlertSender(
            mail, new CommunityHub.Core.Email.EmailContextAccessor(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CommunityHub.Core.Email.EngineAlertSender>.Instance);
        var svc = new SessionBackstagePushService(
            db, zoho, new ZohoOptions(),
            tokenOverride: _ => Task.FromResult<string?>("tok"),
            zohoChanges: new CommunityHub.Core.Email.ZohoChangeNotifier(alerts));
        return (svc, mail);
    }

    /// <summary>Speaker push service; pass a REAL <see cref="FeatureGateService"/> over the
    /// test db to exercise the stage-2 backstage-speaker-sync ring gate (null = ungated).</summary>
    private static SpeakerBackstagePushService NewSpeakerSvc(
        CommunityHubDbContext db, ZohoClient zoho, FeatureGateService? gate = null) =>
        new(db, zoho, new ZohoOptions(), tokenOverride: _ => Task.FromResult<string?>("tok"),
            gate: gate);

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
        // track carries the resolved track ID (not the name); session_type (snake_case — live-verified) is required on create.
        var p = ZohoClient.BuildSessionPayload("  My Talk ", "An abstract", start, 50, "track-123", "PRESENTATION");

        Assert.Equal("My Talk", p["title"]);
        Assert.Equal("An abstract", p["description"]);
        Assert.Equal("track-123", p["track"]);            // the ID, keyed as `track`
        Assert.Equal("PRESENTATION", p["session_type"]);
        Assert.Equal("2027-02-09T09:30:00Z", p["start_time"]);
        Assert.Equal(50, p["duration"]);

        // Blank description/track/sessionType and a null start/zero duration are omitted.
        var min = ZohoClient.BuildSessionPayload("T", "  ", null, 0, null, null);
        Assert.True(min.ContainsKey("title"));
        Assert.False(min.ContainsKey("description"));
        Assert.False(min.ContainsKey("track"));
        Assert.False(min.ContainsKey("session_type"));
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

    // ============ §569 — the §57 session direction gate is REMOVED ============

    [Theory]
    [InlineData(SessionSyncDirection.SessionizeToCeh)] // stage 1
    [InlineData(SessionSyncDirection.ZohoToCeh)]        // stage 3
    [InlineData(SessionSyncDirection.CehToZoho)]        // stage 2
    public async Task Session_push_runs_whatever_the_stored_direction_says(SessionSyncDirection dir)
    {
        // §569 (operator 2026-07-28): "sessions should also NOT have gates, once they sync with
        // sessionize they must flow to zoho … as we are in stage 2 mode". Stage 3 was deleted long
        // ago, so this selector had ONE legal value — and being left on the wrong one silently
        // blocked the push for weeks while the Jobs page showed a healthy green run (§544/§551).
        //
        // This test is the mechanism that stops it coming back: whatever the row says, the push
        // runs. It replaces `Session_push_is_inert_unless_session_direction_is_stage2`, which
        // asserted exactly the behaviour he asked to delete.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, sessionDir: dir, speakerDir: SessionSyncDirection.CehToZoho);
        await SeedSessionAsync(db, "Talk A", null,
            new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero));

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"x\"}"));
        var r = await NewSessionSvc(db, zoho).RunAsync(EventId);

        Assert.True(r.DirectionActive);
        Assert.Null(r.InactiveReason);
        Assert.Equal(1, r.Created);
        Assert.Equal("x", db.Sessions.Single().BackstageSessionId);
    }

    /// <summary>The session push calls, excluding the per-pass GET /tracks lookup.</summary>
    // WRITES to /sessions only — the §301b self-heal adds read-only GETs (agendas +
    // sessions-per-day) that the write assertions must not count.
    private static List<Recorded> SessionCalls(FakeHandler h) =>
        h.Calls.Where(c => c.Method != HttpMethod.Get && c.Url.Contains("/sessions")).ToList();

    [Fact]
    public void Create_gap_report_names_the_unsendable_fields_and_the_ceh_gaps()
    {
        // §574 (operator 2026-07-28): "i missed in the email information about the missing tags for
        // sessions like session language, session level + tags. it should have been in the initial
        // email about the creation, instead of coming later."
        //
        // The sessions API is CREATE-ONLY, so a field not set at create is a manual Backstage edit
        // FOREVER. This test is the mechanism that keeps the create mail self-sufficient.
        var bare = new Session { Title = "Bare Talk" };
        var gaps = SessionBackstagePushService.DescribeCreateGaps(bare, venueId: null, speakerEmails: null);

        // The three he named explicitly, always present. §594: TAGS now get their own paste-ready
        // line rather than a bare mention, because create time is the ONLY moment they are
        // actionable (Zoho neither accepts them on create nor returns them on read).
        Assert.Contains("level", gaps);
        Assert.Contains("language", gaps);
        Assert.Contains("Tags — paste this line into the Tags box:", gaps);
        Assert.Contains("Session Language English", gaps);   // the mandatory derived tag
        // The two §571.3 found live and he was NOT told about.
        Assert.Contains("no room set in CEH", gaps);
        Assert.Contains("NO speaker attached", gaps);

        // A fully-populated session still reports the API-unsendable fields — CEH HAS the values,
        // the create payload simply has nowhere to put them — but reports no CEH gaps.
        var full = new Session
        {
            Title = "Full Talk", Level = "Expert (400)", Tags = "AI, Security",
            Abstract = "About it", Room = "Room-A",
        };
        var fullGaps = SessionBackstagePushService.DescribeCreateGaps(
            full, venueId: "hall-a", speakerEmails: new[] { "s@x.dk" });
        Assert.Contains("Expert (400)", fullGaps);
        Assert.Contains("AI, Security", fullGaps);
        Assert.Contains("description/abstract", fullGaps);
        Assert.DoesNotContain("Gaps in CEH at create time", fullGaps);

        // A room that CEH HAS but which did not resolve to a hall is a DIFFERENT problem from an
        // unset room, and must not be reported as "none".
        var unresolved = SessionBackstagePushService.DescribeCreateGaps(
            full, venueId: null, speakerEmails: new[] { "s@x.dk" });
        Assert.Contains("did not resolve to a Backstage hall", unresolved);
    }

    [Fact]
    public async Task Session_create_mail_entry_carries_the_gap_report()
    {
        // The gap text must ride the SAME mail entry as the create — that is the whole point of
        // §574 ("instead of coming later").
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        await SeedSessionAsync(db, "Gap Talk", backstageId: null,
            new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero));

        var (zoho, _) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-gap\"}"));
        var (svc, mail) = NewSessionSvcWithChangeMail2(db, zoho);
        var r = await svc.RunAsync(EventId);

        Assert.Equal(1, r.Created);
        var (_, _, html, _) = Assert.Single(mail.Messages);
        Assert.Contains("Created session &#39;Gap Talk&#39;", html);
        // The gaps ride the SAME mail as the create — that is the whole point of §574.
        Assert.Contains("language", html);
        Assert.Contains("level", html);
        Assert.Contains("NO speaker attached", html);
        // §594 — tags arrive as a paste-ready line here, since this is the ONLY moment they are
        // actionable (Zoho refuses them on create and never returns them on read).
        Assert.Contains("paste this line into the Tags box", html);
    }

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

        // POST to the sessions endpoint with the LIVE-VERIFIED stage-2 payload shape:
        // a timed session must NOT carry ?day= (400 "Extra param found" — the day derives
        // from start_time) and description is NOT accepted on create.
        var call = Assert.Single(SessionCalls(handler));
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.DoesNotContain("day=", call.Url);
        using var doc = JsonDocument.Parse(call.Body);
        Assert.Equal("Talk A", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(50, doc.RootElement.GetProperty("duration").GetInt32());
        Assert.Equal("2027-02-10T09:00:00Z", doc.RootElement.GetProperty("start_time").GetString());
        Assert.False(doc.RootElement.TryGetProperty("description", out _));
        // Track NAME "Cloud" resolved to its Backstage track ID; session_type is sent.
        Assert.Equal("track-cloud", doc.RootElement.GetProperty("track").GetString());
        Assert.Equal("PRESENTATION", doc.RootElement.GetProperty("session_type").GetString());
    }

    [Fact]
    public async Task Session_push_linked_session_change_mails_once_per_distinct_diff_never_writes()
    {
        // §302 ONE-WAY DECISION (operator 2026-07-24): a linked session is NEVER pushed
        // (create-only API) — a CEH change vs the LIVE Zoho session mails info@ the Zoho
        // GUI field diff ONCE (hash-deduped: "I don't want an email at every sync").
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        await SeedSessionAsync(db, "Talk A", backstageId: "bs-existing",
            new DateTimeOffset(2027, 2, 10, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 12, 0, 0, TimeSpan.Zero));

        HttpResponseMessage Respond(HttpRequestMessage req, string _)
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/agendas"))
                return Json(HttpStatusCode.OK, "{\"agendas\":[{\"index\":0}]}");
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/sessions"))
                // LIVE Zoho: same title, but starts 10:00 — CEH says 11:00 ⇒ one diff.
                return Json(HttpStatusCode.OK,
                    "{\"sessions\":[{\"id\":\"bs-existing\",\"title\":\"Talk A\",\"start_time\":\"2027-02-10T10:00:00Z\",\"duration\":60}]}");
            return Json(HttpStatusCode.OK, "{\"status\":\"success\"}");
        }
        var (zoho, handler) = NewZoho(Respond);
        var (svc, mail) = NewSessionSvcWithChangeMail2(db, zoho);

        var r1 = await svc.RunAsync(EventId);
        Assert.Equal(0, r1.Created);
        Assert.Equal(1, r1.Updated);                             // "updated" = change-NOTIFIED
        Assert.DoesNotContain(handler.Calls, c => c.Method != HttpMethod.Get); // NEVER writes
        var (to, _, html, _) = Assert.Single(mail.Messages);
        Assert.Equal(ZohoChangeNotifier.Recipient, to);   // §493: system alerts go to the operator mailbox
        Assert.Contains("ACTION NEEDED", html);
        Assert.Contains("Session Time", html);                   // the ZOHO GUI field name
        var hash = db.Sessions.Single().ZohoChangeNotifiedHash;
        Assert.False(string.IsNullOrWhiteSpace(hash));

        // Second pass, SAME diff ⇒ NO new mail (hash dedupe), hash unchanged.
        var r2 = await svc.RunAsync(EventId);
        Assert.Equal(0, r2.Updated);
        Assert.Single(mail.Messages);
        Assert.Equal(hash, db.Sessions.Single().ZohoChangeNotifiedHash);
    }

    /// <summary>Session push service + captured change mail (mirrors NewSessionSvcWithChangeMail
    /// but returns the full-capture sender for body assertions).</summary>
    private static (SessionBackstagePushService Svc, CapturingEmailSender Mail) NewSessionSvcWithChangeMail2(
        CommunityHubDbContext db, ZohoClient zoho)
    {
        var mail = new CapturingEmailSender();
        var alerts = new CommunityHub.Core.Email.EngineAlertSender(
            mail, new CommunityHub.Core.Email.EmailContextAccessor(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CommunityHub.Core.Email.EngineAlertSender>.Instance);
        var svc = new SessionBackstagePushService(
            db, zoho, new ZohoOptions(),
            tokenOverride: _ => Task.FromResult<string?>("tok"),
            zohoChanges: new CommunityHub.Core.Email.ZohoChangeNotifier(alerts));
        return (svc, mail);
    }

    // ============ §59: stage-2 UPDATE via the delta queue ==================

    [Fact]
    public async Task Linked_session_pass_never_enqueues_deltas_one_way_decision()
    {
        // §302: the §59 CehToZoho update-delta ENQUEUE is retired — even with the queue
        // wired, a linked session produces NO pending delta and NO inline session write.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        await SeedSessionAsync(db, "Talk A", backstageId: "bs-existing",
            new DateTimeOffset(2027, 2, 10, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 12, 0, 0, TimeSpan.Zero));

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"status\":\"success\"}"));
        var (push, queue) = NewSessionSvcWithQueue(db, zoho);

        var r = await push.RunAsync(EventId);

        Assert.True(r.DirectionActive);
        Assert.Equal(0, r.Created);
        Assert.Equal(0, r.Enqueued);                         // enqueue retired
        Assert.Empty(await queue.ListPendingAsync(EventId)); // no delta
        Assert.Empty(SessionCalls(handler));                 // no inline session write
    }

    [Fact]
    public async Task Approving_a_linked_session_update_pushes_current_values_to_zoho()
    {
        // The queue's apply-on-approve arm still works for a delta enqueued by OTHER
        // flows (the §302 one-way decision only retired the push-pass ENQUEUE).
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        var sessionId = await SeedSessionAsync(db, "Talk A", backstageId: "bs-existing",
            new DateTimeOffset(2027, 2, 10, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 12, 0, 0, TimeSpan.Zero));

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"status\":\"success\"}"));
        var (push, queue) = NewSessionSvcWithQueue(db, zoho);

        // 1) Enqueue directly (RunAsync no longer does — §302).
        await queue.EnqueueSessionUpdateAsync(
            EventId, sessionId, "Talk A", SessionSyncDirection.CehToZoho,
            new[] { new SyncFieldChange(SyncDeltaQueueService.FieldTitle, null, "Talk A") }, default);
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

    // ====== stage-2 go-live: the bulk RunAsync pass ships COMPLETE creates ======

    [Fact]
    public async Task Session_push_skips_a_test_session_entirely()
    {
        // §299 4.5: a UsedForTesting session must NEVER reach the public agenda — the
        // stage-2 bulk pass excludes it up front (no POST, not even an item).
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        await SeedSessionAsync(db, "Talk A", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 9, 50, 0, TimeSpan.Zero));
        var testId = await SeedSessionAsync(db, "Rehearsal", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 11, 0, 0, TimeSpan.Zero));
        db.Sessions.Single(s => s.Id == testId).UsedForTesting = true;
        await db.SaveChangesAsync();

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-real\"}"));
        var r = await NewSessionSvc(db, zoho).RunAsync(EventId);

        Assert.Equal(1, r.Created);
        var call = Assert.Single(SessionCalls(handler));      // ONE create — the real talk
        Assert.Contains("Talk A", call.Body);
        Assert.DoesNotContain(handler.Calls, c => c.Body.Contains("Rehearsal"));
        Assert.DoesNotContain(r.Items, i => i.SessionId == testId);
        Assert.Null(db.Sessions.Single(s => s.Id == testId).BackstageSessionId);
    }

    [Fact]
    public async Task Session_push_bulk_create_carries_venue_and_speakers_and_creates_track_and_hall_once()
    {
        // Two sessions sharing the SAME new track + new room: the pass must create the
        // track and the hall exactly ONCE (per-pass caches) and both creates must carry
        // the resolved ids; the first session also carries its linked speaker's e-mail.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        var a = await SeedSessionAsync(db, "Intune One", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero),
            track: "Intune", room: "Room-A");
        await SeedSessionAsync(db, "Intune Two", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 12, 0, 0, TimeSpan.Zero),
            track: "Intune", room: "Room-A");
        await LinkSpeakerAsync(db, a, "s1@x.dk");

        var n = 0;
        var (zoho, handler) = NewZoho((req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/tracks")
                ? Json(HttpStatusCode.OK, "{\"id\":\"track-intune\",\"name\":\"Intune\"}")
                : req.RequestUri!.AbsolutePath.EndsWith("/halls")
                    ? Json(HttpStatusCode.OK, "{\"hall\":{\"id\":\"hall-a\"}}")
                    : Json(HttpStatusCode.OK, $"{{\"id\":\"bs-bulk-{++n}\"}}"));
        var rooms = NewRooms(("Room-A", 100));
        var gate = await NewSpeakerAttachGateAsync(db);
        var r = await NewSessionSvc(db, zoho, rooms, gate: gate).RunAsync(EventId);

        Assert.Equal(2, r.Created);
        Assert.Equal(0, r.Failed);
        // CACHE PROOF: the shared new track + hall are each created exactly ONCE.
        Assert.Single(handler.Calls, c => c.Method == HttpMethod.Post && c.Url.EndsWith("/tracks"));
        Assert.Single(handler.Calls, c => c.Method == HttpMethod.Post && c.Url.EndsWith("/halls"));
        var creates = SessionCalls(handler);
        Assert.Equal(2, creates.Count);
        foreach (var call in creates)
        {
            using var doc = JsonDocument.Parse(call.Body);
            Assert.Equal("track-intune", doc.RootElement.GetProperty("track").GetString());
            Assert.Equal("hall-a", doc.RootElement.GetProperty("venue").GetString());
        }
        // The linked speaker rides along on ITS session's create only.
        using var first = JsonDocument.Parse(Assert.Single(creates, c => c.Body.Contains("Intune One")).Body);
        Assert.Equal(new[] { "s1@x.dk" },
            first.RootElement.GetProperty("speakers").EnumerateArray().Select(e => e.GetString()));
        using var second = JsonDocument.Parse(Assert.Single(creates, c => c.Body.Contains("Intune Two")).Body);
        Assert.False(second.RootElement.TryGetProperty("speakers", out _));
    }

    [Fact]
    public async Task Session_create_attaches_every_approved_speaker_regardless_of_ring()
    {
        // 🔒 §569 — THE ATTACH RULE, AND THE §326bx BOUNDARY IT MUST STILL RESPECT.
        //
        // Attaching a speaker's e-mail to a session create makes Zoho AUTO-CREATE the speaker AND
        // E-MAIL THEM AN INVITATION. 19 real speakers were invited prematurely on 2026-07-24, which
        // is why a RING filter was added here. That filter then became the live blocker: speakers
        // are Ring 3, the sync ring was Ring 2, so the attach set was EMPTY on every pass — and it
        // failed CLOSED, so it was silent (§571).
        //
        // His rule replaces "ring-eligible" with "categorized + active": setting a category on an
        // active speaker now MEANS "invite this person". So the boundary still exists — it is just
        // drawn at APPROVAL instead of at a ring.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        var id = await SeedSessionAsync(db, "Real Talk", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero));
        await LinkSpeakerAsync(db, id, "ring1@x.dk", Ring.Ring1);
        await LinkSpeakerAsync(db, id, "ring3@x.dk", Ring.Broad);

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-rt\"}"));
        var gate = await NewSpeakerAttachGateAsync(db);
        var r = await NewSessionSvc(db, zoho, gate: gate).RunAsync(EventId);

        Assert.Equal(1, r.Created);
        using var doc = JsonDocument.Parse(Assert.Single(SessionCalls(handler)).Body);
        Assert.Equal(new[] { "ring1@x.dk", "ring3@x.dk" },
            doc.RootElement.GetProperty("speakers").EnumerateArray()
                .Select(e => e.GetString()).OrderBy(e => e));

        // NO LONGER FAIL-CLOSED: with no gate wired the approved speaker still rides along. The old
        // empty-set default is exactly what made the live failure invisible.
        using var db2 = ScenarioFixture.NewDb();
        await SeedEditionAsync(db2, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        var id2 = await SeedSessionAsync(db2, "Gateless Talk", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero));
        await LinkSpeakerAsync(db2, id2, "someone@x.dk");
        var (zoho2, handler2) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-gl\"}"));
        var r2 = await NewSessionSvc(db2, zoho2).RunAsync(EventId);
        Assert.Equal(1, r2.Created);
        using var doc2 = JsonDocument.Parse(Assert.Single(SessionCalls(handler2)).Body);
        Assert.Equal(new[] { "someone@x.dk" },
            doc2.RootElement.GetProperty("speakers").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Session_push_fails_a_blank_track_session_with_no_session_post()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        var id = await SeedSessionAsync(db, "Trackless Talk", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero),
            track: null);

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"never\"}"));
        var r = await NewSessionSvc(db, zoho).RunAsync(EventId);

        Assert.Equal(1, r.Failed);
        Assert.Equal(0, r.Created);
        Assert.Empty(SessionCalls(handler));                  // no session POST
        Assert.DoesNotContain(handler.Calls,                  // and no track create attempt
            c => c.Method == HttpMethod.Post && c.Url.EndsWith("/tracks"));
        var item = Assert.Single(r.Items);
        Assert.Equal(SessionBackstagePushService.PushAction.Failed, item.Action);
        Assert.Contains("no track", item.Error);
        Assert.Null(db.Sessions.Single(s => s.Id == id).BackstageSessionId);
    }

    // ===================== §299 stage-2 PILOT (CreateOneAsync) ==============

    [Fact]
    public async Task Pilot_create_one_pushes_a_single_unlinked_session_even_at_stage1()
    {
        // The pilot is an EXPLICIT operator-authorized single push — deliberately no §57
        // direction gate (stage 1 here), and only the ONE named session is touched.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.SessionizeToCeh);
        var target = await SeedSessionAsync(db, "Azure MC", backstageId: null,
            new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 9, 17, 0, 0, TimeSpan.Zero));
        var other = await SeedSessionAsync(db, "Other Talk", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero));

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-pilot-1\"}"));
        var (ok, message, _) = await NewSessionSvc(db, zoho).CreateOneAsync(EventId, target);

        Assert.True(ok, message);
        Assert.Contains("bs-pilot-1", message);
        Assert.Equal("bs-pilot-1", db.Sessions.Single(s => s.Id == target).BackstageSessionId);
        Assert.Null(db.Sessions.Single(s => s.Id == other).BackstageSessionId); // ONLY the one
        var call = Assert.Single(SessionCalls(handler));
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.DoesNotContain("day=", call.Url);        // timed session: day derives from start_time
    }

    [Fact]
    public async Task Pilot_create_one_refuses_a_test_session()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.SessionizeToCeh);
        var id = await SeedSessionAsync(db, "Rehearsal", backstageId: null,
            new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero));
        db.Sessions.Single(s => s.Id == id).UsedForTesting = true;
        await db.SaveChangesAsync();

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"never\"}"));
        var (ok, message, _) = await NewSessionSvc(db, zoho).CreateOneAsync(EventId, id);

        Assert.False(ok);
        Assert.Contains("Test session", message);
        Assert.Empty(handler.Calls);                    // §299 4.5: never reaches Zoho
        Assert.Null(db.Sessions.Single(s => s.Id == id).BackstageSessionId);
    }

    [Fact]
    public async Task Pilot_create_one_refuses_an_already_linked_session()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.SessionizeToCeh);
        var id = await SeedSessionAsync(db, "Linked Talk", backstageId: "bs-existing",
            new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero));

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"never\"}"));
        var (ok, message, _) = await NewSessionSvc(db, zoho).CreateOneAsync(EventId, id);

        Assert.False(ok);
        Assert.Contains("Already linked", message);
        Assert.Empty(handler.Calls);                    // never double-creates
        Assert.Equal("bs-existing", db.Sessions.Single(s => s.Id == id).BackstageSessionId);
    }

    // ========= §299 bulk-create: venue (halls) + speakers + TrackNameMap ==========

    [Fact]
    public async Task CreateOne_resolves_existing_hall_by_name_and_sends_venue_and_speakers()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.SessionizeToCeh);
        var id = await SeedSessionAsync(db, "Azure MC", backstageId: null,
            new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 9, 16, 0, 0, TimeSpan.Zero),
            room: "Room-5-7-Floor 1-Max 122-MC");
        await LinkSpeakerAsync(db, id, "andreas@x.dk");
        await LinkSpeakerAsync(db, id, "berit@x.dk");

        var (zoho, handler) = NewZoho(
            (_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-mc-1\"}"),
            hallsJson: "{\"halls\":[{\"id\":\"hall-1\",\"name\":\"Room-5-7-Floor 1-Max 122-MC\"}]}");
        var gate = await NewSpeakerAttachGateAsync(db);
        var (ok, message, changes) = await NewSessionSvc(db, zoho, gate: gate).CreateOneAsync(EventId, id);

        Assert.True(ok, message);
        // The hall already exists → resolved by (case-insensitive) name, NO POST /halls.
        Assert.DoesNotContain(handler.Calls,
            c => c.Method == HttpMethod.Post && c.Url.EndsWith("/halls"));
        var call = Assert.Single(SessionCalls(handler));
        Assert.Equal(HttpMethod.Post, call.Method);
        using var doc = JsonDocument.Parse(call.Body);
        Assert.Equal("hall-1", doc.RootElement.GetProperty("venue").GetString());
        var speakers = doc.RootElement.GetProperty("speakers").EnumerateArray()
            .Select(e => e.GetString()).OrderBy(e => e).ToList();
        Assert.Equal(new[] { "andreas@x.dk", "berit@x.dk" }, speakers);
        // No hall was created ⇒ exactly one change line, the session.
        var line = Assert.Single(changes);
        Assert.Contains("Created session 'Azure MC'", line);
    }

    [Fact]
    public async Task CreateOne_lowercases_attached_speaker_emails_zoho_matches_case_sensitively()
    {
        // Live-verified 2026-07-24: Zoho stores speaker e-mails lowercased and matches a
        // session's attach list CASE-SENSITIVELY — a mixed-case e-mail is silently dropped
        // (the speaker never links). The engine must therefore lowercase every attach e-mail.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.SessionizeToCeh);
        var id = await SeedSessionAsync(db, "Case Test", backstageId: null,
            new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero));
        await LinkSpeakerAsync(db, id, "Hub-Test-Speaker-Guest@Expertslive.DK");

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-case-1\"}"));
        var gate = await NewSpeakerAttachGateAsync(db);
        var (ok, message, _) = await NewSessionSvc(db, zoho, gate: gate).CreateOneAsync(EventId, id);

        Assert.True(ok, message);
        var call = Assert.Single(SessionCalls(handler));
        using var doc = JsonDocument.Parse(call.Body);
        var speakers = doc.RootElement.GetProperty("speakers").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "hub-test-speaker-guest@expertslive.dk" }, speakers);
    }

    [Fact]
    public async Task CreateOne_creates_missing_hall_from_registry_capacity_then_uses_its_id()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.SessionizeToCeh);
        var id = await SeedSessionAsync(db, "Azure MC", backstageId: null,
            new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 9, 16, 0, 0, TimeSpan.Zero),
            room: "Room-5-7-Floor 1-Max 122-MC");

        // Zoho has NO halls; the registry knows the room WITH a capacity → create on demand.
        var (zoho, handler) = NewZoho((req, _) =>
            req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/halls")
                ? Json(HttpStatusCode.OK, "{\"hall\":{\"id\":\"hall-new\"}}")
                : Json(HttpStatusCode.OK, "{\"id\":\"bs-mc-2\"}"));
        var rooms = NewRooms(("Room-5-7-Floor 1-Max 122-MC", 122));
        var (ok, message, changes) = await NewSessionSvc(db, zoho, rooms).CreateOneAsync(EventId, id);

        Assert.True(ok, message);
        // POST /halls carried the LIVE-VERIFIED {name, capacity} shape (capacity REQUIRED).
        var hallPost = Assert.Single(handler.Calls,
            c => c.Method == HttpMethod.Post && c.Url.EndsWith("/halls"));
        using (var hallDoc = JsonDocument.Parse(hallPost.Body))
        {
            Assert.Equal("Room-5-7-Floor 1-Max 122-MC", hallDoc.RootElement.GetProperty("name").GetString());
            Assert.Equal(122, hallDoc.RootElement.GetProperty("capacity").GetInt32());
        }
        // The session create used the NEW hall id as its venue.
        var call = Assert.Single(SessionCalls(handler));
        using var doc = JsonDocument.Parse(call.Body);
        Assert.Equal("hall-new", doc.RootElement.GetProperty("venue").GetString());
        // Change lines: the created hall (operator notify rule) + the created session.
        Assert.Equal(2, changes.Count);
        Assert.Contains("Created hall 'Room-5-7-Floor 1-Max 122-MC' (Backstage id hall-new)", changes[0]);
        Assert.Contains("Created session 'Azure MC'", changes[1]);
    }

    [Fact]
    public async Task CreateOne_expo_room_sends_no_venue_and_creates_no_hall()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.SessionizeToCeh);
        var id = await SeedSessionAsync(db, "Expo Talk", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 9, 20, 0, TimeSpan.Zero),
            room: "Expo-Stage");

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-expo\"}"));
        // Registry knows the room but with a NULL capacity (expo) → never POST /halls.
        var rooms = NewRooms(("Expo-Stage", null));
        var (ok, _, _) = await NewSessionSvc(db, zoho, rooms).CreateOneAsync(EventId, id);

        Assert.True(ok);
        Assert.DoesNotContain(handler.Calls,
            c => c.Method == HttpMethod.Post && c.Url.EndsWith("/halls"));
        using var doc = JsonDocument.Parse(Assert.Single(SessionCalls(handler)).Body);
        Assert.False(doc.RootElement.TryGetProperty("venue", out _)); // warn-only: no venue
    }

    [Fact]
    public async Task CreateOne_creates_a_missing_track_with_the_sessionize_name()
    {
        // Operator 2026-07-23: "use track from Sessionize — create if missing". CEH track
        // "Intune" has no same-named Backstage track ⇒ POST /tracks {name:"Intune"}, then the
        // session is created with the NEW track id, and the change lines carry the track.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.SessionizeToCeh);
        var id = await SeedSessionAsync(db, "Intune Deep Dive", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero),
            track: "Intune");

        var (zoho, handler) = NewZoho((req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/tracks")
                ? Json(HttpStatusCode.OK, "{\"id\":\"track-intune\",\"name\":\"Intune\"}")
                : Json(HttpStatusCode.OK, "{\"id\":\"bs-intune\"}"),
            tracksJson: "{\"tracks\":[{\"track_id\":\"track-mw\",\"name\":\"MODERN WORKPLACE\"}]}");
        var (ok, message, changes) = await NewSessionSvc(db, zoho).CreateOneAsync(EventId, id);

        Assert.True(ok, message);
        var trackPost = Assert.Single(handler.Calls.Where(c =>
            c.Method == HttpMethod.Post && c.Url.Contains("/tracks")));
        using var trackDoc = JsonDocument.Parse(trackPost.Body);
        Assert.Equal("Intune", trackDoc.RootElement.GetProperty("name").GetString());
        using var doc = JsonDocument.Parse(Assert.Single(SessionCalls(handler)).Body);
        Assert.Equal("track-intune", doc.RootElement.GetProperty("track").GetString());
        Assert.Contains(changes, c => c.Contains("Created track 'Intune'") && c.Contains("track-intune"));
    }

    [Fact]
    public async Task CreateOne_never_fuzzy_matches_a_track_creates_the_full_sessionize_name()
    {
        // "Data Compliance & Security" contains "Security" — a fuzzy rule would mis-file it
        // under the SECURITY track. Exact-only: no match ⇒ a NEW track with the full name.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.SessionizeToCeh);
        var id = await SeedSessionAsync(db, "Purview MC", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero),
            track: "Data Compliance & Security");

        var (zoho, handler) = NewZoho((req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/tracks")
                ? Json(HttpStatusCode.OK, "{\"id\":\"track-dcs\",\"name\":\"Data Compliance & Security\"}")
                : Json(HttpStatusCode.OK, "{\"id\":\"bs-dcs\"}"),
            tracksJson: "{\"tracks\":[{\"track_id\":\"track-sec\",\"name\":\"SECURITY\"}]}");
        var (ok, message, _) = await NewSessionSvc(db, zoho).CreateOneAsync(EventId, id);

        Assert.True(ok, message);
        using var doc = JsonDocument.Parse(Assert.Single(SessionCalls(handler)).Body);
        Assert.Equal("track-dcs", doc.RootElement.GetProperty("track").GetString()); // NOT track-sec
    }

    [Fact]
    public async Task CreateOne_maps_the_long_sessionize_ai_label_to_the_short_zoho_track()
    {
        // Operator 2026-07-23 mapping: the two AI tracks carry SHORTER names in Zoho.
        // "AI for Makers (Copilot & Agents)" must reuse the existing "AI for Makers" track —
        // no create, no fuzzy logic.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.SessionizeToCeh);
        var id = await SeedSessionAsync(db, "AI Low Code MC", backstageId: null,
            new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 9, 16, 0, 0, TimeSpan.Zero),
            track: "AI for Makers (Copilot & Agents)");

        var (zoho, handler) = NewZoho(
            (_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-ai\"}"),
            tracksJson: "{\"tracks\":[{\"track_id\":\"track-makers\",\"name\":\"AI for Makers\"}]}");
        var (ok, message, changes) = await NewSessionSvc(db, zoho).CreateOneAsync(EventId, id);

        Assert.True(ok, message);
        Assert.DoesNotContain(handler.Calls, c =>
            c.Method == HttpMethod.Post && c.Url.Contains("/tracks"));   // reused, not created
        using var doc = JsonDocument.Parse(Assert.Single(SessionCalls(handler)).Body);
        Assert.Equal("track-makers", doc.RootElement.GetProperty("track").GetString());
        Assert.DoesNotContain(changes, c => c.Contains("Created track"));
    }

    [Fact]
    public async Task CreateOne_fails_clearly_when_the_session_has_no_track()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.SessionizeToCeh);
        var id = await SeedSessionAsync(db, "Trackless Talk", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero),
            track: null);

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"never\"}"));
        var (ok, message, changes) = await NewSessionSvc(db, zoho).CreateOneAsync(EventId, id);

        Assert.False(ok);
        Assert.Contains("no track", message);
        Assert.Empty(SessionCalls(handler));
        Assert.Empty(changes);
        Assert.Null(db.Sessions.Single(s => s.Id == id).BackstageSessionId);
    }

    [Fact]
    public async Task CreateOne_with_suppressNotify_sends_no_per_call_mail_and_returns_change_lines()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.SessionizeToCeh);
        var a = await SeedSessionAsync(db, "MC One", backstageId: null,
            new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 9, 16, 0, 0, TimeSpan.Zero));
        var b = await SeedSessionAsync(db, "MC Two", backstageId: null,
            new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 9, 16, 0, 0, TimeSpan.Zero));

        var n = 0;
        var (zoho, _) = NewZoho((_, _) => Json(HttpStatusCode.OK, $"{{\"id\":\"bs-bulk-{++n}\"}}"));
        var (svc, mail) = NewSessionSvcWithChangeMail(db, zoho);

        // BULK caller contract: suppress the per-call mail, batch the returned lines itself.
        var (ok1, _, changes1) = await svc.CreateOneAsync(EventId, a, suppressNotify: true);
        var (ok2, _, changes2) = await svc.CreateOneAsync(EventId, b, suppressNotify: true);

        Assert.True(ok1);
        Assert.True(ok2);
        Assert.Empty(mail.Sent);                        // ZERO per-call mails
        Assert.Contains("Created session 'MC One'", Assert.Single(changes1));
        Assert.Contains("Created session 'MC Two'", Assert.Single(changes2));
        // The bulk caller can now send ONE batched mail from changes1+changes2 (the
        // notifier's own tests cover the mail shape).
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

    // ====== operator 2026-07-23: CEH→Zoho change mail to info@expertslive.dk ======

    [Fact]
    public async Task Successful_create_sends_one_zoho_change_mail_to_the_ops_mailbox()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        await SeedSessionAsync(db, "Talk A", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 9, 50, 0, TimeSpan.Zero));

        var (zoho, _) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-new-1\"}"));
        var (svc, mail) = NewSessionSvcWithChangeMail(db, zoho);

        var r = await svc.RunAsync(EventId);

        Assert.Equal(1, r.Created);
        // ONE batched mail per pass (never per item), to the EVENT ops mailbox, listing
        // the created session — the operator must publish/delete manually in Backstage.
        var (to, subject, html, _) = Assert.Single(mail.Messages);
        Assert.Equal(ZohoChangeNotifier.Recipient, to);   // §493: system alerts go to the operator mailbox
        Assert.Contains("[CEH→Zoho] Agenda / sessions", subject);
        Assert.Contains("1 change(s)", subject);
        Assert.Contains("Talk A", html);
        Assert.Contains("bs-new-1", html);
        Assert.Contains("not auto-published", html);
    }

    [Fact]
    public async Task Failed_create_sends_no_zoho_change_mail()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        await SeedSessionAsync(db, "Talk A", backstageId: null,
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero));

        var (zoho, _) = NewZoho((_, _) => Json(HttpStatusCode.BadRequest, "{\"message\":\"bad\"}"));
        var (svc, mail) = NewSessionSvcWithChangeMail(db, zoho);

        var r = await svc.RunAsync(EventId);

        // Nothing was actually written to Zoho ⇒ no change mail (empty batch is silent).
        Assert.Equal(1, r.Failed);
        Assert.Equal(0, r.Created);
        Assert.Empty(mail.Sent);
    }

    // ============ §569 — the §58 speaker direction gate is REMOVED ============

    [Theory]
    [InlineData(SessionSyncDirection.SessionizeToCeh)]
    [InlineData(SessionSyncDirection.ZohoToCeh)]
    [InlineData(SessionSyncDirection.CehToZoho)]
    public async Task Speaker_push_runs_whatever_the_stored_direction_says(SessionSyncDirection dir)
    {
        // §569 — same removal, same reasoning as the session direction gate above.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, sessionDir: SessionSyncDirection.CehToZoho, speakerDir: dir);
        await SeedSpeakerAsync(db, "sam@x.dk", backstageId: null, selectedForPublish: true);

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"x\"}"));
        var r = await NewSpeakerSvc(db, zoho).RunAsync(EventId);

        Assert.True(r.DirectionActive);
        Assert.Null(r.InactiveReason);
        Assert.Equal(1, r.Created);
        Assert.Equal("x", db.SpeakerProfiles.Single().BackstageSpeakerId);
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

        // §304: the pass may also GET the live speaker index (adopt-by-email) —
        // count WRITES only.
        var call = Assert.Single(handler.Calls, c => c.Method == HttpMethod.Post);
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
        // No WRITE calls — the §301b self-heal may read the live speaker index (GET),
        // but a linked speaker must never be re-POSTed (no duplicate create).
        Assert.DoesNotContain(handler.Calls, c => c.Method != HttpMethod.Get);
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

        using var doc = JsonDocument.Parse(
            Assert.Single(handler.Calls, c => c.Method == HttpMethod.Post).Body);
        Assert.False(doc.RootElement.GetProperty("featured").GetBoolean()); // HARD publish gate
    }

    // ==== stage-2 go-live: approved-only speakers + backstage-speaker-sync ring ====

    /// <summary>Seed the backstage-speaker-sync kill switch for the edition (the released
    /// ring stays the CATALOG default, Ring1 — no override row).</summary>
    private static async Task SeedSpeakerSyncFeatureAsync(CommunityHubDbContext db, bool enabled)
    {
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = EventId, FeatureKey = "backstage-speaker-sync", Enabled = enabled,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Speaker_push_HOLDS_a_speaker_who_has_not_completed_Get_Started()
    {
        // §415 (operator 2026-07-27: "dont push to zoho before the get started have completed -
        // otherwise it will fail"). His run failed 4 of 4 with HTTP 400 "The country code must be
        // in ISO Alpha-2 format" — an incomplete profile CANNOT satisfy Zoho's validation, so
        // pushing it burns an API call and puts an alarming failure line in the ops mail on every
        // pass. Holding is the correct outcome, not retrying.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerSyncFeatureAsync(db, enabled: true);

        var ready = await SeedSpeakerAsync(db, "done@x.dk", null, selectedForPublish: true);
        var notStarted = await SeedSpeakerAsync(db, "todo@x.dk", null, selectedForPublish: true,
            getStartedDone: false);

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-ok\"}"));
        var r = await NewSpeakerSvc(db, zoho, new FeatureGateService(db)).RunAsync(EventId);

        // Only the completed speaker reached Zoho.
        Assert.Equal(1, r.Created);
        Assert.Equal(1, r.Skipped);
        var call = Assert.Single(handler.Calls, c => c.Method == HttpMethod.Post);
        Assert.Contains("done@x.dk", call.Body);
        Assert.DoesNotContain("todo@x.dk", call.Body);

        var held = Assert.Single(r.Items, i => i.ParticipantId == notStarted
            && i.Action == SpeakerBackstagePushService.PushAction.Skipped);
        Assert.Contains("Get Started not completed", held.Error);

        // Held, not failed — nothing was written for them, so the next pass retries cleanly once
        // they finish onboarding.
        Assert.Null(db.SpeakerProfiles.Single(p => p.ParticipantId == notStarted).BackstageSpeakerId);
        Assert.Equal("bs-ok", db.SpeakerProfiles.Single(p => p.ParticipantId == ready).BackstageSpeakerId);
    }

    [Fact]
    public async Task Speaker_push_gates_on_approval_only_and_the_ring_is_irrelevant()
    {
        // 🔒 §569 (operator 2026-07-28, verbatim): "yes, approved speaker syncs regardless of ring
        // - remove those gates. we dont need more gates here. lets simplify. once the speaker
        // category is set + they are active, they must sync to zoho."
        //
        // THE WHOLE CONDITION: SpeakerProfile.Category set AND the participant active. The ring
        // test that used to live here is what made every speaker read "outside the released ring —
        // held" on /Organizer/PendingSpeakers (§550) — speakers sit at Ring 3, the sync ring was
        // Ring 2 — and it is what silently blocked the live push (§571).
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerSyncFeatureAsync(db, enabled: true); // kill switch ON, ring = catalog Ring1

        var approved = await SeedSpeakerAsync(db, "ring1@x.dk", null, selectedForPublish: true);
        // THE REGRESSION THIS PINS: a Ring-3 speaker is APPROVED, so they sync. Ring plays no part.
        var ring3 = await SeedSpeakerAsync(db, "ring3@x.dk", null, selectedForPublish: true,
            ring: Ring.Broad);
        var inactive = await SeedSpeakerAsync(db, "inactive@x.dk", null, selectedForPublish: true,
            isActive: false);
        var uncategorized = await SeedSpeakerAsync(db, "uncat@x.dk", null, selectedForPublish: true,
            category: null);

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-ok\"}"));
        var r = await NewSpeakerSvc(db, zoho, new FeatureGateService(db)).RunAsync(EventId);

        // BOTH approved speakers reached Zoho — only the un-approved two are held.
        Assert.Equal(2, r.Created);
        Assert.Equal(2, r.Skipped);
        var bodies = handler.Calls.Where(c => c.Method == HttpMethod.Post).Select(c => c.Body).ToList();
        Assert.Contains(bodies, b => b.Contains("ring1@x.dk"));
        Assert.Contains(bodies, b => b.Contains("ring3@x.dk"));
        Assert.Equal("bs-ok",
            db.SpeakerProfiles.Single(p => p.ParticipantId == approved).BackstageSpeakerId);
        Assert.Equal("bs-ok",
            db.SpeakerProfiles.Single(p => p.ParticipantId == ring3).BackstageSpeakerId);

        SpeakerBackstagePushService.SpeakerPushResult Held(int id) =>
            Assert.Single(r.Items, i => i.ParticipantId == id
                && i.Action == SpeakerBackstagePushService.PushAction.Skipped);
        Assert.Contains("not approved", Held(inactive).Error);
        Assert.Contains("not approved", Held(uncategorized).Error);
    }

    [Fact]
    public async Task Speaker_push_kill_switch_still_holds_everyone()
    {
        // §569 keeps the backstage-speaker-sync ON/OFF switch — it is a "stop everything now"
        // control, not a rollout gate. Only the RING behind it was retired, so this must still pass.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerSyncFeatureAsync(db, enabled: false);
        await SeedSpeakerAsync(db, "ring1@x.dk", null, selectedForPublish: true);

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-ok\"}"));
        var r = await NewSpeakerSvc(db, zoho, new FeatureGateService(db)).RunAsync(EventId);

        Assert.Equal(0, r.Created);
        Assert.Equal(1, r.Skipped);
        Assert.DoesNotContain(handler.Calls, c => c.Method == HttpMethod.Post);
        Assert.Contains("feature is disabled", Assert.Single(r.Items).Error);
    }

    [Fact]
    public async Task Speaker_push_holds_a_not_yet_activated_speaker()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerSyncFeatureAsync(db, enabled: true);
        await SeedSpeakerAsync(db, "pre@x.dk", null, selectedForPublish: true,
            lifecycle: ParticipantLifecycleState.Preselected);

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"never\"}"));
        var r = await NewSpeakerSvc(db, zoho, new FeatureGateService(db)).RunAsync(EventId);

        Assert.Equal(0, r.Created);
        Assert.Equal(1, r.Skipped);
        Assert.Empty(handler.Calls);
        Assert.Contains("not approved", Assert.Single(r.Items).Error);
    }

    [Fact]
    public async Task Speaker_push_disabled_feature_holds_every_speaker()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerSyncFeatureAsync(db, enabled: false);   // kill switch OFF
        await SeedSpeakerAsync(db, "ring1@x.dk", null, selectedForPublish: true); // fully approved

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"never\"}"));
        var r = await NewSpeakerSvc(db, zoho, new FeatureGateService(db)).RunAsync(EventId);

        Assert.Equal(0, r.Created);
        Assert.Equal(1, r.Skipped);
        Assert.Empty(handler.Calls);
        Assert.Contains("disabled", Assert.Single(r.Items).Error);
        Assert.Null(db.SpeakerProfiles.Single().BackstageSpeakerId);
    }

    // ==== §301b SELF-HEAL: a stored Backstage id deleted in the UI → NULL + re-create ====

    [Fact]
    public async Task Speaker_heal_recreates_when_stored_id_was_deleted_in_backstage()
    {
        // Operator 2026-07-24: "if id doesn't exist … null the record and create/sync again".
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerAsync(db, "sam@x.dk", backstageId: "bs-gone", selectedForPublish: true);

        var (zoho, handler) = NewZoho((req, _) =>
            req.Method == HttpMethod.Get
                // live index NON-EMPTY (fail-safe gate) but sam's id + e-mail are absent
                ? Json(HttpStatusCode.OK, "{\"speakers\":[{\"id\":\"bs-live-1\",\"email\":\"other@x.dk\"}]}")
                : Json(HttpStatusCode.OK, "{\"id\":\"bs-new\"}"));
        var r = await NewSpeakerSvc(db, zoho).RunAsync(EventId);

        Assert.Equal(1, r.Created);
        Assert.Equal(0, r.AlreadyLinked);
        Assert.Equal("bs-new", db.SpeakerProfiles.Single().BackstageSpeakerId);   // healed + re-created
        Assert.Single(handler.Calls, c => c.Method == HttpMethod.Post);           // exactly one create
    }

    [Fact]
    public async Task Speaker_heal_relinks_by_email_when_record_exists_under_another_id()
    {
        // Deleted + manually re-created in the UI ⇒ same e-mail, new id → adopt it, no POST.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerAsync(db, "sam@x.dk", backstageId: "bs-gone", selectedForPublish: true);

        var (zoho, handler) = NewZoho((req, _) =>
            req.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, "{\"speakers\":[{\"id\":\"bs-new-ui\",\"email\":\"sam@x.dk\"}]}")
                : Json(HttpStatusCode.OK, "{\"id\":\"should-not-happen\"}"));
        var r = await NewSpeakerSvc(db, zoho).RunAsync(EventId);

        Assert.Equal(0, r.Created);
        Assert.Equal(1, r.AlreadyLinked);
        Assert.Equal("bs-new-ui", db.SpeakerProfiles.Single().BackstageSpeakerId);  // re-linked
        Assert.DoesNotContain(handler.Calls, c => c.Method == HttpMethod.Post);     // no duplicate
    }

    [Fact]
    public async Task Session_heal_recreates_when_stored_id_was_deleted_in_backstage()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        var id = await SeedSessionAsync(db, "Healed Session", backstageId: "bs-sess-gone",
            new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero));

        var (zoho, handler) = NewZoho((req, _) =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/agendas"))
                return Json(HttpStatusCode.OK, "{\"agendas\":[{\"index\":0},{\"index\":1}]}");
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/sessions"))
                // live set NON-EMPTY (fail-safe gate) but bs-sess-gone is absent
                return Json(HttpStatusCode.OK, "{\"sessions\":[{\"id\":\"bs-other\"}]}");
            return Json(HttpStatusCode.OK, "{\"id\":\"bs-sess-new\"}");
        });
        var gate = await NewSpeakerAttachGateAsync(db);
        var r = await NewSessionSvc(db, zoho, gate: gate).RunAsync(EventId);

        // §555 — the contract CHANGED, deliberately. A stored id that a COMPLETE live read shows is
        // absent is now REPORTED for operator approval, not silently unlinked and re-created.
        //
        // Operator 2026-07-28: "i dont want to end up have double of sessions + double speakers +
        // double sponsors … alternative let me as organizer approve it". An unlink IS a create, and
        // this API never deletes — so a wrong one duplicates the live agenda irreversibly.
        Assert.Equal(0, r.Created);
        Assert.Equal("bs-sess-gone", db.Sessions.Single().BackstageSessionId);  // link left intact
        Assert.DoesNotContain(SessionCalls(handler), c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Speaker_push_sends_skills_from_accreditation_and_company_via_field_map()
    {
        // §302b bug fixes: (1) "Skills are also missing in zoho" — the old
        // "MvpCategories ?? Accreditation" masked the accreditation checkboxes; the ONE
        // ZohoFieldMap derivation combines both, FULL labels kept. (2) company name is
        // hub-collected (absent from Sessionize) and rides the create when filled.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerAsync(db, "sam@x.dk", backstageId: null, selectedForPublish: true);
        var profile = db.SpeakerProfiles.Single();
        profile.Accreditation = "Microsoft MVP, Microsoft MCT";
        profile.MvpCategories = "Security";
        profile.CompanyName = "Contoso ApS";
        await db.SaveChangesAsync();

        var (zoho, handler) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-sp-map\"}"));
        var r = await NewSpeakerSvc(db, zoho).RunAsync(EventId);

        Assert.Equal(1, r.Created);
        var call = Assert.Single(handler.Calls, c => c.Method == HttpMethod.Post);
        using var doc = JsonDocument.Parse(call.Body);
        Assert.Equal("Microsoft MVP, Microsoft MCT, Security",
            doc.RootElement.GetProperty("skills").GetString());       // full labels, combined
        Assert.Equal("Contoso ApS", doc.RootElement.GetProperty("company").GetString());
    }

    [Fact]
    public void ZohoFieldMap_session_tags_derive_level_language_and_sessionize_labels()
    {
        // §302c: the Sessionize Level VALUE passes through verbatim ("Advanced (300)",
        // "Expert (400)", "Black Belt (500)") so Sessionize and Zoho show identical spelling,
        // plus a MANDATORY language tag, plus unbounded label passthrough (100+ options).
        //
        // 🔒 §594b — NOTE THE ABSENCE OF A COLON, AND DO NOT "RESTORE" IT.
        // Zoho Backstage tags are a REUSABLE, EVENT-WIDE vocabulary that already contains
        // "Session Level Expert (400)" / "(300)" / "(500)" and "Session Language English", and the
        // operator can neither rename nor delete them: *"we cannot add that or change that, so it
        // must be in the code that there is differences from sessionize"*. Emitting
        // "Session Level: …" would not match an existing tag — it would create a SECOND,
        // near-identical tag in that shared vocabulary, permanently.
        var s = new Session
        {
            Level = "Black Belt (500)",
            Tags = "Microsoft Copilot, Microsoft Teams",
        };
        Assert.Equal(new[]
        {
            "Session Level Black Belt (500)",
            "Session Language English",
            "Microsoft Copilot", "Microsoft Teams",
        }, ZohoFieldMap.SessionTags(s));

        // 1:1 UNBOUNDED passthrough (operator: "there are 100+ tags which speakers can
        // add … they must be pushed to zoho if they were in the submission and chosen"):
        // EVERY chosen label ports verbatim — no fixed list, no cap, order preserved.
        var many = "Security, Microsoft Defender for Endpoint, Microsoft Sentinel, Zero Trust, "
                 + "Passkeys, Entra ID, Conditional Access Authentication Contexts, AVD/W365, "
                 + "Azure Kubernetes Service, PowerShell DSC, Terrafom";
        var ported = ZohoFieldMap.SessionTags(new Session { Level = "Advanced (300)", Tags = many });
        Assert.Equal(
            new[] { "Session Level Advanced (300)", "Session Language English" }
                .Concat(many.Split(", ")).ToArray(),
            ported);

        // No level, no labels → still the mandatory language tag.
        Assert.Equal(new[] { "Session Language English" }, ZohoFieldMap.SessionTags(new Session()));
    }

    [Fact]
    public async Task Session_diff_mails_gui_only_tags_and_description_to_add_in_backstage()
    {
        // §302c: Tags + Session Description are GuiOnly (create refuses them; no update
        // API) — the linked-session diff must mail them as ACTION lines in GUI names.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.CehToZoho, SessionSyncDirection.SessionizeToCeh);
        var id = await SeedSessionAsync(db, "Talk A", backstageId: "bs-existing",
            new DateTimeOffset(2027, 2, 10, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 10, 12, 0, 0, TimeSpan.Zero));
        var session = db.Sessions.Single();
        session.Level = "Expert (400)";
        session.Tags = "Microsoft Copilot";
        await db.SaveChangesAsync();

        HttpResponseMessage Respond(HttpRequestMessage req, string _)
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/agendas"))
                return Json(HttpStatusCode.OK, "{\"agendas\":[{\"index\":0}]}");
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/sessions"))
                // LIVE Zoho matches title/time/duration but has NO description and NO tags.
                return Json(HttpStatusCode.OK,
                    "{\"sessions\":[{\"id\":\"bs-existing\",\"title\":\"Talk A\",\"start_time\":\"2027-02-10T11:00:00Z\",\"duration\":60}]}");
            return Json(HttpStatusCode.OK, "{\"status\":\"success\"}");
        }
        var (zoho, _) = NewZoho(Respond);
        var (svc, mail) = NewSessionSvcWithChangeMail2(db, zoho);

        var r = await svc.RunAsync(EventId);

        Assert.Equal(1, r.Updated);
        var (_, _, html, _) = Assert.Single(mail.Messages);

        // DESCRIPTION is still diffed — the agenda read DOES return `description`, so an empty one
        // is a real, closable gap: he pastes the text and the next pass stops reporting it.
        Assert.Contains("Session Description", html);            // the GUI field name
        Assert.Contains("About Talk A", html);                   // the CEH abstract to paste

        // 🔒 §594 — TAGS ARE NOT DIFFED, because the agenda read returns NO `tags` property at all
        // (verified live). Diffing them made every expected tag permanently "missing": the operator
        // pasted them in, the next pass reported them missing again, and no action could ever
        // satisfy it. An unclosable ACTION NEEDED line is worse than none — it is the §582
        // "honor the limitations" rule, and the trust cost is the whole point.
        //
        // Tags are reported ONCE at create time instead (DescribeCreateGaps, §574) — the only
        // moment they are actionable anyway, since the sessions API is create-only.
        Assert.DoesNotContain("Tags missing", html);
        Assert.DoesNotContain("paste the line below into the Tags box", html);
    }

    [Fact]
    public void IntegrationFieldMap_generic_compare_covers_every_field_kind()
    {
        // §302e/§303 — the ONE generic compare rule set (operator: "100% bullet-proof
        // generic concept that works for any integration and any type of field").
        static IntegrationFieldMap.Field F(IntegrationFieldMap.FieldKind k,
            IntegrationFieldMap.ReadSupport r = IntegrationFieldMap.ReadSupport.List) =>
            new("X", "x", "X", Kind: k, Read: r);

        // String: trimmed ordinal compare; missing-or-different ⇒ update.
        Assert.Equal(IntegrationFieldMap.FieldVerdict.UpToDate, IntegrationFieldMap.Compare(F(IntegrationFieldMap.FieldKind.String), " a ", "a"));
        Assert.Equal(IntegrationFieldMap.FieldVerdict.NeedsUpdate, IntegrationFieldMap.Compare(F(IntegrationFieldMap.FieldKind.String), "a", "b"));
        Assert.Equal(IntegrationFieldMap.FieldVerdict.NeedsUpdate, IntegrationFieldMap.Compare(F(IntegrationFieldMap.FieldKind.String), "a", null));
        // Empty source ⇒ nothing to push ⇒ valid.
        Assert.Equal(IntegrationFieldMap.FieldVerdict.UpToDate, IntegrationFieldMap.Compare(F(IntegrationFieldMap.FieldKind.String), null, "b"));

        // MultiLine + Object: EXISTENCE check (rich text reformats; merged objects —
        // operator: "just validate it exist, then i am happy").
        Assert.Equal(IntegrationFieldMap.FieldVerdict.UpToDate, IntegrationFieldMap.Compare(F(IntegrationFieldMap.FieldKind.MultiLine), "long text", "reformatted"));
        Assert.Equal(IntegrationFieldMap.FieldVerdict.NeedsUpdate, IntegrationFieldMap.Compare(F(IntegrationFieldMap.FieldKind.MultiLine), "long text", " "));
        Assert.Equal(IntegrationFieldMap.FieldVerdict.UpToDate, IntegrationFieldMap.Compare(F(IntegrationFieldMap.FieldKind.Object), "url", "{present}"));
        Assert.Equal(IntegrationFieldMap.FieldVerdict.NeedsUpdate, IntegrationFieldMap.Compare(F(IntegrationFieldMap.FieldKind.Object), "url", null));

        // Integer: numeric compare.
        Assert.Equal(IntegrationFieldMap.FieldVerdict.UpToDate, IntegrationFieldMap.Compare(F(IntegrationFieldMap.FieldKind.Integer), "60", " 60"));
        Assert.Equal(IntegrationFieldMap.FieldVerdict.NeedsUpdate, IntegrationFieldMap.Compare(F(IntegrationFieldMap.FieldKind.Integer), "60", "45"));

        // Array: every expected item present (case-insensitive); target extras are fine.
        Assert.Equal(IntegrationFieldMap.FieldVerdict.UpToDate,
            IntegrationFieldMap.Compare(F(IntegrationFieldMap.FieldKind.Array), "A, B", "b, a, extra"));
        Assert.Equal(IntegrationFieldMap.FieldVerdict.NeedsUpdate,
            IntegrationFieldMap.Compare(F(IntegrationFieldMap.FieldKind.Array), "A, B, C", "a, b"));

        // NotReadable: no live compare possible ⇒ engines use the CEH push-stamp (§302d).
        Assert.Equal(IntegrationFieldMap.FieldVerdict.Unverifiable,
            IntegrationFieldMap.Compare(F(IntegrationFieldMap.FieldKind.Object, IntegrationFieldMap.ReadSupport.NotReadable), "url", null));
    }

    [Fact]
    public void Fieldmap_json_overrides_speaker_rows_and_falls_back_when_invalid()
    {
        // §303c schema v2 (explicit source{}/target{} blocks): a loaded map file
        // OVERRIDES the ZohoFieldMap code defaults; a broken file is rejected
        // fail-soft (code rows stay).
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"fm-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, """
                {
                  "integration": "ceh-to-zoho-backstage",
                  "direction": "Outbound",
                  "records": { "Speaker": { "fields": [
                    {
                      "source": { "field": "Tagline", "type": "String" },
                      "target": { "field": "designation", "gui": "Designation (TEST-OVERRIDE)", "type": "String" },
                      "compare": "String"
                    }
                  ] } }
                }
                """);
            var map = IntegrationFieldMap.LoadMapFile(path);
            Assert.NotNull(map);
            ZohoFieldMap.ApplyMapFile(map);
            Assert.Equal("Designation (TEST-OVERRIDE)", ZohoFieldMap.Speaker.Tagline.GuiLabel);
            // Un-overridden rows keep their code defaults.
            Assert.Equal("Skills (comma separated)", ZohoFieldMap.Speaker.Skills.GuiLabel);
            File.Delete(path);

            // Invalid enum value ⇒ reported as a REAL config error (never silently mis-map).
            var bad = Path.Combine(Path.GetTempPath(), $"fm-{Guid.NewGuid():N}.json");
            File.WriteAllText(bad, """
                { "records": { "Speaker": { "fields": [
                  {
                    "source": { "field": "Tagline" },
                    "target": { "field": "designation", "gui": "X" },
                    "compare": "NotAKind"
                  }
                ] } } }
                """);
            var errors = new List<string>();
            Assert.NotNull(IntegrationFieldMap.LoadMapFile(bad, errors.Add));
            Assert.Contains(errors, e => e.Contains("NotAKind"));
            File.Delete(bad);
        }
        finally { ZohoFieldMap.ResetOverrides(); }
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void Shipped_zoho_fieldmap_json_parses_and_covers_every_code_speaker_field()
    {
        // §303: the REAL shipped ceh-to-zoho-backstage.fieldmap.json must parse and
        // contain a row for EVERY code-default speaker field — config drift (a field
        // added in code but not in the file) is caught here.
        var errors = new List<string>();
        var map = IntegrationFieldMap.LoadMapFile(
            FindRepoFile(Path.Combine("config", "integrations", "ceh-to-zoho-backstage.fieldmap.json")),
            errors.Add);
        Assert.True(errors.Count == 0, string.Join("; ", errors));
        Assert.NotNull(map);
        Assert.Equal("CEH", map!.SourceSystem);
        Assert.Equal("Zoho Backstage", map.TargetSystem);
        Assert.True(map.Fields.TryGetValue("Speaker", out var rows));
        // The entity endpoints ride the target block.
        Assert.True(map.Entities.TryGetValue("Speaker", out var entity));
        Assert.Equal("/speakers", entity!.ListEndpoint);
        Assert.Null(entity.UpdateEndpoint);   // create-only, explicit

        try
        {
            ZohoFieldMap.ResetOverrides();   // compare against the CODE defaults
            foreach (var field in new[]
            {
                ZohoFieldMap.Speaker.FirstName, ZohoFieldMap.Speaker.LastName,
                ZohoFieldMap.Speaker.Company, ZohoFieldMap.Speaker.Country,
                ZohoFieldMap.Speaker.Tagline, ZohoFieldMap.Speaker.Biography,
                ZohoFieldMap.Speaker.Skills, ZohoFieldMap.Speaker.LinkedIn,
                ZohoFieldMap.Speaker.Twitter, ZohoFieldMap.Speaker.Featured,
            })
            {
                Assert.True(rows!.TryGetValue(field.CehField, out var json),
                    $"fieldmap json is missing speaker row '{field.CehField}'");
                // The file must agree with the code defaults (it IS the authority once loaded).
                Assert.Equal(field.ApiField, json!.ApiField);
                Assert.Equal(field.GuiLabel, json.GuiLabel);
                Assert.Equal(field.Kind, json.Kind);
            }
        }
        finally { ZohoFieldMap.ResetOverrides(); }
    }

    [Fact]
    public void Shipped_sessionize_fieldmap_json_parses_with_inbound_direction()
    {
        // §303c: the sessionize-to-ceh map (design-first documentation of the import)
        // must parse; direction is INBOUND (Sessionize is the source, CEH the target),
        // and the canonical tag rule rides the Session.Tags row.
        var errors = new List<string>();
        var map = IntegrationFieldMap.LoadMapFile(
            FindRepoFile(Path.Combine("config", "integrations", "sessionize-to-ceh.fieldmap.json")),
            errors.Add);
        Assert.True(errors.Count == 0, string.Join("; ", errors));
        Assert.NotNull(map);
        Assert.Equal("Sessionize", map!.SourceSystem);
        Assert.Equal("CEH", map.TargetSystem);
        Assert.True(map.Fields.TryGetValue("Session", out var session));
        Assert.All(session!.Values, f => Assert.Equal(IntegrationFieldMap.Direction.Inbound, f.Direction));
        Assert.True(map.Fields.TryGetValue("Speaker", out var speaker));
        Assert.NotEmpty(speaker!);
    }

    [Fact]
    public void Shipped_zoho_fieldmap_covers_the_session_sponsor_and_exhibitor_slices()
    {
        // §323: the Sessions + Sponsors slices joined the shipped file — every code
        // Session/Sponsor/Exhibitor row must have a matching JSON row (drift guard).
        var map = IntegrationFieldMap.LoadMapFile(
            FindRepoFile(Path.Combine("config", "integrations", "ceh-to-zoho-backstage.fieldmap.json")));
        Assert.NotNull(map);

        try
        {
            ZohoFieldMap.ResetOverrides();   // compare against the CODE defaults

            Assert.True(map!.Fields.TryGetValue("Session", out var session));
            foreach (var field in new[]
            {
                ZohoFieldMap.Session.Title, ZohoFieldMap.Session.StartTime,
                ZohoFieldMap.Session.Duration, ZohoFieldMap.Session.Track,
                ZohoFieldMap.Session.Hall, ZohoFieldMap.Session.Speakers,
                ZohoFieldMap.Session.Tags, ZohoFieldMap.Session.Description,
            })
            {
                Assert.True(session!.TryGetValue(field.CehField, out var json),
                    $"fieldmap json is missing session row '{field.CehField}'");
                Assert.Equal(field.ApiField, json!.ApiField);
                Assert.Equal(field.GuiLabel, json.GuiLabel);
                Assert.Equal(field.Capability, json.Capability);
            }
            // The GuiOnly pair is the load-bearing §302c rule — pin it explicitly.
            Assert.Equal(IntegrationFieldMap.Capability.GuiOnly, session!["Abstract"].Capability);
            Assert.Equal(IntegrationFieldMap.Capability.GuiOnly, session["Level"].Capability);

            Assert.True(map.Fields.TryGetValue("Sponsor", out var sponsor));
            Assert.Equal(ZohoFieldMap.Sponsor.Description.ApiField, sponsor!["CompanyDescription"].ApiField);
            Assert.Equal(IntegrationFieldMap.ReadSupport.DetailOnly, sponsor["CompanyDescription"].Read);
            Assert.Equal(ZohoFieldMap.Sponsor.Website.ApiField, sponsor["WebsiteUrl"].ApiField);

            Assert.True(map.Fields.TryGetValue("Exhibitor", out var exhibitor));
            foreach (var field in new[]
            {
                ZohoFieldMap.Sponsor.Overview, ZohoFieldMap.Sponsor.LinkedIn,
                ZohoFieldMap.Sponsor.Twitter, ZohoFieldMap.Sponsor.ContactEmail,
                ZohoFieldMap.Sponsor.Booth,
            })
            {
                Assert.True(exhibitor!.TryGetValue(field.CehField, out var json),
                    $"fieldmap json is missing exhibitor row '{field.CehField}'");
                Assert.Equal(field.ApiField, json!.ApiField);
                Assert.Equal(field.Read, json.Read);
            }
            // §302d: the accepted-but-never-echoed fields stay NotReadable.
            Assert.Equal(IntegrationFieldMap.ReadSupport.NotReadable, exhibitor!["CompanyDescription"].Read);

            // The two new entities carry their endpoints (sessions create-only; sponsors PUT).
            Assert.True(map.Entities.TryGetValue("Session", out var sessionEntity));
            Assert.Null(sessionEntity!.UpdateEndpoint);
            Assert.True(map.Entities.TryGetValue("Sponsor", out var sponsorEntity));
            Assert.Equal("/sponsors/{id}", sponsorEntity!.UpdateEndpoint);
        }
        finally { ZohoFieldMap.ResetOverrides(); }
    }

    [Fact]
    public void Sessionize_map_drives_the_importer_group_keywords()
    {
        // §323: the importer's category-group routing keywords COME FROM the shipped
        // sessionize map ("… group title contains 'track'" → "track") — and
        // ContainsKeyword extracts an operator-edited keyword, so re-routing a renamed
        // Sessionize group is a file edit, not a deploy.
        try
        {
            SessionizeFieldMap.ResetOverrides();
            var shipped = IntegrationFieldMap.LoadMapFile(
                FindRepoFile(Path.Combine("config", "integrations", "sessionize-to-ceh.fieldmap.json")));
            Assert.NotNull(shipped);
            SessionizeFieldMap.ApplyMapFile(shipped);
            Assert.Equal("format", SessionizeFieldMap.FormatKeyword);
            Assert.Equal("track", SessionizeFieldMap.TrackKeyword);
            Assert.Equal("level", SessionizeFieldMap.LevelKeyword);
            Assert.Equal("tag", SessionizeFieldMap.TagKeyword);

            // An edited row (same key, changed quoted keyword) re-routes the facet.
            var edited = new IntegrationFieldMap.Field(
                "categoryItems: group title contains 'spor'", "Track", "Track");
            Assert.Equal("spor", SessionizeFieldMap.ContainsKeyword(edited, "track"));
            // No recognisable token ⇒ the code fallback keeps the facet alive.
            var broken = new IntegrationFieldMap.Field("categoryItems (mangled)", "Track", "Track");
            Assert.Equal("track", SessionizeFieldMap.ContainsKeyword(broken, "track"));
        }
        finally { SessionizeFieldMap.ResetOverrides(); }
    }

    [Fact]
    public void Shipped_erp_and_webshop_fieldmaps_parse_with_the_right_directions()
    {
        // §323: the ERPSystem + WebshopSystem hops now ship fieldmaps too. The ERP map is
        // OUTBOUND (CEH→e-conomic; the Order record is documentation-only — owned by the
        // legacy webhook); the webshop map is INBOUND (WooCommerce/Company Manager→CEH).
        var errors = new List<string>();
        var erp = IntegrationFieldMap.LoadMapFile(
            FindRepoFile(Path.Combine("config", "integrations", "ceh-to-economic.fieldmap.json")), errors.Add);
        Assert.True(errors.Count == 0, string.Join("; ", errors));
        Assert.NotNull(erp);
        Assert.Equal("CEH", erp!.SourceSystem);
        Assert.Equal("e-conomic", erp.TargetSystem);
        Assert.True(erp.Fields.TryGetValue("Customer", out var customer));
        Assert.All(customer!.Values, f => Assert.Equal(IntegrationFieldMap.Direction.Outbound, f.Direction));
        Assert.True(erp.Entities.TryGetValue("Customer", out var custEntity));
        Assert.Equal("/customers/{customerNumber}", custEntity!.UpdateEndpoint);
        Assert.True(erp.Entities.ContainsKey("Order"));   // documentation-only record parses

        var shop = IntegrationFieldMap.LoadMapFile(
            FindRepoFile(Path.Combine("config", "integrations", "webshop-to-ceh.fieldmap.json")), errors.Add);
        Assert.True(errors.Count == 0, string.Join("; ", errors));
        Assert.NotNull(shop);
        Assert.Equal("CEH", shop!.TargetSystem);
        Assert.True(shop.Fields.TryGetValue("SponsorOrder", out var order));
        Assert.All(order!.Values, f => Assert.Equal(IntegrationFieldMap.Direction.Inbound, f.Direction));
        Assert.True(shop.Fields.ContainsKey("SponsorContact"));
    }

    [Fact]
    public void EventTimezone_parses_naive_sessionize_wallclock_as_danish_time()
    {
        // §305 CRITICAL (operator: Sessionize 09:00 showed as 10:00 in Zoho): a NAIVE
        // Sessionize timestamp IS Danish wall-clock — February = CET (+01:00), so
        // 09:00 local is 08:00 UTC; July = CEST (+02:00). Explicit offsets honoured.
        var feb = CommunityHub.Core.Integrations.EventTimezone.ParseEventLocal("2027-02-09T09:00:00");
        Assert.NotNull(feb);
        Assert.Equal(TimeSpan.FromHours(1), feb!.Value.Offset);               // CET
        Assert.Equal(new DateTimeOffset(2027, 2, 9, 8, 0, 0, TimeSpan.Zero), feb.Value.ToUniversalTime());

        var jul = CommunityHub.Core.Integrations.EventTimezone.ParseEventLocal("2027-07-01T09:00:00");
        Assert.Equal(TimeSpan.FromHours(2), jul!.Value.Offset);               // CEST

        // Explicit offsets pass through untouched.
        Assert.Equal(TimeSpan.Zero,
            CommunityHub.Core.Integrations.EventTimezone.ParseEventLocal("2027-02-09T08:00:00Z")!.Value.Offset);
        Assert.Equal(TimeSpan.FromHours(2),
            CommunityHub.Core.Integrations.EventTimezone.ParseEventLocal("2027-02-09T10:00:00+02:00")!.Value.Offset);

        Assert.Null(CommunityHub.Core.Integrations.EventTimezone.ParseEventLocal("  "));
        Assert.Null(CommunityHub.Core.Integrations.EventTimezone.ParseEventLocal("not-a-date"));

        // The ops-mail display form is Danish local, never UTC.
        Assert.Equal("2027-02-09 09:00 (Danish time)",
            CommunityHub.Core.Integrations.EventTimezone.ToEventLocalString(
                new DateTimeOffset(2027, 2, 9, 8, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void ZohoFieldMap_speaker_skills_derivation_keeps_full_labels_and_dedupes()
    {
        static SpeakerProfile P(string? accred, string? mvp) =>
            new() { Accreditation = accred, MvpCategories = mvp };

        Assert.Equal("Microsoft MVP, Microsoft MCT",
            ZohoFieldMap.SpeakerSkills(P("Microsoft MVP, Microsoft MCT", null)));
        Assert.Equal("Microsoft MVP, Security",
            ZohoFieldMap.SpeakerSkills(P("Microsoft MVP", "Security, microsoft mvp")));  // deduped (case-insensitive)
        Assert.Equal("Security", ZohoFieldMap.SpeakerSkills(P(null, "Security")));
        Assert.Null(ZohoFieldMap.SpeakerSkills(P(null, null)));
    }

    [Fact]
    public async Task Speaker_push_adopts_existing_backstage_record_by_email_instead_of_duplicate_create()
    {
        // §304: an UNLINKED approved speaker whose e-mail already has a Backstage record
        // (e.g. re-imported from Sessionize after a CEH delete) ADOPTS that record —
        // no duplicate create, no re-invitation; the approval flow reaches Zoho clean.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerAsync(db, "sam@x.dk", backstageId: null, selectedForPublish: true);

        var (zoho, handler) = NewZoho((req, _) =>
            req.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, "{\"speakers\":[{\"id\":\"bs-existing-77\",\"email\":\"sam@x.dk\"}]}")
                : Json(HttpStatusCode.OK, "{\"id\":\"should-not-happen\"}"));
        var r = await NewSpeakerSvc(db, zoho).RunAsync(EventId);

        Assert.Equal(0, r.Created);
        Assert.Equal(1, r.AlreadyLinked);                              // adopted, not created
        Assert.Equal("bs-existing-77", db.SpeakerProfiles.Single().BackstageSpeakerId);
        Assert.DoesNotContain(handler.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Speaker_approval_service_lists_blockers_and_save_clears_them_all()
    {
        // §304: a Sessionize-imported speaker arrives ring 3 / inactive / uncategorized
        // — PendingAsync names every blocker; ApproveAsync (the page's Save) sets the
        // category, places the ring and ACTIVATES in one write.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerSyncFeatureAsync(db, enabled: true);          // released ring = Ring1
        var pid = await SeedSpeakerAsync(db, "new@x.dk", backstageId: null, selectedForPublish: true,
            isActive: false, lifecycle: ParticipantLifecycleState.Inactive,
            ring: Settings.Ring.Broad, category: null);

        var svc = new CommunityHub.Core.Organizer.SpeakerApprovalService(
            db, new FeatureGateService(db), TimeProvider.System);

        var pending = await svc.PendingAsync(EventId);
        var row = Assert.Single(pending.Speakers, s => s.ParticipantId == pid);
        Assert.Contains(row.Blockers, b => b.Contains("no speaker category"));
        Assert.Contains(row.Blockers, b => b.Contains("inactive"));
        Assert.Contains(row.Blockers, b => b.Contains("not activated"));
        // 🔒 §569 — THE RING IS NOT A BLOCKER ANY MORE. This assertion is inverted deliberately:
        // the ring blocker is what made EVERY speaker show as pending on /Organizer/PendingSpeakers
        // (§550), which is the opposite of the page's purpose — listing who needs a DECISION.
        Assert.DoesNotContain(row.Blockers, b => b.Contains("outside the released"));

        Assert.True(await svc.ApproveAsync(EventId, pid, SpeakerCategory.Community, Settings.Ring.Ring1));

        var after = await svc.PendingAsync(EventId);
        Assert.DoesNotContain(after.Speakers, s => s.ParticipantId == pid);   // every gate cleared
        var p = db.Participants.Single(x => x.Id == pid);
        Assert.True(p.IsActive);
        Assert.Equal(ParticipantLifecycleState.Active, p.LifecycleState);
        Assert.Equal(Settings.Ring.Ring1, p.Ring);
        Assert.Equal(SpeakerCategory.Community, db.SpeakerProfiles.Single(x => x.ParticipantId == pid).Category);

        // The immediate mail body: lists the pending speaker + links the admin page.
        var html = CommunityHub.Core.Organizer.SpeakerApprovalService.BuildPendingMailHtml(
            pending, "https://hub.example");
        Assert.NotNull(html);
        Assert.Contains("new@x.dk", html);
        Assert.Contains("/Organizer/PendingSpeakers", html);
        Assert.Null(CommunityHub.Core.Organizer.SpeakerApprovalService.BuildPendingMailHtml(after, "https://hub.example"));
    }

    [Fact]
    public async Task Speaker_push_failure_carries_the_real_zoho_error()
    {
        // Operator 2026-07-24: hourly failure mails said only "see logs" — useless. The
        // item error must carry the Zoho HTTP status + body (e.g. the duplicate-e-mail
        // refusal when a stub record already exists in Backstage).
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerAsync(db, "sam@x.dk", backstageId: null, selectedForPublish: true);

        var (zoho, _) = NewZoho((_, _) => Json(HttpStatusCode.BadRequest,
            "{\"message\":\"Speaker email already exists\"}"));
        var r = await NewSpeakerSvc(db, zoho).RunAsync(EventId);

        Assert.Equal(1, r.Failed);
        var item = Assert.Single(r.Items);
        Assert.Contains("HTTP 400", item.Error);
        Assert.Contains("already exists", item.Error);
        Assert.Null(db.SpeakerProfiles.Single().BackstageSpeakerId);
    }

    [Fact]
    public async Task Speaker_created_after_its_session_pushes_action_needed_link_line_in_ops_mail()
    {
        // Operator 2026-07-24: a session already in Zoho can never gain a speaker via the
        // API (create-only), and delete+recreate loses attendees' favorites — so when a
        // speaker is created AFTER their session was pushed, the info@ ops mail must carry
        // an ACTION NEEDED line telling the operator to link them in the Backstage UI.
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        var pid = await SeedSpeakerAsync(db, "sam@x.dk", backstageId: null, selectedForPublish: true);
        var sid = await SeedSessionAsync(db, "Existing MC", backstageId: "bs-s-1",
            new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 9, 16, 0, 0, TimeSpan.Zero));
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = sid, ParticipantId = pid });
        await db.SaveChangesAsync();

        var (zoho, _) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-sp-9\"}"));
        var mail = new CapturingEmailSender();
        var alerts = new CommunityHub.Core.Email.EngineAlertSender(
            mail, new CommunityHub.Core.Email.EmailContextAccessor(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CommunityHub.Core.Email.EngineAlertSender>.Instance);
        var svc = new SpeakerBackstagePushService(
            db, zoho, new ZohoOptions(), tokenOverride: _ => Task.FromResult<string?>("tok"),
            zohoChanges: new CommunityHub.Core.Email.ZohoChangeNotifier(alerts));
        var r = await svc.RunAsync(EventId);

        Assert.Equal(1, r.Created);
        var (to, _, html, _) = Assert.Single(mail.Messages);
        Assert.Equal(ZohoChangeNotifier.Recipient, to);   // §493: system alerts go to the operator mailbox
        Assert.Contains("ACTION NEEDED", html);
        Assert.Contains("Existing MC", html);
        Assert.Contains("sam@x.dk", html);
        Assert.Contains("Backstage UI", html);
    }

    /// <summary>
    /// §363 (operator 2026-07-26) — the Zoho speaker API has NO photo field (§356), so the picture
    /// can never be pushed and the speaker would silently appear photo-less on the public programme.
    /// The ops mail must therefore carry a manual upload step, WITH the link to the stored photo so
    /// the organizer does not have to hunt for it (the §322l precedent).
    /// </summary>
    [Fact]
    public async Task Created_speaker_gets_an_action_needed_photo_upload_line_with_the_photo_link()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        var pid = await SeedSpeakerAsync(db, "sam@x.dk", backstageId: null, selectedForPublish: true);
        var prof = db.SpeakerProfiles.Single(s => s.ParticipantId == pid);
        prof.PhotoSharePointPath = "https://example.test/sites/eldk/Speakers/sam.jpg";
        await db.SaveChangesAsync();

        var html = await RunSpeakerPushAndCaptureOpsMailAsync(db);

        Assert.Contains("ACTION NEEDED", html);
        Assert.Contains("upload the photo", html);
        Assert.Contains("sam.jpg", html);                     // the link, so it is not a hunt
        Assert.Contains("no photo field", html);               // and WHY it must be manual
    }

    /// <summary>
    /// No photo on file is a DIFFERENT action — there is nothing to upload yet, so the mail must say
    /// "ask the speaker" rather than point at a link that does not exist.
    /// </summary>
    [Fact]
    public async Task Created_speaker_with_no_photo_is_told_to_ask_the_speaker_first()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedEditionAsync(db, SessionSyncDirection.SessionizeToCeh, SessionSyncDirection.CehToZoho);
        await SeedSpeakerAsync(db, "sam@x.dk", backstageId: null, selectedForPublish: true);

        var html = await RunSpeakerPushAndCaptureOpsMailAsync(db);

        Assert.Contains("ACTION NEEDED", html);
        Assert.Contains("ask the speaker", html);
    }

    /// <summary>Runs the speaker push with a capturing ops-mail sender and returns the mail body.</summary>
    private static async Task<string> RunSpeakerPushAndCaptureOpsMailAsync(CommunityHubDbContext db)
    {
        var (zoho, _) = NewZoho((_, _) => Json(HttpStatusCode.OK, "{\"id\":\"bs-sp-9\"}"));
        var mail = new CapturingEmailSender();
        var alerts = new CommunityHub.Core.Email.EngineAlertSender(
            mail, new CommunityHub.Core.Email.EmailContextAccessor(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CommunityHub.Core.Email.EngineAlertSender>.Instance);
        var svc = new SpeakerBackstagePushService(
            db, zoho, new ZohoOptions(), tokenOverride: _ => Task.FromResult<string?>("tok"),
            zohoChanges: new CommunityHub.Core.Email.ZohoChangeNotifier(alerts));

        var r = await svc.RunAsync(EventId);
        Assert.Equal(1, r.Created);
        var (to, _, html, _) = Assert.Single(mail.Messages);
        Assert.Equal(ZohoChangeNotifier.Recipient, to);   // §493: system alerts go to the operator mailbox
        return html;
    }
}
