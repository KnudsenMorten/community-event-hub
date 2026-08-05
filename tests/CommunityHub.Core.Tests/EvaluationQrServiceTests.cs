using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

using State = CommunityHub.Core.Evaluation.EvaluationQrService.FeedbackState;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §748 C5/C6 — the QR channel: a printed token resolves to a session, and a scan is recorded as an
/// ordinary <see cref="EvaluationResponse"/>.
/// </summary>
/// <remarks>
/// The behaviour these pin is mostly about REFUSING correctly. The brief is blunt about why:
/// *"A form that appears to work but discards the result is worse than an honest refusal"* — and the
/// failure is invisible, because the attendee walks away believing they were heard.
/// </remarks>
public sealed class EvaluationQrServiceTests
{
    private const int EventId = 1;
    private const int CehSessionId = 55;
    private const string Token = "printed-token-aaaaaaaaaaaa";

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public void Set(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>The session runs 10:00–11:00; the window closes 30 minutes after it ends.</summary>
    private static readonly DateTimeOffset Start = new(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(1);
    private static readonly DateTimeOffset WindowCloses = End.AddMinutes(30);
    private static readonly DateTimeOffset DuringSession = Start.AddMinutes(30);

    private static async Task<CommunityHubDbContext> SeedAsync(bool synced = true)
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "Experts Live Denmark 2027",
            Code = "ELDK27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.Sessions.Add(new Session
        {
            Id = CehSessionId, EventId = EventId, Title = "Keeping identity boring",
            PublicToken = Token,
        });

        if (synced)
        {
            db.EvaluationSessions.Add(new EvaluationSession
            {
                Id = 900, EventId = EventId, CehSessionId = CehSessionId,
                Title = "Keeping identity boring",
                ScheduledStart = Start, ScheduledEnd = End,
                CollectionWindowOpensAt = Start, CollectionWindowClosesAt = WindowCloses,
                CreatedAt = Start.AddDays(-1), UpdatedAt = Start.AddDays(-1),
            });
        }

        await db.SaveChangesAsync();
        return db;
    }

    private static EvaluationQrService Svc(CommunityHubDbContext db, DateTimeOffset now) =>
        new(db, new FixedClock(now));

    // ---- resolution ---------------------------------------------------------------------------

    [Fact]
    public async Task Token_resolves_to_the_session_during_the_window()
    {
        var db = await SeedAsync();

        var r = await Svc(db, DuringSession).ResolveAsync(Token);

        Assert.Equal(State.Open, r.State);
        Assert.Equal("Keeping identity boring", r.Title);
        Assert.Equal(900, r.EvaluationSessionId);
    }

    /// <summary>
    /// 🔒 The brief requires a DELETED session's printed code to resolve to an honest "not found"
    /// rather than silently accepting orphan feedback.
    /// </summary>
    [Fact]
    public async Task Unknown_token_is_not_found()
    {
        var db = await SeedAsync();
        var svc = Svc(db, DuringSession);

        Assert.Equal(State.NotFound, (await svc.ResolveAsync("no-such-token")).State);
        Assert.Equal(State.NotFound, (await svc.ResolveAsync("")).State);
        Assert.Equal(State.NotFound, (await svc.ResolveAsync(null)).State);
    }

    /// <summary>
    /// 🔑 The title comes back even when the window is SHUT. The brief wants an attendee who scanned
    /// the wrong code to notice before submitting — equally true when the answer is a refusal, and a
    /// nameless "closed" page tells them nothing about which code they scanned.
    /// </summary>
    [Fact]
    public async Task Refusals_still_name_the_session()
    {
        var db = await SeedAsync();

        var closed = await Svc(db, WindowCloses.AddMinutes(1)).ResolveAsync(Token);
        var early = await Svc(db, Start.AddMinutes(-10)).ResolveAsync(Token);

        Assert.Equal(State.Closed, closed.State);
        Assert.Equal("Keeping identity boring", closed.Title);
        Assert.Equal(State.NotOpenYet, early.State);
        Assert.Equal("Keeping identity boring", early.Title);
    }

