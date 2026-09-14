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
    private readonly ILinkedInPostPublisher _publisher;

    public SoMeSettingsModel(
        ICurrentParticipantAccessor participant, SoMeSettingsService settings,
        Core.Data.CommunityHubDbContext db,
        // §1197 — so the page can ask whether posting is really live rather than assert it.
        ILinkedInPostPublisher publisher)
    {
        _participant = participant;
        _settings = settings;
        _db = db;
        _publisher = publisher;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    /// <summary>
    /// 🔴 §1197 — ASK THE PUBLISHER, do not assert. Operator 2026-09-12: <i>"this is also wrong"</i>.
    /// </summary>
    /// <remarks>
    /// <para>This was <c>=> false</c>, with a comment saying "currently never — gated seam". True on
    /// the day it was written and false ever since: <c>LiveLinkedInPostPublisher</c> is registered
    /// whenever LinkedIn is enabled, and posts have been publishing to the company page for weeks.
    /// So the page told him the queue was inert while it was publishing — the worst direction for a
    /// safety banner to be wrong in, because it invites exactly the "nothing can go out" assumption.</para>
    ///
    /// <para>🔑 The publisher already answers this question (<c>CanPublish</c>). A page that keeps its
    /// own copy of a fact the system owns will drift from it, and this one drifted silently because
    /// nothing could fail: a hardcoded literal has no test that can catch it being out of date.</para>
    /// </remarks>
    public bool PublisherWired => _publisher.CanPublish;

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

    /// <summary>
    /// §1202 — whether auto-approval is actually DOING anything, stated on the page.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"i dont belive we use this anymore"</i> — about a control that
    /// was ticked, running every ten minutes, and had approved the very posts he had complained about
    /// that morning. Then, an hour later: <i>"1 hr ago i raised this as a bug, why is it still there.
    /// do we use it ?"</i></para>
    ///
    /// <para>🔑 <b>He could not tell from the page, and he was right not to trust it.</b> A checkbox
    /// says what is CONFIGURED, never what is HAPPENING — answering "do we use this" needed the job
    /// logs, which is not a question an operator should have to take to App Insights. So the section
    /// states its own last run and what it did.</para>
    /// </remarks>
    public DateTimeOffset? AutoApproveLastRunAt { get; private set; }

    /// <summary>How many queued posts the gate is currently refusing to approve.</summary>
    public int AutoApproveBlocked { get; private set; }

    /// <summary>How many are eligible right now — approvable on the next tick.</summary>
    public int AutoApproveEligible { get; private set; }

    /// <summary>
    /// 🔴 §1204 — WHY they are held back, grouped, with counts.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"what are these 19 hold back ?"</i> — a question the system
    /// could not answer. §918 logs <i>"19 waiting on a missing dependency"</i>, which is a COUNT of
    /// a problem rather than the problem, and §854 settled that argument long ago: he has to chase
    /// these people, and "19 posts" is not chaseable.</para>
    ///
    /// <para>🔑 The reasons already exist — <c>SoMeApprovalGate</c> composes a sentence per post
    /// naming exactly what is missing and who owes it. Nothing was collecting them anywhere he
    /// looks. Grouped, because twelve posts blocked on one sponsor's logo is ONE thing to do.</para>
    /// </remarks>
    public IReadOnlyList<(string Reason, int Count)> AutoApproveBlockers { get; private set; } =
        Array.Empty<(string, int)>();

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

    /// <summary>§1179 — the floor for sponsor and tier posts.</summary>
    [BindProperty] public DateOnly? SponsorAnnouncementFrom { get; set; }

    /// <summary>§1181 — the two sponsor-TIER rounds. Windows, not floors.</summary>
    [BindProperty] public DateOnly? SponsorCategoryRound1From { get; set; }
    [BindProperty] public DateOnly? SponsorCategoryRound2From { get; set; }

    /// <summary>§1181 — track round 2, and the Type 5 window's closing day.</summary>
    [BindProperty] public DateOnly? SpeakerTracksRound2From { get; set; }
    [BindProperty] public DateOnly? EventPostWindowEndsOn { get; set; }

    /// <summary>§1184 — sponsor round 2.</summary>
    [BindProperty] public DateOnly? SponsorRound2From { get; set; }

    /// <summary>§1185 — the third track round.</summary>
    [BindProperty] public DateOnly? SpeakerTracksRound3From { get; set; }

    /// <summary>§1186 — the floor for keynotes, technical sessions and panels.</summary>
    [BindProperty] public DateOnly? SessionAnnouncementFrom { get; set; }

    /// <summary>
    /// §1185 — how many rounds the TRACK cadence is actually set to, so the page can say when a
    /// round-3 date would govern nothing. A setting that silently does nothing is the §1178 defect.
    /// </summary>
    public int TrackRoundsConfigured { get; private set; } = 3;

    /// <summary>
    /// §925.2 — the day the Call for Speakers closes; the floor under the track settle signal.
    /// </summary>
    /// <remarks>
    /// 🔴 Without it a track that is merely EMPTY reads as FINISHED (§925.1): every track held only
    /// its June master classes, so "nothing new for six weeks" scored as a settled line-up while the
    /// CfS was still open.
    /// </remarks>
    [BindProperty] public DateOnly? CallForSpeakersClosesOn { get; set; }

    /// <summary>
    /// The master classes this window governs — shown for §927's reason: a rule whose effect you
    /// cannot see is a rule you have to test in production.
    /// </summary>
    public IReadOnlyList<string> MasterClassTitles { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// §1195 — one row per announcement category, as the page renders and posts it.
    /// </summary>
    /// <param name="Category">Which category this governs.</param>
    /// <param name="Label">"Type 2a — master classes", his numbering.</param>
    /// <param name="Enabled">False stops the category without deleting anything it has produced.</param>
    /// <param name="Rounds">How many times each subject is announced. "Add a round" raises this.</param>
    /// <param name="EndsOn">The last day it may be announced. Null = the event.</param>
    /// <param name="RoundStarts">
    /// The day each round opens, indexed from 0 for round 1. A null entry hands that round back to
    /// the spread — it is not "today".
    /// </param>
    public record CategoryRuleInput(
        SoMeAnnouncementCategory Category,
        string Label,
        bool Enabled,
        int Rounds,
        DateOnly? EndsOn,
        List<DateOnly?> RoundStarts)
    {
        public CategoryRuleInput() : this(default, "", true, 1, null, []) { }
    }

    /// <summary>
    /// §1195 — every category's rule. Bound as a LIST so the form posts them all in one save.
    /// </summary>
    /// <remarks>
    /// 🔑 One save for the whole page, not one per category: the rules are read together by the
    /// planner, and saving them one at a time would let the page show a half-applied campaign.
    /// </remarks>
    [BindProperty]
    public List<CategoryRuleInput> Rules { get; set; } = [];

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
        CallForSpeakersClosesOn = s.CallForSpeakersClosesOn;
        SponsorAnnouncementFrom = s.SponsorAnnouncementFrom;   // §1179
        SponsorCategoryRound1From = s.SponsorCategoryRound1From;   // §1181
        SponsorCategoryRound2From = s.SponsorCategoryRound2From;
        SpeakerTracksRound2From = s.SpeakerTracksRound2From;
        EventPostWindowEndsOn = s.EventPostWindowEndsOn;
        SponsorRound2From = s.SponsorRound2From;   // §1184
        SpeakerTracksRound3From = s.SpeakerTracksRound3From;   // §1185
        SessionAnnouncementFrom = s.SessionAnnouncementFrom;   // §1186
        await LoadExcludedTitlesAsync(me.EventId, ct);
        await LoadMasterClassTitlesAsync(me.EventId, ct);
        await LoadTrackRoundsAsync(me.EventId, ct);   // §1185
        await LoadRulesAsync(me.EventId, ct);         // §1195
        await LoadAutoApproveStateAsync(me.EventId, ct);   // §1202
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
            callForSpeakersClosesOn: CallForSpeakersClosesOn,
            sponsorAnnouncementFrom: SponsorAnnouncementFrom,   // §1179
            sponsorCategoryRound1From: SponsorCategoryRound1From,   // §1181
            sponsorCategoryRound2From: SponsorCategoryRound2From,
            speakerTracksRound2From: SpeakerTracksRound2From,
            eventPostWindowEndsOn: EventPostWindowEndsOn,
            sponsorRound2From: SponsorRound2From,   // §1184
            speakerTracksRound3From: SpeakerTracksRound3From,   // §1185
            sessionAnnouncementFrom: SessionAnnouncementFrom,   // §1186
            updateAnnouncementWindows: true);

        // 🔴 §1195 — the category rules, saved with everything else. Operator 2026-09-12:
        // *"basically we define the rules like start date, end date, cadence inside the some
        // settings and the some planner must recalculate if they are changed"*.
        // 🔑 One save for the whole page: the planner reads these together, so saving them
        // category-by-category would let a run see a half-applied campaign.
        var ruleSvc = new SoMeCategoryRules(_db);
        foreach (var input in Rules)
        {
            var days = new Dictionary<int, DateOnly?>();
            for (var i = 0; i < input.RoundStarts.Count; i++) days[i + 1] = input.RoundStarts[i];

            await ruleSvc.SaveAsync(
                me.EventId, input.Category, input.Enabled, input.Rounds, input.EndsOn,
                days, me.Email, ct);
        }

        await LoadExcludedTitlesAsync(me.EventId, ct);
        await LoadMasterClassTitlesAsync(me.EventId, ct);
        await LoadTrackRoundsAsync(me.EventId, ct);   // §1185
        await LoadRulesAsync(me.EventId, ct);         // §1195
        await LoadAutoApproveStateAsync(me.EventId, ct);   // §1202

        Message = PostCopyIncomplete
            ? "Saved — but the post footer is incomplete, so automatic posts will go out without "
              + "part of it. Fill in the link, the hashtags and the organizer credit."
            : $"Saved SoMe settings. {ActionPhraseCount} action phrase(s) in the catalog.";
        return Page();
    }

    /// <summary>§927 — the session titles his patterns match today.</summary>
    /// <summary>
    /// §1185 — how many rounds the TRACK cadence actually allows, read the same way the planner
    /// reads it: a SAVED row wins, the catalog default only fills in for an edition that has none.
    /// </summary>
    /// <summary>
    /// §1195 — every category rule, with a date slot per round so the form can render them.
    /// </summary>
    /// <summary>
    /// §1202 — what auto-approval is actually doing: when it last ran, and what it is holding.
    /// </summary>
    /// <remarks>
    /// 🔑 Counted through the SAME gate the job uses (`SoMeApprovalGate`), so the page cannot claim a
    /// post is approvable while the job refuses it — the §1178 disagreement, in a new place.
    /// ⚠️ Bounded: only posts that are QUEUED and not yet approved can be candidates, which is a few
    /// dozen rows, and the gate is asked once per row exactly as the job asks it.
    /// </remarks>
    private async Task LoadAutoApproveStateAsync(int eventId, CancellationToken ct)
    {
        AutoApproveLastRunAt = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(
                _db.JobRunStates.Where(j => j.FunctionName == "SoMeAutoApproveJob")
                    .Select(j => j.LastRunAt), ct);

        var candidates = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(
                _db.SoMePosts.Where(p => p.EventId == eventId && !p.IsDeleted && !p.IsActive
                                         && p.Status == SoMePostStatus.Queued
                                         && p.TemplateKind != null), ct);

        // 🔑 §1206 — through the SHARED readiness rule, so this page, the queue and the calendar
        // cannot disagree about which posts are held back or why.
        var held = await new SoMeReadiness(_db).HeldReasonsAsync(candidates, ct);

        AutoApproveBlocked = held.Count;
        AutoApproveEligible = candidates.Count - held.Count;
        var reasons = held.Values.ToList();

        // 🔴 §1204 — GROUPED, because twelve posts blocked on one sponsor's logo is ONE thing to do.
        // ⚠️ Grouped on the reason's opening clause, not the whole sentence: the gate's wording names
        // the subject too ("Surveil has not delivered …"), so whole-sentence grouping would produce
        // one row per post and reproduce the count it replaces.
        AutoApproveBlockers = reasons
            .GroupBy(SoMeReadiness.Short)
            .Select(g => (Reason: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();
    }

    // §1206 — `Shorten` moved to `SoMeReadiness.Short`: the queue and the calendar need the same
    // wording, and a second copy is how two screens start describing one condition differently.

    private async Task LoadRulesAsync(int eventId, CancellationToken ct)
    {
        var svc = new SoMeCategoryRules(_db);
        var rules = await svc.GetAllAsync(eventId, ct);
        var starts = await svc.RoundStartsAsync(eventId, ct);

        Rules = SoMeCategoryRules.All.Select(c =>
        {
            var rule = rules[c];
            var named = starts.TryGetValue(c, out var n) ? n : null;

            // 🔴 §1207 — the number the PLANNER will use, not the number in the column. A round he
            // has dated counts; showing the lower saved value would have the page disagree with the
            // plan about how many rounds this category has, which is how the bug stayed invisible.
            var rounds = Math.Max(1, SoMeCategoryRules.EffectiveRounds(rule, named));

            // ⚠️ One slot per round, ALWAYS — a round with no date still needs its box, or there
            // is no way to give it one.
            var days = Enumerable.Range(1, rounds)
                .Select(r => named is not null && named.TryGetValue(r, out var d) ? (DateOnly?)d : null)
                .ToList();

            return new CategoryRuleInput(
                c, SoMeCategoryRules.Label(c), rule.Enabled, rounds, rule.EndsOn, days);
        }).ToList();
    }

    private async Task LoadTrackRoundsAsync(int eventId, CancellationToken ct)
    {
        var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(
                _db.SoMeCadenceSettings.Where(c => c.EventId == eventId
                                                   && c.Kind == SoMeTemplateKind.SpeakerTracks), ct);

        TrackRoundsConfigured = row is null
            ? SoMeCadenceService.DefaultOccurrences(SoMeTemplateKind.SpeakerTracks)
            : (row.Enabled ? row.Occurrences : 0);
    }

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
    /// <remarks>
    /// 🔴 §1178 — this filtered on <c>IsTestData</c>, which nothing has ever written, so the list read
    /// back the TEST master class as one of the sessions about to be announced — on the very page
    /// where he sets the date that announces them. The shared scope answers it the same way the
    /// planner and the graphics sweep do.
    /// </remarks>
    private async Task LoadMasterClassTitlesAsync(int eventId, CancellationToken ct)
    {
        var excluded = await new CommunityHub.Core.Integrations.SoMeSubjectScope(_db)
            .ExcludedSessionIdsAsync(eventId, ct);

        MasterClassTitles = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(
                _db.Sessions
                    .Where(x => x.EventId == eventId
                                && x.Type == CommunityHub.Core.Domain.SessionType.MasterClass
                                && !x.IsServiceSession
                                && !excluded.Contains(x.Id))
                    .OrderBy(x => x.Title)
                    .Select(x => x.Title),
                ct);
    }
}
