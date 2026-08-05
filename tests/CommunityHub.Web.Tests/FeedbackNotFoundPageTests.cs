using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Evaluation;
using CommunityHub.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §748.4 — an unrecognised QR token must render an honest PAGE that still carries a real
/// <c>404</c>, not the empty body a bare <c>NotFound()</c> produces.
/// </summary>
/// <remarks>
/// <para>The person this protects is standing in a room, holding a phone, having just scanned a sign
/// on a wall. Before this, they got a browser error page: no branding, no wording, and no way to tell
/// whether the fault was theirs, the code's, or the event's. Every OTHER refusal state on
/// <c>/f/{token}</c> — closed, not-started, not-yet-mirrored — already rendered properly; this was the
/// one hole, and it is the state a CANCELLED session's printed code lands in.</para>
///
/// <para>🔑 Both halves are asserted together on purpose, because either alone is a plausible wrong
/// fix: returning the page with a <c>200</c> (the usual mistake — it tells crawlers, uptime probes
/// and link checkers that a revoked code is a healthy URL), or keeping the honest <c>404</c> with no
/// body at all (what it did before).</para>
///
/// <para>EF Core InMemory, FAKE names only; no network, no SQL, no secrets.</para>
/// </remarks>
public sealed class FeedbackNotFoundPageTests
{
    private const int EventId = 4;
    private const int CehSessionId = 61;
    private const string LiveToken = "printed-token-bbbbbbbbbbbb";

    private static readonly DateTimeOffset Start = new(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DuringSession = Start.AddMinutes(30);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DuringSession;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"feedback-404-{Guid.NewGuid():N}")
            .Options);

    private static async Task<FeedbackModel> PageAsync()
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "Experts Live Denmark 2027",
            Code = "ELDK27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.Sessions.Add(new Session
        {
            Id = CehSessionId, EventId = EventId, Title = "Keeping identity boring",
            PublicToken = LiveToken,
        });
        await db.SaveChangesAsync();

        return new FeedbackModel(new EvaluationQrService(db, new FixedClock()),
                                 NullLogger<FeedbackModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
        };
    }

    /// <summary>
    /// 🔒 The headline: a PAGE (so the attendee reads something) carrying a 404 (so machines still
    /// see a dead URL). A bare <c>NotFound()</c> would fail the first assertion; a plain
    /// <c>Page()</c> would fail the second.
    /// </summary>
    [Fact]
    public async Task An_unknown_token_renders_a_PAGE_and_still_answers_404()
    {
        var page = await PageAsync();

        var result = await page.OnGetAsync("no-such-token", CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, page.HttpContext.Response.StatusCode);
    }

    /// <summary>
    /// The view branches on <c>Resolution.State</c> to pick its heading and its wording, so the state
    /// must survive onto the model rather than being consumed by the early return.
    /// </summary>
    [Fact]
    public async Task The_NotFound_state_reaches_the_view_so_it_can_say_which_refusal_this_is()
    {
        var page = await PageAsync();

        await page.OnGetAsync("no-such-token", CancellationToken.None);

        Assert.NotNull(page.Resolution);
        Assert.Equal(EvaluationQrService.FeedbackState.NotFound, page.Resolution!.State);
        // Nothing to name — that is what NotFound MEANS, and why this state gets its own heading
        // instead of rendering an empty <h1>.
        Assert.True(string.IsNullOrEmpty(page.Resolution.Title));
    }

    /// <summary>
    /// 🔑 The POST path refuses identically. A form left open on a phone can be posted after the
    /// session is deleted, and an empty 404 there would read as "your rating went nowhere, silently"
    /// — the exact failure the brief calls worse than an honest refusal.
    /// </summary>
    [Fact]
    public async Task Posting_an_unknown_token_also_renders_the_page_with_404_and_writes_nothing()
    {
        var page = await PageAsync();
        page.Rating = 4;
        page.Comment = "Should never be stored.";

        var result = await page.OnPostAsync("no-such-token", CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, page.HttpContext.Response.StatusCode);
        Assert.False(page.SubmittedOk);
    }

    /// <summary>
    /// The guard rail: a LIVE token must not be swept up by any of the above. Without this, deleting
    /// the resolve call entirely would leave the three tests above green.
    /// </summary>
    [Fact]
    public async Task A_live_token_still_renders_the_form_with_200()
    {
        var page = await PageAsync();

        var result = await page.OnGetAsync(LiveToken, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(StatusCodes.Status200OK, page.HttpContext.Response.StatusCode);
        Assert.NotEqual(EvaluationQrService.FeedbackState.NotFound, page.Resolution!.State);
    }
}
