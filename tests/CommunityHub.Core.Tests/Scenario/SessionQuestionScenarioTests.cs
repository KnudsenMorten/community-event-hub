using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Actor = CommunityHub.Core.Domain.SessionQuestionService.ActorContext;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: attendee questions per session (pre-event, public, hub-only).
///
/// An attendee asks a question for a session via a PUBLIC, no-login page addressed
/// by an unguessable per-session token. The question lands in the hub ONLY (never
/// auto-public); ORGANIZERS see all; an INVOLVED SPEAKER sees + answers questions
/// for THEIR session(s), and that response is visible to the OTHER speakers on the
/// same session. A speaker on a different session is denied.
///
/// These drive the real <see cref="SessionQuestionService"/> — the same authority
/// the public, organizer and speaker pages call — so they prove the visibility /
/// permission model end-to-end against the EF model, not just the page glue.
/// </summary>
public sealed class SessionQuestionScenarioTests
{
    private static SessionQuestionService NewService(CommunityHubDbContext db)
        => new(db, ScenarioFixture.Clock);

    private static Actor Organizer(ScenarioSeed.SeedResult s)
        => new(s.OrganizerId, ScenarioSeed.OrganizerEmail, ParticipantRole.Organizer, s.EventId);

    private static Actor SpeakerOne(ScenarioSeed.SeedResult s)
        => new(s.SpeakerOneId, ScenarioSeed.SpeakerOneEmail, ParticipantRole.Speaker, s.EventId);

    private static Actor SpeakerTwo(ScenarioSeed.SeedResult s)
        => new(s.SpeakerTwoId, ScenarioSeed.SpeakerTwoEmail, ParticipantRole.Speaker, s.EventId);

    private static Actor Attendee(ScenarioSeed.SeedResult s)
        => new(s.AttendeeId, ScenarioSeed.AttendeeEmail, ParticipantRole.Attendee, s.EventId);