    /// <summary>A CEH session never mirrored into the evaluation model has no window to check.</summary>
    [Fact]
    public async Task Unsynced_session_is_not_open()
    {
        var db = await SeedAsync(synced: false);

        var r = await Svc(db, DuringSession).ResolveAsync(Token);

        Assert.Equal(State.NotSynced, r.State);
        Assert.Null(r.EvaluationSessionId);
        Assert.Equal("Keeping identity boring", r.Title);   // still named
    }

    // ---- the window boundaries ----------------------------------------------------------------

    /// <summary>
    /// The exact edges. A window that is open AT its closing instant and shut one tick later is the
    /// difference between a rating counted and a rating refused, and off-by-one here is invisible.
    /// </summary>
    [Fact]
    public async Task Window_boundaries_are_inclusive_at_both_ends()
    {
        var db = await SeedAsync();

        Assert.Equal(State.NotOpenYet, (await Svc(db, Start.AddTicks(-1)).ResolveAsync(Token)).State);
        Assert.Equal(State.Open, (await Svc(db, Start).ResolveAsync(Token)).State);
        Assert.Equal(State.Open, (await Svc(db, WindowCloses).ResolveAsync(Token)).State);
        Assert.Equal(State.Closed, (await Svc(db, WindowCloses.AddTicks(1)).ResolveAsync(Token)).State);
    }

    /// <summary>
    /// 🔑 The 30-minute grace tail is the point of the window: people rate on their way out, after
    /// the session has ended.
    /// </summary>
    [Fact]
    public async Task Feedback_is_accepted_after_the_session_ends_within_the_grace_tail()
    {
        var db = await SeedAsync();

        var result = await Svc(db, End.AddMinutes(20)).SubmitAsync(Token, 4, null);

        Assert.True(result.Saved);
    }

    // ---- submission ---------------------------------------------------------------------------

    /// <summary>
    /// 🔒 A scan writes the SAME table as a device press — one instrument, two delivery paths — with
    /// `Source` recording the path for diagnostics only, and no device fields.
    /// </summary>
    [Fact]
    public async Task Submission_writes_an_ordinary_response_marked_qr()
    {
        var db = await SeedAsync();

        var result = await Svc(db, DuringSession).SubmitAsync(Token, 3, "  Great pacing  ");

        Assert.True(result.Saved);
        var row = await db.EvaluationResponses.SingleAsync();
        Assert.Equal(EvaluationResponseSources.Qr, row.Source);
        Assert.Equal(3, row.Rating);
        Assert.Equal(EventId, row.EventId);
        Assert.Equal(900, row.SessionId);
        Assert.Equal("Great pacing", row.FreeText);      // trimmed
        Assert.Null(row.SerialNumber);
        Assert.Null(row.DeviceRecordId);
        Assert.Equal(DuringSession, row.CollectionTimestamp);
        Assert.Equal(DuringSession, row.ReceivedTimestamp);
    }

    [Fact]
    public async Task Blank_comment_is_stored_as_null_not_empty()
    {
        var db = await SeedAsync();

        await Svc(db, DuringSession).SubmitAsync(Token, 4, "   ");

        Assert.Null((await db.EvaluationResponses.SingleAsync()).FreeText);
    }

    /// <summary>
    /// 🔒 The window is re-checked at SUBMIT, not trusted from the rendered form. A page left open
    /// on a phone can be posted after the window shuts, and that submission must be refused rather
    /// than counted.
    /// </summary>
    [Fact]
    public async Task Submitting_after_the_window_closes_writes_nothing()
    {
        var db = await SeedAsync();

        var result = await Svc(db, WindowCloses.AddMinutes(5)).SubmitAsync(Token, 4, "too late");

        Assert.False(result.Saved);
        Assert.Equal(State.Closed, result.State);
        Assert.Empty(db.EvaluationResponses);
    }

    [Fact]
    public async Task Submitting_before_the_window_opens_writes_nothing()
    {
        var db = await SeedAsync();

        var result = await Svc(db, Start.AddHours(-2)).SubmitAsync(Token, 4, null);

        Assert.False(result.Saved);
        Assert.Equal(State.NotOpenYet, result.State);
        Assert.Empty(db.EvaluationResponses);
    }

