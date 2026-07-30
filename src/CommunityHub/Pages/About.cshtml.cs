using CommunityHub.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Html;

namespace CommunityHub.Pages;

/// <summary>
/// §327g (operator 2026-07-25: "can this page be public available so i can show it to people
/// outside the event" → "remove the important security stuff and publish") — the PUBLIC,
/// sign-in-free copy of the Community Event Hub introduction.
///
/// <para>It renders the SAME markdown as the in-hub <c>/Info/ceh-introduction</c> page, with
/// blocks fenced <c>internal-only</c> stripped BEFORE rendering — currently the whole Security
/// chapter and its entry in the quick-links table. Stripping at the source, not with CSS,
/// matters: the withheld text never reaches the HTML, so it cannot be read from "view
/// source".</para>
///
/// <para>One markdown file feeds both pages on purpose. A duplicated public copy would drift,
/// and the copy that drifts is always the one strangers read.</para>
/// </summary>
[AllowAnonymous]
public class AboutModel : PageModel
{
    /// <summary>The shared content slug — the same file the in-hub Info page renders.</summary>
    public const string Slug = "ceh-introduction";

    private readonly ContentMarkdownRenderer _renderer;
    private readonly CommunityHub.Core.Data.CommunityHubDbContext _db;
    private readonly CommunityHub.Core.Settings.FeatureGateService _gate;

    public AboutModel(
        ContentMarkdownRenderer renderer,
        CommunityHub.Core.Data.CommunityHubDbContext db,
        CommunityHub.Core.Settings.FeatureGateService gate)
    {
        _renderer = renderer;
        _db = db;
        _gate = gate;
    }

    public HtmlString BodyHtml { get; private set; } = HtmlString.Empty;
    public bool ContentMissing { get; private set; }

    public async Task<IActionResult> OnGetAsync(System.Threading.CancellationToken ct)
    {
        // §351-5 — the public copy resolves the [feature:key] directives against the ACTIVE
        // edition, exactly as the in-hub page does; a strangers-facing page listing a capability
        // the event has switched off is the worst place for that claim to be wrong.
        //
        // No active event ⇒ no truth to read, so the lookup is skipped and every row shows (the
        // behaviour before this existed). Failing OPEN here is deliberate: the alternative,
        // falling back to the catalog defaults, would silently blank most of the feature tables
        // on a page whose whole job is to describe the product.
        var eventId = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(
                _db.Events.Where(e => e.IsActive).OrderByDescending(e => e.Id).Select(e => (int?)e.Id),
                ct);

        var features = eventId is int id
            ? await _renderer.BuildFeatureLookupAsync(Slug, _gate, id, ct)
            : null;

        // publicView: true ⇒ internal-only blocks are removed from the markdown first.
        if (_renderer.TryRender(Slug, out var html, publicView: true, features))
        {
            BodyHtml = new HtmlString(html);
        }
        else
        {
            ContentMissing = true;
        }
        return Page();
    }
}