    /// <summary>Create a session in the edition and link the given speaker ids.</summary>
    private static async Task<Session> AddSessionAsync(
        CommunityHubDbContext db, ScenarioSeed.SeedResult s, string title, params int[] speakerIds)
    {
        var session = new Session
        {
            EventId = s.EventId,
            SessionizeId = Guid.NewGuid().ToString("N"),
            Title = title,
            Abstract = "An interesting talk.",
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        foreach (var pid in speakerIds)
        {
            db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = pid });
        }
        await db.SaveChangesAsync();
        return session;
    }

    // =====================================================================
    //  Public submit -> hub-only storage, never auto-public.
    // =====================================================================

    [Fact]
    public async Task Public_token_resolves_session_with_speakers_and_is_unguessable()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var session = await AddSessionAsync(db, seed, "Two-speaker masterclass", seed.SpeakerOneId, seed.SpeakerTwoId);

        var token = await svc.EnsurePublicTokenAsync(session.Id);
        // Token is long + URL-safe (not the sequential id).
        Assert.True(token.Length >= 40);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.NotEqual(session.Id.ToString(), token);

        // Idempotent: a second call returns the same token.
        Assert.Equal(token, await svc.EnsurePublicTokenAsync(session.Id));

        // Resolves back to the session, with its speakers loaded for the public page.
        var resolved = await svc.ResolveByPublicTokenAsync(token);
        Assert.NotNull(resolved);
        Assert.Equal(session.Id, resolved!.Id);
        Assert.Equal(2, resolved.SessionSpeakers.Count);

        // An unknown / blank token resolves to nothing (page -> 404).
        Assert.Null(await svc.ResolveByPublicTokenAsync("not-a-real-token"));
        Assert.Null(await svc.ResolveByPublicTokenAsync(null));
    }

    [Fact]
    public async Task Public_submit_lands_in_hub_only_and_is_never_auto_public()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var session = await AddSessionAsync(db, seed, "Talk A", seed.SpeakerOneId);
        var token = await svc.EnsurePublicTokenAsync(session.Id);

        // No login, no actor — the public page calls this directly.
        var q = await svc.SubmitPublicQuestionAsync(
            token, askerName: "Curious Person", askerEmail: "ask@example.test",
            questionText: "Will you cover migration from an existing setup?", ipHash: "abc123");

        Assert.NotNull(q);
        Assert.Equal(session.Id, q!.SessionId);
        Assert.Equal(seed.EventId, q.EventId);
        Assert.Equal(SessionQuestionStatus.Open, q.Status);       // not answered
        Assert.Null(q.ResponseText);                              // never auto-public/answered
        Assert.Equal("Curious Person", q.AskerName);

        // It is in the hub store, linked to the session.
        Assert.Equal(1, await db.SessionQuestions.CountAsync(x => x.SessionId == session.Id));
    }

    [Fact]
    public async Task Public_submit_allows_anonymous_and_rejects_empty_question()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var session = await AddSessionAsync(db, seed, "Talk B", seed.SpeakerOneId);
        var token = await svc.EnsurePublicTokenAsync(session.Id);

        // Anonymous (no name/email) is fine.
        var anon = await svc.SubmitPublicQuestionAsync(token, null, null, "Anonymous but valid?", null);
        Assert.NotNull(anon);
        Assert.Null(anon!.AskerName);

        // Empty/too-short text is rejected.
        await Assert.ThrowsAsync<SessionQuestionValidationException>(
            () => svc.SubmitPublicQuestionAsync(token, null, null, " ", null));

        // Unknown token -> null (page maps to 404), no write.
        Assert.Null(await svc.SubmitPublicQuestionAsync("bogus", null, null, "hi there", null));
    }

    [Fact]
    public async Task Ip_hash_recent_count_supports_a_soft_rate_limit()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var session = await AddSessionAsync(db, seed, "Talk C", seed.SpeakerOneId);
        var token = await svc.EnsurePublicTokenAsync(session.Id);

        for (var i = 0; i < 3; i++)
            await svc.SubmitPublicQuestionAsync(token, null, null, $"Question {i}", "same-ip-hash");

        var since = ScenarioFixture.Clock.GetUtcNow().AddHours(-1);
        Assert.Equal(3, await svc.CountRecentByIpHashAsync(seed.EventId, "same-ip-hash", since));
        Assert.Equal(0, await svc.CountRecentByIpHashAsync(seed.EventId, "other-ip-hash", since));
        Assert.Equal(0, await svc.CountRecentByIpHashAsync(seed.EventId, null, since));
    }

    // =====================================================================
    //  Organizer sees all.
    // =====================================================================

    [Fact]
    public async Task Organizer_sees_all_questions_across_the_edition()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var s1 = await AddSessionAsync(db, seed, "Talk One", seed.SpeakerOneId);
        var s2 = await AddSessionAsync(db, seed, "Talk Two", seed.SpeakerTwoId);
        var t1 = await svc.EnsurePublicTokenAsync(s1.Id);
        var t2 = await svc.EnsurePublicTokenAsync(s2.Id);

        await svc.SubmitPublicQuestionAsync(t1, null, null, "Q on talk one", null);
        await svc.SubmitPublicQuestionAsync(t2, null, null, "Q on talk two", null);

        var all = await svc.LoadAllForEventAsync(Organizer(seed));
        Assert.Equal(2, all.Count);
        Assert.Contains(all, q => q.SessionId == s1.Id);
        Assert.Contains(all, q => q.SessionId == s2.Id);
    }

    [Fact]
    public async Task Non_organizer_cannot_load_all_questions()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        await Assert.ThrowsAsync<SessionQuestionAccessDeniedException>(
            () => svc.LoadAllForEventAsync(SpeakerOne(seed)));
        await Assert.ThrowsAsync<SessionQuestionAccessDeniedException>(
            () => svc.LoadAllForEventAsync(Attendee(seed)));
    }

    // =====================================================================
    //  Speaker respond + visibility scope.
    // =====================================================================

    [Fact]
    public async Task Involved_speaker_responds_and_co_speaker_sees_the_response()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        // ONE session with TWO speakers (co-speakers / masterclass).
        var session = await AddSessionAsync(db, seed, "Co-presented masterclass", seed.SpeakerOneId, seed.SpeakerTwoId);
        var token = await svc.EnsurePublicTokenAsync(session.Id);
        var q = await svc.SubmitPublicQuestionAsync(token, "Attendee", null, "Please cover live demos", null);

        // Both speakers can SEE the question (it's their session).
        Assert.Single(await svc.LoadForSessionAsync(SpeakerOne(seed), session.Id));
        Assert.Single(await svc.LoadForSessionAsync(SpeakerTwo(seed), session.Id));

        // Speaker One responds.
        Assert.True(await svc.RespondAsync(SpeakerOne(seed), q!.Id, "Yes — two live demos planned."));

        var answered = await db.SessionQuestions.SingleAsync(x => x.Id == q.Id);
        Assert.Equal(SessionQuestionStatus.Answered, answered.Status);
        Assert.Equal(seed.SpeakerOneId, answered.RespondedByParticipantId);
        Assert.Equal(ScenarioSeed.SpeakerOneEmail, answered.RespondedByEmail);
        Assert.NotNull(answered.RespondedAt);

        // Speaker Two (the OTHER speaker on the same session) sees the response.
        var asSeenByTwo = Assert.Single(await svc.LoadForSessionAsync(SpeakerTwo(seed), session.Id));
        Assert.Equal("Yes — two live demos planned.", asSeenByTwo.ResponseText);
    }

    [Fact]
    public async Task Speaker_cannot_see_or_answer_a_session_they_are_not_on()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        // Speaker One's session only — Speaker Two is NOT linked.
        var session = await AddSessionAsync(db, seed, "Solo talk", seed.SpeakerOneId);
        var token = await svc.EnsurePublicTokenAsync(session.Id);
        var q = await svc.SubmitPublicQuestionAsync(token, null, null, "A question", null);

        Assert.False(await svc.CanAccessSessionAsync(SpeakerTwo(seed), session.Id));

        // Reading is denied.
        await Assert.ThrowsAsync<SessionQuestionAccessDeniedException>(
            () => svc.LoadForSessionAsync(SpeakerTwo(seed), session.Id));
        // Answering is denied.
        await Assert.ThrowsAsync<SessionQuestionAccessDeniedException>(
            () => svc.RespondAsync(SpeakerTwo(seed), q!.Id, "I shouldn't be able to answer this"));

        // The attendee role cannot read/answer either.
        await Assert.ThrowsAsync<SessionQuestionAccessDeniedException>(
            () => svc.LoadForSessionAsync(Attendee(seed), session.Id));
    }

    [Fact]
    public async Task Organizer_can_respond_to_any_question()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var session = await AddSessionAsync(db, seed, "Talk", seed.SpeakerOneId);
        var token = await svc.EnsurePublicTokenAsync(session.Id);
        var q = await svc.SubmitPublicQuestionAsync(token, null, null, "Logistics question", null);

        Assert.True(await svc.RespondAsync(Organizer(seed), q!.Id, "Handled by the organizer team."));
        var answered = await db.SessionQuestions.SingleAsync(x => x.Id == q.Id);
        Assert.Equal(ScenarioSeed.OrganizerEmail, answered.RespondedByEmail);
        Assert.Equal(SessionQuestionStatus.Answered, answered.Status);
    }

    [Fact]
    public async Task My_sessions_lists_only_the_speakers_own_sessions()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        await AddSessionAsync(db, seed, "Mine", seed.SpeakerOneId);
        await AddSessionAsync(db, seed, "Theirs", seed.SpeakerTwoId);

        var mine = await svc.LoadMySessionsAsync(SpeakerOne(seed));
        var one = Assert.Single(mine);
        Assert.Equal("Mine", one.Title);

        // Organizers don't use this list (they use LoadAllForEvent).
        Assert.Empty(await svc.LoadMySessionsAsync(Organizer(seed)));
    }

    [Fact]
    public async Task Closing_a_question_sets_closed_status()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);
        var session = await AddSessionAsync(db, seed, "Talk", seed.SpeakerOneId);
        var token = await svc.EnsurePublicTokenAsync(session.Id);
        var q = await svc.SubmitPublicQuestionAsync(token, null, null, "Off-topic", null);

        Assert.True(await svc.CloseAsync(SpeakerOne(seed), q!.Id));
        Assert.Equal(SessionQuestionStatus.Closed,
            (await db.SessionQuestions.SingleAsync(x => x.Id == q.Id)).Status);
    }
}
