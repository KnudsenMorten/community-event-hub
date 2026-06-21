using CommunityHub.Auth;
using CommunityHub.Core.Config;
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
    private readonly EventEditionConfigLoader _eventConfigLoader;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        MasterClassLogisticsService logistics,
        ICurrentParticipantAccessor participant,
        EventEditionConfigLoader eventConfigLoader,
        EventConfigOptions eventConfigOptions,
        ILogger<IndexModel> logger)
    {
        _logistics = logistics;
        _participant = participant;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
        _logger = logger;
    }

    public string Slug { get; private set; } = string.Empty;
    public MasterClassLogisticsView? View { get; private set; }

    /// <summary>
    /// The edition timezone (IANA id, e.g. <c>Europe/Copenhagen</c>) so the
    /// "last updated" stamp reads in the venue's local time, not raw UTC
    /// (REQUIREMENTS §21). Blank ⇒ the view falls back to UTC honestly.
    /// </summary>
    public string? TimezoneId { get; private set; }

    /// <summary>The "last updated" stamp formatted in edition-local time, with its zone.</summary>
    public string? UpdatedLocal =>
        View?.UpdatedAt is { } at ? EventLocalTime.Format(at, TimezoneId) : null;

    /// <summary>True when the signed-in viewer may edit (involved speaker / organizer).</summary>
    public bool CanEdit { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    [BindProperty] public string? EditLogisticsText { get; set; }

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        Slug = slug ?? string.Empty;
        LoadTimezone();
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
        LoadTimezone();
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

    /// <summary>
    /// Read the edition timezone from config (best-effort; a broken/missing
    /// config must never 500 a public page — fall back to a blank id so the
    /// view shows UTC honestly).
    /// </summary>
    private void LoadTimezone()
    {
        try
        {
            TimezoneId = _eventConfigLoader
                .Load(_eventConfigOptions.EventConfigPath)
                .Dates?.Timezone;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MasterClass: failed to load edition timezone from {Path}",
                _eventConfigOptions.EventConfigPath);
            TimezoneId = null;
        }
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
