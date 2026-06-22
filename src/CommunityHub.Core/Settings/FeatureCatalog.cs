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
    Ring DefaultReleasedToRing = Ring.Ring1,
    FeatureSurface Surface = FeatureSurface.Engine)
{
    /// <summary>Convenience: advanced features default OFF, core default ON.</summary>
    public bool IsAdvanced => Tier == FeatureTier.Advanced;

    /// <summary>True for a USER-IMPACT feature (category 4: a user experiences it —
    /// email received / task visible / GUI surface). Ring-scoped on the target user.</summary>
    public bool IsUserImpact => Surface == FeatureSurface.UserImpact;

    /// <summary>True for a QUEUE feature (category 3: organizer stages + Commits data
    /// for an engine; org-admin only; 2nd-confirm + ring-scoped impact at commit).</summary>
    public bool IsQueue => Surface == FeatureSurface.Queue;

    /// <summary>True for an ENGINE feature (category 1 plumbing OR category 2
    /// queue-fed engine). Never ring-scoped — governed only by the kill switch (GA).</summary>
    public bool IsEngine => Surface is FeatureSurface.Engine or FeatureSurface.EngineQueued;

    /// <summary>True when the feature is RING-SCOPED (categories 3 Queue + 4 UserImpact):
    /// staged rollout limits who it touches. Engine/EngineQueued are NOT ring-scoped.
    /// This is the single predicate the GUI badge+gate and the ring gate key off.</summary>
    public bool IsRingScoped => Surface is FeatureSurface.Queue or FeatureSurface.UserImpact;
}

/// <summary>
/// The FOUR feature surfaces (REQUIREMENTS §23a, operator 2026-06-22, locked). The
/// surface decides how a feature is governed: Engine/EngineQueued by a kill switch
/// only (GA, never ring-scoped); Queue/UserImpact additionally ring-scoped (staged
/// rollout). Decision tree: "would a non-organizer notice this happened to them?"
/// yes ⇒ UserImpact; "is it an organizer staging+committing data for an engine?"
/// ⇒ Queue; "is it backend that only runs on committed queue data?" ⇒ EngineQueued;
/// else ⇒ Engine.
/// </summary>
public enum FeatureSurface
{
    /// <summary>(1) Core backend / plumbing — mail routing/transport, pulls, syncs,
    /// schedulers. No per-user experience. On/off only, default ON, GA — never
    /// ring-scoped.</summary>
    Engine = 0,

    /// <summary>(4) A user experiences it — an email received, a task visible with a
    /// deadline, a GUI feature. Ring-scoped on the TARGET user (staged rollout).</summary>
    UserImpact = 1,

    /// <summary>(2) A backend engine with a data dependency on a queue/job (LinkedIn
    /// poster, the volunteer/hotel APPLIER). On/off, default ON, GA — never
    /// ring-scoped — but inert until an organizer Commits scoped data from a Queue.</summary>
    EngineQueued = 2,

