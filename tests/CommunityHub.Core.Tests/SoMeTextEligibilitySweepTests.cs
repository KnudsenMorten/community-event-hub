using System.Net;
using System.Text;
using CommunityHub.Core.Assistant;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1060(l) — the daily sweep that forms the stored verdict.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"do a daily rerun against ai … save it as an record on the session
/// row. each time they will be 0 until the text is eligible."</i></para>
///
/// <para>🔑 Two policies carry the whole design and both are pinned below: <b>re-ask the 0s, skip the
/// unchanged 1s</b>, and <b>an unreachable model writes nothing</b>.</para>
/// </remarks>
public sealed class SoMeTextEligibilitySweepTests
{
    private const int EventId = 42;

    /// <summary>The project's clock convention — a fixed instant, so a stored timestamp is assertable.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Answers with a fixed verdict, and COUNTS calls — the skip policy is only observable
    /// as a call that did not happen.</summary>
    private sealed class StubHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string Answer(int eligible, string reason = "") =>
        "{\"choices\":[{\"message\":{\"content\":\"{\\\"eligible\\\":" + eligible
        + ",\\\"reason\\\":\\\"" + reason + "\\\"}\"}}]}";

    private static (SoMeTextEligibilitySweep Sweep, StubHandler Http) Build(
        CommunityHubDbContext db, HttpStatusCode code, string body, TimeProvider clock)
    {
        var http = new StubHandler(code, body);
        var judge = new SoMeTextEligibilityJudge(
            new HttpClient(http),
            // ⚠️ `Enabled` is part of IsConfigured and defaults to FALSE — without it the judge
            // no-ops and every assertion here would report "no verdict" rather than a wrong one.
            new OpenAiOptions
            {
                Enabled = true, Endpoint = "https://ai.test", Deployment = "gpt", ApiKey = "k",
            });
        return (new SoMeTextEligibilitySweep(db, judge, clock), http);
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"sweep-{Guid.NewGuid():N}").Options);

    private static async Task<CommunityHubDbContext> DbWithSessionAsync(
        string? abstractText = "A practical hour on Kubernetes cost control, with demos.",
        Action<Session>? tweak = null)
    {
        var db = NewDb();
        db.Events.Add(new Event { Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "D", IsActive = true });
        var s = new Session { Id = 1, EventId = EventId, Title = "Kubernetes cost control", Abstract = abstractText };
        tweak?.Invoke(s);
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task An_eligible_verdict_is_recorded_with_its_hash_and_timestamp()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 11, 3, 0, 0, TimeSpan.Zero));
        using var db = await DbWithSessionAsync();
        var (sweep, _) = Build(db, HttpStatusCode.OK, Answer(1), clock);

        var r = await sweep.RunAsync(EventId);

        Assert.Equal(1, r.Judged);
        Assert.Equal(1, r.Eligible);

        var s = await db.Sessions.FindAsync(1);
        Assert.True(s!.SoMeTextEligible);
        Assert.Equal(clock.GetUtcNow(), s.SoMeTextEligibleCheckedAt);
        Assert.Equal(SoMeTextEligibilitySweep.HashOf(s.Title, s.Abstract), s.SoMeTextEligibleHash);
        // 🔒 No reason on a YES — a stale refusal quoted under an eligible session would be worse
        // than no explanation at all.
        Assert.Null(s.SoMeTextEligibleReason);
    }

    /// <summary>
    /// ⚠️ The fixture text is deliberately one the RULES let through — a note addressed to the
    /// organizers rather than to an audience. My first attempt used <i>"The abstract will be
    /// published shortly"</i>, which <see cref="SoMePlaceholderText"/> catches on its own, so the
    /// judge short-circuited and the assertion measured the rules' wording instead of the model's.
    /// 🔑 A test of the AI path has to use text only the AI can judge, or it is not testing that path.
    /// </summary>
    [Fact]
    public async Task A_refusal_keeps_the_models_reason_for_the_blocker_to_quote()
    {
        using var db = await DbWithSessionAsync(
            "Note for the organizers: please put this one in the big room after lunch.");
        var (sweep, _) = Build(db, HttpStatusCode.OK, Answer(0, "promises the abstract later"),
            new FixedClock(new DateTimeOffset(2026, 8, 11, 3, 0, 0, TimeSpan.Zero)));

        await sweep.RunAsync(EventId);

        var s = await db.Sessions.FindAsync(1);
        Assert.False(s!.SoMeTextEligible);
        Assert.Equal("promises the abstract later", s.SoMeTextEligibleReason);
    }

    /// <summary>
    /// 🔑 <b>Re-ask the 0s.</b> His words: <i>"each time they will be 0 until the text is eligible"</i>
    /// — a not-yet-eligible session is asked again even when nothing changed, because the previous
    /// answer is the one we are trying to overturn.
    /// </summary>
    [Fact]
    public async Task A_previously_refused_session_is_asked_again_even_with_unchanged_text()
    {
        using var db = await DbWithSessionAsync(tweak: s =>
        {
            s.SoMeTextEligible = false;
            s.SoMeTextEligibleHash = SoMeTextEligibilitySweep.HashOf(
                "Kubernetes cost control", "A practical hour on Kubernetes cost control, with demos.");
        });
        var (sweep, http) = Build(db, HttpStatusCode.OK, Answer(1), new FixedClock(new DateTimeOffset(2026, 8, 11, 3, 0, 0, TimeSpan.Zero)));

        var r = await sweep.RunAsync(EventId);

        Assert.Equal(1, http.Calls);
        Assert.Equal(1, r.Eligible);
        Assert.True((await db.Sessions.FindAsync(1))!.SoMeTextEligible);
    }

    /// <summary>
    /// 🔒 <b>Skip the unchanged 1s.</b> Not merely an optimisation: re-asking an already-eligible
    /// session daily lets model drift silently WITHDRAW a session that is already in the campaign,
    /// and a session vanishing from the plan for no visible reason is the hardest kind of bug to
    /// notice. Only observable as a call that did not happen.
    /// </summary>
    [Fact]
    public async Task An_already_eligible_session_with_unchanged_text_is_not_asked_again()
    {
        using var db = await DbWithSessionAsync(tweak: s =>
        {
            s.SoMeTextEligible = true;
            s.SoMeTextEligibleHash = SoMeTextEligibilitySweep.HashOf(
                "Kubernetes cost control", "A practical hour on Kubernetes cost control, with demos.");
        });
        var (sweep, http) = Build(db, HttpStatusCode.OK, Answer(0), new FixedClock(new DateTimeOffset(2026, 8, 11, 3, 0, 0, TimeSpan.Zero)));

        var r = await sweep.RunAsync(EventId);

        Assert.Equal(0, http.Calls);
        Assert.Equal(1, r.Skipped);
        Assert.True((await db.Sessions.FindAsync(1))!.SoMeTextEligible);
    }

    /// <summary>⚠️ An EDITED abstract must lose its old verdict — that is what the hash is for.</summary>
    [Fact]
    public async Task An_edited_abstract_is_re_judged_despite_a_previous_yes()
    {
        using var db = await DbWithSessionAsync(tweak: s =>
        {
            s.SoMeTextEligible = true;
            s.SoMeTextEligibleHash = "a-hash-of-some-older-text";
        });
        var (sweep, http) = Build(db, HttpStatusCode.OK, Answer(0, "now says TBD"), new FixedClock(new DateTimeOffset(2026, 8, 11, 3, 0, 0, TimeSpan.Zero)));

        await sweep.RunAsync(EventId);

        Assert.Equal(1, http.Calls);
        Assert.False((await db.Sessions.FindAsync(1))!.SoMeTextEligible);
    }

    /// <summary>
    /// 🔴 <b>THE OUTAGE CASE.</b> An unreachable model must leave the previous verdict standing. If a
    /// 500 wrote <c>false</c>, one bad afternoon would empty the entire campaign — and it would look
    /// like every speaker had suddenly written a bad abstract, not like an outage.
    /// </summary>
    [Fact]
    public async Task An_unreachable_model_writes_nothing_and_keeps_the_previous_verdict()
    {
        using var db = await DbWithSessionAsync(tweak: s =>
        {
            s.SoMeTextEligible = true;
            s.SoMeTextEligibleHash = "older";
        });
        var (sweep, _) = Build(db, HttpStatusCode.InternalServerError, "boom", new FixedClock(new DateTimeOffset(2026, 8, 11, 3, 0, 0, TimeSpan.Zero)));

        var r = await sweep.RunAsync(EventId);

        Assert.Equal(1, r.NoVerdict);
        Assert.Equal(0, r.Judged);
        var s = await db.Sessions.FindAsync(1);
        Assert.True(s!.SoMeTextEligible);          // untouched
        Assert.Equal("older", s.SoMeTextEligibleHash);
    }

    /// <summary>
    /// 🔒 Sessions that can never be announced are not judged — spending a call to answer a question
    /// nothing will read. Each exclusion is asserted separately because they come from different
    /// rules (§909 test data, Sessionize service rows, §1060(h) the flag, §1060(m) the type).
    /// </summary>
    [Theory]
    [InlineData("test")]
    [InlineData("service")]
    [InlineData("excluded")]
    [InlineData("asktheexperts")]
    public async Task A_session_that_can_never_be_announced_is_never_judged(string kind)
    {
        using var db = await DbWithSessionAsync(tweak: s =>
        {
            switch (kind)
            {
                case "test": s.IsTestData = true; break;
                case "service": s.IsServiceSession = true; break;
                case "excluded": s.ExcludeFromSoMeAnnouncements = true; break;
                case "asktheexperts": s.Type = SessionType.AskTheExperts; break;
            }
        });
        var (sweep, http) = Build(db, HttpStatusCode.OK, Answer(1), new FixedClock(new DateTimeOffset(2026, 8, 11, 3, 0, 0, TimeSpan.Zero)));

        var r = await sweep.RunAsync(EventId);

        Assert.Equal(0, r.Considered);
        Assert.Equal(0, http.Calls);
    }

    /// <summary>⚠️ A reformat is not a change — otherwise every whitespace edit costs a call.</summary>
    [Fact]
    public void The_hash_ignores_whitespace_but_not_wording()
    {
        Assert.Equal(
            SoMeTextEligibilitySweep.HashOf("T", "one  two\nthree"),
            SoMeTextEligibilitySweep.HashOf("T", "one two three"));

        Assert.NotEqual(
            SoMeTextEligibilitySweep.HashOf("T", "one two three"),
            SoMeTextEligibilitySweep.HashOf("T", "one two four"));

        // 🔑 The TITLE is part of the question the model is asked, so a retitled session is a new
        // question even when the abstract is untouched.
        Assert.NotEqual(
            SoMeTextEligibilitySweep.HashOf("T", "same"),
            SoMeTextEligibilitySweep.HashOf("U", "same"));
    }
}
