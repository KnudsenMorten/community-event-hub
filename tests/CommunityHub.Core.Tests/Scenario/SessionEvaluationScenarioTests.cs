using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: per-session attendee EVALUATION (HappyOrNot-style, public, hub-only).
///
/// An attendee rates a session 1–5 (+ optional comment) from a PUBLIC, no-login page
/// addressed by the session's unguessable public token (reached via the room QR). The
/// rating lands in the hub ONLY (never public); one rating per attendee/session is
/// enforced softly by upserting on a per-session voter key; the organizer results
/// dashboard rolls the ratings up PER SESSION and PER ROOM (average, count, comments)
/// and is filterable. The public token is SHARED with the ask page (one token, two
/// public pages).
///
/// These drive the real <see cref="SessionEvaluationService"/> — the same authority the
/// public submit page and organizer dashboard call — so they prove the model end-to-end
/// against the EF model, not just the page glue.
/// </summary>
public sealed class SessionEvaluationScenarioTests
{
    private static SessionEvaluationService NewService(CommunityHub.Core.Data.CommunityHubDbContext db)
        => new(db, ScenarioFixture.Clock);

    /// <summary>Create a session in the edition (optional room + type) and link speakers.</summary>
    private static async Task<Session> AddSessionAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, ScenarioSeed.SeedResult s,
        string title, string? room = null,
        SessionType type = SessionType.TechnicalSession, params int[] speakerIds)
    {
        var session = new Session
        {
            EventId = s.EventId,
            SessionizeId = Guid.NewGuid().ToString("N"),
            Title = title,
            Room = room,
            Type = type,
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        foreach (var pid in speakerIds)
            db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = pid });
        await db.SaveChangesAsync();
        return session;
    }

    // =====================================================================
    //  QR target: public token resolves the session and is shared with ask.
    // =====================================================================

    [Fact]
    public async Task Public_token_is_unguessable_resolves_the_session_and_is_shared_with_ask()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var eval = NewService(db);
        var ask = new SessionQuestionService(db, ScenarioFixture.Clock);
        var session = await AddSessionAsync(db, seed, "Keynote", "Room A", SessionType.TechnicalSession, seed.SpeakerOneId);

        var token = await eval.EnsurePublicTokenAsync(session.Id);
        Assert.True(token.Length >= 40);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.NotEqual(session.Id.ToString(), token);

        // Idempotent — the QR can be minted repeatedly without rotating.
        Assert.Equal(token, await eval.EnsurePublicTokenAsync(session.Id));

        // SHARED with the ask page: the same token addresses both public pages.
        Assert.Equal(token, await ask.EnsurePublicTokenAsync(session.Id));

        // The evaluate page resolves the session (with speakers) by the QR token.
        var resolved = await eval.ResolveByPublicTokenAsync(token);
        Assert.NotNull(resolved);
        Assert.Equal(session.Id, resolved!.Id);
        Assert.Single(resolved.SessionSpeakers);

        // Unknown / blank token resolves to nothing (page -> 404).
        Assert.Null(await eval.ResolveByPublicTokenAsync("not-a-real-token"));
        Assert.Null(await eval.ResolveByPublicTokenAsync(null));
    }

    // =====================================================================
    //  Public submit -> hub-only storage; rating range; anonymous comment.
    // =====================================================================

    [Fact]
    public async Task Public_submit_stores_a_rating_in_the_hub_only()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var session = await AddSessionAsync(db, seed, "Talk A", "Room A");
        var token = await svc.EnsurePublicTokenAsync(session.Id);

        var saved = await svc.SubmitPublicEvaluationAsync(
            token, rating: 5, comment: "Loved it!", voterKey: "voter-1", ipHash: "ip-1");

        Assert.NotNull(saved);
        Assert.Equal(session.Id, saved!.SessionId);
        Assert.Equal(seed.EventId, saved.EventId);
        Assert.Equal(5, saved.Rating);
        Assert.Equal("Loved it!", saved.Comment);
        Assert.Equal(1, await db.SessionEvaluations.CountAsync(x => x.SessionId == session.Id));
    }

    [Fact]
    public async Task Rating_out_of_range_is_rejected_and_unknown_token_is_null()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var session = await AddSessionAsync(db, seed, "Talk B");
        var token = await svc.EnsurePublicTokenAsync(session.Id);

        await Assert.ThrowsAsync<SessionEvaluationValidationException>(
            () => svc.SubmitPublicEvaluationAsync(token, 0, null, "v", null));
        await Assert.ThrowsAsync<SessionEvaluationValidationException>(
            () => svc.SubmitPublicEvaluationAsync(token, 6, null, "v", null));

        // Unknown token -> null (page maps to 404), no write.
        Assert.Null(await svc.SubmitPublicEvaluationAsync("bogus", 4, null, "v", null));
        Assert.Equal(0, await db.SessionEvaluations.CountAsync());
    }

    [Fact]
    public async Task Comment_is_optional_anonymous_ratings_are_allowed()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var session = await AddSessionAsync(db, seed, "Talk C");
        var token = await svc.EnsurePublicTokenAsync(session.Id);

        var saved = await svc.SubmitPublicEvaluationAsync(token, 4, null, null, null);
        Assert.NotNull(saved);
        Assert.Null(saved!.Comment);
        Assert.Null(saved.VoterKey);
    }

    // =====================================================================
    //  One rating per attendee/session (soft, via voter key upsert).
    // =====================================================================

    [Fact]
    public async Task Same_voter_key_re_rates_in_place_instead_of_duplicating()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var session = await AddSessionAsync(db, seed, "Talk D");
        var token = await svc.EnsurePublicTokenAsync(session.Id);

        var first = await svc.SubmitPublicEvaluationAsync(token, 2, "meh", "device-x", "ip");
        var second = await svc.SubmitPublicEvaluationAsync(token, 5, "changed my mind!", "device-x", "ip");

        // Same row updated — NOT a second row.
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(1, await db.SessionEvaluations.CountAsync(x => x.SessionId == session.Id));
        var row = await db.SessionEvaluations.SingleAsync(x => x.SessionId == session.Id);
        Assert.Equal(5, row.Rating);
        Assert.Equal("changed my mind!", row.Comment);
        Assert.NotNull(row.UpdatedAt);

        // A DIFFERENT device is a distinct rating.
        await svc.SubmitPublicEvaluationAsync(token, 3, null, "device-y", "ip");
        Assert.Equal(2, await db.SessionEvaluations.CountAsync(x => x.SessionId == session.Id));
    }

    [Fact]
    public async Task Cookieless_submits_are_each_counted_separately()
    {
        // No voter key (e.g. cookies blocked): each submit is a distinct row — the
        // soft de-dup simply doesn't apply, the rating still counts.
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var session = await AddSessionAsync(db, seed, "Talk E");
        var token = await svc.EnsurePublicTokenAsync(session.Id);

        await svc.SubmitPublicEvaluationAsync(token, 4, null, null, "ip");
        await svc.SubmitPublicEvaluationAsync(token, 5, null, null, "ip");
        Assert.Equal(2, await db.SessionEvaluations.CountAsync(x => x.SessionId == session.Id));
    }

    [Fact]
    public async Task Ip_hash_recent_count_supports_a_soft_rate_limit()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var session = await AddSessionAsync(db, seed, "Talk F");
        var token = await svc.EnsurePublicTokenAsync(session.Id);

        for (var i = 0; i < 3; i++)
            await svc.SubmitPublicEvaluationAsync(token, 4, null, $"voter-{i}", "flooder-ip");

        var since = ScenarioFixture.Clock.GetUtcNow().AddHours(-1);
        Assert.Equal(3, await svc.CountRecentByIpHashAsync(seed.EventId, "flooder-ip", since));
        Assert.Equal(0, await svc.CountRecentByIpHashAsync(seed.EventId, "other-ip", since));
        Assert.Equal(0, await svc.CountRecentByIpHashAsync(seed.EventId, null, since));
    }

    // =====================================================================
    //  Organizer dashboard: per-session + per-room aggregates, filterable.
    // =====================================================================

    [Fact]
    public async Task Dashboard_aggregates_per_session_and_per_room()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        // Room A: two sessions; Room B: one session.
        var a1 = await AddSessionAsync(db, seed, "A1", "Room A", SessionType.TechnicalSession);
        var a2 = await AddSessionAsync(db, seed, "A2", "Room A", SessionType.TechnicalSession);
        var b1 = await AddSessionAsync(db, seed, "B1", "Room B", SessionType.TechnicalSession);

        var ta1 = await svc.EnsurePublicTokenAsync(a1.Id);
        var ta2 = await svc.EnsurePublicTokenAsync(a2.Id);
        var tb1 = await svc.EnsurePublicTokenAsync(b1.Id);

        // A1: ratings 4 and 2 (avg 3.0) + a comment.
        await svc.SubmitPublicEvaluationAsync(ta1, 4, "good", "v1", null);
        await svc.SubmitPublicEvaluationAsync(ta1, 2, null, "v2", null);
        // A2: rating 5 (avg 5.0).
        await svc.SubmitPublicEvaluationAsync(ta2, 5, "great", "v3", null);
        // B1: ratings 3 and 5 (avg 4.0).
        await svc.SubmitPublicEvaluationAsync(tb1, 3, null, "v4", null);
        await svc.SubmitPublicEvaluationAsync(tb1, 5, "nice", "v5", null);

        var dash = await svc.BuildDashboardAsync(seed.EventId);

        Assert.Equal(5, dash.TotalCount);
        Assert.Equal(3.8, dash.OverallAverage); // (4+2+5+3+5)/5 = 19/5 = 3.8

        // Per-session: A1 avg 3.0 (n=2), A2 avg 5.0 (n=1), B1 avg 4.0 (n=2).
        Assert.Equal(3.0, Assert.Single(dash.Sessions, s => s.SessionId == a1.Id).AverageRating);
        Assert.Equal(5.0, Assert.Single(dash.Sessions, s => s.SessionId == a2.Id).AverageRating);
        Assert.Equal(4.0, Assert.Single(dash.Sessions, s => s.SessionId == b1.Id).AverageRating);

        // Per-room: Room A rolls up A1+A2 (4,2,5) avg 3.666... -> 3.67; Room B avg 4.0.
        Assert.Equal(2, Assert.Single(dash.Rooms, r => r.Room == "Room A").SessionCount);
        Assert.Equal(3.67, Assert.Single(dash.Rooms, r => r.Room == "Room A").AverageRating);
        Assert.Equal(4.0, Assert.Single(dash.Rooms, r => r.Room == "Room B").AverageRating);
    }

    [Fact]
    public async Task Dashboard_per_session_numbers_are_correct()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        var a1 = await AddSessionAsync(db, seed, "A1", "Room A");
        var ta1 = await svc.EnsurePublicTokenAsync(a1.Id);
        await svc.SubmitPublicEvaluationAsync(ta1, 4, "good", "v1", null);
        await svc.SubmitPublicEvaluationAsync(ta1, 2, null, "v2", null);

        var dash = await svc.BuildDashboardAsync(seed.EventId);
        var sess = Assert.Single(dash.Sessions, s => s.SessionId == a1.Id);
        Assert.Equal(2, sess.Count);
        Assert.Equal(3.0, sess.AverageRating);
        // Only the rating WITH a comment surfaces in the comment list.
        var c = Assert.Single(sess.Comments);
        Assert.Equal("good", c.Comment);
        Assert.Equal(4, c.Rating);
    }

    [Fact]
    public async Task Dashboard_per_room_rolls_up_all_sessions_in_the_room()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        var a1 = await AddSessionAsync(db, seed, "A1", "Room A");
        var a2 = await AddSessionAsync(db, seed, "A2", "Room A");
        var ta1 = await svc.EnsurePublicTokenAsync(a1.Id);
        var ta2 = await svc.EnsurePublicTokenAsync(a2.Id);
        await svc.SubmitPublicEvaluationAsync(ta1, 4, null, "v1", null);
        await svc.SubmitPublicEvaluationAsync(ta1, 2, null, "v2", null);
        await svc.SubmitPublicEvaluationAsync(ta2, 3, null, "v3", null);

        var dash = await svc.BuildDashboardAsync(seed.EventId);
        var room = Assert.Single(dash.Rooms, r => r.Room == "Room A");
        Assert.Equal(2, room.SessionCount);
        Assert.Equal(3, room.Count);
        Assert.Equal(3.0, room.AverageRating); // (4+2+3)/3
    }

    [Fact]
    public async Task Dashboard_filters_by_type_and_by_room()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        var tech = await AddSessionAsync(db, seed, "Tech", "Room A", SessionType.TechnicalSession);
        var sponsor = await AddSessionAsync(db, seed, "Sponsor", "Room B", SessionType.Keynote);
        var tTech = await svc.EnsurePublicTokenAsync(tech.Id);
        var tSpon = await svc.EnsurePublicTokenAsync(sponsor.Id);
        await svc.SubmitPublicEvaluationAsync(tTech, 5, null, "v1", null);
        await svc.SubmitPublicEvaluationAsync(tSpon, 1, null, "v2", null);

        // Filter by type.
        var techOnly = await svc.BuildDashboardAsync(seed.EventId, type: SessionType.TechnicalSession);
        Assert.Single(techOnly.Sessions);
        Assert.Equal("Tech", techOnly.Sessions[0].Title);
        Assert.Equal(1, techOnly.TotalCount);

        // Filter by room.
        var roomBOnly = await svc.BuildDashboardAsync(seed.EventId, room: "Room B");
        Assert.Single(roomBOnly.Sessions);
        Assert.Equal("Sponsor", roomBOnly.Sessions[0].Title);
        Assert.Equal(1, roomBOnly.TotalCount);
    }

    [Fact]
    public async Task ListRooms_returns_distinct_sorted_rooms()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        await AddSessionAsync(db, seed, "S1", "Room B");
        await AddSessionAsync(db, seed, "S2", "Room A");
        await AddSessionAsync(db, seed, "S3", "Room A"); // duplicate room
        await AddSessionAsync(db, seed, "S4", null);     // no room

        var rooms = await svc.ListRoomsAsync(seed.EventId);
        Assert.Equal(new[] { "Room A", "Room B" }, rooms);
    }

    [Fact]
    public async Task Empty_event_dashboard_is_safe()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        var dash = await svc.BuildDashboardAsync(seed.EventId);
        Assert.Equal(0, dash.TotalCount);
        Assert.Null(dash.OverallAverage);
        Assert.Empty(dash.Rooms);
        // Sessions with zero ratings still appear (count 0, null average) if any exist,
        // but this edition has no sessions yet -> empty.
        Assert.Empty(dash.Sessions);
    }
}
