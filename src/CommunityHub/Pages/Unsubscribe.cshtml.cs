using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

/// <summary>
/// §1080 stage 2 — THE WAY OUT: the page every campaign mail links to.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-12: <i>"ability for people to be excluded from ELDK27 Event Mails (tick
/// box) with link in mail"</i>. The legal basis for mailing the imported list is the
/// existing-customer relationship — and that basis <b>requires</b> this page to work.</para>
///
/// <para>🔴 <b>The GET does not unsubscribe anybody.</b> Mail clients, security scanners and link
/// previewers fetch every URL in a message; a one-click GET would silently unsubscribe people who
/// never clicked, and we would never know. The GET shows who it is about and a button; the POST is
/// the act.</para>
///
/// <para>🔒 <b>The link is signed</b> (HMAC over edition + address), so changing the address in the
/// URL invalidates it — nobody can unsubscribe somebody else by editing a query string. A bad
/// signature is a plain "this link is not valid" and never confirms whether the address exists.</para>
/// </remarks>
[AllowAnonymous]
public class UnsubscribeModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly MailSuppressionService _suppression;

    public UnsubscribeModel(CommunityHubDbContext db, MailSuppressionService suppression)
    {
        _db = db;
        _suppression = suppression;
    }

    public bool LinkIsValid { get; private set; }
    public bool AlreadyDone { get; private set; }
    public bool JustDone { get; private set; }
    public string? Email { get; private set; }
    public string? EventName { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? e, string? t, CancellationToken ct)
    {
        await LoadAsync(e, t, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? e, string? t, CancellationToken ct)
    {
        await LoadAsync(e, t, ct);
        if (!LinkIsValid) return Page();

        var eventId = await ActiveEventIdAsync(ct);
        if (eventId is null) return Page();

        await _suppression.SuppressAsync(
            eventId.Value, e, MailSuppressionReason.Unsubscribed, "Unsubscribed from a mail link", ct);

        JustDone = true;
        AlreadyDone = true;
        return Page();
    }

    private async Task LoadAsync(string? e, string? t, CancellationToken ct)
    {
        var eventId = await ActiveEventIdAsync(ct);
        if (eventId is null) return;

        EventName = await _db.Events.Where(x => x.Id == eventId)
            .Select(x => x.DisplayName).FirstOrDefaultAsync(ct);

        LinkIsValid = _suppression.VerifyToken(eventId.Value, e, t);
        if (!LinkIsValid) return;

        Email = MailSuppression.Normalise(e);
        AlreadyDone = await _suppression.IsSuppressedAsync(eventId.Value, e, ct);
    }

    private Task<int?> ActiveEventIdAsync(CancellationToken ct) =>
        _db.Events.Where(x => x.IsActive).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
}
