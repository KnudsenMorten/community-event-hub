using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests.Organizer;

/// <summary>
/// §1049 / §1050 — CREATING A POST MUST BE FINDABLE, AND THERE MUST BE ONLY ONE PLACE TO COMPOSE.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10: <i>"missing ability to create new"</i> — and it was not missing. The
/// compose panel had been on this page since §834.5. §889.4 then moved it BELOW the list and
/// collapsed it, for a good reason (the page's job is finding an EXISTING post, and it is a tall
/// form). The result: he reported creation as ABSENT while it sat on the page, shut, under 63 rows.
/// 🔑 <b>A capability folded shut below the fold is indistinguishable from one that does not
/// exist</b> — §1042 again, where the approve-walk was "gone" behind a card named for a calendar.</para>
///
/// <para>He then asked for a post PREVIEW and for VARIABLES on that panel, and immediately answered
/// his own question: <i>"why not make the new post part of the other edit option"</i> · <i>"instead
/// of reengineering"</i> · <i>"so we have 1 solution"</i>. The Post Editor already had all of it.
/// ⇒ §1050: creating a post makes a blank HELD post and opens it in the editor, and the ad-hoc
/// compose form — with its own text, image and schedule fields — was DELETED rather than left
/// unreachable.</para>
///
/// <para>⚠️ These tests pin PLACEMENT and SINGULARITY, not mere presence. A test that only asserted
/// "a way to create exists" would have been green through the entire episode.</para>
/// </remarks>
public sealed class WriteFromScratchIsFindableTests
{
    private const int EventId = 93;

    private static string PageSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var p = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages", "Organizer", file);
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate {file} from " + AppContext.BaseDirectory);
    }

    /// <summary>🔴 THE REGRESSION THAT COST AN EVENING: creation must come BEFORE the post list.</summary>
    [Fact]
    public void The_create_control_comes_before_the_post_list()
    {
        var razor = PageSource("SoMeQueue.cshtml");

        var panel = razor.IndexOf("id=\"write-from-scratch\"", StringComparison.Ordinal);
        var list = razor.IndexOf("§889 — THE LIST IS THE LANDING", StringComparison.Ordinal);

        Assert.True(panel >= 0, "The create control is gone from SoMeQueue.cshtml entirely.");
        Assert.True(list >= 0, "The list section marker moved — this test can no longer see the order.");
        Assert.True(panel < list,
            "'Write a new post' must sit ABOVE the post list. Below it, the operator reported "
            + "creating a post as MISSING while the control was on the page (§1049).");
    }

    /// <summary>It posts to the handler that opens the editor — not to a second compose form.</summary>
    [Fact]
    public void The_create_control_hands_off_to_the_editor()
    {
        var razor = PageSource("SoMeQueue.cshtml");
        Assert.Contains("asp-page-handler=\"NewPost\"", razor, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔒 §1050 — ONE SOLUTION. The old ad-hoc compose form composed a post (text + image +
    /// schedule) beside a page whose job is finding one, and the moment it needed a preview and
    /// variables it was going to become a second editor. It is deleted, not merely unlinked: an
    /// orphaned POST handler is reachable by URL and by nothing else, which is the worst of both —
    /// this page has been there before (§889's two handlers that outlived their forms).
    /// </summary>
    [Fact]
    public void The_queue_page_no_longer_composes_a_post_itself()
    {
        var razor = PageSource("SoMeQueue.cshtml");
        var model = PageSource("SoMeQueue.cshtml.cs");

        Assert.DoesNotContain("asp-page-handler=\"AdHoc\"", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("[BindProperty] public string? AdHocText", model, StringComparison.Ordinal);

        // ⚠️ Match the DECLARATION, not the name. The tombstone comment recording why the handler
        // was removed necessarily says its name, and a bare substring check fails on the very
        // comment that documents the fix — which is how a correct test reports a false regression.
        Assert.DoesNotContain("Task<IActionResult> OnPostAdHocAsync", model, StringComparison.Ordinal);
    }

    /// <summary>Duplicate is reachable from the LIST, and reuses the editor's handler (§913).</summary>
    [Fact]
    public void Every_row_can_be_duplicated_through_the_editors_own_handler()
    {
        var razor = PageSource("SoMeQueue.cshtml");

        Assert.Contains("asp-page-handler=\"Duplicate\"", razor, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Organizer/SoMePostEditor\"", razor, StringComparison.Ordinal);
    }

    // ---- the handler ------------------------------------------------------------------

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"scratch-{Guid.NewGuid():N}").Options);

    private sealed class FakeAccessor(CurrentParticipant? cur) : ICurrentParticipantAccessor
    {
        public CurrentParticipant? Current { get; } = cur;
    }

    private static CurrentParticipant? Session(Participant p)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        };
        return CurrentParticipant.FromPrincipal(new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    }

    private static async Task<Participant> SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "Q27", CommunityName = "C", DisplayName = "Q 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        var org = new Participant
        {
            EventId = EventId, FullName = "Org", Email = "org@example.test",
            Role = ParticipantRole.Organizer, IsActive = true,
        };
        db.Participants.Add(org);
        await db.SaveChangesAsync();
        return org;
    }


    /// <summary>
    /// 🔒 §1169 — a FIXED clock, set before every seeded date.
    /// </summary>
    /// <remarks>
    /// The queue now orders “next first”, which is relative to NOW. With the real clock these
    /// tests would have quietly changed meaning as the seeded dates slid into the past — they are
    /// about FILTERING, not ordering, and §1169 has its own tests. Pinning the clock keeps each
    /// test about one thing.
    /// </remarks>
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static readonly TimeProvider BeforeEverything =
        new FixedClock(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
    private static SoMeQueueModel NewModel(CommunityHubDbContext db, Participant org) =>
        new(new FakeAccessor(Session(org)), new SoMeQueueService(db, TimeProvider.System),
            new SoMeSubjectLabeller(db), BeforeEverything, new SoMeReadiness(db))
        {
            PageContext = new PageContext(),
        };

    /// <summary>Creating hands straight off to the editor, on the post just made.</summary>
    [Fact]
    public async Task Creating_a_post_opens_it_in_the_editor()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var page = NewModel(db, org);

        var result = await page.OnPostNewPostAsync(default);

        var post = await db.SoMePosts.SingleAsync();
        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Organizer/SoMePostEditor", redirect.PageName);
        Assert.Equal(post.Id, redirect.RouteValues!["id"]);
    }

    /// <summary>
    /// 🔒 §915.1 survives the consolidation: a post written from scratch is born HELD. This is the
    /// one kind a human makes in a hurry, and it was once the one kind that skipped the gate — a
    /// blank post that could publish would be the worst possible version of that bug.
    /// </summary>
    [Fact]
    public async Task A_new_post_is_held_and_scheduled_in_the_future()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var page = NewModel(db, org);

        await page.OnPostNewPostAsync(default);

        var post = await db.SoMePosts.SingleAsync();
        Assert.False(post.IsActive);
        Assert.Equal(SoMePostStatus.Queued, post.Status);
        Assert.Equal(SoMePostType.AdHoc, post.Type);
        // A default in the past would read as overdue the moment it is created (§889.1).
        Assert.True(post.ScheduledAtUtc > DateTimeOffset.UtcNow);
    }
}
