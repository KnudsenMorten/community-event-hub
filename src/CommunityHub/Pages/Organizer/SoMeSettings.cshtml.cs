using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer settings for the LinkedIn company-page SoMe scheduling queue
/// (REQUIREMENTS §19). Configure: enable/disable posting, the LinkedIn company
/// page URL / organization id (operator config — placeholder only in committed
/// files), the T-5-minute speaker pre-alert organizer, and the publish
/// notification array + on/off toggle.
///
/// The LinkedIn OAuth access token is a SECRET and is NOT entered here — it lives
/// in Key Vault (secret name <c>linkedin-some-access-token</c>) and is read by the
/// live publisher only. Organizer-only, mobile-first, a11y.
/// </summary>
[Authorize]
public class SoMeSettingsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SoMeSettingsService _settings;

    public SoMeSettingsModel(
        ICurrentParticipantAccessor participant, SoMeSettingsService settings)
    {
        _participant = participant;
        _settings = settings;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    /// <summary>True once a live LinkedIn publisher + token are wired (currently never — gated seam).</summary>
    public bool PublisherWired => false;

    [BindProperty] public bool Enabled { get; set; }
    [BindProperty] public string? CompanyPageUrlOrOrgId { get; set; }
    [BindProperty] public string? SpeakerPreAlertOrganizerEmail { get; set; }
    [BindProperty] public string? NotificationEmails { get; set; }
    [BindProperty] public bool NotifyOnPublish { get; set; } = true;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var s = await _settings.GetOrDefaultAsync(me.EventId, ct);
        Enabled = s.Enabled;
        CompanyPageUrlOrOrgId = s.CompanyPageUrlOrOrgId;
        SpeakerPreAlertOrganizerEmail = s.SpeakerPreAlertOrganizerEmail;
        NotificationEmails = s.NotificationEmails;
        NotifyOnPublish = s.NotifyOnPublish;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        await _settings.SaveAsync(
            me.EventId, Enabled, CompanyPageUrlOrOrgId, SpeakerPreAlertOrganizerEmail,
            NotificationEmails, NotifyOnPublish, me.Email, ct);
        Message = "Saved SoMe settings.";
        return Page();
    }
}
