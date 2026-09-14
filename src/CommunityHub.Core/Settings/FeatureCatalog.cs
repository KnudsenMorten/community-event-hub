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
/// SUPERSEDES the earlier "new features are ring 0" rule (2026-06-20).
///
/// §700 Batch A — a new feature is now declared DIRECTLY in the group it belongs to
/// (its role, or <see cref="FeatureGroup.EventSettings"/>) and carries its own ring.
/// The old "born in Incubation, graduate later" convention is gone: nothing ever
/// graduated, so the birthplace became a parking space for 14 shipped features.
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
    FeatureSurface Surface = FeatureSurface.Engine,
    bool TileOnly = false,
    // §742 — this feature SENDS AN OPS NOTICE to a fixed mailbox, so its Settings row states WHERE
    // it goes and lets an organizer change it (FeatureSetting.NotificationRecipientEmail).
    // 🔒 Default false: the box must never appear on a switch that mails nobody — the page's own
    // rule that a control may not appear where it governs nothing (§326bx).
    bool SendsOpsNotice = false)
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

    /// <summary>
    /// True when the feature is RING-SCOPED — i.e. the ring genuinely decides WHO a
    /// participant-facing thing reaches. The single predicate the GUI badge+gate and the ring
    /// gate key off.
    /// </summary>
    /// <remarks>
    /// 🔒 §566 step 3 — THIS DELIBERATELY EXCLUDES Queue AND TileOnly. Do not widen it back.
    ///
    /// The operator lost confidence in the Settings page (§563/§564) because a ring badge appeared
    /// on switches whose ring did not limit any audience. His rule (§569, and the §566 sign-off):
    /// **rings are for participant-facing rollout only — who RECEIVES an e-mail, who SEES a
    /// feature.** Everything else is organizer tooling, and an organizer's authority is their role,
    /// not a ring.
    ///
    ///   • <b>Queue</b> — organizer staging/approval surfaces. §589 (operator 2026-07-28):
    ///     *"queues are all managed by an organizer who accept/approve, etc. so no need for
    ///     ring-gate here"*. The organizer's APPROVAL is the gate; a participant rollout ring on
    ///     top of it is the same category error §569 removed from the Zoho speaker/session push.
    ///   • <b>TileOnly</b> — the ring only ever hid an organizer TILE; it never gated the function
    ///     or the e-mail whose name it carried. That is the §326bx incident verbatim: "Sponsor
    ///     welcome … Released to Ring 1" read as if sponsor welcome MAILS were limited to Ring 1.
    ///     They were not. He dropped this category outright: *"Category 4 (tile-only) - drop-it"*.
    ///   • <b>Engine / EngineQueued</b> — backend, on/off only, runs for everyone (unchanged).
    ///
    /// A TileOnly or Queue feature keeps its enabled/disabled switch and its role visibility; it
    /// simply has no ring. A ring badge appears ONLY where a ring does something.
    ///
    /// ⚠️ <b>THE ONE AUDIENCE CONSEQUENCE, STATED PLAINLY AND ACCEPTED BY HIM.</b> A few Queue keys
    /// are passed as the <c>EmailContext.FeatureKey</c> at a real send site — notably
    /// <c>volunteer-allocation</c> on the volunteer COMMIT notification. With the queue ring gone,
    /// that mail is bounded only by the <c>outbound-email</c> ceiling, so a Broad-ring volunteer who
    /// is held today WILL receive it. That is the §566 model working as signed off
    /// (<c>audience = MIN(ceiling, that mail's own ring)</c>, and a send with no mail-level ring
    /// rides the ceiling alone) and it is coherent: an organizer who COMMITS an allocation is
    /// deciding those volunteers should be told.
    /// </remarks>
    /// <remarks>
    /// 🔒 THE E-MAIL ESCAPE HATCH IS A SAFETY PROPERTY, NOT A SPECIAL CASE. Any key that is a
    /// known e-mail FeatureKey keeps its ring no matter how it is classified, because dropping a
    /// ring from an e-mail WIDENS ITS AUDIENCE — silently, on the next deploy, to real people.
    /// <c>magic-link</c> is exactly this collision: flagged <c>TileOnly</c> AND present in
    /// <see cref="FeatureCatalog.EmailFeatureKeys"/>. Without this clause the §326bx tile-only
    /// cleanup would have un-gated sign-in link mails as a side effect of a page tidy-up.
    /// Re-classifying such a key is a decision to take deliberately, per key, with him — never a
    /// by-product of this predicate.
    /// </remarks>
    public bool IsRingScoped => Surface == FeatureSurface.UserImpact && !TileOnly;

    /// <summary>
    /// §327e — this switch's ONLY consumer is an organizer HUB TILE: its `FeatureKey` in a
    /// `.cshtml` tile list, which `_HubGrid` uses to badge the tile and hide it from an
    /// organizer outside the released ring. It does NOT gate the underlying function or the
    /// e-mail its name describes. The §326bx audit found six of these reading as if they
    /// controlled the feature itself — "Sponsor welcome … Released to Ring 1" invites the
    /// conclusion that sponsor welcome MAILS are limited to Ring 1. They are not.
    /// Flagged so the Settings page can say what the ring actually does.
    /// </summary>
    public bool GatesTileVisibilityOnly => TileOnly;

    /// <summary>
    /// §326bz — which of THREE things this switch governs, for the Settings page. The four
    /// <see cref="FeatureSurface"/> values describe the engineering shape; an organizer needs
    /// the simpler question answered: does turning the ring down stop an <b>e-mail</b>, hide a
    /// <b>feature</b>, or do <b>nothing</b> (backend)?
    /// </summary>
    public FeatureGoverns Governs =>
        !IsRingScoped ? FeatureGoverns.Backend
        : FeatureCatalog.EmailFeatureKeys.Contains(Key) ? FeatureGoverns.Email
        : FeatureGoverns.Feature;
}

