using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// One setup step. <paramref name="Done"/> mirrors <c>SponsorWizardStep.Done</c> — nullable, because
/// "cannot be determined" is not "not done".
/// </summary>
public sealed record SoMeWizardStep(
    string Key, string Title, string What, string Href, bool? Done, string? Detail = null);

/// <summary>Progress over the setup steps, and the one thing to do next.</summary>
public sealed record SoMeWizardView(IReadOnlyList<SoMeWizardStep> Steps, SoMeEngineState Engine)
{
    public int TotalSteps => Steps.Count;
    public int DoneCount => Steps.Count(s => s.Done == true);
    public bool AllDone => TotalSteps > 0 && DoneCount >= TotalSteps;
    public int Percent => TotalSteps == 0 ? 0 : (int)Math.Round(100.0 * DoneCount / TotalSteps);

    /// <summary>The first unfinished step — what the page tells him to do next.</summary>
    public SoMeWizardStep? NextStep => Steps.FirstOrDefault(s => s.Done == false);
}

/// <summary>
/// What the ENGINE has done — reported, never presented as a task.
/// </summary>
/// <remarks>
/// 🔒 §834.2 (operator: <i>"as the engine must autobuild everything"</i>). Planning, scheduling,
/// composing and holding for approval are the engine's work, so they are a STATUS LINE here rather
/// than wizard steps. An earlier draft made them steps 5–7; that taught the opposite of how his own
/// engine works, and he called it out (<i>"so dont overengineer wizard"</i>).
/// </remarks>
public sealed record SoMeEngineState(int Planned, int Held, int Approved, int Published);

/// <summary>
/// §834 — THE SoMe SETUP WIZARD. Four steps, modelled on the Get Started flow he named
/// (<i>"we have method from get started idea"</i>).
///
/// <para>🔒 <b>SETUP-ONLY, AND FOR A NEW EDITION</b> (§834.2: <i>"wizard is for new only"</i>). It
/// covers what a HUMAN must supply once — connect, footer, templates, deck — and then stands down.
/// Everything downstream the engine autobuilds, so it is reported as state and never as a chore.</para>
///
/// <para>🔒 <b>Every step is computed from live data. Nothing is stored</b> — same as the sponsor
/// wizard. A stored "step 3 complete" is a second source of truth that goes stale the moment someone
/// clears the footer, and it would need a migration and a reset path to be wrong in.</para>
///
/// <para>⚠️ Organizer-only for now (§834.1).</para>
/// </summary>
public sealed class SoMeWizardService
{
    private readonly CommunityHubDbContext _db;

    public SoMeWizardService(CommunityHubDbContext db) => _db = db;

    public async Task<SoMeWizardView> BuildAsync(int eventId, CancellationToken ct = default)
    {
        // --- 1. Connect LinkedIn ---------------------------------------------------------------
        // ⚠️ The org token is NOT event-scoped — it is the connection to the company PAGE, which
        // outlives an edition. Filtering it by EventId would report "not connected" on every
        // edition but the one that happened to connect it.
        var token = await _db.LinkedInOAuthTokens
            .Where(t => t.Kind == "org")
            .OrderByDescending(t => t.Id)
            .Select(t => new { t.ExpiresAt })
            .FirstOrDefaultAsync(ct);

        var tokenLive = token is not null && token.ExpiresAt > DateTimeOffset.UtcNow;

        // --- 2. The post footer ----------------------------------------------------------------
        var footer = await _db.SoMeSettings
            .Where(s => s.EventId == eventId)
            .Select(s => new { s.EventSystemUrl, s.EventTags, s.OrganizerCredits })
            .FirstOrDefaultAsync(ct);

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(footer?.EventSystemUrl)) missing.Add("event link");
        if (string.IsNullOrWhiteSpace(footer?.EventTags)) missing.Add("hashtags");
        if (string.IsNullOrWhiteSpace(footer?.OrganizerCredits)) missing.Add("organizer credit");

        // --- 3. The post types -----------------------------------------------------------------
        var edited = await _db.SoMeTemplates.CountAsync(t => t.EventId == eventId, ct);

        // --- 4. The event-post deck ------------------------------------------------------------
        var eventPosts = await _db.EventSoMePosts.CountAsync(p => p.EventId == eventId, ct);
        var eventRuns = await _db.EventSoMePostOccurrences.CountAsync(o => o.Post.EventId == eventId, ct);

        var steps = new List<SoMeWizardStep>
        {
            new("connect", "Connect LinkedIn",
                "Give CEH permission to post on your company page.",
                "/Organizer/LinkedInConnect", tokenLive,
                token is null
                    ? "Not connected yet."
                    : tokenLive
                        ? $"Connected. Expires {token.ExpiresAt:yyyy-MM-dd}."
                        : $"Expired {token.ExpiresAt:yyyy-MM-dd} — reconnect to post."),

            new("footer", "Write the post footer",
                "The link, hashtags and organizer credit that end every post.",
                "/Organizer/SoMeSettings", missing.Count == 0,
                missing.Count == 0
                    ? "Complete."
                    // 🔑 The §824.23 refusal, in his words. Previously only in App Insights.
                    : $"Missing: {string.Join(", ", missing)}. The engine plans nothing until this is "
                      + "filled in — posts are written once and never rewritten, so it will not queue "
                      + "them without their footer."),

            new("templates", "Design the post types",
                "The wording each of the five post types is built from.",
                "/Organizer/SoMeTemplates", true,
                edited == 0
                    ? "Using the shipped defaults — worth reading so the posts sound like you."
                    : $"{edited} of 5 edited for this edition."),

            new("eventposts", "Import your event posts",
                "The general posts you write yourself, imported from your markdown deck.",
                "/Organizer/EventPosts", eventPosts > 0,
                eventPosts == 0
                    ? "Nothing imported yet."
                    : $"{eventPosts} posts, {eventRuns} scheduled runs."),
        };

        var posts = await _db.SoMePosts
            .Where(p => p.EventId == eventId)
            .Select(p => new { p.Status, p.IsActive, p.TemplateKind })
            .ToListAsync(ct);

        var engine = new SoMeEngineState(
            Planned: posts.Count(p => p.TemplateKind != null),
            Held: posts.Count(p => p.Status == SoMePostStatus.Queued && !p.IsActive),
            Approved: posts.Count(p => p.Status == SoMePostStatus.Queued && p.IsActive),
            Published: posts.Count(p => p.Status == SoMePostStatus.Published));

        return new SoMeWizardView(steps, engine);
    }
}
