using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// Speaker editor for a Master Class's PREP content (FEATURE 2):
/// <c>/Speaker/MasterClassPrep/{sessionId}</c>. A speaker LINKED to the master-class
/// session (or an organizer) edits the "how to prepare" text shown on the attendee
/// landing page (what to expect, bring a laptop, set up in advance). The edit POST
/// re-checks the link server-side via <see cref="MasterClassPrepService.CanEditAsync"/>,
/// so a non-linked speaker cannot write. Mobile-first + a11y.
/// </summary>
[Authorize]
public class MasterClassPrepModel : PageModel
{
    private readonly MasterClassPrepService _prep;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly MasterClassNotificationService _notify;

    public MasterClassPrepModel(
        MasterClassPrepService prep,
        ICurrentParticipantAccessor participant,
        MasterClassNotificationService notify)
    {
        _prep = prep;
        _participant = participant;
        _notify = notify;
    }

    public int SessionId { get; private set; }
    public bool AccessDenied { get; private set; }
    public bool NotFoundState { get; private set; }
    public MasterClassPrepService.LandingView? View { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    /// <summary>The attendee landing page link for this master class (preview).</summary>
    public string? LandingLink { get; private set; }

    [BindProperty] public string? PrepContent { get; set; }

    public async Task<IActionResult> OnGetAsync(int sessionId, CancellationToken ct)
    {
        SessionId = sessionId;
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        View = await _prep.GetLandingAsync(me.EventId, sessionId, ct);
        if (View is null) { NotFoundState = true; return Page(); }

        if (!await _prep.CanEditAsync(me.EventId, sessionId, me.ParticipantId, me.Role, ct))
        {
            AccessDenied = true;
            return Page();
        }

        PrepContent = View.PrepContent;
        LandingLink = Url.Page("/MasterClassPage", null, new { sessionId }, Request.Scheme);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int sessionId, CancellationToken ct)
    {
        SessionId = sessionId;
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        View = await _prep.GetLandingAsync(me.EventId, sessionId, ct);
        if (View is null) { NotFoundState = true; return Page(); }

        // §383: only notify when the text ACTUALLY CHANGED. A speaker who opens the editor and
        // saves without editing — or saves twice — must not mail the whole class again. Compared
        // before the write, because afterwards there is nothing left to compare against.
        var before = View.PrepContent ?? string.Empty;
        var changed = !string.Equals(before, PrepContent ?? string.Empty, StringComparison.Ordinal);

        try
        {
            await _prep.UpdatePrepAsync(
                me.EventId, sessionId, me.ParticipantId, me.Role, PrepContent, ct);
            Message = "Preparation notes saved.";

            if (changed)
            {
                // After the save, and swallowed: a mail failure must never make it look as though
                // the notes were not saved. They were.
                try
                {
                    await _notify.NotifyInstructionsUpdatedAsync(
                        me.EventId, sessionId, me.FullName ?? "A speaker",
                        $"{Request.Scheme}://{Request.Host}",
                        actingParticipantId: me.ParticipantId, ct);
                }
                catch { /* the edit stands even if the notification fails */ }
            }
        }
        catch (MasterClassPrepAccessDeniedException ex) { AccessDenied = true; Error = ex.Message; }
        catch (MasterClassPrepValidationException ex) { Error = ex.Message; }

        // Re-read so the form reflects the saved value.
        View = await _prep.GetLandingAsync(me.EventId, sessionId, ct);
        PrepContent = View?.PrepContent;
        LandingLink = Url.Page("/MasterClassPage", null, new { sessionId }, Request.Scheme);
        return Page();
    }
}
