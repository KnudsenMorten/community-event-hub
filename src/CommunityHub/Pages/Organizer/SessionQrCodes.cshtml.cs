using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// ⚰️ §784.7(C) — MERGED INTO <c>/Organizer/SessionFeedback</c>. This is a redirect, on purpose.
/// </summary>
/// <remarks>
/// <para>Generating codes, previewing one and downloading them all as a ZIP moved to the merged
/// page, where the code sits on the same row as the score and the report state — the point of the
/// merge being that these are facts about ONE session.</para>
///
/// <para>🔒 The rule that survived the move unchanged: <b>the token is permanent and there is no
/// regenerate.</b> Printed material depends on it, so a session that moves room or time keeps the
/// code already on the wall.</para>
///
/// <para>🔒 Kept as a redirect, not deleted — §784.7: *"Leave redirects from the old URLs"*. Both
/// file handlers forward too, so a saved image or ZIP link still works.</para>
/// </remarks>
[Authorize]
public class SessionQrCodesModel : PageModel
{
    public IActionResult OnGet() => RedirectToPagePermanent("/Organizer/SessionFeedback");

    /// <summary>Old: <c>?handler=Image&amp;sessionId=</c> → the merged page's <c>Qr</c> handler.</summary>
    public IActionResult OnGetImage(int sessionId) =>
        RedirectToPagePermanent("/Organizer/SessionFeedback", "Qr", new { sessionId });

    /// <summary>Old: <c>?handler=DownloadAll</c> → the merged page's <c>DownloadAllQr</c>.</summary>
    public IActionResult OnGetDownloadAll() =>
        RedirectToPagePermanent("/Organizer/SessionFeedback", "DownloadAllQr");
}
