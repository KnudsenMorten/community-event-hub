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
    private readonly Core.Data.CommunityHubDbContext _db;

    public SoMeSettingsModel(
        ICurrentParticipantAccessor participant, SoMeSettingsService settings,
        Core.Data.CommunityHubDbContext db)
    {
        _participant = participant;
        _settings = settings;
        _db = db;
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

    /// <summary>
    /// §885 <c>{Action_catalog_random}</c> — the call-to-action phrases, one per line. Each post
    /// draws one when it is created and keeps it, so editing this changes FUTURE posts only.
    /// </summary>
    [BindProperty] public string? ActionCatalog { get; set; }

    /// <summary>How many usable phrases the catalog holds — shown so a typo is visible immediately.</summary>
    public int ActionPhraseCount => CommunityHub.Core.Integrations.SoMeActionCatalog
        .Parse(ActionCatalog).Count;

    /// <summary>§888.2 <c>{EventVenueCityCountry}</c> — e.g. "Copenhagen, Denmark".</summary>
    [BindProperty] public string? EventVenueCityCountry { get; set; }

    /// <summary>§918 — approve template-built posts automatically once they are far enough out.</summary>
    [BindProperty] public bool AutoApproveEnabled { get; set; }

    /// <summary>§918 — how many days ahead a post must be before it may auto-approve.</summary>
    [BindProperty] public int AutoApproveLeadDays { get; set; } = 7;

    /// <summary>§927 — session titles never to announce. One pattern per line, <c>*</c> wildcard.</summary>
    [BindProperty] public string? ExcludedSessionTitlePatterns { get; set; }

    /// <summary>The sessions his patterns match right now — shown so a filter is never guesswork.</summary>
    /// <remarks>
    /// 🔑 A pattern is only safe if he can SEE what it removes. "ask the experts*" reads harmless
    /// until it silently takes a talk he wanted; listing the matches turns the rule into something
    /// checkable on the page where it is written, rather than after a post fails to appear.
    /// </remarks>
    public IReadOnlyList<string> ExcludedSessionTitles { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// §851 — the earliest date a SPEAKER-derived post (tracks, sessions) may be scheduled.
    /// </summary>
    /// <remarks>
    /// 🔴 §925.1 — <b>load-bearing, and shown here for exactly that reason.</b> This date is the only
    /// thing keeping the eight track posts from going out today naming one or two speakers out of an
    /// expected hundred: the settle signal reads every track as finished because their master classes
    /// arrived in June and the Call for Speakers has not closed yet. It was invisible until now,
    /// which made it a value nobody could check and nobody could protect. Clearing it publishes the
    /// speaker campaign immediately.
    /// </remarks>
    [BindProperty] public DateOnly? SpeakerAnnouncementFrom { get; set; }

    /// <summary>§928 — the day the master-class announcements start, all together.</summary>
    [BindProperty] public DateOnly? MasterClassAnnouncementFrom { get; set; }

    /// <summary>
    /// The master classes this window governs — shown for §927's reason: a rule whose effect you
    /// cannot see is a rule you have to test in production.
    /// </summary>
    public IReadOnlyList<string> MasterClassTitles { get; private set; } = Array.Empty<string>();

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
        ActionCatalog = s.ActionCatalog;
        EventVenueCityCountry = s.EventVenueCityCountry;
        AutoApproveEnabled = s.AutoApproveEnabled;
        AutoApproveLeadDays = s.AutoApproveLeadDays;
        ExcludedSessionTitlePatterns = s.ExcludedSessionTitlePatterns;
        SpeakerAnnouncementFrom = s.SpeakerAnnouncementFrom;
        MasterClassAnnouncementFrom = s.MasterClassAnnouncementFrom;
        await LoadExcludedTitlesAsync(me.EventId, ct);
        await LoadMasterClassTitlesAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // §885 — refuse a phrase that would publish as visible markup, and NAME it. §326k escapes
        // these characters, so the post would go out with backslashes through it. Catching this on
        // save is the whole difference between a typo and a bad post to 2,000 followers.
        var badPhrases = CommunityHub.Core.Integrations.SoMeActionCatalog.Invalid(ActionCatalog);
        if (badPhrases.Count > 0)
        {
            Message = "NOT saved — these action phrases contain a character LinkedIn treats as "
                    + "formatting, so they would publish with backslashes through them. Remove it "
                    + "and save again: "
                    + string.Join(" · ", badPhrases.Select(b => $"\"{b.Phrase}\" (the '{b.Character}')"));
            return Page();
        }

        await _settings.SaveAsync(
            me.EventId, Enabled, CompanyPageUrlOrOrgId, SpeakerPreAlertOrganizerEmail,
            NotificationEmails, NotifyOnPublish, me.Email, ct,
            // §824.19 — this page owns the post copy, so it says so explicitly. A blank field here
            // MEANS blank (it is how he removes the tag block); the flag is what keeps other callers
            // of SaveAsync from wiping it by omission.
            EventSystemUrl, EventTags, OrganizerCredits, ActionCatalog, EventVenueCityCountry,
            // §927 — the exclusion list is post copy in the same sense: this page owns it.
            ExcludedSessionTitlePatterns,
            updatePostCopy: true,
            // §918 — this page owns the auto-approval knobs too, and says so explicitly.
            autoApproveEnabled: AutoApproveEnabled,
            autoApproveLeadDays: AutoApproveLeadDays,
            // §928 — this page owns the two announcement windows, and says so, because for these
            // two a BLANK is a real instruction ("no window") rather than "leave it alone".
            speakerAnnouncementFrom: SpeakerAnnouncementFrom,
            masterClassAnnouncementFrom: MasterClassAnnouncementFrom,
            updateAnnouncementWindows: true);

        await LoadExcludedTitlesAsync(me.EventId, ct);
        await LoadMasterClassTitlesAsync(me.EventId, ct);

        Message = PostCopyIncomplete
            ? "Saved — but the post footer is incomplete, so automatic posts will go out without "
              + "part of it. Fill in the link, the hashtags and the organizer credit."
            : $"Saved SoMe settings. {ActionPhraseCount} action phrase(s) in the catalog.";
        return Page();
    }

    /// <summary>§927 — the session titles his patterns match today.</summary>
    private async Task LoadExcludedTitlesAsync(int eventId, CancellationToken ct)
    {
        var patterns = SoMeTitleExclusions.Parse(ExcludedSessionTitlePatterns);
        if (patterns.Count == 0) { ExcludedSessionTitles = Array.Empty<string>(); return; }

        var titles = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            _db.Sessions.Where(x => x.EventId == eventId).Select(x => x.Title), ct);

        ExcludedSessionTitles = titles
            .Where(t => SoMeTitleExclusions.IsExcluded(t, patterns))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>§928 — the master classes the window moves, listed so the count is never a guess.</summary>
    private async Task LoadMasterClassTitlesAsync(int eventId, CancellationToken ct)
    {
        MasterClassTitles = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(
                _db.Sessions
                    .Where(x => x.EventId == eventId
                                && x.Type == CommunityHub.Core.Domain.SessionType.MasterClass
                                && !x.IsServiceSession
                                && !x.IsTestData)
                    .OrderBy(x => x.Title)
                    .Select(x => x.Title),
                ct);
    }
}