/// <summary>
/// §326bz — the three plain-language classes the Settings page is organised by (operator
/// 2026-07-25: "restructure the page into features w/user impact (ring-gated), emails with
/// user impact (ring-gated) — and lastly backend features (no ring-gates)").
/// </summary>
public enum FeatureGoverns
{
    /// <summary>Ring-gated in-app capability — the ring decides WHO SEES / can use it.</summary>
    Feature = 0,

    /// <summary>Ring-gated e-mail — the ring decides WHO RECEIVES it.</summary>
    Email = 1,

    /// <summary>Backend plumbing — on/off only, runs for everyone, no ring.</summary>
    Backend = 2,
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
    // 🗑 §695 — the four MECHANISM groups (Email = 0, SocialMedia = 3, Surveys = 4, Reminders = 5)
    // are DELETED. Every member was re-homed to a ROLE or to EventSettings, per his rule: *"things
    // must only exist 1 time - no overlap. structured per role or a generic Event settings"*. A group
    // named after a MECHANISM competing with one named after a ROLE is what gave a sponsor mail two
    // plausible homes in the first place.
    //
    // 🔒 THE VALUES ARE NOT REUSED, and the `Email = 0` slot in particular stays vacant. Two reasons,
    // both checked before deleting rather than assumed:
    //   • `FeatureGroupSetting.Group` and `FeatureSetting.GroupOverride` persist this enum as an INT,
    //     so re-using a value would silently re-point any surviving row at a different group. Verified
    //     zero rows and zero non-null overrides in BOTH editions.
    //   • 0 was the enum's DEFAULT. Leaving it vacant means `default(FeatureGroup)` names nothing,
    //     which is honest — an unset group should not silently read as "Email". Verified no
    //     `default(FeatureGroup)` path exists: `FeatureGroupSetting.Group` is always assigned
    //     explicitly (`SetGroupRingAsync`), never defaulted.
    SpeakersSessions = 1,
    Sponsors = 2,
    Attendees = 6,

    // 🗑 §700 Batch A — `Incubation = 7` DELETED 2026-07-29. It was declared as a
    // BIRTHPLACE with graduation as the exit (§23a), but nothing ever graduated, so it
    // silently became the default parking space: 14 shipped, daily-use features sat in a
    // group labelled "Incubation (test)" and therefore read as provisional. The operator
    // (§695): *"things must only exist 1 time - no overlap. structured per role or a
    // generic Event settings"*. The three members below are the homes those features
    // actually needed — their absence is the whole reason Incubation filled up.
    //
    // 🔒 The value 7 is NOT reused. `FeatureSetting.GroupOverride` and
    // `FeatureGroupSetting.Group` persist this enum as an int, so re-using 7 would
    // silently re-point any surviving row at a different group. Verified 2026-07-29
    // against BOTH editions: zero `FeatureGroupSettings` rows and zero non-null
    // `GroupOverride` values exist, so nothing points at 7 today — but the gap stays.

    /// <summary>§695 — organizer-facing tooling and allocation (the organizer's own role home).</summary>
    Organizers = 8,

    /// <summary>§695 — everything filed under the VOLUNTEER role.</summary>
    Volunteers = 9,

    /// <summary>
    /// §695 — the generic home for anything NOT tied to a single role: the outbound-email
    /// controls composed in the GUI, event logistics (hotel, group photo) and operational
    /// tooling (test-data cleanup). Operator 2026-07-29: *"move the group photo into Event
    /// settings and other relevant to here"*. Rendered last in the GUI.
    /// </summary>
    EventSettings = 10,
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
    /// §326bz — the RING-SCOPED feature keys that govern an outbound E-MAIL, i.e. the ones
    /// where lowering the ring stops a message reaching someone. Two sources, both real:
    /// <list type="number">
    ///   <item>every key an <see cref="Email.EmailTemplateCatalog"/> template is filed
    ///         under — derived, so a new template classifies itself; and</item>
    ///   <item>keys a send site passes as <c>EmailContext.FeatureKey</c> without owning a
    ///         catalog template (calendar invites, sign-in links, resends).</item>
    /// </list>
    /// A key here that is NOT ring-scoped is ignored — an Engine key such as
    /// <c>outbound-email</c> is the transport itself, not a per-audience gate.
    /// <para><b>Keep list (2) in step with the send sites.</b> A ring-gated mail whose key is
    /// missing here is only mis-GROUPED on the Settings page; a mail whose send site passes
    /// no key at all is not ring-gated AT ALL — see §326bx.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> EmailFeatureKeys =
        Email.EmailTemplateCatalog.Map.Values
            .Select(v => v.FeatureKey)
            .Concat(new[]
            {
                "hotel-invite",     // HotelCalendarInviter passes FeatureKey: "hotel-invite"
                "email-resend",     // organizer-triggered resend of a previous mail
                // 🔒 §619 — "sponsor-welcome" REMOVED, found by the §566-step-5 declared-vs-observed
                // test the moment it was written. NO send site ever passed it: the sponsor welcome
                // is sent through WelcomeEmailService and is gated by "welcome-email". So its
                // presence here declared a ring that governed nothing — the §326bx defect, the same
                // shape as "magic-link" (§589). Removing it changes NO audience, because nothing
                // consulted it.
                // 🔒 §589 — "magic-link" REMOVED (operator 2026-07-28: "magic-link is ok it goes
                // out to anyone and should not be ringgated … we can remove ring gates for magic
                // link"). VERIFIED before removing, not assumed:
                //   • NO send site passes "magic-link" as a FeatureKey — the only references were
                //     this list, the catalog row, one DependsOn and EnableEmailFeaturesJob.
                //   • NO e-mail template declares it.
                //   • Sign-in mail is gated by a DIFFERENT and correct mechanism: PinLoginService
                //     sends with EmailContext("pin-signin", RingExempt: true), which bypasses the
                //     ring outright — a person who asks for a sign-in link and hears nothing back
                //     cannot diagnose it, so that mail is never ring-gated.
                //   • Its live PROD ring was already Broad (3), so nothing changes operationally.
                // It was therefore a ring badge that gated nothing — the exact §326bx defect.
            })
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>§326ay — the Zoho webhook DRAIN job (the real-time reconcile leg).
    /// Separate from <c>attendee-reconcile</c> so the minute-by-minute incremental path can
    /// be retired while the guarded 10-minute full sync keeps running. Default OFF.</summary>
    public const string WebhookDrainKey = "zoho-webhook-drain";

