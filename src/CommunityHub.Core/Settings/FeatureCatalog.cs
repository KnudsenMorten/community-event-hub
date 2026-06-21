namespace CommunityHub.Core.Settings;

/// <summary>
/// The TIER of a capability in the hub (REQUIREMENTS §23).
/// </summary>
public enum FeatureTier
{
    /// <summary>
    /// Essential hub surface — always on, NOT customizable (participant/session
    /// search, view, modify, sign-in/auth, the public pages). Core features are
    /// never gated and never appear with a kill switch.
    /// </summary>
    Core = 0,

    /// <summary>
    /// Optional integration / automation / side-effecting capability that MUST be
    /// customizable (Sessionize import, Backstage/Zoho sync, ERP sync, SoMe
    /// scheduling, reminder/digest jobs, welcome/magic-link email, surveys …).
    /// A new advanced feature defaults OFF so a deploy never springs a new
    /// behaviour on a live event.
    /// </summary>
    Advanced = 1,
}

/// <summary>
/// One customizable capability in the hub — the unit the GUI, the jobs and the
/// tests all read (REQUIREMENTS §23). Immutable: a descriptor is metadata, not
/// state. The on/off state lives in the persisted <see cref="FeatureSetting"/>
/// store, never here.
/// </summary>
/// <param name="Key">
/// Stable machine key (kebab-case). The persisted kill switch and every gate
/// check key off this; never rename a shipped key.
/// </param>
/// <param name="DisplayNameKey">The i18n resource key for the human-friendly name.</param>
/// <param name="DescriptionKey">The i18n resource key for the one-line description.</param>
/// <param name="Group">The feature GROUP (chapter) this belongs to in the GUI.</param>
/// <param name="Tier"><see cref="FeatureTier.Core"/> or <see cref="FeatureTier.Advanced"/>.</param>
/// <param name="DefaultEnabled">
/// The fallback when no per-edition kill switch is persisted. Advanced features
/// default <c>false</c> (opt-in); core features are always enabled.
/// </param>
/// <param name="DependsOn">
/// Keys of features this one needs. Disabling a dependency warns; enabling this
/// prompts for its prerequisites. Empty for no dependency.
/// </param>
/// <param name="DefaultReleasedToRing">
/// The fallback "released-to ring" when no per-edition <see cref="FeatureSetting"/>
/// row is persisted (REQUIREMENTS §23 progressive rollout).
///
/// OPERATOR RULE 1 — the descriptor default is <see cref="Ring.Ring1"/> (operator
/// 2026-06-21: "default features released to ring 1, not 0"). A feature is therefore
/// released to ring 1 by default — visible to ring-0 AND ring-1 testers (so a ring-1
/// reviewer sees the whole portal) — and NOT yet to ring-2 / Broad users; it is then
/// PROMOTED to <see cref="Ring.Broad"/> for general availability once proven. Every
/// EXISTING / already-delivered feature is also at ring 1 (the guardrail below). This
/// SUPERSEDES the earlier "new features are ring 0" rule (2026-06-20). New features
/// are conventionally declared in <see cref="FeatureGroup.Incubation"/> and graduated
/// to a target group later (adopting that group's ring).
///
/// OPERATOR RULE 2 — every outbound-EMAIL feature also pins this to
/// <see cref="Ring.Ring1"/> so mail reaches only ring 0 + ring 1 (a critical
/// safety net while the rollout is proven; ring 2 / Broad get nothing).
/// </param>
public sealed record FeatureDescriptor(
    string Key,
    string DisplayNameKey,
    string DescriptionKey,
    FeatureGroup Group,
    FeatureTier Tier,
    bool DefaultEnabled,
    IReadOnlyList<string> DependsOn,
    Ring DefaultReleasedToRing = Ring.Ring1)
{
    /// <summary>Convenience: advanced features default OFF, core default ON.</summary>
    public bool IsAdvanced => Tier == FeatureTier.Advanced;
}

/// <summary>
/// The feature GROUPS (chapters) the settings GUI renders, in display order.
/// Mirrors the REQUIREMENTS/FEATURES chapter structure.
/// </summary>
public enum FeatureGroup
{
    Email = 0,
    SpeakersSessions = 1,
    Sponsors = 2,
    SocialMedia = 3,
    Surveys = 4,
    Reminders = 5,
    Attendees = 6,