    [Fact]
    public async Task Submitting_on_an_unknown_token_writes_nothing()
    {
        var db = await SeedAsync();

        var result = await Svc(db, DuringSession).SubmitAsync("nope", 4, null);

        Assert.False(result.Saved);
        Assert.Equal(State.NotFound, result.State);
        Assert.Empty(db.EvaluationResponses);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public async Task Out_of_range_rating_is_rejected(int rating)
    {
        var db = await SeedAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Svc(db, DuringSession).SubmitAsync(Token, rating, null));
        Assert.Empty(db.EvaluationResponses);
    }

    /// <summary>
    /// 🔒 APPEND-ONLY (§8.1): a second submission is a NEW row, never an edit. That is what keeps
    /// recomputation after late data safe to repeat — and it matches the device, where nothing stops
    /// the same person pressing twice either.
    /// </summary>
    [Fact]
    public async Task A_second_submission_appends_rather_than_updating()
    {
        var db = await SeedAsync();
        var svc = Svc(db, DuringSession);

        await svc.SubmitAsync(Token, 4, "first");
        await svc.SubmitAsync(Token, 1, "second");

        Assert.Equal(2, await db.EvaluationResponses.CountAsync());
    }

    [Fact]
    public async Task Free_text_longer_than_the_column_is_truncated_not_thrown()
    {
        var db = await SeedAsync();

        await Svc(db, DuringSession).SubmitAsync(
            Token, 4, new string('x', EvaluationQrService.MaxFreeTextLength + 500));

        var row = await db.EvaluationResponses.SingleAsync();
        Assert.Equal(EvaluationQrService.MaxFreeTextLength, row.FreeText!.Length);
    }

    // ---- the token ----------------------------------------------------------------------------

    /// <summary>
    /// 🔒 An existing token is NEVER replaced — printed material depends on it, and it is the same
    /// token the ask page addresses.
    /// </summary>
    [Fact]
    public async Task EnsureToken_never_replaces_an_existing_token()
    {
        var db = await SeedAsync();

        Assert.Equal(Token, await Svc(db, DuringSession).EnsureTokenAsync(CehSessionId));
    }

    [Fact]
    public async Task EnsureToken_mints_one_when_absent_and_is_then_stable()
    {
        var db = await SeedAsync();
        db.Sessions.Add(new Session { Id = 77, EventId = EventId, Title = "No token yet" });
        await db.SaveChangesAsync();
        var svc = Svc(db, DuringSession);

        var first = await svc.EnsureTokenAsync(77);
        var second = await svc.EnsureTokenAsync(77);

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, second);
    }

    /// <summary>Unguessable is the entire security model of an anonymous page, and it must be URL-safe.</summary>
    [Fact]
    public void New_tokens_are_unguessable_and_url_safe()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => EvaluationQrService.NewToken()).ToList();

        Assert.Equal(200, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, t =>
        {
            Assert.True(t.Length >= 30, $"token too short: {t}");
            Assert.All(t, c => Assert.True(
                char.IsLetterOrDigit(c) || c is '-' or '_', $"unsafe character '{c}'"));
        });
    }

    // ---- labels are presentation ---------------------------------------------------------------

    /// <summary>
    /// 🔑 Rating 2 must NOT read as neutral. There is no midpoint, so 2 counts as negative in every
    /// figure — and a button labelled "OK" would leave respondents' mental model out of step with
    /// how their answer aggregates, which biases the result rather than merely confusing them.
    /// </summary>
    [Fact]
    public void Rating_two_is_labelled_negatively()
    {
        var two = EvaluationRatingLabels.Label(2);

        Assert.False(string.IsNullOrWhiteSpace(two));
        Assert.DoesNotContain("ok", two, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("neutral", two, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("average", two, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Buttons_run_best_to_worst_and_all_four_are_labelled()
    {
        Assert.Equal(new[] { 4, 3, 2, 1 }, EvaluationRatingLabels.Order);
        Assert.All(EvaluationRatingLabels.Order,
            r => Assert.False(string.IsNullOrWhiteSpace(EvaluationRatingLabels.Label(r))));
    }
}