    /// <summary>§707.13 — the webhook RECEIVER (the <c>ZohoOrderWebhook</c> HTTP endpoint that
    /// ACCEPTS Backstage's POST and queues it). Separate from <see cref="WebhookDrainKey"/>, which
    /// only decides whether queued rows are APPLIED. Default ON — see the descriptor.</summary>
    public const string WebhookReceiverKey = "zoho-webhook-receiver";

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
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: true,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1),

        new("welcome-email", "Settings.Feat.WelcomeEmail.Name",
            "Settings.Feat.WelcomeEmail.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // §720 (operator 2026-07-31): *"i would like to get email to mok@expertslive.dk when someone
        // completes the get started wizard in full … make a notification on/off feature in settings
        // for this. then i know it and can reach out to ask them for their experience"*.
        //
        // 🔑 Deliberately NOT ring-scoped (no Surface: UserImpact). This is an OPS notice addressed
        // to the operator himself — the recipient is a fixed mailbox, not a participant — so there
        // is no audience to narrow, and a ring here would be a control that governs nothing: the
        // §326bx / §619 defect, twice found and twice removed. The ON/OFF is the whole control,
        // which is exactly what he asked for.
        // ⚠️ DefaultEnabled: FALSE, enforced by FeatureCatalogClassificationTests — *"an advanced
        // feature must default OFF (opt-in) so a deploy never springs new behaviour"*. It sends
        // MAIL, so that rule is exactly right here and I did not weaken it: he switches it on in
        // Settings, which is the control he asked for anyway.
        // §742 — SendsOpsNotice: the row now STATES where the mail goes and lets him change it.
        new("getstarted-complete-notice", "Settings.Feat.GetStartedCompleteNotice.Name",
            "Settings.Feat.GetStartedCompleteNotice.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Broad,
            SendsOpsNotice: true),

        // §336 — TILE ONLY, a SEVENTH one (§327e found six). Verified by exhausting every form
        // a key can take: the only quoted consumers are the two organizer HubTiles on
        // People.cshtml ("Welcome sign-in links", "Permanent sign-in links"). There is NO
        // IsFeatureEnabledAsync / IsTargetInReleasedRingAsync call in MagicLinkService,
        // WelcomeLinks or AccessLinks, and no EmailTemplateCatalog row maps to this key — so no
        // e-mail carries it as a FeatureKey either. (EnableEmailFeaturesJob names it, but that
        // ENABLES the switch; it does not gate on it.) Turning this ring down hides two
        // organizer tiles and does NOT stop a single auto-login link being issued or used.
        new("magic-link", "Settings.Feat.MagicLink.Name",
            "Settings.Feat.MagicLink.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact, TileOnly: true),

        // §754 — the SIGNAGE agenda mirror: pull the complete Zoho Backstage agenda (talks, master
        // classes, breaks, registration, lunch, party) into CEH every 5 minutes so the venue screens
        // render from a local cache instead of from Zoho at request time.
        //
        // ENGINE surface: a pull with no per-user experience, so never ring-scoped — a screen in a
        // corridor has no ring. Off by default like every advanced feature, which here is also the
        // operationally right default: the screens exist for the event days, and until the operator
        // switches this on there is no reason to ask Zoho for the whole agenda every 5 minutes.
        //
        // 🔒 ONE switch, deliberately. §754 §10's on/off controls are per VIEW and per ORIENTATION —
        // they decide what a screen displays. This one decides whether the cache is refreshed at
        // all. Wiring the views to this key as well would recreate the two-switch trap: a GUI that
        // says a view is ON while nothing behind it is running.
        new("signage-agenda-sync", "Settings.Feat.SignageAgendaSync.Name",
            "Settings.Feat.SignageAgendaSync.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.Engine),

        // §26c "Help Promote": email speakers when their promo graphics are released,
        // pointing them to /Speaker/Graphics. Ring-scoped + off by default.
        // §694.1 — GROUPED UNDER SPEAKERS, NOT EMAIL (operator 2026-07-29: "this one should be
        // under speaker role"). Grouping by DELIVERY MECHANISM put every speaker-facing switch into
        // one long Email list, so an organizer asking "what do speakers get?" had to read a list
        // sorted by something they were not asking about. The mechanism is the least interesting
        // thing about it: this is a SPEAKER feature that happens to arrive by mail.
        //
        // 🔒 A group carries a lifecycle RING, so re-homing normally adopts the new group's ring.
        // This row keeps its own explicit per-feature override, which WINS over the group — so the
        // move changes where it is LISTED, not who receives it.
        new("speaker-graphics-promote", "Settings.Feat.SpeakerGraphicsPromote.Name",
            "Settings.Feat.SpeakerGraphicsPromote.Desc",
            FeatureGroup.SpeakersSessions, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // §26c: per-speaker ring gate for the Zoho Backstage speaker sync (create-only
        // API). UserImpact so it's ring-scoped; off by default; speakers at a ring above
        // the released ring are held (never synced) until promoted.
        new("backstage-speaker-sync", "Settings.Feat.BackstageSpeakerSync.Name",
            "Settings.Feat.BackstageSpeakerSync.Desc",
            FeatureGroup.SpeakersSessions, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // --- Speakers & sessions --------------------------------------------
        // GA (operator 2026-06-22): released to Broad — runs for everyone, unscoped.
        new("sessionize-import", "Settings.Feat.Sessionize.Name",
            "Settings.Feat.Sessionize.Desc",
            FeatureGroup.SpeakersSessions, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // §38e (operator 2026-06-25): an automatic engine that detects when a session's
        // TIME or LOCATION changed in Zoho Backstage vs what CEH stored, and EMAILS the
        // affected speaker(s). USER-IMPACT (a speaker receives mail) ⇒ ring-scoped; off
        // by default; born at Ring1 so ring-0/ring-1 testers exercise it NOW. Depends on
        // outbound email. (§234, 2026-07-07: the extra broad-rings DATE gate
        // (FeatureSetting.ActiveFromForBroadRings = 1 Dec 2026) was retired — dead code
        // since §59 moved the speaker email to the operator-approved queue apply step.)
        // 🔴🔴 §1020 — THIS NO LONGER CONTROLS THE ZOHO→CEH SYNC. IT IS THE SPEAKER MAIL, ONLY.
        //
        // Operator 2026-08-09: *"Zoho→CEH kill switch - disable/remove this from settings totally
        // and leave it off in the code. we cannot have anyone turn this on by mistake."*
        //
        // 🔑 The key had TWO jobs and that was the danger: it gated the Zoho→CEH engine (§1000) AND
        // owns the `session-time-location-changed` mail. Deleting it outright would have removed a
        // PARTICIPANT MAIL from the settings page, breaking his standing rule that *"everything
        // targetting one of the roles must be defined in settings, no exception"*. So the two were
        // severed instead: the sync is hard-off in code with NO switch (see
        // SessionChangeDetectionService), and what remains here controls only whether a speaker is
        // told their room or time changed — a mail that §1004 now sends from the CEH editor.
        //
        // ⚠️ The KEY is deliberately unchanged. Renaming it would orphan the existing
        // FeatureSettings rows and the EmailTemplateCatalog mapping for no gain; the NAME and
        // DESCRIPTION are what an operator reads, and both now say what it really does.
        new("session-change-alerts", "Settings.Feat.SessionChangeAlerts.Name",
            "Settings.Feat.SessionChangeAlerts.Desc",
            FeatureGroup.SpeakersSessions, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // §38e/§58 (operator 2026-06-26): the SPEAKER analogue of session-change-alerts —
        // an engine that detects when a speaker's name/tagline/bio/country/social changed in
        // Zoho Backstage vs what CEH stored, and ENQUEUES the change to the §59 approval queue
        // (it never emails or auto-applies; the operator approves it). Off by default. A
        // Queue-surface engine (kill switch only, not ring-scoped) — its effect reaches a user
        // only after an operator approves the queued delta. Gated additionally on the §58
        // SPEAKER sync direction being stage 3 (ZohoToCeh).
        new("speaker-change-alerts", "Settings.Feat.SpeakerChangeAlerts.Name",
            "Settings.Feat.SpeakerChangeAlerts.Desc",
            FeatureGroup.SpeakersSessions, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.Queue),

        // §871 — the switch for the daily "Speaker detail gaps (Backstage)" mail. Operator
        // 2026-08-05: "it could be nice to have a button to DISABLE this one, as i expect us to do
        // that soon".
        //
        // ⚠️ §623 deliberately left that job UN-gated. This reverses it, and the reason is §871.1:
        // Backstage does not return Country at all, so the mail lists it for every speaker forever
        // and can never be satisfied.
        // 🔒 DefaultEnabled stays TRUE — it reports real gaps today, and switching it off must be
        // HIS decision rather than a default that quietly hides one.
        new("speaker-gap-report", "Settings.Feat.SpeakerGapReport.Name",
            "Settings.Feat.SpeakerGapReport.Desc",
            FeatureGroup.SpeakersSessions, FeatureTier.Advanced, DefaultEnabled: true,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.Queue),

        // --- Sponsors -------------------------------------------------------
        // GA (operator 2026-06-22): tested backend syncs — released to Broad, unscoped.
        new("backstage-sync", "Settings.Feat.BackstageSync.Name",
            "Settings.Feat.BackstageSync.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // economic-erp-sync feature REMOVED (§252 gap audit F7, 2026-07-07): it was
        // an inert DUPLICATE toggle — the ERP reconcile job actually gates on
        // erp-webshop-reconcile (below), so Settings showed two switches where one
        // did nothing (GUI ≠ behavior).

        // GA (operator 2026-06-22): tested backend pull — released to Broad, unscoped.
        new("sponsor-order-pull", "Settings.Feat.SponsorOrderPull.Name",
            "Settings.Feat.SponsorOrderPull.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // ENGINE (operator 2026-06-24): after the order pull, create/link the Zoho
        // Backstage sponsor + exhibitor records from webshop data (replaces the legacy
        // PowerShell sync). Off by default — enable to let CEH own the create flow.
        // §1165 — the sponsor swag catalogue page. OFF by default: operator 2026-09-01, *"page must
        // be hidden in menu for now until i have approved it"*. While off there is no nav entry and a
        // sponsor who reaches the URL is told it is not available; an organizer still sees it, with a
        // preview banner, because he cannot approve what he cannot open.
        new("sponsor-swag-catalog", "Settings.Feat.SponsorSwagCatalog.Name",
            "Settings.Feat.SponsorSwagCatalog.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        new("sponsor-zoho-provision", "Settings.Feat.SponsorZohoProvision.Name",
            "Settings.Feat.SponsorZohoProvision.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // ENGINE (operator 2026-06-24): scheduled ERP→webshop contact reconcile (the C#
        // port of Sync-ERP-Contacts-to-Webshop.ps1). Off by default — the org enables it
        // to retire the legacy script. The service self-guards on e-conomic+CM config.
        new("erp-webshop-reconcile", "Settings.Feat.ErpWebshopReconcile.Name",
            "Settings.Feat.ErpWebshopReconcile.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // §786 ENGINE (operator 2026-08-04): hourly webshop order → e-conomic DRAFT invoice (the C#
        // port of Sync-Webshop-Orders-Create-ERP-Invoice.ps1). 🔒 OFF by default, and turning it ON
        // IS THE CUTOVER — the scheduled script must be switched off first, or two systems are
        // invoicing the same orders and only the shared WebshopOrderId-<n> marker is stopping a
        // duplicate (§786.2). Same shape as erp-webshop-reconcile above, for the same reason.
        new("webshop-erp-invoicing", "Settings.Feat.WebshopErpInvoicing.Name",
            "Settings.Feat.WebshopErpInvoicing.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // §787 ENGINE (operator 2026-08-04): hourly claimed-COUPON ticket → e-conomic DRAFT invoice
        // (the C# port of Create-ERP-Invoice-Coupon-Tickets.ps1). 🔒 OFF by default.
        // ⚠️ Unlike webshop-erp-invoicing above, turning this on is NOT a cutover race: the retired
        // coupon script has never run (§787.5), so there is no second system to switch off first.
        // What it DOES need first is the coupon mappings on /Organizer/CouponInvoicing — without
        // them every claim is reported as unmapped instead of invoiced.
        // 🔒 Invoicing:DryRun (default TRUE) still holds every write even once this is on (§788).
        new("coupon-erp-invoicing", "Settings.Feat.CouponErpInvoicing.Name",
            "Settings.Feat.CouponErpInvoicing.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // ENGINE (operator 2026-06-22): a backend export of leads to the sponsor (today
        // just a Zoho Backstage link — no API yet). Kill-switch only, NOT ring-scoped.
        new("sponsor-leads", "Settings.Feat.SponsorLeads.Name",
            "Settings.Feat.SponsorLeads.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // §1077 stage 2 ENGINE (operator 2026-08-11): when a company reaches ten attendees, ask the
        // organizer mailbox who approves its volume-package benefits. 🔒 OFF by default — outbound
        // mail is the part he said he wants to approve before it runs, and the COUNTING is not gated
        // by this, so switching it off stops the asking and never stops the answer.
        // ⚠️ It never writes to the company: it names the suggested purchaser in a mail to info@.
        new("volume-package-approval-mail", "Settings.Feat.VolumePackageApprovalMail.Name",
            "Settings.Feat.VolumePackageApprovalMail.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // §1077 stage 3 (operator 2026-08-11) — the invitation that carries the wizard link to the
        // COMPANY's approver. 🔴 OFF by default, and the reason is sharper than for the stage-2
        // switch above: that one mails info@, this one mails a customer. It is the exact line the
        // operator's "critical adjustment … tested very detailed … approved before going into PROD"
        // was drawn around, and there is no job behind it — only a deliberate organizer click.
        new("volume-package-invite-mail", "Settings.Feat.VolumePackageInviteMail.Name",
            "Settings.Feat.VolumePackageInviteMail.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // §1077 stage 4 — the WEEKLY reminder to a company that was invited and has not answered.
        // 🔴 OFF by default, and of the three volume-package switches this is the one to be most
        // careful with: the other two send when a human decides, this one sends on a schedule.
        // 🔒 DependsOn the invitation: a reminder without a first mail is not a reminder, and a
        // company can only be chased about something it was actually asked.
        // §1080 — MASS MAIL. 🔴 The single most dangerous switch in the hub: campaigns are the only
        // thing that can write to thousands at once, and one audience reaches people who are not
        // participants at all — whom the transport's ring gate exists to refuse.
        // 🔒 OFF by default, and the switch is only the FIRST of three guards: a send also needs an
        // ACKNOWLEDGED dry-run (a person has seen the count and a sample) and a per-recipient
        // suppression check at send time. His decision, 2026-08-12.
        new("mail-campaigns", "Settings.Feat.MailCampaigns.Name",
            "Settings.Feat.MailCampaigns.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        // §1077.9 — the post-event thank-you with the picture link and the LinkedIn tagging ask.
        // 🔒 OFF by default and sent by a PERSON: "the event is over" is not a date the hub should
        // infer, because the gallery has to exist first and nothing here can check that.
        new("volume-package-post-event-mail", "Settings.Feat.VolumePackagePostEvent.Name",
            "Settings.Feat.VolumePackagePostEvent.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad),

        new("volume-package-reminders", "Settings.Feat.VolumePackageReminders.Name",
            "Settings.Feat.VolumePackageReminders.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { "volume-package-invite-mail" }, DefaultReleasedToRing: Ring.Broad),


        // --- Social media (category 2: ENGINE-QUEUED dispatch — GA/Broad, never
        // ring-scoped, but inert until the SoMe queue commits scoped posts) -------
        new("some-scheduling", "Settings.Feat.SoMe.Name",
            "Settings.Feat.SoMe.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.EngineQueued),

        new("linkedin-queue", "Settings.Feat.LinkedIn.Name",
            "Settings.Feat.LinkedIn.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { "some-scheduling" }, DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.EngineQueued),

        // Content Studio (§31): generate WordPress DRAFT posts (+ LinkedIn short text)
        // from ticket-sales telemetry & master-class data. Engine surface (an organizer
        // tool that produces DRAFTS only — nothing publishes, no per-user impact), GA-safe,
        // default OFF so it's opt-in.
        new("content-studio", "Settings.Feat.ContentStudio.Name",
            "Settings.Feat.ContentStudio.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.Engine),

        // --- Surveys --------------------------------------------------------
        new("surveys", "Settings.Feat.Surveys.Name",
            "Settings.Feat.Surveys.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact, TileOnly: true),

        // --- Reminders / digests --------------------------------------------
        // RULE 2: reminders + digests are OUTBOUND EMAIL ⇒ released to ring 1 only.
        new("reminder-jobs", "Settings.Feat.ReminderJobs.Name",
            "Settings.Feat.ReminderJobs.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        new("digest-emails", "Settings.Feat.DigestEmails.Name",
            "Settings.Feat.DigestEmails.Desc",
            FeatureGroup.SpeakersSessions, FeatureTier.Advanced, DefaultEnabled: false,
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

        // §326ay (operator 2026-07-25: "i also propose we simplify and disable the webhook
        // so we only have 1 sync routine") — the real-time webhook DRAIN, split out of
        // attendee-reconcile so it can be switched off WITHOUT stopping the full sync.
        // DEFAULT OFF. The drain ran its own reconcile every minute through
        // SyncOrderAsync, which has none of the §326ao/§326aq/§326as guards the full sync
        // has — six times more often, and unprotected. With one reconcile path there is one
        // place to reason about and one place to guard. Cost: a purchase or cancellation
        // shows up within 10 minutes instead of ~1. The webhook RECEIVER keeps queueing
        // rows either way, so nothing is lost and switching this back on replays them.
        new(WebhookDrainKey, "Settings.Feat.ZohoWebhookDrain.Name",
            "Settings.Feat.ZohoWebhookDrain.Desc",
            FeatureGroup.Attendees, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { "attendee-reconcile" }, DefaultReleasedToRing: Ring.Broad),

        // 🔒 §707.13 — THE RECEIVER, which until now had NO control on any page. Operator
        // 2026-07-30, after reading "Zoho webhook drain — FEATURE OFF" on the Jobs page and
        // reasonably concluding webhooks were off: *"ok, then we need the receiver in the portal
        // as well (settings)"*.
        //
        // The DRAIN (above) and the RECEIVER are different halves. Switching the drain off stops
        // queued events being APPLIED; the endpoint keeps ACCEPTING Backstage's POSTs and writing
        // queue rows — verified live on 2026-07-30: 2 pending, 0 ever processed. Nothing was
        // wrong, but the only visible switch described half the system, and the queue grows
        // unattended (harmless at 2 rows, less so at ticket-launch volume).
        //
        // Until now the receiver's only control was the `Zoho__WebhookEnabled` APP SETTING on the
        // Functions host — invisible here and changeable only by a deploy.
        //
        // ⚠️ DEFAULTS **OFF**, and that IS a behaviour change on the day it deploys: the
        // `Zoho__WebhookEnabled` app setting is currently true, so Backstage POSTs are being
        // accepted and queued right now. After this, they get a clean 200 no-op and nothing is
        // queued until the operator switches it on here.
        //
        // 🔑 That is the state he believes he is already in ("we have turned off webhooks, as i was
        // worried of the impact"), and the 10-minute full pull is VERIFIED sufficient on its own —
        // his 2026-07-30 purchase was queued by the receiver, never drained (2 pending, 0 ever
        // processed), and the pull handled it two minutes later. It also stops the queue growing
        // unattended, which matters at ticket-launch volume.
        //
        // Effective rule stays `app setting AND this switch`, so the app setting remains a
        // deploy-level kill and this is the day-to-day control.
        new(WebhookReceiverKey, "Settings.Feat.ZohoWebhookReceiver.Name",
            "Settings.Feat.ZohoWebhookReceiver.Desc",
            FeatureGroup.Attendees, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { "attendee-reconcile" }, DefaultReleasedToRing: Ring.Broad),

        // attendee-welcome feature REMOVED (operator 2026-06-23): there is no separate
        // attendee welcome — attendees receive only the Master Class confirmed-seat
        // mail (masterclass-confirmed). The attendee-missing-* chasers ride on
        // attendee-reconcile.

        // §242 (operator 2026-07-07): the whole 1-DAY ATTENDEE hub experience is
        // SUSPENDED behind this flag (default OFF — "we might open up for this later").
        // While OFF: 1-day tickets are STILL mirror-synced (attendee-reconcile), but no
        // 1-day login participant is provisioned, the welcome-attendee-1day send is
        // skipped, their party task/reminders stop, and EXISTING 1-day-only logins are
        // locked out (reversibly — the sync's ReconcileOneDayAccessAsync sweep restores
        // them when this turns back ON). 2-day holders are entirely unaffected.
        // UserImpact: sign-in + welcome + tasks are all things the person notices.
        new("attendee-1day-access", "Settings.Feat.Attendee1DayAccess.Name",
            "Settings.Feat.Attendee1DayAccess.Desc",
            FeatureGroup.Attendees, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // --- USER-IMPACT GUI actions, ring-tested before GA ---------------------
        // (operator 2026-06-22) Every GUI action that a person NOTICES happening to
        // them — a mass email, a task that appears, an assignment, an account being
        // provisioned — is a USER-IMPACT feature, declared at Ring1 so only ring-1
        // testers see + exercise it, promoted to Broad in /Organizer/Settings once
        // proven. No deploy springs these on a live event. These back the hub TILES
        // (HubTile.FeatureKey) so _HubGrid badges + gates them as the nav does.
        //
        // §700 Batch A — these used to sit in `Incubation` as a block. They are now
        // filed by ROLE (or EventSettings), which is what §695 asked for. The RING is
        // what protects the audience, and every one of them keeps the ring it had.

        // 🗑 §705.12 — "broadcast-email" and "invitation-email" DELETED 2026-07-29, with the pages,
        // templates and filters behind them.
        //
        // BROADCAST (operator: "you are welcome to delete broadcast as i will newer use it, as my
        // point i will build a new"). It was also the ONE mail that could never satisfy §705: its
        // subject, wording AND audience were all chosen at send time, so it could carry no fixed
        // name, no fixed subject and no meaningful per-role ring. Deleting it removes the only real
        // exception to "every mail has a subject, an internal name and a ring".
        //
        // INVITATION (operator: "same with invitation - delete it if not used" … "maybe it was an
        // early wording, we changed to welcome"). Exactly right, and the evidence agrees: it was an
        // early access-mail concept superseded by the welcome mails, which already carry a magic
        // link (§226).
        //
        // 🔒 VERIFIED UNUSED BEFORE DELETING, not assumed: PROD `SentReminders` held **zero rows**
        // for both `invitation` and `broadcast` — neither had ever sent a single mail in the live
        // edition. `invitation-email` had one caller (the deleted page) and no job; its function is
        // covered three times over by the welcome magic link, `pin-signin`, and the `calendar-invite`
        // 1-year link (§169).
        //
        // ⚠️ "Broadcast" still exists in this codebase as a DIFFERENT concept — the Signal CHAT
        // broadcast group (`SignalGroupsConfig.BroadcastLabel`, the Signal wizard step). That is
        // unrelated and stays.

        // Re-send an arbitrary logged/templated email to people (Comms, Email log).
        new("email-resend", "Settings.Feat.EmailResend.Name",
            "Settings.Feat.EmailResend.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Bulk "redo this onboarding step" emails (Action queue).
        new("onboarding-step-reset", "Settings.Feat.OnboardingStepReset.Name",
            "Settings.Feat.OnboardingStepReset.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Activate / provision login-capable accounts (Pre-selection queue, bulk
        // participant ops) — enables sign-in for real people.
        new("participant-activation", "Settings.Feat.ParticipantActivation.Name",
            "Settings.Feat.ParticipantActivation.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact, TileOnly: true),

        // masterclass-invites feature REMOVED (§252 gap audit F5, 2026-07-07): the
        // whole Master Class email funnel (selection invite §241, confirmed,
        // waitlisted, cancelled §243, reassignment, offer, promotion) rides the ONE
        // welcome-email ring, so raising a single ring at go-live can never split
        // the funnel (invited but never confirmed, or confirmed-mail without invites).

        // Email session-evaluation (HappyOrNot) results to speakers.
        // §694.4 — GRADUATED out of Incubation (operator 2026-07-29: "if these are still active,
        // then they are placed wrong"). It emails SPEAKERS their evaluation results, so it belongs
        // with speakers, not in the group labelled "Incubation (test)" — which made shipped,
        // daily-use functionality read as provisional.
        new("session-eval-email", "Settings.Feat.SessionEvalEmail.Name",
            "Settings.Feat.SessionEvalEmail.Desc",
            FeatureGroup.SpeakersSessions, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // 🗑 §699 — "sponsor-welcome" DELETED 2026-07-29 (operator: "we must not have anything which
        // are not used, then delete it").
        //
        // §619 already established the finding and removed it from the declared-user-impact list:
        // "NO send site ever passed it: the sponsor welcome is sent through WelcomeEmailService and
        // is gated by 'welcome-email'. So its presence here declared a ring that governed nothing."
        // The CATALOG ENTRY was left behind, so /Organizer/Settings kept rendering a switch — with
        // an on/off and a ring — that controlled nothing at all. Verified again before deleting:
        // the ONLY two references in the whole solution were §619's comment and this declaration.
        //
        // 🔒 Removing it changes NO audience, because nothing consulted it. A control that does
        // nothing is worse than a missing one: it invites an operator to "fix" a mail problem by
        // flipping it, and then to trust the result.

        // Tasks created for sponsor companies (appear to all their contacts).
        new("sponsor-tasks", "Settings.Feat.SponsorTasks.Name",
            "Settings.Feat.SponsorTasks.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact, TileOnly: true),

        // Reminder emails to sponsors (App game).
        // §694.4 — GRADUATED into Sponsors. Live: gates the app-game gift reminder (§693).
        new("sponsor-reminders", "Settings.Feat.SponsorReminders.Name",
            "Settings.Feat.SponsorReminders.Desc",
            FeatureGroup.Sponsors, FeatureTier.Advanced, DefaultEnabled: false,
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
            FeatureGroup.Volunteers, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.Queue),

        new("volunteer-allocation", "Settings.Feat.VolunteerAllocation.Name",
            "Settings.Feat.VolunteerAllocation.Desc",
            FeatureGroup.Volunteers, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.Queue),

        // §150 ORGANIZER allocation queue — the organizer-side mirror of
        // volunteer-allocation (allocates ELDK / ELDK-MOK organizers to tasks). Same
        // QUEUE surface (category 3: organizer stages + Commits, ring-scoped impact at
        // commit; the task BECOMING VISIBLE to the organizer is the user-impact, gated
        // per target). Released to Broad (GA) so every target is in scope; lower it to
        // Ring1 in /Organizer/Settings to ring-TEST a change.
        new("organizer-allocation", "Settings.Feat.OrganizerAllocation.Name",
            "Settings.Feat.OrganizerAllocation.Desc",
            FeatureGroup.Organizers, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.Queue),

        new("hotel-assignment", "Settings.Feat.HotelAssignment.Name",
            "Settings.Feat.HotelAssignment.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Broad,
            Surface: FeatureSurface.Queue),

        // Group-photo session invite emails.
        // §700 Batch A — re-homed to EventSettings, named explicitly by the operator (§695):
        // "move the group photo into Event settings". 🔒 This is the ONE the §694.4 hazard was
        // written about — his screenshot read "(inherited from group)", and it does inherit: it
        // has a PROD FeatureSettings row with a NULL ReleasedToRingOverride. Verified before
        // moving: there are ZERO FeatureGroupSettings rows in either edition, so the inheritance
        // step resolves past the group to this catalog default (Ring1) both before and after.
        // Effective ring unchanged.
        new("group-photo-invites", "Settings.Feat.GroupPhotoInvites.Name",
            "Settings.Feat.GroupPhotoInvites.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Travel-reimbursement payment confirmation emails.
        // §694.4 — GRADUATED into SpeakersSessions (operator 2026-07-29: "this one is fo speaker").
        // Travel reimbursement is a SPEAKER deliverable — it has its own speaker deadline task
        // (§679) — so the confirmation belongs beside the rest of the speaker features.
        new("travel-reimbursement-email", "Settings.Feat.TravelReimbursementEmail.Name",
            "Settings.Feat.TravelReimbursementEmail.Desc",
            FeatureGroup.SpeakersSessions, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // 🗑 §705.14a — "graphics-release" DELETED 2026-07-29. The operator asked *"graphics-release is
        // an email to speakers"* … *"or does it target something else"*. It targets something else, and
        // the NAME was the defect: its only two references were this entry and ONE organizer tile
        // (/Organizer/SoMe → /Organizer/Graphics). It sent no mail at all.
        //
        // The email to speakers DOES exist, under a different key: `speaker-graphics-promote` →
        // template `speaker-graphics-ready` ("Speaker: promo graphics ready"), sent by
        // SpeakerGraphicsReadyNotifier. That one keeps its own ring.
        //
        // 🔒 So this was §326bx BY NAME rather than by ring: the yellow ring badge was already removed
        // from TileOnly switches, but a name implying it governed the speaker mail was left behind —
        // and it misled its own author. Per his EmailCenter correction, organizer tooling is gated by
        // ROLE, not by a switch, so the tile needs no key.

        // Bulk delete of test participants / data — impactful, ring-test only.
        new("test-data-cleanup", "Settings.Feat.TestDataCleanup.Name",
            "Settings.Feat.TestDataCleanup.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: Array.Empty<string>(), DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact, TileOnly: true),

        // §383 — Master Class landing-page notifications (a Q&A post, or a speaker updating the
        // preparation instructions). UserImpact: an attendee/speaker receives mail because SOMEONE
        // ELSE acted, so it is outreach and IS ring-scoped — the §326by participant-clicked
        // exemption deliberately does not apply. The per-person opt-out lives on the page itself.
        new("masterclass-notifications", "Settings.Feat.MasterClassNotifications.Name",
            "Settings.Feat.MasterClassNotifications.Desc",
            FeatureGroup.Attendees, FeatureTier.Advanced, DefaultEnabled: false,
            DependsOn: new[] { OutboundEmailKey }, DefaultReleasedToRing: Ring.Ring1,
            Surface: FeatureSurface.UserImpact),

        // Attendee calendar invites — independently dialable per email (operator 2026-06-22).
        new("hotel-invite", "Settings.Feat.HotelInvite.Name",
            "Settings.Feat.HotelInvite.Desc",
            FeatureGroup.EventSettings, FeatureTier.Advanced, DefaultEnabled: false,
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

    /// <summary>
    /// §700 Batch B — the order groups are RENDERED in: roles first, <see cref="FeatureGroup.EventSettings"/>
    /// last. Lower sorts earlier.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Deliberately NOT the enum's numeric order.</b> §695 asks for a per-ROLE page, but the enum is
    /// append-only — Batch A's three groups landed at 8/9/10 and would otherwise render after every
    /// mechanism group. Renumbering the enum would fix the order and is exactly what must NOT happen:
    /// <c>FeatureSetting.GroupOverride</c> and <c>FeatureGroupSetting.Group</c> persist these as ints, so
    /// renumbering silently re-points any stored row at a different group. That is safe TODAY only
    /// because no such row exists in either edition (Batch A verified it) — luck, not a guarantee.
    /// Display order belongs in the display layer.
    ///
    /// <para>The mechanism groups (Email, Reminders, Social media, Surveys) are not roles and sort after
    /// the roles. Re-homing their members is an audience-affecting change and is not part of the IA.</para>
    /// </remarks>
    public static int DisplayOrder(FeatureGroup group) => group switch
    {
        FeatureGroup.SpeakersSessions => 0,
        FeatureGroup.Sponsors         => 1,
        FeatureGroup.Volunteers       => 2,
        FeatureGroup.Attendees        => 3,
        FeatureGroup.Organizers       => 4,
        // §695 — the four MECHANISM groups are DELETED (see the enum); nothing to order.
        FeatureGroup.EventSettings    => 20,
        _                             => 99,
    };

    /// <summary>The catalog grouped by chapter, in display order, groups in enum order.</summary>
    public static IReadOnlyList<IGrouping<FeatureGroup, FeatureDescriptor>> ByGroup() =>
        All.GroupBy(f => f.Group)
           .OrderBy(g => (int)g.Key)
           .ToList();

    /// <summary>
    /// The DEFAULT lifecycle ring for a feature GROUP (REQUIREMENTS §23a) — the
    /// initial value shown for the group's ring control until an organizer sets a
    /// per-group ring.
    ///
    /// 🔒 <b>EVERY group returns <see cref="Ring.Ring1"/></b> — there is no per-group
    /// variation. (The previous summary here claimed Email + Reminders at Ring1,
    /// Incubation at Ring0 and everything else at Broad. That was never what the code
    /// did; it was left behind by the operator's 2026-06-21 "default is ring 1"
    /// decision, and it is the §700/§ceh-decision-vs-hold shape: a doc that reads as
    /// settled while the code says otherwise. Corrected 2026-07-29.)
    ///
    /// NOTE: the runtime gate uses an explicit per-group ring ROW if set, else the
    /// FEATURE's own catalog default — this is for DISPLAY + the group control's
    /// starting point, not a hidden gate input.
    /// </summary>
    public static Ring GroupDefaultRing(FeatureGroup group) => group switch
    {
        // Operator 2026-06-21: ALL groups default to Ring1 — controlled-rollout posture
        // before go-live, so ring-0 AND ring-1 testers see every feature until an
        // organizer promotes it (group or feature) up to Broad for general availability.
        //
        // 🔑 §700 Batch A relies on this being UNIFORM: because no group has a different
        // default, and no edition has a persisted FeatureGroupSettings row, re-homing a
        // feature between groups cannot change its effective ring.
        _ => Ring.Ring1,
    };
}
