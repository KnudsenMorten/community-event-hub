using System.Reflection;
using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// The organizer Feature settings page (REQUIREMENTS §23) — the controlled-rollout
/// surface. It renders the feature catalog grouped into chapters; each advanced
/// feature gets an enable/disable toggle, and a disabled feature is shown DIMMED
/// with a small "Disabled" label (never hidden, for discoverability). It also
/// surfaces the first-class email controls: the global outbound-email kill switch,
/// plus the (read-only, infra-managed) allowlist + redirect for transparency.
///
/// Organizer-only (server-enforced), mobile-first (~360px), a11y, English.
/// State persists to the per-edition <see cref="FeatureSetting"/> store via
/// <see cref="FeatureSettingsService"/>; the same store the web + jobs gate on.
/// </summary>
[Authorize]
public class SettingsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly FeatureSettingsService _settings;
    private readonly FeatureGateService _gate;
    private readonly EmailOptions _email;
    private readonly IWebHostEnvironment _env;
    // §340-H: the ENVIRONMENT default, so the page can show what "inherit" resolves to.
    private readonly CommunityHub.Core.Integrations.ExternalWriteOptions _externalWrites;

    // §515 — the per-template rings listed under each role section.
    private readonly CommunityHub.Core.Email.EmailTemplateRingService _templateRings;

    // §703 — DEV/PROD, resolved the same way the §702 alert-mail tag is.
    private readonly CommunityHub.Core.Diagnostics.HubEnvironment _hubEnv;

    // §707.11 — how often each RECURRING mail repeats. Optional so existing constructions and
    // tests are unchanged; wired by DI at runtime.
    private readonly CommunityHub.Core.Email.EmailReminderCadenceService? _cadence;

    public SettingsModel(
        ICurrentParticipantAccessor participant,
        FeatureSettingsService settings,
        FeatureGateService gate,
        IOptions<EmailOptions> email,
        IWebHostEnvironment env,
        CommunityHub.Core.Integrations.ExternalWriteOptions externalWrites,
        CommunityHub.Core.Email.EmailTemplateRingService templateRings,
        CommunityHub.Core.Diagnostics.HubEnvironment hubEnv,
        CommunityHub.Core.Email.EmailReminderCadenceService? cadence = null)
    {
        _participant = participant;
        _settings = settings;
        _gate = gate;
        _email = email.Value;
        _env = env;
        _externalWrites = externalWrites;
        _templateRings = templateRings;
        _hubEnv = hubEnv;
        _cadence = cadence;
    }

    /// <summary>
    /// §707.11 — the repeat interval in force for each RECURRING mail, keyed by template.
    /// Empty when the cadence service is not wired (older constructions).
    /// </summary>
    public IReadOnlyDictionary<string, int?> CadenceByTemplate { get; private set; }
        = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// §881 — the per-ROLE repeat intervals, keyed by (template, role). A missing entry means that
    /// role has none of its own and follows <see cref="CadenceByTemplate"/>.
    /// </summary>
    public IReadOnlyDictionary<(string TemplateKey, ParticipantRole Role), int?> CadenceByRole
    { get; private set; } = new Dictionary<(string, ParticipantRole), int?>();

    /// <summary>
    /// §707.25 — show mails that are RETIRED IN CODE. Default FALSE (operator 2026-07-30: *"as this
    /// is retired in code, it should not be shown here — maybe add so i can tick on to show retired
    /// features in code"*). The hidden count is always stated, so the page never lies by omission.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "showRetired")]
    public bool ShowRetired { get; set; }

    /// <summary>How many mails the retired filter is hiding.</summary>
    public int RetiredHidden { get; private set; }

    /// <summary>
    /// §707.11 — set how often ONE recurring mail repeats (operator 2026-07-30: *"i can define the
    /// cadence for the emails that are recurring until fixed"*).
    /// </summary>
    /// <remarks>
    /// 🔒 Refused for a mail that is not in <c>EmailTemplateCatalog.RecurringMails</c>, so a cadence
    /// box can never exist for a mail whose cadence would govern nothing — the §326bx rule this page
    /// keeps being corrected for. The rule it writes is <c>lastSent + N days</c> (§707.10), and the
    /// completion condition (what stops the chasing) is shown beside it.
    /// </remarks>
    /// <param name="role">
    /// §881 — blank/absent sets the ALL-ROLES cadence (the filing home's box). A role sets THAT ROLE
    /// alone, which is what the box on a cross-listed section does: operator 2026-08-05, *"i need to
    /// define the cadence for reminders for get started pending for sponsor — like this one mentioned
    /// under speaker"*. Setting one role never moves another, exactly as with the per-role rings.
    /// </param>
    public async Task<IActionResult> OnPostMailCadenceAsync(
        string templateKey, int intervalDays, CancellationToken ct, ParticipantRole? role = null)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        if (_cadence is null) return RedirectToPage();

        // 0 (or less) is the operator saying "stop repeating" — stored as null = once, ever.
        int? interval = intervalDays > 0 ? intervalDays : null;
        var ok = await _cadence.SetIntervalAsync(
            me.EventId, templateKey, interval, me.Email, role, ct);
        Saved = ok;
        if (!ok)
        {
            Message = role is null
                ? $"'{templateKey}' is not a recurring mail — nothing changed."
                : $"'{templateKey}' does not reach {role} — nothing changed.";
        }
        return RedirectToPage();
    }

    /// <summary>
    /// §881 — drop a ROLE's own cadence so it follows the all-roles value again. The cadence twin of
    /// the ring picker's "— follow the all-roles ring —"; without it a per-role number could be set
    /// and never un-set, because 0 already means "send once, ever".
    /// </summary>
    public async Task<IActionResult> OnPostMailCadenceFollowAsync(
        string templateKey, ParticipantRole role, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        if (_cadence is null) return RedirectToPage();

        Saved = await _cadence.ClearIntervalAsync(me.EventId, templateKey, role, ct);
        return RedirectToPage();
    }

    /// <summary>
    /// §742 — set WHERE a feature's ops notice goes (operator 2026-07-31: *"i need to be able to
    /// control where it goes and state in settings page"*). Blank CLEARS it back to the built-in
    /// ops mailbox, which is why an empty submit is a valid action rather than a no-op.
    /// </summary>
    public async Task<IActionResult> OnPostNotificationRecipientAsync(
        string key, string? email, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var ok = await _settings.SetNotificationRecipientAsync(me.EventId, key, email, me.Email, ct);
        Saved = ok;
        if (!ok)
        {
            Message = $"'{key}' does not send a notification, so there is nowhere to send it.";
        }
        return RedirectToPage();
    }

    /// <summary>
    /// §515 — every e-mail template with its effective ring, grouped by the ROLE that receives it
    /// (operator 2026-07-28: "i still dont see the complete list of welcome emails and others on
    /// the feature settings page, so i cannot control it").
    /// </summary>
    public IReadOnlyList<CommunityHub.Core.Email.EmailTemplateRingState> Templates { get; private set; }
        = Array.Empty<CommunityHub.Core.Email.EmailTemplateRingState>();

    // ---- §566 the FOUR sections, in his agreed order -------------------------------------------
    // 1 Master ceiling · 2 E-mails by ROLE · 3 Participant features · 4 Backend & tools (no rings).
    // Computed here so the view stays a renderer: the view must never decide WHICH bucket a switch
    // belongs to, or the page and the engine could disagree again (§563/§564).

    /// <summary>§566 §1 — the ceiling. Everything below is MIN(this, its own ring).</summary>
    public FeatureState? OutboundEmail =>
        Groups.SelectMany(g => g).FirstOrDefault(f => f.Descriptor.Key == FeatureCatalog.OutboundEmailKey);

    /// <summary>
    /// §707.27 B — ONE appearance of a mail inside one role section.
    /// </summary>
    /// <param name="Template">The mail and its rings.</param>
    /// <param name="IsPrimary">
    /// True on the mail's FILING HOME (<c>AudienceFor</c>) — the single row that carries the
    /// all-roles ring control and the full per-role block. False on a cross-listing, which shows only
    /// that section's own role ring.
    /// </param>
    /// <param name="SectionRole">
    /// The role this section represents, when it maps to one. Null for the cross-role
    /// ("Any role — audience depends on the send") section.
    /// </param>
    public sealed record MailListing(
        CommunityHub.Core.Email.EmailTemplateRingState Template,
        bool IsPrimary,
        ParticipantRole? SectionRole);

    /// <summary>§707.27 B — one role section of the e-mail chapter.</summary>
    public sealed record MailSection(
        CommunityHub.Core.Email.EmailAudience Audience,
        IReadOnlyList<MailListing> Mails);

    /// <summary>
    /// §566 §2 — every mail, grouped by the ROLE that receives it. §707.27 B — a SHARED mail now
    /// appears under EVERY role it reaches, not only under its one filing home.
    /// </summary>
    /// <remarks>
    /// 🔒 Exactly one appearance per mail is PRIMARY (its <c>AudienceFor</c> home). Only that row
    /// renders the all-roles ring control; a cross-listed row renders that role's ring alone. Letting
    /// both render the all-roles control would put the same stored value behind two dropdowns in two
    /// sections — the trap §707.27 B calls out, and the same shape of defect as §515's invisible
    /// shared level.
    /// </remarks>
    public IReadOnlyList<MailSection> EmailsByRole => BuildSections(Templates);

    private static IReadOnlyList<MailSection> BuildSections(
        IReadOnlyList<CommunityHub.Core.Email.EmailTemplateRingState> templates)
    {
        var byAudience = new Dictionary<CommunityHub.Core.Email.EmailAudience, List<MailListing>>();

        foreach (var t in templates)
        {
            var audiences =
                CommunityHub.Core.Email.EmailTemplateCatalog.ListingAudiencesFor(t.TemplateKey);
            for (var i = 0; i < audiences.Count; i++)
            {
                var a = audiences[i];
                if (!byAudience.TryGetValue(a, out var list))
                {
                    list = new List<MailListing>();
                    byAudience[a] = list;
                }
                // ListingAudiencesFor puts the filing home first — that is the primary row.
                list.Add(new MailListing(t, i == 0, RoleForAudience(a)));
            }
        }

        return byAudience
            .OrderBy(kv => (int)kv.Key)
            .Select(kv => new MailSection(kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>The role a section stands for, or null for the cross-role section.</summary>
    private static ParticipantRole? RoleForAudience(CommunityHub.Core.Email.EmailAudience a) => a switch
    {
        CommunityHub.Core.Email.EmailAudience.Speaker      => ParticipantRole.Speaker,
        CommunityHub.Core.Email.EmailAudience.Sponsor      => ParticipantRole.Sponsor,
        CommunityHub.Core.Email.EmailAudience.Volunteer    => ParticipantRole.Volunteer,
        CommunityHub.Core.Email.EmailAudience.Attendee     => ParticipantRole.Attendee,
        CommunityHub.Core.Email.EmailAudience.Media        => ParticipantRole.Media,
        CommunityHub.Core.Email.EmailAudience.EventPartner => ParticipantRole.EventPartner,
        CommunityHub.Core.Email.EmailAudience.Organizer    => ParticipantRole.Organizer,
        _                                                  => null,
    };

    /// <summary>
    /// §566 §3 — participant-facing FEATURES whose ring decides who SEES them. Ring-scoped and not
    /// the transport itself.
    /// </summary>
    public IReadOnlyList<FeatureState> ParticipantFeatures =>
        Groups.SelectMany(g => g)
            .Where(f => f.Descriptor.IsRingScoped && f.Descriptor.Key != FeatureCatalog.OutboundEmailKey)
            .OrderBy(f => f.Descriptor.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// §566 §4 — backend, tooling, queues and the tile-only seven: ON/OFF ONLY, no ring shown.
    /// </summary>
    /// <remarks>
    /// 🔒 A RING BADGE APPEARS ONLY WHERE A RING DOES SOMETHING. Showing one here is the §326bx
    /// defect that cost the operator's confidence — "Sponsor welcome … Released to Ring 1" read as
    /// if the MAILS were limited to Ring 1. They never were.
    /// </remarks>
    public IReadOnlyList<FeatureState> BackendAndTools =>
        Groups.SelectMany(g => g)
            .Where(f => !f.Descriptor.IsRingScoped && f.Descriptor.Key != FeatureCatalog.OutboundEmailKey)
            .OrderBy(f => f.Descriptor.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// §700 Batch B — the groups in DISPLAY order (roles first), each appearing EXACTLY ONCE.
    /// </summary>
    /// <remarks>
    /// 🔑 This is the fix for his original complaint: *"it seems as many things are mentioned 2-3 times
    /// on that page = redundant"*. The page used to loop `governs × chapter`, so a chapter with e.g. an
    /// email feature AND a backend feature printed its heading under BOTH classes — up to three times.
    /// Each switch only ever appeared once, but the HEADINGS repeated, which is what he was reading.
    /// Now the group is the outer loop and what a switch governs is a sub-heading inside it.
    /// </remarks>
    public IReadOnlyList<IGrouping<FeatureGroup, FeatureState>> GroupsInDisplayOrder =>
        Groups.OrderBy(g => FeatureCatalog.DisplayOrder(g.Key)).ToList();

    /// <summary>
    /// §700 Batch B item 9 — the mails a feature GATES, answering "what does this switch actually do?".
    /// </summary>
    /// <remarks>
    /// 🔑 <b>This single line would have prevented §694, §694.2 and §698.</b> He read three feature rows
    /// correctly and drew the wrong conclusion from each, because a row named a capability but never
    /// said what rode on it. "Welcome emails" gating THIRTEEN mails — five persona welcomes plus the
    /// whole Master Class funnel — is not guessable from the row.
    ///
    /// <para>Derived from <see cref="CommunityHub.Core.Email.EmailTemplateCatalog"/>, so it cannot drift
    /// from what actually sends.</para>
    /// </remarks>
    public IReadOnlyList<string> MailsGatedBy(string featureKey) =>
        CommunityHub.Core.Email.EmailTemplateCatalog.Map
            .Where(kv => string.Equals(kv.Value.FeatureKey, featureKey, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool AccessDenied { get; private set; }
    public bool Saved { get; private set; }

    /// <summary>
    /// The deployed environment — <c>DEV</c> / <c>PROD</c>. With the build stamp this answers
    /// "which build + env am I looking at" so dev-vs-prod is visible at a glance.
    /// </summary>
    /// <remarks>
    /// 🔒 §703 — this USED to be <c>IWebHostEnvironment.EnvironmentName</c>, which returns
    /// <b>"Production" on DEV as well as prod</b> (both apps set
    /// <c>ASPNETCORE_ENVIRONMENT=Production</c>; verified against the live apps 2026-07-29). So the
    /// badge §611 added — expressly so a PROD tab could not be mistaken for a DEV one, after the
    /// operator read a PROD "Writes to external systems" card believing it was DEV — was printing
    /// the SAME word in both editions and could not distinguish the two tabs it existed to
    /// distinguish. A badge that is confidently wrong is worse than no badge, and this one guards
    /// the card where misreading means writing to the real Zoho/webshop.
    ///
    /// <para>Now resolved by <see cref="CommunityHub.Core.Diagnostics.HubEnvironment"/> — the same
    /// source as the §702 <c>[DEV]</c>/<c>[PROD]</c> alert-mail tag, so the badge and the alert can
    /// never disagree.</para>
    /// </remarks>
    public string EnvName => _hubEnv.Label;

    /// <summary>
    /// The deployed build, as <c>v&lt;version&gt; (&lt;sha7&gt;)</c>, read from the
    /// entry assembly's <see cref="AssemblyInformationalVersionAttribute"/>
    /// (<c>1.0.0+&lt;gitsha&gt;</c>). One number per deployment — NOT per feature.
    /// </summary>
    public string BuildVersion
    {
        get
        {
            var info = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info)) return "unknown";
            var plus = info.IndexOf('+');
            if (plus < 0) return $"v{info}";
            var ver = info[..plus];
            var sha = info[(plus + 1)..];
            var sha7 = sha.Length > 7 ? sha[..7] : sha;
            return $"v{ver} ({sha7})";
        }
    }

    /// <summary>The catalog states grouped by chapter, in display order.</summary>
    public IReadOnlyList<IGrouping<FeatureGroup, FeatureState>> Groups { get; private set; }
        = Array.Empty<IGrouping<FeatureGroup, FeatureState>>();

    /// <summary>Enabled-but-prerequisite-off warnings (feature, missing dependency).</summary>
    public IReadOnlyList<(FeatureDescriptor Feature, FeatureDescriptor Missing)> UnmetDependencies
    { get; private set; } = Array.Empty<(FeatureDescriptor, FeatureDescriptor)>();

    /// <summary>The effective lifecycle ring of every feature group (§23a) — the per-group control state.</summary>
    public IReadOnlyList<GroupRingState> GroupRings { get; private set; }
        = Array.Empty<GroupRingState>();

    /// <summary>True when all background jobs are currently PAUSED for this edition (master switch).</summary>
    public bool JobsPaused { get; private set; }

    /// <summary>
    /// §340-H — the ENVIRONMENT default for outbound writes to third-party systems
    /// (<c>Integrations:AllowExternalWrites</c>; dev false / prod true). Shown next to the
    /// override so the organizer can see what "inherit" actually resolves to on this host.
    /// </summary>
    public bool ExternalWritesEnvDefault { get; private set; }

    /// <summary>
    /// §340-H — the organizer's per-edition override, or null when none is set (⇒ inherit
    /// <see cref="ExternalWritesEnvDefault"/>). Three states, deliberately NOT collapsed
    /// into a bool: "inherit" must stay distinguishable from "explicitly on", or DEV's
    /// out-of-the-box safety would silently depend on a past toggle.
    /// </summary>
    public bool? ExternalWritesOverride { get; private set; }

    /// <summary>§340-H — what will ACTUALLY happen: the override if set, else the env default.</summary>
    public bool ExternalWritesEffective => ExternalWritesOverride ?? ExternalWritesEnvDefault;

    /// <summary>The effective group ring for one group (for the GUI group header).</summary>
    public GroupRingState GroupRingFor(FeatureGroup g) =>
        GroupRings.FirstOrDefault(x => x.Group == g) ?? new GroupRingState(g, Ring.Broad, false);

    // --- Email controls (read-only context for the settings surface) ---------
    /// <summary>The process-wide config kill switch (forces email OFF regardless of the per-edition switch).</summary>
    public bool EmailConfigKillSwitch => _email.KillSwitch;
    /// <summary>The optional ring CEILING (DEV caps outbound email at e.g. Ring1). Empty = no ceiling (PROD).</summary>
    public string MaxReleaseRingDisplay => string.IsNullOrWhiteSpace(_email.MaxReleaseRing)
        ? string.Empty : _email.MaxReleaseRing;
    public string RedirectDisplay => string.IsNullOrWhiteSpace(_email.RedirectAllTo)
        ? string.Empty : _email.RedirectAllTo;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Toggle one feature's kill switch. Posts the feature key + the new state;
    /// the service ignores core/unknown keys. After saving the page re-renders the
    /// full surface so the toggle, the dimmed state and any dependency warning
    /// reflect the new reality immediately.
    /// </summary>
    public async Task<IActionResult> OnPostToggleAsync(
        string key, bool enable, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!string.IsNullOrWhiteSpace(key))
        {
            await _settings.SetEnabledAsync(me.EventId, key, enable, me.Email, ct);
            Saved = true;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Set one feature's RELEASED-TO ring (0–3) for this edition — the
    /// progressive-rollout control (REQUIREMENTS §23). A feature is active for a
    /// resource only when its effective ring ≤ this released ring.
    /// </summary>
    public async Task<IActionResult> OnPostReleaseRingAsync(
        string key, Ring ring, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!string.IsNullOrWhiteSpace(key))
        {
            await _settings.SetReleasedRingAsync(me.EventId, key, ring, me.Email, ct);
            Saved = true;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    // 🗑 §700 Batch B — `OnPostGroupRingAsync` DELETED. It set a whole group's lifecycle ring, a
    // control §705 removes outright ("rings exist only on emails; features have only on/off") and
    // whose UI went with the IA rewrite. Leaving the handler behind would have left a reachable POST
    // endpoint able to create `FeatureGroupSettings` rows that nothing on the page could then show or
    // undo — a way to silently re-arm the §694.4 inheritance hazard from outside the UI.
    //
    // 🔒 Verified before deleting, in BOTH editions: zero `FeatureGroupSettings` rows and zero
    // `FeatureSetting.GroupOverride` values. So no stored state depended on it, and with the setter
    // gone the group term can never fire again — which is also what makes re-homing features between
    // groups provably safe.
    //
    // `FeatureSettingsService.SetGroupRingAsync` REMAINS: the gate still reads group rings as the
    // middle term of `perFeatureOverride ?? groupRing ?? catalogDefault`, and `FeatureGroupRingTests`
    // still pins that resolution. Only the page's ability to WRITE one is gone.

    /// <summary>
    /// Clear a feature's ring override so it adopts its (effective) group's ring
    /// again (§23a "adopt the group's lifecycle").
    /// </summary>
    public async Task<IActionResult> OnPostInheritGroupRingAsync(string key, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!string.IsNullOrWhiteSpace(key))
        {
            await _settings.ClearReleasedRingOverrideAsync(me.EventId, key, me.Email, ct);
            Saved = true;
        }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// RE-HOME / graduate a feature into a different group (§23a) — it then adopts
    /// the destination group's lifecycle ring.
    /// </summary>
    public async Task<IActionResult> OnPostFeatureGroupAsync(
        string key, FeatureGroup group, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!string.IsNullOrWhiteSpace(key))
        {
            await _settings.SetFeatureGroupAsync(me.EventId, key, group, me.Email, ct);
            Saved = true;
        }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// PAUSE or RESUME all background jobs for this edition — the master switch.
    /// Every timer job consults this (via JobsPauseMiddleware) and no-ops while
    /// paused; resume takes effect on each job's next tick.
    /// </summary>
    public async Task<IActionResult> OnPostSetJobsPausedAsync(bool paused, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        await _settings.SetJobsPausedAsync(me.EventId, paused, me.Email, ct);
        Saved = true;

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// §340-H — set or clear the per-edition override for outbound writes to third-party
    /// systems (operator 2026-07-26: "i can as organizer control this on the settings page").
    /// <paramref name="allow"/> is null when the organizer chooses "inherit", which DELETES
    /// the row so the edition follows the environment default again.
    /// </summary>
    public async Task<IActionResult> OnPostSetExternalWritesAsync(bool? allow, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        await _settings.SetExternalWritesOverrideAsync(me.EventId, allow, me.Email, ct);
        Saved = true;

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Groups = await _settings.GetByGroupAsync(eventId, ct);
        UnmetDependencies = await _settings.GetUnmetDependenciesAsync(eventId, ct);
        GroupRings = await _settings.GetGroupRingsAsync(eventId, ct);
        JobsPaused = await _gate.AreJobsPausedAsync(eventId, ct);
        ExternalWritesEnvDefault = _externalWrites.AllowExternalWrites;
        ExternalWritesOverride = await _settings.GetExternalWritesOverrideAsync(eventId, ct);
        var allTemplates = await _templateRings.GetAllAsync(eventId, ct);

        // §707.25 — hide mails that are RETIRED IN CODE unless asked for, and always COUNT them.
        RetiredHidden = allTemplates.Count(t =>
            CommunityHub.Core.Email.EmailTemplateCatalog.IsRetiredInCode(t.TemplateKey));
        Templates = ShowRetired
            ? allTemplates
            : allTemplates
                .Where(t => !CommunityHub.Core.Email.EmailTemplateCatalog.IsRetiredInCode(t.TemplateKey))
                .ToList();

        // §707.11 — the repeat interval in force per recurring mail, read once for the page.
        if (_cadence is not null)
        {
            CadenceByTemplate = (await _cadence.GetAllAsync(eventId, ct))
                .ToDictionary(c => c.TemplateKey, c => c.IntervalDays, StringComparer.OrdinalIgnoreCase);

            // §881 — and the per-role rows, so a cross-listed section can render its OWN box instead
            // of a sentence explaining where the control lives.
            CadenceByRole = await _cadence.GetPerRoleAsync(eventId, ct);
        }
    }

    /// <summary>
    /// §515 — set ONE mail's own release ring, or clear it back to its feature's ring.
    ///
    /// <para>This is the control the operator asked for: "ring 2 for speakers welome and ring 1 for
    /// sponsors". Before it, the only ring lived on the shared <c>welcome-email</c> feature, so
    /// changing "the ring for this template" silently moved all five persona welcomes AND the whole
    /// Master Class funnel together.</para>
    /// </summary>
    /// <summary>
    /// §566 — "SET ALL IN THIS ROLE": write the SAME ring onto every mail in one audience group.
    /// </summary>
    /// <remarks>
    /// 🔒 A BUTTON, NOT AN INHERITED ROLE-LEVEL RING — his explicit choice (*"use the button"*).
    /// It writes each mail's OWN ring, so the truth stays in ONE place (on the mail) and no new
    /// inheritance level is introduced. That matters because the §515 trap was exactly an invisible
    /// shared level: changing "the ring for this template" silently moved all five persona welcomes
    /// and the whole Master Class funnel together.
    ///
    /// <para>He works role by role — *"He tests a role at a time and sets every mail in that role to
    /// the same ring"* — so this is the action the page exists to make cheap.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostSetRoleRingAsync(
        CommunityHub.Core.Email.EmailAudience audience, Ring ring, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var all = await _templateRings.GetAllAsync(me.EventId, ct);
        var section = BuildSections(all).FirstOrDefault(s => s.Audience == audience);
        var changed = 0;
        var perRole = 0;

        // §704.1c — SKIP ring-exempt mails. Writing a ring row for one would persist a value that
        // no send site consults: the §326bx defect, created by a bulk action rather than a typo.
        // They are excluded from the count too, so the confirmation line stays truthful.
        foreach (var listing in (section?.Mails ?? Array.Empty<MailListing>())
                     .Where(l => !CommunityHub.Core.Email.EmailTemplateCatalog
                         .IsRingExempt(l.Template.TemplateKey)))
        {
            var key = listing.Template.TemplateKey;
            var reach = CommunityHub.Core.Email.EmailTemplateCatalog.RecipientRolesFor(key);

            // 🔒 §707.27 B — on a SHARED mail this button writes THAT ROLE's ring, never the
            // all-roles one. "Set ALL in this role" means this role; writing the all-roles row here
            // would silently move every other role the mail reaches — the §515 trap, fired by a bulk
            // action. Operator's rule: "1 mail type to a role = 1 ring gate."
            var asRole = listing.SectionRole is ParticipantRole r && reach.Count > 1 && reach.Contains(r)
                ? r
                : (ParticipantRole?)null;

            var ok = asRole is null
                ? await _templateRings.SetRingAsync(me.EventId, key, ring, me.Email, ct)
                : await _templateRings.SetRingAsync(me.EventId, key, ring, me.Email, ct, asRole.Value);

            if (!ok) continue;
            changed++;
            if (asRole is not null) perRole++;
        }

        Saved = changed > 0;
        var label = CommunityHub.Core.Email.EmailTemplateCatalog.AudienceLabel(audience);
        Message = perRole > 0
            ? $"{changed} mail(s) under {label} set to Ring {(int)ring} "
              + $"— {perRole} of them as that role's own ring (shared templates; other roles untouched)."
            : $"{changed} mail(s) for {audience} set to Ring {(int)ring}.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>A short confirmation line for the last bulk action.</summary>
    public string? Message { get; private set; }

    public async Task<IActionResult> OnPostTemplateRingAsync(
        string templateKey, string ring, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (string.Equals(ring, "inherit", StringComparison.OrdinalIgnoreCase))
        {
            await _templateRings.ClearRingAsync(me.EventId, templateKey, ct);
            Saved = true;
        }
        else if (Enum.TryParse<Ring>(ring, out var r)
                 && await _templateRings.SetRingAsync(me.EventId, templateKey, r, me.Email, ct))
        {
            Saved = true;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// §705.3b — set (or clear) ONE ROLE's ring for one mail. This is the control his rule needs:
    /// *"1 mail type to a role = 1 ring gate. that is it. so reminder to speaker is NOT the same as
    /// reminder to organizer or reminder to sponsor."*
    /// </summary>
    /// <remarks>
    /// 🔒 Separate from <see cref="OnPostTemplateRingAsync"/> on purpose. That one writes the ALL-ROLES
    /// row; this writes a role row beside it. Sharing a handler risked the two colliding — a per-role
    /// write landing on the all-roles row would silently move every other role's audience, the exact
    /// §515 trap this work exists to remove.
    ///
    /// <para>"inherit" here means *drop back to the all-roles ring* (and to FAIL-CLOSED if there is no
    /// all-roles row), never *inherit a feature ring* — the feature fallback is gone (§705.2).</para>
    /// </remarks>
    public async Task<IActionResult> OnPostTemplateRoleRingAsync(
        string templateKey, ParticipantRole role, string ring, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (string.Equals(ring, "inherit", StringComparison.OrdinalIgnoreCase))
        {
            await _templateRings.ClearRingAsync(me.EventId, templateKey, ct, role);
            Saved = true;
        }
        else if (Enum.TryParse<Ring>(ring, out var r)
                 && await _templateRings.SetRingAsync(me.EventId, templateKey, r, me.Email, ct, role))
        {
            Saved = true;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// §705.3b — the per-ROLE ring rows for one mail: the roles it actually reaches, each with its own
    /// ring if it has one and the effective ring it resolves to.
    /// </summary>
    /// <remarks>
    /// Only the roles a mail REALLY reaches are offered (`EmailTemplateCatalog.RecipientRolesFor`, read
    /// from the senders in the §705.9 audit). Offering all seven everywhere would put six meaningless
    /// controls on most rows — and a control that governs nothing is the §326bx defect that cost him
    /// confidence in this page.
    /// </remarks>
    public IReadOnlyList<(ParticipantRole Role, string Label, Ring? OwnRing, Ring Effective)>
        RoleRingsFor(CommunityHub.Core.Email.EmailTemplateRingState t)
    {
        var roles = CommunityHub.Core.Email.EmailTemplateCatalog.RecipientRolesFor(t.TemplateKey);
        if (roles.Count <= 1) return Array.Empty<(ParticipantRole, string, Ring?, Ring)>();

        return roles.Select(role =>
        {
            var own = t.RoleRings.TryGetValue(role, out var v) ? (Ring?)v : null;
            // Specific beats general; with neither, the mail fails closed for that role.
            var effective = own ?? t.OverrideRing ?? Ring.Ring0;
            return (role, RoleLabel(role), own, effective);
        }).ToList();
    }

    private static string RoleLabel(ParticipantRole role) => role switch
    {
        ParticipantRole.Organizer => "Organizers",
        ParticipantRole.Speaker => "Speakers",
        ParticipantRole.Volunteer => "Volunteers",
        ParticipantRole.Sponsor => "Sponsors",
        ParticipantRole.Attendee => "Attendees",
        ParticipantRole.Media => "Media",
        ParticipantRole.EventPartner => "Event partners",
        _ => role.ToString(),
    };
}
