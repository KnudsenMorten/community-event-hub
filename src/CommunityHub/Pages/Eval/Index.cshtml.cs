using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Eval;

/// <summary>
/// §1259 — the WEB KIOSK page: an iPad in a stand doing the job of a built evaluation box.
/// </summary>
/// <remarks>
/// <para>🔒 <see cref="AllowAnonymousAttribute"/> is REQUIRED, not incidental — the same reason the
/// ingest controller carries it. The app has a fail-closed authorization fallback, so without this
/// an iPad in a corridor would be redirected to a PIN login it has no way to complete, in front of
/// attendees.</para>
///
/// <para>🔑 <b>No server state and no model.</b> The page is a single static document; its identity
/// lives in the browser's own storage after pairing, and everything it does afterwards is two API
/// calls. That is what lets one bookmarked URL serve all 20 iPads and survive a server restart
/// mid-session without anyone touching a stand.</para>
/// </remarks>
[AllowAnonymous]
public class IndexModel : PageModel
{
    public void OnGet()
    {
    }
}
