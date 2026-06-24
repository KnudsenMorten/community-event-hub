using CommunityHub.Core.Settings;

namespace CommunityHub.Core.Domain;

/// <summary>
/// One participant, scoped to one event edition. The email is the identity
/// used for PIN login (CONTEXT.md section 5). A person attending two editions
/// has two Participant rows - one per EventId.
/// </summary>
public class Participant
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    /// <summary>The edition this profile belongs to. Every query is scoped by this.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- Identity -----------------------------------------------------------
    /// <summary>
    /// Login identity. Stored lower-cased and trimmed. Unique within an edition.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    /// <summary>Optional - phone, for organizer contact only.</summary>
    public string? Phone { get; set; }

    /// <summary>
    /// Optional EXTRA address for this person. When set, every email the hub
    /// sends to this participant also goes here as a <b>CC</b> (a colleague's
    /// inbox, a shared team alias, a personal backup address …). It is purely
    /// ADDITIVE: the primary recipient is still the effective address
    /// (a speaker's <see cref="SpeakerProfile.ContactEmailOverride"/> ?? this
    /// <see cref="Email"/> identity), and this is layered on top as CC. Distinct
    /// from the speaker override (#24), which CHANGES the primary To — this never
    /// changes the primary, it only adds a copy. Addable at onboarding (organizer)
    /// or later by the person in their hub profile. Null/blank = no extra CC.
    /// </summary>
    public string? SecondaryEmail { get; set; }

    /// <summary>
    /// Optional ALTERNATE LOGIN address (operator 2026-06-24, §26d). A person can
    /// sign in (PIN / magic-link request) with EITHER their primary <see cref="Email"/>
    /// OR this address — useful when someone registered with a community address but
    /// uses a work address day-to-day. Stored lower-cased + trimmed. The resolved
    /// participant's primary <see cref="Email"/> remains the canonical identity (the
    /// cookie + all downstream keys still use it); this only widens what you can type
    /// to log in. Distinct from <see cref="SecondaryEmail"/> (CC-only, not a login).
    /// </summary>
    public string? AlternateEmail { get; set; }

    // --- Role ---------------------------------------------------------------
    /// <summary>Admin-set. Drives the personalized hub. One role per person.</summary>
    public ParticipantRole Role { get; set; }

    /// <summary>
    /// For a Sponsor-role participant: the WooCommerce / Company Manager
    /// company id (the order's _cm_company_id) this contact belongs to. Lets
    /// the sponsor area show and edit only this company's tasks. Null for
    /// non-sponsor participants, and for sponsors whose company is not yet
    /// known. There is no company *entity* in the hub - this is just the
    /// external id carried for scoping (CONTEXT.md 11g).
    /// </summary>
    public string? SponsorCompanyId { get; set; }

    /// <summary>
    /// For a Sponsor-role contact: the Company Manager / WordPress <c>user_id</c>
    /// — the <b>unique identifier that links this hub contact back to its Company
    /// Manager user</b> (the canonical id-link the operator's ERP→webshop sync
    /// uses; CM links a user by <c>user_id</c> and a company by <c>company_id</c>,
    /// never by name). Written at sync time by
    /// <see cref="Integrations.SponsorContactSyncService"/> from the CM
    /// <c>/companies/{id}/users</c> response. Correlation back to CM (e.g. the
    /// recipient resolver) prefers this id over email — email is reserved only for
    /// the ERP→CM bridge where e-conomic and WordPress share no common id. Null for
    /// non-sponsor participants and for sponsors not yet synced from CM. Tolerant
    /// parse: CM returns it as a string ("68") on <c>/companies/{id}/users</c> and a
    /// number (68) on <c>/users/{id}</c> — both resolve to this int.
    /// </summary>
    public int? CmUserId { get; set; }

    // --- Sponsor-contact roles (independent flags) --------------------------
    /// <summary>
    /// For a Sponsor-role contact: this person is a <b>signer</b> for the company
    /// (they sign the contract / approve the order). Independent of
    /// <see cref="IsEventCoordinator"/> — a contact can be both. <b>Signer-only
    /// contacts never receive sponsor mail</b> (the universal sponsor-email
    /// audience rule, REQUIREMENTS §7c): the shared
    /// <c>SponsorRecipientResolver</c> selects recipients purely on
    /// <see cref="IsEventCoordinator"/>. Default false. Sourced from Company
    /// Manager's company-level <c>default_signer_id</c> at sync time (CM exposes
    /// no per-user roles — see REQUIREMENTS §7c), and editable by an organizer.
    /// </summary>
    public bool IsSigner { get; set; }

    /// <summary>
    /// For a Sponsor-role contact: this person is an <b>event coordinator</b> for
    /// the company (they handle the event logistics / onboarding). Independent of
    /// <see cref="IsSigner"/> — a contact can be both, and a both-roles contact
    /// STILL receives sponsor mail because they are a coordinator. This flag is
    /// the SOLE audience selector for every sponsor email path (welcome/intro,
    /// sponsor-overdue, sponsor task-deadline-reminder, organizer sponsor
    /// broadcast) via <c>SponsorRecipientResolver</c> (REQUIREMENTS §7c). A
    /// company can have several coordinators. Default false. Sourced from Company
    /// Manager's company-level <c>event_coordination_default_contact_id</c> at
    /// sync time, and editable by an organizer.
    /// </summary>
    public bool IsEventCoordinator { get; set; }

    /// <summary>
    /// For a Sponsor-role contact: this person staffs the company's BOOTH at the
    /// event (an exhibitor present on site), as opposed to a purely digital /
    /// administrative sponsor contact. Drives the sponsor-hat order entitlements
    /// (<see cref="Entitlements.OrderEntitlements"/>): a booth member gets
    /// polo + dinner + main-day lunch, a non-booth sponsor gets nothing from the
    /// sponsor hat. Default false.
    /// </summary>
    public bool IsBoothMember { get; set; }

    /// <summary>
    /// Optional self-reference to the <see cref="Id"/> of the PRIMARY participant
    /// row this person is a duplicate of (the same physical person registered
    /// under more than one hat / email). When set, this row is NOT counted on its
    /// own in order tallies — the primary it points to represents them, so each
    /// physical person is counted exactly once
    /// (<see cref="Entitlements.OrderCountService"/>). Null = this row is itself a
    /// primary (the normal case). No cascade delete: removing the primary must not
    /// delete the duplicate row.
    /// </summary>
    public int? SamePersonAsId { get; set; }

    /// <summary>The primary participant this row duplicates (see <see cref="SamePersonAsId"/>).</summary>
    public Participant? SamePersonAs { get; set; }

    /// <summary>
    /// False = the person cannot log in (e.g. withdrew). Login checks this.
    /// This stays the deactivation / cancellation switch — orthogonal to
    /// <see cref="LifecycleState"/> (the onboarding pre-selection gate).
    /// </summary>
    public bool IsActive { get; set; } = true;

    // --- Onboarding lifecycle (pre-selection gate) --------------------------
    /// <summary>
    /// The onboarding pre-selection state: <c>Inactive → Preselected → Active</c>
    /// (default <see cref="ParticipantLifecycleState.Inactive"/>). A person lands
    /// in the pre-selection queue inactive/preselected; an organizer validates +
    /// activates them. <b>Login requires BOTH <see cref="IsActive"/> AND
    /// <see cref="LifecycleState"/> == Active</b>, so a not-yet-activated queue
    /// entry cannot sign in. Distinct from <see cref="IsActive"/>, which is the
    /// withdrawal/cancellation switch.
    /// </summary>
    public ParticipantLifecycleState LifecycleState { get; set; }
        = ParticipantLifecycleState.Inactive;

    /// <summary>
    /// Where this participant row entered the hub (organizer / Sessionize sync /
    /// volunteer interest form / media-team sign-up). Drives the pre-selection
    /// queue's source column + filter. Default <see cref="ParticipantQueueSource.Manual"/>.
    /// </summary>
    public ParticipantQueueSource QueueSource { get; set; }
        = ParticipantQueueSource.Manual;

    // --- Onboarding wizard per-step completion flags ------------------------
    /// <summary>Onboarding step (a): the participant verified / updated their bio.</summary>
    public bool OnboardingCompleted_Bio { get; set; }

    /// <summary>Onboarding step (b): the participant updated / replaced their bio picture.</summary>
    public bool OnboardingCompleted_Picture { get; set; }

    /// <summary>Onboarding step (c): the participant completed the hotel form.</summary>
    public bool OnboardingCompleted_Hotel { get; set; }

    /// <summary>Onboarding step (d): the participant completed the appreciation form.</summary>
    public bool OnboardingCompleted_Appreciation { get; set; }

    /// <summary>Onboarding step (e): the participant completed the swag form.</summary>
    public bool OnboardingCompleted_Swag { get; set; }

    // --- Per-step completed-at timestamps -----------------------------------
    // When each onboarding step was last marked complete (null = not done).
    // Stamped by OnboardingService.MarkStepCompleteAsync alongside the bit, and
    // cleared back to null when an organizer re-opens the step. Gives the admin
    // overview an honest "when did they finish this" signal without inferring it
    // from a separate audit table.
    /// <summary>When step (a) bio was completed (null = not done).</summary>
    public DateTimeOffset? OnboardingCompleted_BioAt { get; set; }

    /// <summary>When step (b) picture was completed (null = not done).</summary>
    public DateTimeOffset? OnboardingCompleted_PictureAt { get; set; }

    /// <summary>When step (c) hotel was completed (null = not done).</summary>
    public DateTimeOffset? OnboardingCompleted_HotelAt { get; set; }

    /// <summary>When step (d) appreciation was completed (null = not done).</summary>
    public DateTimeOffset? OnboardingCompleted_AppreciationAt { get; set; }

    /// <summary>When step (e) swag was completed (null = not done).</summary>
    public DateTimeOffset? OnboardingCompleted_SwagAt { get; set; }

    /// <summary>
    /// True only when EVERY onboarding step bit is set. Convenience for the raw
    /// "all five steps" count. <b>NOTE:</b> persona-aware completion (a persona
    /// only needs ITS required steps) is computed by
    /// <c>OnboardingStepSets.IsComplete</c> / <c>OnboardingService</c>, not here —
    /// this property is the all-steps superset and is not persisted.
    /// </summary>
    public bool IsFullyOnboarded =>
        OnboardingCompleted_Bio
        && OnboardingCompleted_Picture
        && OnboardingCompleted_Hotel
        && OnboardingCompleted_Appreciation
        && OnboardingCompleted_Swag;

    // --- Multi-hotel placement ---------------------------------------------
    /// <summary>
    /// The <see cref="Hotel"/> this participant has been placed in by an
    /// organizer (null = not yet assigned to any hotel). Because room blocks are
    /// split across several hotels, organizers assign each person to one hotel
    /// and then view/manage everyone grouped by hotel. Distinct from
    /// <see cref="HotelBooking"/>, which is the person's own room-need / dates
    /// preference; this is the physical venue they are placed in.
    /// </summary>
    public int? HotelId { get; set; }
    public Hotel? Hotel { get; set; }

    /// <summary>
    /// The per-person hotel confirmation / reservation number, set by an
    /// organizer once the hotel returns it. Surfaced to the participant in their
    /// hotel/onboarding email alongside the assigned hotel name + address. Null =
    /// not yet confirmed. Distinct from <see cref="HotelBooking.ConfirmationNumber"/>
    /// (the legacy single-vendor field); this travels with the multi-hotel
    /// placement model.
    /// </summary>
    public string? HotelConfirmationNumber { get; set; }

    /// <summary>
    /// True = this row is test/dummy data, not a real participant. Lets go-live
    /// cleanup delete or deactivate everything WHERE IsTestUser = true without
    /// touching real registrations. Default false (real participant).
    /// </summary>
    public bool IsTestUser { get; set; }

    // --- Release ring (controlled-rollout access level, REQUIREMENTS §23) ----
    /// <summary>
    /// This person's RELEASE RING — their access level for the progressive
    /// feature rollout. Lower ring = earlier access. A feature is active for this
    /// person iff it is enabled (not killed) AND this person's <b>effective ring</b>
    /// is ≤ the feature's released-to ring.
    ///
    /// For a <b>sponsor contact</b> this is the CONTACT-level ring, which
    /// SUPERSEDES their company's default ring (<see cref="SponsorInfo.Ring"/>);
    /// the effective ring is <c>contact.Ring ?? company.Ring ?? Broad</c>. For
    /// every other role there is no company default, so the effective ring is
    /// simply this value.
    ///
    /// Defaults to <see cref="Ring.Broad"/> (general availability): an unassigned
    /// person sees ONLY fully-released features, and a feature released to Broad
    /// (the default) is visible to everyone — so today's behaviour is unchanged.
    /// Assigning Ring0/Ring1 IS the "let this person see test features" override
    /// (it replaces the old per-user test-on flag).
    /// </summary>
    public Ring Ring { get; set; } = Rings.Default;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>
    /// Per-participant secret for the subscribable iCal calendar feed
    /// (<c>GET /calendar/{token}.ics</c>). Unguessable, URL-safe; a calendar
    /// client fetches the feed with this token instead of a login session, so
    /// it is a bearer secret scoped to exactly this participant's own items.
    /// Null = the participant has never opened the calendar section (the token
    /// is minted lazily on first view). Regenerating it (revoke) replaces the
    /// value so a previously-shared URL stops resolving. Stored unique so a
    /// token resolves to exactly one participant.
    /// </summary>
    public string? CalendarFeedToken { get; set; }

    /// <summary>
    /// When the participant first saw (and acknowledged) the welcome landing
    /// page. Null = they have never been redirected through /Welcome — the hub
    /// landing page will bounce them there on next visit. Set to UtcNow when
    /// they click "Continue" on /Welcome.
    /// </summary>
    public DateTimeOffset? WelcomeShownAt { get; set; }

    /// <summary>
    /// When the role-aware <b>welcome email with one-click auto-login</b> was
    /// last sent to this participant (DEV-only sender — see
    /// <c>WelcomeWithLoginEmailService</c>). Null = it has never been sent.
    /// Unlike the legacy once-ever welcome (recorded in <c>SentReminder</c>),
    /// this send is deliberately <b>re-sendable for testing</b>: each send
    /// overwrites this stamp, so the column records <i>that</i> and <i>when</i>
    /// it was last sent (the "who was sent" audit) without gating a re-send.
    /// </summary>
    public DateTimeOffset? WelcomeWithLoginSentAt { get; set; }

    // --- Navigation ---------------------------------------------------------
    public ICollection<ParticipantTask> AssignedTasks { get; set; } = new List<ParticipantTask>();
    public ICollection<LoginPin> LoginPins { get; set; } = new List<LoginPin>();
}