    /// <summary>(3) Organizer-operated staging surface (org admin only): stage data,
    /// hit Commit (2nd-confirm + consequences) to write to SQL. Impact is RING-SCOPED
    /// at commit — out-of-ring rows persist but stay dormant until the ring widens.</summary>
    Queue = 3,
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
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        new("magic-link", "Settings.Feat.MagicLink.Name",
            "Settings.Feat.MagicLink.Desc",
            FeatureGroup.Email, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // --- Speakers & sessions --------------------------------------------
        // GA (operator 2026-06-22): released to Broad — runs for everyone, unscoped.
        new("sessionize-import", "Settings.Feat.Sessionize.Name",
            "Settings.Feat.Sessionize.Desc",
            FeatureGroup.SpeakersSessions, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // --- Sponsors -------------------------------------------------------
        // GA (operator 2026-06-22): tested backend syncs — released to Broad, unscoped.
        new("backstage-sync", "Settings.Feat.BackstageSync.Name",
            "Settings.Feat.BackstageSync.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        new("economic-erp-sync", "Settings.Feat.EconomicErp.Name",
            "Settings.Feat.EconomicErp.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // GA (operator 2026-06-22): tested backend pull — released to Broad, unscoped.
        new("sponsor-order-pull", "Settings.Feat.SponsorOrderPull.Name",
            "Settings.Feat.SponsorOrderPull.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // ENGINE (operator 2026-06-22): a backend export of leads to the sponsor (today
        // just a Zoho Backstage link — no API yet). Kill-switch only, NOT ring-scoped.
        new("sponsor-leads", "Settings.Feat.SponsorLeads.Name",
            "Settings.Feat.SponsorLeads.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // GA (operator 2026-06-22): tested backend job — released to Broad, unscoped.
        new("sponsor-upload-watch", "Settings.Feat.SponsorUploadWatch.Name",
            "Settings.Feat.SponsorUploadWatch.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // --- Social media (category 2: ENGINE-QUEUED dispatch — GA/Broad, never
        // ring-scoped, but inert until the SoMe queue commits scoped posts) -------
        new("some-scheduling", "Settings.Feat.SoMe.Name",
            "Settings.Feat.SoMe.Desc",
            FeatureGroup.SocialMedia, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.EngineQueued),

        new("linkedin-queue", "Settings.Feat.LinkedIn.Name",
            "Settings.Feat.LinkedIn.Desc",
            FeatureGroup.SocialMedia, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { "some-scheduling" }, DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.EngineQueued),

        // --- Surveys --------------------------------------------------------
        new("surveys", "Settings.Feat.Surveys.Name",
            "Settings.Feat.Surveys.Desc",
            FeatureGroup.Surveys, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // --- Reminders / digests --------------------------------------------
        // RULE 2: reminders + digests are OUTBOUND EMAIL ⇒ released to ring 1 only.
        new("reminder-jobs", "Settings.Feat.ReminderJobs.Name",
            "Settings.Feat.ReminderJobs.Desc",
            FeatureGroup.Reminders, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        new("digest-emails", "Settings.Feat.DigestEmails.Name",
            "Settings.Feat.DigestEmails.Desc",
            FeatureGroup.Reminders, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { "reminder-jobs", OutboundEmailKey },
            DefaultReleasedToRing: Ring.Ring1, Surface: FeatureSurface.UserImpact),

        // --- Attendees ------------------------------------------------------
        // GA (operator 2026-06-22): the Zoho attendee pull is GA — it runs for all.
        // Impact is scoped PER USER (Participant.Ring + IsTestUser): pulled attendees
        // default to Broad; flag specific test attendees Ring1 to trial ring-1
        // features on them. So the pull itself is NOT ring-gated.
        new("attendee-reconcile", "Settings.Feat.AttendeeReconcile.Name",
            "Settings.Feat.AttendeeReconcile.Desc",
            FeatureGroup.Attendees, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // Auto-provision a login-capable Attendee Participant per 2-day-ticket
        // holder + email a one-click magic-link welcome. DEFAULT OFF: this sends
        // real email to real attendees, so it must be turned on deliberately by an
        // organizer (a mass action — never auto-enabled by a deploy).
        new("attendee-welcome", "Settings.Feat.AttendeeWelcome.Name",
            "Settings.Feat.AttendeeWelcome.Desc",
            FeatureGroup.Attendees, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { "attendee-reconcile", OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // --- Incubation: NEW user-impact GUI actions, ring-tested before GA -----
        // (operator 2026-06-22) Every GUI action that a person NOTICES happening to
        // them — a mass email, a task that appears, an assignment, an account being
        // provisioned — is a USER-IMPACT feature: born in Incubation at Ring1 so only
        // ring-1 testers see + exercise it, promoted group/feature -> Broad in
        // /Organizer/Settings once proven. No deploy springs these on a live event.
        // These back the hub TILES (HubTile.FeatureKey) so _HubGrid badges + gates
        // them the same way the nav does. They graduate into a real group later.

        // Mass / one-off outbound mail composed in the GUI (Email Center, Broadcast).
        new("broadcast-email", "Settings.Feat.BroadcastEmail.Name",
            "Settings.Feat.BroadcastEmail.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Invitation email blast (SendInvitations) — mints + emails sign-in links.
        new("invitation-email", "Settings.Feat.InvitationEmail.Name",
            "Settings.Feat.InvitationEmail.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey, "magic-link" }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Re-send an arbitrary logged/templated email to people (Comms, Email log).
        new("email-resend", "Settings.Feat.EmailResend.Name",
            "Settings.Feat.EmailResend.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Bulk "redo this onboarding step" emails (Action queue).
        new("onboarding-step-reset", "Settings.Feat.OnboardingStepReset.Name",
            "Settings.Feat.OnboardingStepReset.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Activate / provision login-capable accounts (Pre-selection queue, bulk
        // participant ops) — enables sign-in for real people.
        new("participant-activation", "Settings.Feat.ParticipantActivation.Name",
            "Settings.Feat.ParticipantActivation.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Master Class invite + waitlist-promotion emails w/ self-service links.
        new("masterclass-invites", "Settings.Feat.MasterClassInvites.Name",
            "Settings.Feat.MasterClassInvites.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Email session-evaluation (HappyOrNot) results to speakers.
        new("session-eval-email", "Settings.Feat.SessionEvalEmail.Name",
            "Settings.Feat.SessionEvalEmail.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Sponsor welcome / intro email (per-company or all).
        new("sponsor-welcome", "Settings.Feat.SponsorWelcome.Name",
            "Settings.Feat.SponsorWelcome.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Tasks created for sponsor companies (appear to all their contacts).
        new("sponsor-tasks", "Settings.Feat.SponsorTasks.Name",
            "Settings.Feat.SponsorTasks.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Reminder emails to sponsors (App game).
        new("sponsor-reminders", "Settings.Feat.SponsorReminders.Name",
            "Settings.Feat.SponsorReminders.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // QUEUE (category 3): organizer stages task assignments + Commits (2nd-confirm,
        // ring-scoped impact). The task BECOMING VISIBLE to a volunteer is the
        // user-impact, gated per target at commit time. These three are ALREADY-SHIPPED
        // features, so their released ring defaults to BROAD (GA) — current behaviour is
        // preserved (every target in scope). The ring-scoping MECHANISM is wired
        // (CommitAsync filters drafts by each target's ring): lower the ring to Ring1 in
        // /Organizer/Settings to ring-TEST a change so only Ring1 targets are committed.
        // (A genuinely NEW queue feature is born Ring1 — the catalog default.)
        new("volunteer-tasks", "Settings.Feat.VolunteerTasks.Name",
            "Settings.Feat.VolunteerTasks.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.Queue),

        new("volunteer-allocation", "Settings.Feat.VolunteerAllocation.Name",
            "Settings.Feat.VolunteerAllocation.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.Queue),

        new("hotel-assignment", "Settings.Feat.HotelAssignment.Name",
            "Settings.Feat.HotelAssignment.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.Queue),

        // Group-photo session invite emails.
        new("group-photo-invites", "Settings.Feat.GroupPhotoInvites.Name",
            "Settings.Feat.GroupPhotoInvites.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Travel-reimbursement payment confirmation emails.
        new("travel-reimbursement-email", "Settings.Feat.TravelReimbursementEmail.Name",
            "Settings.Feat.TravelReimbursementEmail.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Release SoMe graphics to speakers (becomes visible to them).
        new("graphics-release", "Settings.Feat.GraphicsRelease.Name",
            "Settings.Feat.GraphicsRelease.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Bulk delete of test participants / data — impactful, ring-test only.
        new("test-data-cleanup", "Settings.Feat.TestDataCleanup.Name",
            "Settings.Feat.TestDataCleanup.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Attendee calendar invites — independently dialable per email (operator 2026-06-22).
        new("hotel-invite", "Settings.Feat.HotelInvite.Name",
            "Settings.Feat.HotelInvite.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        new("dinner-invite", "Settings.Feat.DinnerInvite.Name",
            "Settings.Feat.DinnerInvite.Desc",
            FeatureGroup.Incubation, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),
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