    /// <summary>
    /// The incubation / "test" group (REQUIREMENTS §23a): the birthplace of NEW
    /// features. Its group lifecycle ring defaults to <see cref="Ring.Ring1"/>
    /// (operator 2026-06-21: default is ring 1), so anything new is visible to ring-0
    /// AND ring-1 testers until promoted to Broad. "Graduating" a feature = re-homing
    /// it into a target group, after which it adopts that group's ring. Rendered last
    /// in the GUI.
    /// </summary>
    Incubation = 7,
}

/// <summary>
/// The single SOURCE OF TRUTH for every customizable capability (REQUIREMENTS
/// §23). A static, immutable list the GUI renders, the gate service reads its
/// defaults from, and the classification test asserts over. Adding a new
/// advanced capability is a one-line entry here (default OFF) — the gate, the
/// GUI row and the test coverage follow automatically.
///
/// CORE capabilities (search / view / modify / auth / public pages) are
/// deliberately NOT listed: they are never gated. Only customizable (advanced)
/// capabilities — and the email controls — appear here.
/// </summary>
public static class FeatureCatalog
{
    /// <summary>The global outbound-email kill switch feature key.</summary>
    public const string OutboundEmailKey = "outbound-email";

    /// <summary>
    /// Every customizable capability, in GUI order. Each carries its key, names,
    /// group (chapter), tier, default-enabled and dependencies. Advanced features
    /// default OFF (opt-in) except the email master switch, which defaults ON so
    /// the hub can mail on day one — turning it OFF is the instant global kill.
    /// </summary>
    public static readonly IReadOnlyList<FeatureDescriptor> All = new List<FeatureDescriptor>
    {
        // NOTE on the DefaultReleasedToRing of these entries (§23a, OPERATOR
        // 2026-06-21): every feature — existing AND future/new — is at Ring.Ring1.
        // The descriptor DEFAULT is now Ring1 too (was Ring0), so a feature added
        // with no explicit ring is born at ring 1. This is the controlled-rollout
        // posture before go-live: ring-0 AND ring-1 testers see every feature until
        // an organizer promotes it (group or feature) up to Broad for GA.

        // --- Email (first-class controls) -----------------------------------
        // The outbound-email MASTER switch. Defaults ON so transactional mail
        // (PIN sign-in, welcome) works out of the box; flipping it OFF is the
        // global kill switch every send path honours (web + jobs). RULE 2: every
        // EMAIL feature is released only to ring 1 (mail reaches ring 0 + 1 only).
        new(OutboundEmailKey, "Settings.Feat.OutboundEmail.Name",
            "Settings.Feat.OutboundEmail.Desc",
            FeatureGroup.Email, FeatureTier.Advanced, DefaultEnabled: true,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1),

        new("welcome-email", "Settings.Feat.WelcomeEmail.Name",
            "Settings.Feat.WelcomeEmail.Desc",
            FeatureGroup.Email, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1),

        new("magic-link", "Settings.Feat.MagicLink.Name",
            "Settings.Feat.MagicLink.Desc",
            FeatureGroup.Email, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1),

        // --- Speakers & sessions --------------------------------------------
        new("sessionize-import", "Settings.Feat.Sessionize.Name",
            "Settings.Feat.Sessionize.Desc",
            FeatureGroup.SpeakersSessions, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1),

        // --- Sponsors -------------------------------------------------------
        new("backstage-sync", "Settings.Feat.BackstageSync.Name",
            "Settings.Feat.BackstageSync.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1),

        new("economic-erp-sync", "Settings.Feat.EconomicErp.Name",
            "Settings.Feat.EconomicErp.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1),

        new("sponsor-order-pull", "Settings.Feat.SponsorOrderPull.Name",
            "Settings.Feat.SponsorOrderPull.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1),

        new("sponsor-leads", "Settings.Feat.SponsorLeads.Name",
            "Settings.Feat.SponsorLeads.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1),

        new("sponsor-upload-watch", "Settings.Feat.SponsorUploadWatch.Name",
            "Settings.Feat.SponsorUploadWatch.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1),

        // --- Social media ---------------------------------------------------
        new("some-scheduling", "Settings.Feat.SoMe.Name",
            "Settings.Feat.SoMe.Desc",
            FeatureGroup.SocialMedia, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1),

        new("linkedin-queue", "Settings.Feat.LinkedIn.Name",
            "Settings.Feat.LinkedIn.Desc",
            FeatureGroup.SocialMedia, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { "some-scheduling" }, DefaultReleasedToRing: Ring.Ring1),

        // --- Surveys --------------------------------------------------------
        new("surveys", "Settings.Feat.Surveys.Name",
            "Settings.Feat.Surveys.Desc",
            FeatureGroup.Surveys, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1),

        // --- Reminders / digests --------------------------------------------
        // RULE 2: reminders + digests are OUTBOUND EMAIL ⇒ released to ring 1 only.
        new("reminder-jobs", "Settings.Feat.ReminderJobs.Name",
            "Settings.Feat.ReminderJobs.Desc",
            FeatureGroup.Reminders, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1),

        new("digest-emails", "Settings.Feat.DigestEmails.Name",
            "Settings.Feat.DigestEmails.Desc",
            FeatureGroup.Reminders, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { "reminder-jobs", OutboundEmailKey },
            DefaultReleasedToRing: Ring.Ring1),

        // --- Attendees ------------------------------------------------------
        new("attendee-reconcile", "Settings.Feat.AttendeeReconcile.Name",
            "Settings.Feat.AttendeeReconcile.Desc",
            FeatureGroup.Attendees, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1),

        // Auto-provision a login-capable Attendee Participant per 2-day-ticket
        // holder + email a one-click magic-link welcome. DEFAULT OFF: this sends
        // real email to real attendees, so it must be turned on deliberately by an
        // organizer (a mass action — never auto-enabled by a deploy).
        new("attendee-welcome", "Settings.Feat.AttendeeWelcome.Name",
            "Settings.Feat.AttendeeWelcome.Desc",
            FeatureGroup.Attendees, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { "attendee-reconcile", OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1),
    };

    /// <summary>Look up a descriptor by key, or null if not in the catalog.</summary>
    public static FeatureDescriptor? Find(string key) =>
        All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));

    /// <summary>The catalog default-enabled for a key (true if the key is unknown — fail-open for non-features).</summary>
    public static bool DefaultEnabled(string key) =>
        Find(key)?.DefaultEnabled ?? true;

    /// <summary>
    /// The catalog default "released-to ring" for a key (REQUIREMENTS §23). An
    /// unknown key falls open to <see cref="Ring.Broad"/> (visible to everyone) so
    /// a non-feature call is never silently restricted.
    /// </summary>
    public static Ring DefaultReleasedToRing(string key) =>
        Find(key)?.DefaultReleasedToRing ?? Rings.Default;

    /// <summary>The catalog grouped by chapter, in display order, groups in enum order.</summary>
    public static IReadOnlyList<IGrouping<FeatureGroup, FeatureDescriptor>> ByGroup() =>
        All.GroupBy(f => f.Group)
           .OrderBy(g => (int)g.Key)
           .ToList();

    /// <summary>
    /// The DEFAULT lifecycle ring for a feature GROUP (REQUIREMENTS §23a) — the
    /// initial value shown for the group's ring control until an organizer sets a
    /// per-group ring. Email + Reminders (outbound mail) start at <see cref="Ring.Ring1"/>;
    /// Incubation at <see cref="Ring.Ring0"/> (innermost — new features); every other
    /// group at <see cref="Ring.Broad"/> (GA). NOTE: the runtime gate uses an explicit
    /// per-group ring row if set, else the FEATURE's own catalog default — this is for
    /// DISPLAY + the group control's starting point, not a hidden gate input.
    /// </summary>
    public static Ring GroupDefaultRing(FeatureGroup group) => group switch
    {
        // Operator 2026-06-21: ALL groups (incl. Incubation, the birthplace of new
        // features) default to Ring1 — controlled-rollout posture before go-live, so
        // ring-0 AND ring-1 testers see every feature until an organizer promotes it
        // (group or feature) up to Broad for general availability.
        _ => Ring.Ring1,
    };
}
