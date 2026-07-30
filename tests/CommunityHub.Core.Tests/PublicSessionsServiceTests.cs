using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the PUBLIC sessions overview read service (<see cref="PublicSessionsService"/>,
/// REQUIREMENTS § session management — public Type/Length filters). The service backs the
/// anonymous <c>/Sessions</c> page: it resolves the active edition, lists its non-service
/// sessions with their linked speaker name(s), and applies the Type / Length / Room / search
/// filters. Proves:
///  - filter by <b>type</b> and by <b>length</b> narrows the list (and combine),
///  - <b>speaker linkage</b> surfaces each session's speaker name(s),
///  - master-class rows expose their public logistics slug; the ask token is exposed,
///  - <b>empty-state</b>: no active event → null; no match → zero rows but the facets/total stand,
///  - <b>event scoping</b>: another edition's sessions never leak in,
///  - <b>service sessions</b> (breaks) are excluded.
///
/// In-memory DbContext; synthetic ids + example.test — no real data.
/// </summary>
public sealed class PublicSessionsServiceTests
{
    private static readonly DateTimeOffset Day1 =
        new(2027, 2, 9, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Seed an active edition with three speakers and a representative session mix:
    /// a full-day master class (with a public slug + public token), a 50-min tech
    /// session (two co-speakers), a 20-min sponsor session, and a service session
    /// (must be excluded). Returns the event id.
    /// </summary>
    private static async Task<int> SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "PUB27",
            CommunityName = "Public Community",
            DisplayName = "Public Community 2027",
            StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        Participant Spk(string name, string email)
        {
            var p = new Participant
            {
                EventId = evt.Id, FullName = name, Email = email,
                Role = ParticipantRole.Speaker, IsActive = true,
            };
            db.Participants.Add(p);
            return p;
        }

        var alice = Spk("Alice Adams", "alice@example.test");
        var bob = Spk("Bob Brown", "bob@example.test");
        var carol = Spk("Carol Clark", "carol@example.test");
        await db.SaveChangesAsync();

        Session Sess(string id, string title, SessionType type, SessionLength len,
            string? room, DateTimeOffset? start, params Participant[] speakers)
        {
            var s = new Session
            {
                EventId = evt.Id, SessionizeId = id, Title = title,
                Abstract = $"About {title}.", Type = type, Length = len,
                Room = room, StartsAt = start,
                EndsAt = start?.AddMinutes((int)len == 0 ? 480 : (int)len),
            };
            foreach (var sp in speakers)
                s.SessionSpeakers.Add(new SessionSpeaker { Session = s, Participant = sp });
            db.Sessions.Add(s);
            return s;
        }

        var mc = Sess("sess-mc", "Kubernetes Workshop",
            SessionType.MasterClass, SessionLength.FullDay, "Room A", Day1, alice);
        mc.PublicSlug = "mc-slug-123";
        mc.PublicToken = "ask-token-mc";

        var tech = Sess("sess-tech", "Intro to Bicep",
            SessionType.TechnicalSession, SessionLength.FiftyMin, "Room B",
            Day1.AddHours(2), alice, bob);
        tech.PublicToken = "ask-token-tech";

        Sess("sess-sponsor", "Sponsor Showcase",
            SessionType.Keynote, SessionLength.TwentyMin, "Expo",
            Day1.AddHours(3), carol);

        // A service session (break) — must NEVER appear in the public overview.
        Sess("sess-break", "Coffee Break",
            SessionType.TechnicalSession, SessionLength.TwentyMin, "Foyer",
            Day1.AddHours(1)).IsServiceSession = true;

        await db.SaveChangesAsync();
        return evt.Id;
    }

    [Fact]
    public async Task No_active_event_returns_null()
    {
        using var db = TestDb.New();
        var svc = new PublicSessionsService(db);

        var view = await svc.BuildAsync();

        Assert.Null(view);
    }

    [Fact]
    public async Task Lists_all_non_service_sessions_with_speakers()
    {
        using var db = TestDb.New();
        await SeedAsync(db);
        var svc = new PublicSessionsService(db);

        var view = await svc.BuildAsync();

        Assert.NotNull(view);
        Assert.Equal("Public Community 2027", view!.EventDisplayName);
        // 3 real sessions; the service session is excluded.
        Assert.Equal(3, view.TotalCount);
        Assert.Equal(3, view.Sessions.Count);
        Assert.DoesNotContain(view.Sessions, s => s.Title == "Coffee Break");

        // Speaker linkage: the tech session shows BOTH co-speakers, ordered by name.
        var tech = view.Sessions.Single(s => s.Title == "Intro to Bicep");
        Assert.Equal(new[] { "Alice Adams", "Bob Brown" },
            tech.Speakers.Select(sp => sp.Name).ToArray());

        // Room facet covers the real sessions' rooms (not the service session's).
        Assert.Equal(new[] { "Expo", "Room A", "Room B" }, view.Rooms);
    }

    [Fact]
    public async Task Filter_by_type_narrows_the_list()
    {
        using var db = TestDb.New();
        await SeedAsync(db);
        var svc = new PublicSessionsService(db);

        var view = await svc.BuildAsync(type: SessionType.Keynote);

        Assert.NotNull(view);
        Assert.Equal(3, view!.TotalCount);          // total is unfiltered
        Assert.Equal(1, view.MatchCount);
        Assert.Single(view.Sessions);
        Assert.Equal("Sponsor Showcase", view.Sessions[0].Title);
    }

    // §299.8/b7: the public length filter takes MINUTES (the config quick-pick
    // values) now — the enum-bucket filter is retired. A legacy full-day row
    // without a numeric LengthMinutes still answers the 420 full-day pick.
    [Fact]
    public async Task Filter_by_length_minutes_narrows_the_list()
    {
        using var db = TestDb.New();
        await SeedAsync(db);
        var svc = new PublicSessionsService(db);

        var view = await svc.BuildAsync(lengthMinutes: 420);

        Assert.NotNull(view);
        Assert.Single(view!.Sessions);
        Assert.Equal("Kubernetes Workshop", view.Sessions[0].Title);
    }

    [Fact]
    public async Task Filter_by_type_and_length_minutes_combine()
    {
        using var db = TestDb.New();
        await SeedAsync(db);
        var svc = new PublicSessionsService(db);

        // Tech AND 50 min → exactly the Bicep session.
        var hit = await svc.BuildAsync(
            type: SessionType.TechnicalSession, lengthMinutes: 50);
        Assert.Single(hit!.Sessions);
        Assert.Equal("Intro to Bicep", hit.Sessions[0].Title);

        // Tech AND full-day (420) → none (the full-day one is a master class).
        var miss = await svc.BuildAsync(
            type: SessionType.TechnicalSession, lengthMinutes: 420);
        Assert.Empty(miss!.Sessions);
        Assert.Equal(0, miss.MatchCount);
        Assert.Equal(3, miss.TotalCount);   // total still reflects the edition
    }

    // §299.8/b7: a CUSTOM (non-quick-pick) minutes value filters exactly — the
    // config list drives the UI options, never the validation of what can match.
    [Fact]
    public async Task Filter_by_custom_length_minutes_matches_exact_rows()
    {
        using var db = TestDb.New();
        var eventId = await SeedAsync(db);
        db.Sessions.Add(new Session
        {
            EventId = eventId, SessionizeId = "sess-37", Title = "Lightning 37",
            Type = SessionType.TechnicalSession, Length = SessionLength.TwentyMin,
            LengthMinutes = 37,
        });
        await db.SaveChangesAsync();

        var svc = new PublicSessionsService(db);
        var view = await svc.BuildAsync(lengthMinutes: 37);

        Assert.Single(view!.Sessions);
        Assert.Equal("Lightning 37", view.Sessions[0].Title);
    }

    [Fact]
    public async Task Filter_by_room_is_case_insensitive()
    {
        using var db = TestDb.New();
        await SeedAsync(db);
        var svc = new PublicSessionsService(db);

        var view = await svc.BuildAsync(room: "room a");

        Assert.Single(view!.Sessions);
        Assert.Equal("Kubernetes Workshop", view.Sessions[0].Title);
    }

    [Fact]
    public async Task Search_matches_title_speaker_and_abstract()
    {
        using var db = TestDb.New();
        await SeedAsync(db);
        var svc = new PublicSessionsService(db);

        // By speaker name.
        var bySpeaker = await svc.BuildAsync(search: "carol");
        Assert.Single(bySpeaker!.Sessions);
        Assert.Equal("Sponsor Showcase", bySpeaker.Sessions[0].Title);

        // By title fragment.
        var byTitle = await svc.BuildAsync(search: "bicep");
        Assert.Single(byTitle!.Sessions);
        Assert.Equal("Intro to Bicep", byTitle.Sessions[0].Title);

        // No match → empty result but the total/facets remain.
        var none = await svc.BuildAsync(search: "nonexistent-zzz");
        Assert.Empty(none!.Sessions);
        Assert.Equal(0, none.MatchCount);
        Assert.Equal(3, none.TotalCount);
    }

    [Fact]
    public async Task Masterclass_exposes_public_slug_others_do_not()
    {
        using var db = TestDb.New();
        await SeedAsync(db);
        var svc = new PublicSessionsService(db);

        var view = await svc.BuildAsync();

        var mc = view!.Sessions.Single(s => s.Title == "Kubernetes Workshop");
        Assert.Equal("mc-slug-123", mc.PublicSlug);   // master class → logistics link
        Assert.Equal("ask-token-mc", mc.AskToken);

        var tech = view.Sessions.Single(s => s.Title == "Intro to Bicep");
        Assert.Null(tech.PublicSlug);                 // non-master-class → no logistics link
        Assert.Equal("ask-token-tech", tech.AskToken);

        var sponsor = view.Sessions.Single(s => s.Title == "Sponsor Showcase");
        Assert.Null(sponsor.PublicSlug);
        Assert.Null(sponsor.AskToken);                // no token minted yet
    }

    [Fact]
    public async Task Sessions_from_another_edition_never_leak()
    {
        using var db = TestDb.New();
        await SeedAsync(db);

        // A second, INACTIVE edition with its own session — must not appear.
        var other = new Event
        {
            Code = "OLD26", CommunityName = "Old", DisplayName = "Old 2026",
            StartDate = new DateOnly(2026, 2, 1), EndDate = new DateOnly(2026, 2, 2),
            IsActive = false,
        };
        db.Events.Add(other);
        await db.SaveChangesAsync();
        db.Sessions.Add(new Session
        {
            EventId = other.Id, SessionizeId = "old-sess", Title = "Last Year Talk",
            Type = SessionType.TechnicalSession, Length = SessionLength.SixtyMin,
        });
        await db.SaveChangesAsync();

        var svc = new PublicSessionsService(db);
        var view = await svc.BuildAsync();

        Assert.NotNull(view);
        Assert.Equal("Public Community 2027", view!.EventDisplayName); // the active one
        Assert.DoesNotContain(view.Sessions, s => s.Title == "Last Year Talk");
        Assert.Equal(3, view.TotalCount);
    }

    [Fact]
    public async Task GetById_returns_detail_with_links_for_active_edition_session()
    {
        using var db = TestDb.New();
        var eventId = await SeedAsync(db);
        var svc = new PublicSessionsService(db);

        var mcId = (await svc.BuildAsync())!.Sessions
            .Single(s => s.Title == "Kubernetes Workshop").Id;

        var detail = await svc.GetByIdAsync(mcId);

        Assert.NotNull(detail);
        Assert.Equal("Kubernetes Workshop", detail!.Title);
        Assert.Equal("Public Community 2027", detail.EventDisplayName);
        Assert.Equal(SessionType.MasterClass, detail.Type);
        Assert.Equal("mc-slug-123", detail.PublicSlug);   // master class → logistics link
        Assert.Equal("ask-token-mc", detail.AskToken);
        Assert.Contains(detail.Speakers, sp => sp.Name == "Alice Adams");
    }

    [Fact]
    public async Task GetById_marks_only_published_speakers_for_cross_linking()
    {
        using var db = TestDb.New();
        var eventId = await SeedAsync(db);

        // Publish ONLY Alice; Bob co-speaks the tech session but stays unselected.
        var alice = await db.Participants.FirstAsync(p => p.FullName == "Alice Adams");
        db.SpeakerProfiles.Add(new SpeakerProfile
        { EventId = eventId, ParticipantId = alice.Id, SelectedForPublish = true });
        await db.SaveChangesAsync();

        var svc = new PublicSessionsService(db);
        var techId = (await svc.BuildAsync())!.Sessions
            .Single(s => s.Title == "Intro to Bicep").Id;

        var detail = await svc.GetByIdAsync(techId);

        Assert.NotNull(detail);
        var aliceRow = detail!.Speakers.Single(s => s.Name == "Alice Adams");
        var bobRow = detail.Speakers.Single(s => s.Name == "Bob Brown");
        Assert.True(aliceRow.IsPublished);    // selected → cross-linked
        Assert.False(bobRow.IsPublished);     // unselected → plain text only
    }

    [Fact]
    public async Task GetById_returns_null_for_service_session_or_unknown_id()
    {
        using var db = TestDb.New();
        await SeedAsync(db);
        var svc = new PublicSessionsService(db);

        // A service session is never publicly addressable.
        var breakId = (await db.Sessions.FirstAsync(s => s.Title == "Coffee Break")).Id;
        Assert.Null(await svc.GetByIdAsync(breakId));

        // An id that doesn't exist.
        Assert.Null(await svc.GetByIdAsync(999999));
    }

    // §193: the public per-session ".ics" download (BuildIcsAsync) was removed — a
    // public talk is no longer offered as a downloadable calendar file, so its tests
    // are gone. Signed-in participants e-mail themselves a calendar invitation instead.

    [Fact]
    public async Task Empty_edition_returns_view_with_zero_sessions()
    {
        using var db = TestDb.New();
        var evt = new Event
        {
            Code = "EMPTY27", CommunityName = "Empty", DisplayName = "Empty 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var svc = new PublicSessionsService(db);
        var view = await svc.BuildAsync();

        Assert.NotNull(view);          // active event exists → not null
        Assert.Empty(view!.Sessions);  // but nothing published yet
        Assert.Equal(0, view.TotalCount);
        Assert.Empty(view.Rooms);
    }

    // §154: an edition whose sessions carry Track + Level, used to prove the new
    // Track/Level facets + filters. Two tracks (Security/Azure), two levels.
    private static async Task<int> SeedWithTrackLevelAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "TL27", CommunityName = "TL", DisplayName = "TL 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        void Sess(string id, string title, string? track, string? level, int? minutes)
        {
            db.Sessions.Add(new Session
            {
                EventId = evt.Id, SessionizeId = id, Title = title,
                Type = SessionType.TechnicalSession, Length = SessionLength.SixtyMin,
                Track = track, Level = level, LengthMinutes = minutes,
            });
        }

        Sess("s-sec1", "Securing Identity", "Security", "Expert (400)", 60);
        Sess("s-sec2", "Threat Hunting", "Security", "Intermediate (200)", 50);
        Sess("s-az1", "Landing Zones", "Azure", "Intermediate (200)", 60);
        Sess("s-none", "Opening", null, null, null);
        await db.SaveChangesAsync();
        return evt.Id;
    }

    [Fact]
    public async Task Track_and_level_facets_are_distinct_and_sorted()
    {
        using var db = TestDb.New();
        await SeedWithTrackLevelAsync(db);
        var svc = new PublicSessionsService(db);

        var view = await svc.BuildAsync();

        Assert.NotNull(view);
        Assert.Equal(new[] { "Azure", "Security" }, view!.Tracks);
        // §299.8/b7: levels sort by NUMERIC code (200 before 400), never
        // alphabetically (which would wrongly put "Expert (400)" first here —
        // and Black Belt before Expert in the real level list).
        Assert.Equal(new[] { "Intermediate (200)", "Expert (400)" }, view.Levels);
    }

    // §299.8/b7: the numeric-code sort proves the operator's example — Black Belt
    // (500) must come AFTER Expert (400) even though it alphabetizes before it.
    [Fact]
    public async Task Level_facet_sorts_black_belt_after_expert_by_code()
    {
        using var db = TestDb.New();
        var evt = new Event
        {
            Code = "LV27", CommunityName = "LV", DisplayName = "LV 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        void Sess(string id, string level, int? code) => db.Sessions.Add(new Session
        {
            EventId = evt.Id, SessionizeId = id, Title = id,
            Type = SessionType.TechnicalSession, Level = level, LevelCode = code,
        });

        // Stored codes (as the import derives them from the config level list).
        Sess("s-bb", "Black Belt (500)", 500);
        Sess("s-ex", "Expert (400)", 400);
        Sess("s-ad", "Advanced (300)", 300);
        await db.SaveChangesAsync();

        var view = await new PublicSessionsService(db).BuildAsync();

        Assert.Equal(
            new[] { "Advanced (300)", "Expert (400)", "Black Belt (500)" },
            view!.Levels);
    }

    [Fact]
    public async Task Filter_by_track_narrows_the_list()
    {
        using var db = TestDb.New();
        await SeedWithTrackLevelAsync(db);
        var svc = new PublicSessionsService(db);

        // Case-insensitive, matches across all sessions of that track.
        var view = await svc.BuildAsync(track: "security");

        Assert.NotNull(view);
        Assert.Equal(2, view!.MatchCount);
        Assert.All(view.Sessions, s => Assert.Equal("Security", s.Track));
    }

    [Fact]
    public async Task Filter_by_level_narrows_the_list()
    {
        using var db = TestDb.New();
        await SeedWithTrackLevelAsync(db);
        var svc = new PublicSessionsService(db);

        var view = await svc.BuildAsync(level: "Expert (400)");

        Assert.NotNull(view);
        Assert.Single(view!.Sessions);
        Assert.Equal("Securing Identity", view.Sessions[0].Title);
    }

    [Fact]
    public async Task Filter_by_track_and_level_combine()
    {
        using var db = TestDb.New();
        await SeedWithTrackLevelAsync(db);
        var svc = new PublicSessionsService(db);

        // Security AND Intermediate → exactly the Threat Hunting talk.
        var view = await svc.BuildAsync(track: "Security", level: "Intermediate (200)");

        Assert.Single(view!.Sessions);
        Assert.Equal("Threat Hunting", view.Sessions[0].Title);
    }

    [Fact]
    public async Task LengthMinutes_surfaces_on_the_row()
    {
        using var db = TestDb.New();
        await SeedWithTrackLevelAsync(db);
        var svc = new PublicSessionsService(db);

        var view = await svc.BuildAsync();
        var row = view!.Sessions.Single(s => s.Title == "Threat Hunting");
        Assert.Equal(50, row.LengthMinutes);
        Assert.Equal("Intermediate (200)", row.Level);
    }

    // The single public-visibility signal both speaker surfaces (hub + evaluations)
    // now reuse: GetPubliclyViewableSessionIdsAsync returns exactly the candidate
    // ids whose /Sessions/{id} would resolve under the SAME gate GetByIdAsync uses
    // (active edition + non-service session).
    [Fact]
    public async Task PubliclyViewable_ids_match_the_GetById_gate()
    {
        using var db = TestDb.New();
        await SeedAsync(db);
        var svc = new PublicSessionsService(db);

        var tech = await db.Sessions.FirstAsync(s => s.SessionizeId == "sess-tech");
        var breakSess = await db.Sessions.FirstAsync(s => s.SessionizeId == "sess-break");

        var set = await svc.GetPubliclyViewableSessionIdsAsync(
            new[] { tech.Id, breakSess.Id, 999999 });

        // The real non-service session is viewable; the service session and the
        // unknown id are not.
        Assert.Contains(tech.Id, set);
        Assert.DoesNotContain(breakSess.Id, set);
        Assert.DoesNotContain(999999, set);

        // Equivalence with GetByIdAsync (the gate the public page itself applies).
        Assert.NotNull(await svc.GetByIdAsync(tech.Id));
        Assert.Null(await svc.GetByIdAsync(breakSess.Id));
    }

    [Fact]
    public async Task PubliclyViewable_excludes_session_outside_active_edition()
    {
        using var db = TestDb.New();
        var activeEventId = await SeedAsync(db);

        // A second, NON-active edition with its own session. A speaker scoped to
        // that edition would still see this session in their own lists, but its
        // public page 404s — so it must be excluded from the viewable set.
        var old = new Event
        {
            Code = "OLD26", CommunityName = "Old", DisplayName = "Old 2026",
            StartDate = new DateOnly(2026, 2, 9), EndDate = new DateOnly(2026, 2, 10),
            IsActive = false,
        };
        db.Events.Add(old);
        await db.SaveChangesAsync();
        var oldSession = new Session
        {
            EventId = old.Id, SessionizeId = "old-sess", Title = "Last Year Talk",
            Type = SessionType.TechnicalSession,
        };
        db.Sessions.Add(oldSession);
        await db.SaveChangesAsync();

        var svc = new PublicSessionsService(db);
        var set = await svc.GetPubliclyViewableSessionIdsAsync(new[] { oldSession.Id });

        Assert.Empty(set);
        Assert.Null(await svc.GetByIdAsync(oldSession.Id)); // same gate: 404
        Assert.NotEqual(0, activeEventId);
    }

    [Fact]
    public async Task PubliclyViewable_is_empty_with_no_candidates_or_no_active_event()
    {
        using var db = TestDb.New();
        var svc = new PublicSessionsService(db);

        // No candidates → empty (no query needed).
        Assert.Empty(await svc.GetPubliclyViewableSessionIdsAsync(Array.Empty<int>()));

        // Candidates but no active event → empty.
        Assert.Empty(await svc.GetPubliclyViewableSessionIdsAsync(new[] { 1, 2, 3 }));
    }
}
