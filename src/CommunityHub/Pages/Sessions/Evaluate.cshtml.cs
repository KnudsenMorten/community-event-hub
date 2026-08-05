using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sessions;

/// <summary>
/// 🔴 <b>RETIRED §748 — this page no longer collects anything.</b> It now redirects to
/// <c>/f/{token}</c>, the four-point QR feedback page.
/// </summary>
/// <remarks>
/// <para>This used to be the HappyOrNot-style <b>1–5 smiley</b> rating writing a
/// <c>SessionEvaluation</c> row. Operator 2026-07-31 (§743.9): *"this replaces old evaluation
/// specs"* — §743's four-point forced-choice scale supersedes it, and the two must not both be able
/// to collect. A 1–5 rating and a 1–4 forced choice are **different instruments**: they cannot be
/// pooled, averaged together, or compared, so a single row written here after the cut-over would
/// corrupt a figure nobody would think to question.</para>
///
/// <para>🔑 <b>The redirect is what makes the retirement safe, and it is why the write path is gone
/// rather than merely unlinked.</b> Removing the link would have left the handler reachable by
/// anyone holding an old URL. The class stays so the route keeps resolving — nothing is printed with
/// it (verified: 0 of 13 sessions carried a token at cut-over), but a link may have been shared, and
/// a redirect is kinder than a 404 to someone trying to give feedback.</para>
///
/// <para>The remaining 1–5 code — the entity, the aggregation service and the organizer dashboard —
/// is dead but still compiled; see §748.1. It is safe to leave because **nothing can write to it any
/// more**, which is the property that actually mattered.</para>
/// </remarks>
[AllowAnonymous]
public class EvaluateModel : PageModel
{
    /// <summary>
    /// 🔒 <b>302, not 301.</b> A permanent redirect is cached by browsers and intermediaries
    /// indefinitely — if this route ever needs to mean something else, a 301 would be undoable on
    /// devices we cannot reach. The cost of a temporary redirect here is one round trip.
    /// </summary>
    public IActionResult OnGet(string token) => RedirectToPage("/Feedback", new { token });

    /// <summary>An old form posting here must not silently succeed — send it to the live page.</summary>
    public IActionResult OnPost(string token) => RedirectToPage("/Feedback", new { token });
}
