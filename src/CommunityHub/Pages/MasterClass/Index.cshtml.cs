using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.MasterClass;

/// <summary>
/// PUBLIC (no-auth) master-class logistics page (REQUIREMENTS § 6c). Anyone with
/// the link reads the published logistics + setup instructions for a master
/// class (e.g. "bring your laptop charged"). The clean URL is
/// <c>/MasterClass/{slug}</c> where the slug is an unguessable per-session value
/// (so the numeric id is never exposed).
///
/// <b>Edit scope:</b> only an <b>involved speaker of that session OR an
/// organizer</b> may edit. A signed-in eligible participant sees an inline edit
/// form (a "show public link" affordance lives on the organizer + speaker
/// session views); the edit POST re-checks the scope server-side via
/// <see cref="MasterClassLogisticsService.CanEditAsync"/>, so there is no
/// anonymous write path to abuse — spam-resistant by construction.
///
/// Mobile-first (~360px) + a11y (semantic headings, labelled textarea).
/// </summary>
[AllowAnonymous]
public class IndexModel : PageModel
{
    private readonly MasterClassLogisticsService _logistics;
    private readonly ICurrentParticipantAccessor _participant;

    public IndexModel(
        MasterClassLogisticsService logistics,
        ICurrentParticipantAccessor participant)
    {
        _logistics = logistics;
        _participant = participant;
    }

    public string Slug { get; private set; } = string.Empty;
    public MasterClassLogisticsView? View { get; private set; }

    /// <summary>True when the signed-in viewer may edit (involved speaker / organizer).</summary>
    public bool CanEdit { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    [BindProperty] public string? EditLogisticsText { get; set; }

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        Slug = slug ?? string.Empty;
        View = await _logistics.GetPublicViewAsync(Slug, ct);
        // Unknown/expired slug: render the friendly "Master class not found" page
        // (the view handles View == null) rather than a raw 404. A public link that
        // no longer resolves should explain itself, never show the platform 404.
        if (View is null) return Page();

        await ResolveEditAsync(ct);
        EditLogisticsText = View.LogisticsText;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string slug, CancellationToken ct)
    {
        Slug = slug ?? string.Empty;
        View = await _logistics.GetPublicViewAsync(Slug, ct);
        if (View is null) return NotFound();

        var me = _participant.Current;
        if (me is null)
        {
            Error = "You must be signed in as an involved speaker or organizer to edit.";
            await ResolveEditAsync(ct);
            return Page();
        }

        try
        {
            await _logistics.UpdateLogisticsAsync(
                me.EventId, View.SessionId, me.ParticipantId, me.Role,
                me.Email, EditLogisticsText, ct);
            Message = "Logistics saved.";
        }
        catch (UnauthorizedAccessException ex)
        {
            Error = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }

        // Re-read so the public view reflects the save.
        View = await _logistics.GetPublicViewAsync(Slug, ct);
        await ResolveEditAsync(ct);
        EditLogisticsText = View?.LogisticsText;
        return Page();
    }

    private async Task ResolveEditAsync(CancellationToken ct)
    {
        CanEdit = false;
        var me = _participant.Current;
        if (me is null || View is null) return;
        CanEdit = await _logistics.CanEditAsync(
            me.EventId, View.SessionId, me.ParticipantId, me.Role, ct);
    }
}
