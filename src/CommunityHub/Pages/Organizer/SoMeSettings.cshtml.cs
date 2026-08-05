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

    // --- §824.19: the three values every post template ends with ---------------------------
    // On THIS page rather than a new one: it is already "how this edition posts to LinkedIn",
    // and a second settings page for three fields is one more place to have to look.

    /// <summary>§824.3 <c>{EventSystemUrl}</c> — the link every post points at.</summary>
    [BindProperty] public string? EventSystemUrl { get; set; }

    /// <summary>§824.3 <c>{EventTags}</c> — the standing hashtag block.</summary>
    [BindProperty] public string? EventTags { get; set; }

    /// <summary>§824.3 <c>{OrganizerLinkedInUrls}</c> — the organizer credit line.</summary>
    [BindProperty] public string? OrganizerCredits { get; set; }

    /// <summary>True while any of the three is blank — the post footer would be missing.</summary>
    /// <remarks>
    /// Surfaced deliberately, because the templates handle a blank CORRECTLY (the gap closes,
    /// §824.15) and that is precisely what makes it easy to miss: the post looks fine, just quietly
    /// without its hashtags or its organizer credit. Silent-but-wrong output is the failure mode this
    /// codebase keeps removing.
    /// </remarks>
    public bool PostCopyIncomplete =>
        string.IsNullOrWhiteSpace(EventSystemUrl)
        || string.IsNullOrWhiteSpace(EventTags)
        || string.IsNullOrWhiteSpace(OrganizerCredits);

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
        EventSystemUrl = s.EventSystemUrl;
        EventTags = s.EventTags;
        OrganizerCredits = s.OrganizerCredits;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        await _settings.SaveAsync(
            me.EventId, Enabled, CompanyPageUrlOrOrgId, SpeakerPreAlertOrganizerEmail,
            NotificationEmails, NotifyOnPublish, me.Email, ct,
            // §824.19 — this page owns the post copy, so it says so explicitly. A blank field here
            // MEANS blank (it is how he removes the tag block); the flag is what keeps other callers
            // of SaveAsync from wiping it by omission.
            EventSystemUrl, EventTags, OrganizerCredits, updatePostCopy: true);

        Message = PostCopyIncomplete
            ? "Saved — but the post footer is incomplete, so automatic posts will go out without "
              + "part of it. Fill in the link, the hashtags and the organizer credit."
            : "Saved SoMe settings.";
        return Page();
    }
}
