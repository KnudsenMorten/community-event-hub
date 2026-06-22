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

    public MasterClassPrepModel(
        MasterClassPrepService prep, ICurrentParticipantAccessor participant)
    {
        _prep = prep;
        _participant = participant;
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

        try
        {
            await _prep.UpdatePrepAsync(
                me.EventId, sessionId, me.ParticipantId, me.Role, PrepContent, ct);
            Message = "Preparation notes saved.";
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
