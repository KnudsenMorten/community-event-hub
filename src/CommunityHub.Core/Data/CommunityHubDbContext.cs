using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Data;

/// <summary>
/// The EF Core data model for the Community Hub. Stage 2 establishes the schema;
/// later stages add the forms (hotel, dinner, volunteer) and sponsor entities.
///
/// Every year-specific entity is scoped to an <see cref="Event"/> via EventId,
/// so an edition's data is isolated and ELDK28 is just a new Event row - no
/// schema change, no new deployment (CONTEXT.md section 3).
/// </summary>
public class CommunityHubDbContext : DbContext, IDataProtectionKeyContext
{
    public CommunityHubDbContext(DbContextOptions<CommunityHubDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// The ASP.NET DataProtection key ring, persisted in SQL so it is SHARED across
    /// deployment slots + restarts. Without a shared store the keys default to
    /// per-slot %HOME%, so every slot-swap deploy rotated them and invalidated all
    /// auth cookies (everyone logged out). Wired via PersistKeysToDbContext in Program.cs.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Event> Events => Set<Event>();
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<LoginPin> LoginPins => Set<LoginPin>();
    public DbSet<ParticipantTask> Tasks => Set<ParticipantTask>();
    public DbSet<SentReminder> SentReminders => Set<SentReminder>();
    public DbSet<EmailLog> EmailLogs => Set<EmailLog>();
    public DbSet<Attendee> Attendees => Set<Attendee>();

    /// <summary>Order-level mirror of the full Zoho Backstage dataset (REQUIREMENTS §125).
    /// One order has many tickets/attendees; reconciled strictly one-way Zoho→CEH.</summary>
    public DbSet<Order> Orders => Set<Order>();

    /// <summary>Last-successful-sync markers, one per (EventId, Key) — drives the
    /// telemetry "Updated &lt;t&gt;" footer (REQUIREMENTS §125/§127).</summary>
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    /// <summary>Fleet-wide per-job health markers (consecutive-failure counter) that drive
    /// the "alert only on 2 consecutive failures" gate for background jobs.</summary>
    public DbSet<JobHealthMarker> JobHealthMarkers => Set<JobHealthMarker>();
    public DbSet<HotelBooking> HotelBookings => Set<HotelBooking>();
    public DbSet<Hotel> Hotels => Set<Hotel>();
    public DbSet<DinnerSignup> DinnerSignups => Set<DinnerSignup>();
    public DbSet<DietaryRequirement> DietaryRequirements => Set<DietaryRequirement>();
    public DbSet<VolunteerAvailability> VolunteerAvailabilities => Set<VolunteerAvailability>();
    public DbSet<VolunteerDayAvailability> VolunteerDayAvailabilities => Set<VolunteerDayAvailability>();
    public DbSet<SwagPreference> SwagPreferences => Set<SwagPreference>();
    public DbSet<LunchSignup> LunchSignups => Set<LunchSignup>();
    public DbSet<SpeakerProfile> SpeakerProfiles => Set<SpeakerProfile>();
    public DbSet<ParticipantOrderOverride> ParticipantOrderOverrides => Set<ParticipantOrderOverride>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionSpeaker> SessionSpeakers => Set<SessionSpeaker>();
    public DbSet<SessionQuestion> SessionQuestions => Set<SessionQuestion>();

    /// <summary>Q&amp;A comments on a master class's attendee landing page (FEATURE 2).</summary>
    public DbSet<MasterClassComment> MasterClassComments => Set<MasterClassComment>();
    public DbSet<SessionEvaluation> SessionEvaluations => Set<SessionEvaluation>();
    public DbSet<SpeakerBackstageEmailSync> SpeakerBackstageEmailSyncs => Set<SpeakerBackstageEmailSync>();
    public DbSet<SessionizeEndpointSetting> SessionizeEndpointSettings => Set<SessionizeEndpointSetting>();
    public DbSet<TravelReimbursement> TravelReimbursements => Set<TravelReimbursement>();
    public DbSet<TravelReceipt> TravelReceipts => Set<TravelReceipt>();
    public DbSet<OrganizerActionItem> OrganizerActionItems => Set<OrganizerActionItem>();
    public DbSet<SponsorInfo> SponsorInfos => Set<SponsorInfo>();
    public DbSet<SponsorBoothMember> SponsorBoothMembers => Set<SponsorBoothMember>();
    public DbSet<SponsorBoothMaterial> SponsorBoothMaterials => Set<SponsorBoothMaterial>();
    public DbSet<SponsorUploadLocation> SponsorUploadLocations => Set<SponsorUploadLocation>();
    public DbSet<SponsorUploadFile> SponsorUploadFiles => Set<SponsorUploadFile>();
    public DbSet<SponsorUploadAudit> SponsorUploadAudits => Set<SponsorUploadAudit>();
    public DbSet<SurveyResponse> SurveyResponses => Set<SurveyResponse>();
    public DbSet<SurveyResponsePick> SurveyResponsePicks => Set<SurveyResponsePick>();
    public DbSet<SurveyState> SurveyStates => Set<SurveyState>();
    public DbSet<SponsorLead> SponsorLeads => Set<SponsorLead>();
    public DbSet<SponsorLeadNotificationPref> SponsorLeadNotificationPrefs => Set<SponsorLeadNotificationPref>();
    public DbSet<SponsorApiKey> SponsorApiKeys => Set<SponsorApiKey>();
    public DbSet<SponsorTokenVersion> SponsorTokenVersions => Set<SponsorTokenVersion>();
    public DbSet<GroupPhotoRegistration> GroupPhotoRegistrations => Set<GroupPhotoRegistration>();
    public DbSet<AppGameParticipation> AppGameParticipations => Set<AppGameParticipation>();
    public DbSet<ErpCustomerLink> ErpCustomerLinks => Set<ErpCustomerLink>();
    public DbSet<ErpOrderLink> ErpOrderLinks => Set<ErpOrderLink>();

    // --- SoMe graphics & SharePoint asset store (REQUIREMENTS §18) -----------
    public DbSet<GraphicAsset> GraphicAssets => Set<GraphicAsset>();
    public DbSet<GraphicsAssetLocation> GraphicsAssetLocations => Set<GraphicsAssetLocation>();

    // --- Organizer grid v2: secretary tokens + impersonation audit ----------
    public DbSet<ParticipantSecretaryToken> ParticipantSecretaryTokens => Set<ParticipantSecretaryToken>();
    public DbSet<ImpersonationAudit> ImpersonationAudits => Set<ImpersonationAudit>();

    /// <summary>Single-use, revocable, audited welcome auto-login link grants.</summary>
    public DbSet<MagicLinkGrant> MagicLinkGrants => Set<MagicLinkGrant>();

    /// <summary>Per-edition active SESSION source (Sessionize vs Zoho Backstage).</summary>
    public DbSet<SessionSourceSetting> SessionSourceSettings => Set<SessionSourceSetting>();

    /// <summary>Hub-side role + notes for e-conomic customer contacts (no native e-conomic field).</summary>
    public DbSet<EconomicContactAnnotation> EconomicContactAnnotations => Set<EconomicContactAnnotation>();

    /// <summary>Anonymous (no-login) Party RSVPs.</summary>
    public DbSet<PartyRsvp> PartyRsvps => Set<PartyRsvp>();

    /// <summary>Per-participant Code of Conduct + Privacy acceptance (§119, who/when).</summary>
    public DbSet<ParticipantPolicyAcceptance> ParticipantPolicyAcceptances => Set<ParticipantPolicyAcceptance>();

    /// <summary>In-hub Master Class seat + waitlist signups (CEH-owned; replaces Zoho Bookings).</summary>
    public DbSet<MasterClassSignup> MasterClassSignups => Set<MasterClassSignup>();

    /// <summary>Per-edition Master Class signup settings (offer-hold hours + promotion mode).</summary>
    public DbSet<MasterClassSettings> MasterClassSettings => Set<MasterClassSettings>();

    // --- LinkedIn company-page SoMe scheduling queue (REQUIREMENTS §19) -------
    public DbSet<SoMePost> SoMePosts => Set<SoMePost>();
    public DbSet<SoMeSettings> SoMeSettings => Set<SoMeSettings>();

    // --- Volunteer work structure (Category -> Subcategory -> Task) ----------
    public DbSet<VolunteerCategory> VolunteerCategories => Set<VolunteerCategory>();
    public DbSet<VolunteerSubcategory> VolunteerSubcategories => Set<VolunteerSubcategory>();
    public DbSet<VolunteerTask> VolunteerTasks => Set<VolunteerTask>();
    public DbSet<VolunteerTaskAssignment> VolunteerTaskAssignments => Set<VolunteerTaskAssignment>();
    public DbSet<VolunteerHelpRequest> VolunteerHelpRequests => Set<VolunteerHelpRequest>();
    // Buckets feature (2026-06-15): multi-supervisor + draft→commit allocation.
    public DbSet<VolunteerBucketSupervisor> VolunteerBucketSupervisors => Set<VolunteerBucketSupervisor>();
    public DbSet<TaskAllocationDraft> TaskAllocationDrafts => Set<TaskAllocationDraft>();
    public DbSet<AllocationScenario> AllocationScenarios => Set<AllocationScenario>();
    public DbSet<AllocationScenarioMove> AllocationScenarioMoves => Set<AllocationScenarioMove>();

    // --- Attendee personal session plan ("My plan" — saved sessions) ---------
    public DbSet<SavedSession> SavedSessions => Set<SavedSession>();

    // --- Feature customization & controlled rollout (REQUIREMENTS §23) --------
    // Per-edition kill switches for the customizable (advanced) capabilities;
    // the catalog (CommunityHub.Core.Settings.FeatureCatalog) holds the metadata.
    public DbSet<FeatureSetting> FeatureSettings => Set<FeatureSetting>();

    // Per-edition GROUP lifecycle rings (§23a): one optional ring per feature
    // group; features without their own override inherit their group's ring.
    public DbSet<FeatureGroupSetting> FeatureGroupSettings => Set<FeatureGroupSetting>();

    // Generic, role-tagged event SCHEDULE / key dates (move-in … party … dinner);
    // organizer-managed, rendered role-filtered + synced to participants' calendars.
    public DbSet<ScheduleEntry> ScheduleEntries => Set<ScheduleEntry>();

    // --- Admin-editable config overrides (HYBRID config model, Phase 1) -------
    // Per-edition partial JSON overrides for the shipped event/sponsor/
    // integrations config files; deep-merged on top of the shipped default at
    // runtime. No row ⇒ the shipped default applies unchanged. No secrets here.
    public DbSet<ConfigOverride> ConfigOverrides => Set<ConfigOverride>();

    // --- Per-edition editable email templates (REQUIREMENTS §25h) -------------
    // Override the shipped on-disk templates per edition; the renderer uses the
    // override at send + preview time, else the shipped default.
    public DbSet<EmailTemplateOverride> EmailTemplateOverrides => Set<EmailTemplateOverride>();

    // --- Unified AUDIT TRAIL (REQUIREMENTS §24) -------------------------------
    // Append-only record of every user action + backend/engine event, reviewed by
    // organizers in /Organizer/AuditTrail for troubleshooting + usage insight.
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    // --- DELTA-APPROVAL QUEUE (REQUIREMENTS §59) ------------------------------
    // Detected sync changes that need an operator's approve/reject in
    // /Organizer/SyncQueue before they are applied (never auto-applied).
    public DbSet<SyncDelta> SyncDeltas => Set<SyncDelta>();

    // --- AiHelper INTAKE "CEH feed" (REQUIREMENTS §137) -----------------------
    // Bug/feature reports the AiHelper detected + questions users forwarded to the
    // organizers; the durable record behind the intake email, reviewed in /Organizer/Feed.
    public DbSet<FeedbackItem> FeedbackItems => Set<FeedbackItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // --- Audit trail (§24): edition + time index for the org trail view -----
        b.Entity<AuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).IsRequired().HasMaxLength(200);
            e.Property(x => x.ActorEmail).IsRequired().HasMaxLength(256);
            e.Property(x => x.ActorRole).HasMaxLength(64);
            e.Property(x => x.OnBehalfOf).HasMaxLength(256);
            e.Property(x => x.TargetType).HasMaxLength(128);
            e.Property(x => x.TargetId).HasMaxLength(128);
            e.Property(x => x.Summary).HasMaxLength(1024);
            e.Property(x => x.Detail).HasMaxLength(4000);
            e.Property(x => x.HttpMethod).HasMaxLength(16);
            e.Property(x => x.Path).HasMaxLength(512);
            // The trail view orders by edition + newest-first; usage counts filter by
            // edition + category.
            e.HasIndex(x => new { x.EventId, x.OccurredUtc });
            e.HasIndex(x => new { x.EventId, x.Category });
        });

        // --- SyncDelta (delta-approval queue, §59) ----------------------------
        b.Entity<SyncDelta>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EntityType).HasConversion<int>();
            e.Property(x => x.Source).HasConversion<int>();
            e.Property(x => x.ChangeKind).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.EntityId).HasMaxLength(128);
            e.Property(x => x.EntityLabel).HasMaxLength(512);
            // The serialized field-diff list — a single unbounded text column. No HasMaxLength
            // (and no explicit HasColumnType) so EF maps it to nvarchar(max) on SQL Server and
            // TEXT on SQLite (the relational test provider) — mirroring the SoMePost.AutoText
            // pattern. An explicit "nvarchar(max)" type breaks the SQLite test provider.
            e.Property(x => x.DecidedByEmail).HasMaxLength(320);
            e.Property(x => x.Notes).HasMaxLength(2000);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // The dedupe lookup: the single PENDING row per
            // (edition, entity type, entity id, change kind). Also the hot query for
            // the queue page (pending for an edition).
            e.HasIndex(x => new { x.EventId, x.Status, x.EntityType, x.EntityId, x.ChangeKind });
        });

        // --- FeedbackItem (AiHelper intake "CEH feed", §137) ------------------
        b.Entity<FeedbackItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasConversion<int>();
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Message).IsRequired().HasMaxLength(4000);
            e.Property(x => x.PageUrl).HasMaxLength(2000);
            e.Property(x => x.RoutedTo).HasMaxLength(320);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                // NoAction (not Cascade) — the Event already cascade-deletes the edition;
                // SQL Server refuses a second cascade path to Participant from the same root.
                .OnDelete(DeleteBehavior.NoAction);

            // The feed view: newest-first per edition, with an open/resolved split.
            e.HasIndex(x => new { x.EventId, x.ResolvedAt, x.CreatedAt });
        });

        // --- Event ----------------------------------------------------------
        b.Entity<Event>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).IsRequired().HasMaxLength(32);
            e.Property(x => x.CommunityName).IsRequired().HasMaxLength(200);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
            e.Property(x => x.VenueName).HasMaxLength(200);
            e.Property(x => x.HubHostname).HasMaxLength(255);
            // Calendar sync on by default — existing editions keep it after upgrade.
            e.Property(x => x.CalendarSyncEnabled).HasDefaultValue(true);
            // Edition codes are unique.
            e.HasIndex(x => x.Code).IsUnique();
            // Fast lookup of "the active edition".
            e.HasIndex(x => x.IsActive);
        });

        // --- Participant ----------------------------------------------------
        b.Entity<Participant>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).IsRequired().HasMaxLength(320);
            e.Property(x => x.FullName).IsRequired().HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(40);
            // Optional extra CC address (10a-5). 320 = max RFC 5321 email length.
            e.Property(x => x.SecondaryEmail).HasMaxLength(320);
            // Optional alternate LOGIN address (§26d). Indexed for the login lookup.
            e.Property(x => x.AlternateEmail).HasMaxLength(320);
            e.HasIndex(x => new { x.EventId, x.AlternateEmail });
            e.Property(x => x.Role).HasConversion<int>();
            e.Property(x => x.SponsorCompanyId).HasMaxLength(64);
            e.Property(x => x.CalendarFeedToken).HasMaxLength(64);
            // Onboarding lifecycle: the pre-selection gate + inbound source.
            e.Property(x => x.LifecycleState).HasConversion<int>();
            e.Property(x => x.QueueSource).HasConversion<int>();

            // Release ring (controlled rollout §23): stored as int, defaults to
            // Broad (3) so an unassigned person sees only fully-released features
            // and existing rows upgrade to the default with no backfill. The
            // sentinel is Broad (the CLR default of the property) so EF uses the
            // DB default only for an UNSET value — an explicit earlier ring
            // (Ring0/1/2) is always persisted, never silently reset to Broad.
            e.Property(x => x.Ring)
                .HasConversion<int>()
                .HasSentinel(CommunityHub.Core.Settings.Ring.Broad)
                .HasDefaultValue(CommunityHub.Core.Settings.Ring.Broad);

            e.Property(x => x.HotelConfirmationNumber).HasMaxLength(64);

            e.HasOne(x => x.Event)
                .WithMany(ev => ev.Participants)
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // Multi-hotel placement: a participant is placed in at most one hotel.
            // NoAction (not SetNull/Cascade): the Event already cascade-deletes the
            // edition's participants AND its hotels, so a second cascade path to
            // Participant from the same root would be ambiguous to SQL Server.
            e.HasOne(x => x.Hotel)
                .WithMany(h => h.Participants)
                .HasForeignKey(x => x.HotelId)
                .OnDelete(DeleteBehavior.NoAction);

            // Same-person duplicate pointer (order dedup): nullable self-reference,
            // Restrict (no cascade) so removing a primary never deletes the
            // duplicate row that points at it. The duplicate is excluded from
            // tallies by OrderCountService, not by an FK action.
            e.HasOne(x => x.SamePersonAs)
                .WithMany()
                .HasForeignKey(x => x.SamePersonAsId)
                .OnDelete(DeleteBehavior.Restrict);

            // "Everyone in this hotel" grouping query.
            e.HasIndex(x => new { x.EventId, x.HotelId });

            // Email is the login identity - unique WITHIN an edition, so the
            // same person can exist in ELDK27 and ELDK28 as separate rows.
            e.HasIndex(x => new { x.EventId, x.Email }).IsUnique();

            // Pre-selection queue: filter an edition's rows by lifecycle state.
            e.HasIndex(x => new { x.EventId, x.LifecycleState });

            // Sponsor-contact recipient resolution (REQUIREMENTS §7c): the shared
            // SponsorRecipientResolver loads a company's contacts by
            // (EventId, SponsorCompanyId) then filters by IsEventCoordinator.
            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId });

            // Unique-identifier contact link (REQUIREMENTS §7c): the CM/WordPress
            // user_id written at sync time. Correlation from CM → hub prefers this
            // id over name/email. Indexed by (EventId, CmUserId) for the id-based
            // lookup; the many NULLs (non-sponsor / not-yet-synced rows) sit fine in
            // a non-unique index.
            e.HasIndex(x => new { x.EventId, x.CmUserId });

            // Calendar-feed token is the bearer secret for GET /calendar/{token}.ics.
            // Unique (filtered so the many NULLs before first-view don't collide)
            // so a presented token resolves to exactly one participant.
            e.HasIndex(x => x.CalendarFeedToken)
                .IsUnique()
                .HasFilter("[CalendarFeedToken] IS NOT NULL");
        });

        // --- ParticipantSecretaryToken (organizer grid v2) ------------------
        b.Entity<ParticipantSecretaryToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Token).IsRequired().HasMaxLength(64);
            e.Property(x => x.Label).HasMaxLength(120);
            e.Property(x => x.IssuedByEmail).HasMaxLength(320);

            e.HasOne(x => x.Event)
                .WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // Single-person scope: a withdrawn person's secretary link must die.
            // NoAction (not Cascade) on the Participant FK: the Event root already
            // cascade-deletes both Participants and these tokens, so a second
            // Participant→Token cascade path is a SQL Server multiple-cascade-path
            // error (same pattern as the volunteer/sessions tables). The token is
            // deleted with the Event; tidying a single participant's grants is done
            // in app code (or the Event-level cascade) rather than via this FK.
            e.HasOne(x => x.Participant)
                .WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.NoAction);

            // The bearer secret resolves to exactly one grant.
            e.HasIndex(x => x.Token).IsUnique();
            // Organizer lists a participant's grants.
            e.HasIndex(x => new { x.EventId, x.ParticipantId });
        });

        // --- MagicLinkGrant (welcome auto-login: single-use + revocable) ----
        b.Entity<MagicLinkGrant>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasConversion<int>();
            e.Property(x => x.Purpose).IsRequired().HasMaxLength(32);
            // SHA-256 hex of the random token id (64 chars).
            e.Property(x => x.TokenIdHash).IsRequired().HasMaxLength(64);
            e.Property(x => x.RecipientEmail).HasMaxLength(320);

            e.HasOne(x => x.Event)
                .WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // NoAction on the Participant FK: the Event root already cascade-deletes
            // both Participants and these grants, so a second Participant→Grant
            // cascade path would be a SQL Server multiple-cascade-path error (same
            // pattern as the secretary-token table above).
            e.HasOne(x => x.Participant)
                .WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.NoAction);

            // A presented token resolves to exactly one grant by hash.
            e.HasIndex(x => x.TokenIdHash).IsUnique();
            // Organizer lists / revokes a participant's grants of a kind.
            e.HasIndex(x => new { x.EventId, x.ParticipantId, x.Purpose });
        });

        // --- SessionSourceSetting (per-edition active session source) -------
        b.Entity<SessionSourceSetting>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Source).IsRequired().HasMaxLength(32);
            // §57 session sync direction/stage; stored as int, defaults to stage 1
            // (SessionizeToCeh) so existing rows + new editions keep §38e inert. The CLR
            // sentinel matches the default (stage 1 is the property's initial value) so EF
            // applies the DB default only on a genuine insert-without-value, not on stage 1.
            e.Property(x => x.SyncDirection)
                .HasConversion<int>()
                .HasDefaultValue(SessionSyncDirection.SessionizeToCeh)
                .HasSentinel(SessionSyncDirection.SessionizeToCeh);
            // §58 SPEAKER sync direction/stage; same encoding/default/sentinel as the
            // session SyncDirection above so existing rows + new editions default to stage 1.
            e.Property(x => x.SpeakerSyncDirection)
                .HasConversion<int>()
                .HasDefaultValue(SessionSyncDirection.SessionizeToCeh)
                .HasSentinel(SessionSyncDirection.SessionizeToCeh);
            e.Property(x => x.UpdatedByEmail).HasMaxLength(320);
            e.HasOne(x => x.Event)
                .WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            // One active source per edition.
            e.HasIndex(x => x.EventId).IsUnique();
        });

        // --- EconomicContactAnnotation (hub-side role + notes for e-conomic) -
        b.Entity<EconomicContactAnnotation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasConversion<int>();
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.UpdatedByEmail).HasMaxLength(320);
            // One annotation per e-conomic (customer, contact).
            e.HasIndex(x => new { x.CustomerNumber, x.ContactNumber }).IsUnique();
        });

        // --- PartyRsvp (anonymous party sign-up) ----------------------------
        b.Entity<PartyRsvp>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Email).IsRequired().HasMaxLength(320);
            e.Property(x => x.IpHash).HasMaxLength(64);
            e.HasOne(x => x.Event)
                .WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            // One RSVP per email per edition (upsert).
            e.HasIndex(x => new { x.EventId, x.Email }).IsUnique();
        });

        // --- ParticipantPolicyAcceptance (§119 CoC + Privacy "I accept") -----
        b.Entity<ParticipantPolicyAcceptance>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AcceptedByEmail).IsRequired().HasMaxLength(320);
            e.Property(x => x.CodeOfConductUrl).HasMaxLength(500);
            e.Property(x => x.PrivacyPolicyUrl).HasMaxLength(500);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            // NoAction on the Participant FK: the Event root already cascade-deletes
            // both Participants and these acceptance rows, so a second
            // Participant→Acceptance cascade path would be a SQL Server
            // multiple-cascade-path error (same pattern as the secretary-token table).
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.NoAction);

            // One acceptance per participant per edition (upsert / "already accepted").
            e.HasIndex(x => new { x.EventId, x.ParticipantId }).IsUnique();
        });

        // --- MasterClassSignup (in-hub MC seat + waitlist) ------------------
        b.Entity<MasterClassSignup>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<int>();
            e.HasOne(x => x.Event)
                .WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            // NoAction on Session + Attendee FKs: the Event root already cascade-
            // deletes Sessions, Attendees AND these rows, so a second cascade path
            // would be a SQL Server multiple-cascade-path error.
            e.HasOne(x => x.Session)
                .WithMany()
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Attendee)
                .WithMany()
                .HasForeignKey(x => x.AttendeeId)
                .OnDelete(DeleteBehavior.NoAction);
            // A person may hold up to TWO entries: one confirmed seat + one
            // waitlist/offer for a DIFFERENT MC (≤1 confirmed enforced in logic),
            // so the per-attendee index is NOT unique. One entry per
            // (attendee, session) IS unique (no double signup to the same MC).
            e.HasIndex(x => new { x.EventId, x.AttendeeId });
            e.HasIndex(x => new { x.EventId, x.AttendeeId, x.SessionId }).IsUnique();
            e.HasIndex(x => new { x.EventId, x.SessionId, x.Status });
        });

        // --- MasterClassSettings (per-edition offer-hold + promotion mode) --
        b.Entity<MasterClassSettings>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PromotionMode).HasConversion<int>();
            e.Property(x => x.UpdatedByEmail).HasMaxLength(320);
            e.HasOne(x => x.Event)
                .WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.EventId).IsUnique();
        });

        // --- ImpersonationAudit (organizer grid v2) -------------------------
        b.Entity<ImpersonationAudit>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ActorKind).HasConversion<int>();
            e.Property(x => x.ActorLabel).IsRequired().HasMaxLength(320);
            e.Property(x => x.Action).IsRequired().HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(1000);

            e.HasOne(x => x.Event)
                .WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // Newest-first review of an edition's acting-as history.
            e.HasIndex(x => new { x.EventId, x.CreatedAt });
            // "Who acted as this person?" lookup.
            e.HasIndex(x => new { x.EventId, x.TargetParticipantId });
        });

        // --- LoginPin -------------------------------------------------------
        b.Entity<LoginPin>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PinHash).IsRequired().HasMaxLength(256);

            e.HasOne(x => x.Participant)
                .WithMany(cp => cp.LoginPins)
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.Cascade);

            // Verifier looks up the newest redeemable PIN for a profile.
            e.HasIndex(x => new { x.ParticipantId, x.ExpiresAt });
        });

        // --- ParticipantTask -------------------------------------------------------
        b.Entity<ParticipantTask>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).IsRequired().HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.SourceKey).HasMaxLength(100);
            e.Property(x => x.SponsorCompanyId).HasMaxLength(64);
            e.Property(x => x.State).HasConversion<int>();

            e.HasOne(x => x.Event)
                .WithMany(ev => ev.Tasks)
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // A task may be unassigned; deleting a profile must not delete the
            // task, so the FK is restrict + nullable.
            e.HasOne(x => x.AssignedParticipant)
                .WithMany(cp => cp.AssignedTasks)
                .HasForeignKey(x => x.AssignedParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.EventId, x.AssignedParticipantId });
            e.HasIndex(x => new { x.EventId, x.DueDate });
            // Scopes a sponsor's task list / toggle to their own company.
            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId });
        });

        // --- SentReminder ---------------------------------------------------
        b.Entity<SentReminder>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RecipientEmail).IsRequired().HasMaxLength(320);
            e.Property(x => x.ReminderType).IsRequired().HasMaxLength(64);
            e.Property(x => x.OccasionKey).IsRequired().HasMaxLength(256);

            e.HasOne(x => x.Event)
                .WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // The dedup key. UNIQUE so the reminder job can rely on the
            // database to guarantee a reminder is never sent twice, even if
            // two runs overlap (CONTEXT.md 11e - idempotent, self-healing).
            e.HasIndex(x => new
            {
                x.EventId,
                x.RecipientEmail,
                x.ReminderType,
                x.OccasionKey
            }).IsUnique();
        });

        // --- EmailLog (audit of every outbound send) ------------------------
        b.Entity<EmailLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Category).IsRequired().HasMaxLength(64);
            e.Property(x => x.ToEmail).IsRequired().HasMaxLength(320);
            e.Property(x => x.ActualToEmail).HasMaxLength(320);
            e.Property(x => x.CcEmails).HasMaxLength(1000);
            e.Property(x => x.RecipientName).HasMaxLength(200);
            e.Property(x => x.Subject).HasMaxLength(998);
            e.Property(x => x.Error).HasMaxLength(2000);

            // NO FK to Event/Participant: the log must survive a participant or
            // edition delete (it is an audit trail) and a bootstrap mail may have
            // no edition at all (EventId 0). ParticipantId is a soft pointer.

            // Organizer log view: edition-scoped, newest first.
            e.HasIndex(x => new { x.EventId, x.SentAt });
            // Per-person view + the email filter (substring still scans, but the
            // exact-address per-person lookup is indexed).
            e.HasIndex(x => new { x.EventId, x.ToEmail });
            e.HasIndex(x => new { x.EventId, x.ParticipantId });
        });

        // --- Attendee -------------------------------------------------------
        b.Entity<Attendee>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).IsRequired().HasMaxLength(320);
            e.Property(x => x.FirstName).HasMaxLength(200);
            e.Property(x => x.LastName).HasMaxLength(200);
            e.Property(x => x.TicketClassName).HasMaxLength(200);
            e.Property(x => x.MasterClassName).HasMaxLength(200);
            e.Property(x => x.OrderId).HasMaxLength(128);
            e.Property(x => x.TicketStatus).HasConversion<int>();
            e.Property(x => x.BookingStatus).HasConversion<int>();
            e.Property(x => x.MirrorState).HasConversion<int>();
            e.Property(x => x.BackstageTicketId).HasMaxLength(128);

            e.HasOne(x => x.Event)
                .WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // Order-level mirror link (REQUIREMENTS §125): a ticket belongs to its
            // Backstage order via the (EventId, OrderId) → (EventId, BackstageOrderId)
            // principal key. Optional — OrderId is nullable on legacy rows, so a null
            // OrderId means "no linked order" rather than a dangling FK. NoAction (not
            // Cascade): the Event root already cascade-deletes both Orders and Attendees,
            // so a second Order→Attendee cascade path would be a SQL Server
            // multiple-cascade-path error (same pattern as the other child tables).
            e.HasOne(x => x.Order)
                .WithMany(o => o.Attendees)
                .HasForeignKey(x => new { x.EventId, x.OrderId })
                .HasPrincipalKey(o => new { o.EventId, o.BackstageOrderId })
                .OnDelete(DeleteBehavior.NoAction);

            // Email unique within an edition (legacy email-keyed reconcile path).
            e.HasIndex(x => new { x.EventId, x.Email }).IsUnique();
            // Active-vs-cancelled split (§128): exclude soft-cancelled rows from counts.
            e.HasIndex(x => new { x.EventId, x.MirrorState });
            // The STABLE ticket-id identity (REQUIREMENTS §6) — unique per edition
            // where present (filtered so legacy null-ticket rows don't collide), so
            // the ticket-keyed sync upserts by (EventId, BackstageTicketId) and a
            // reassignment transfers the MC instead of orphaning it.
            e.HasIndex(x => new { x.EventId, x.BackstageTicketId })
                .IsUnique()
                .HasFilter("[BackstageTicketId] IS NOT NULL");
            // Fast filter for the organizer mismatch view.
            e.HasIndex(x => new { x.EventId, x.HasReconciliationMismatch });
        });

        // --- Order (order-level Zoho Backstage mirror, REQUIREMENTS §125) ----
        b.Entity<Order>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.BackstageOrderId).IsRequired().HasMaxLength(128);
            e.Property(x => x.BuyerName).HasMaxLength(200);
            e.Property(x => x.BuyerEmail).HasMaxLength(320);
            e.Property(x => x.CompanyName).HasMaxLength(200);
            e.Property(x => x.Country).HasMaxLength(100);
            e.Property(x => x.CountryCode).HasMaxLength(10);
            e.Property(x => x.City).HasMaxLength(200);
            e.Property(x => x.Postcode).HasMaxLength(40);
            e.Property(x => x.TaxId).HasMaxLength(100);
            e.Property(x => x.OrderStatus).HasMaxLength(60);
            e.Property(x => x.MirrorState).HasConversion<int>();
            // RawJson — a single unbounded text column (full raw Zoho order). No
            // HasMaxLength so EF maps it to nvarchar(max) on SQL Server and TEXT on
            // SQLite (cf. SyncDelta.ChangesJson); an explicit "nvarchar(max)" type
            // breaks the SQLite test provider.

            e.HasOne(x => x.Event)
                .WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // The natural identity within an edition — also the principal key the
            // Attendee→Order FK targets, so a ticket links to its order by the
            // Backstage order id. The alternate key supplies the unique index over
            // (EventId, BackstageOrderId), so no separate unique index is declared.
            e.HasAlternateKey(x => new { x.EventId, x.BackstageOrderId });
            // Active-vs-cancelled split (§128): exclude soft-cancelled orders from counts.
            e.HasIndex(x => new { x.EventId, x.MirrorState });
        });

        // --- SyncRun (last-successful-sync marker, REQUIREMENTS §125/§127) ----
        b.Entity<SyncRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).IsRequired().HasMaxLength(60);
            e.Property(x => x.Summary).HasMaxLength(400);

            e.HasOne(x => x.Event)
                .WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // One marker per (edition, sync) — the upsert key.
            e.HasIndex(x => new { x.EventId, x.Key }).IsUnique();
        });

        // --- JobHealthMarker (fleet-wide consecutive-failure counter per job) --
        b.Entity<JobHealthMarker>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.JobKey).IsRequired().HasMaxLength(80);
            e.Property(x => x.LastError).HasMaxLength(1000);

            // One marker per job key — the upsert key. NOT event-scoped: the
            // reconcile engines run once for the whole fleet.
            e.HasIndex(x => x.JobKey).IsUnique();
        });

        // --- HotelBooking ---------------------------------------------------
        b.Entity<HotelBooking>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RoomShareWith).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.ConfirmationNumber).HasMaxLength(64);
            e.Property(x => x.RoomType).HasMaxLength(120);
            e.Property(x => x.ConfirmationState).HasConversion<int>();

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            // One booking per participant per edition.
            e.HasIndex(x => new { x.EventId, x.ParticipantId }).IsUnique();
        });

        // --- Hotel (organizer-defined; multi-hotel placement) ----------------
        b.Entity<Hotel>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.ContactEmail).HasMaxLength(320);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.ConfirmationNumber).HasMaxLength(64);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // Organizer hotel list + "is this name already used" lookup.
            e.HasIndex(x => new { x.EventId, x.Name });
        });

        // --- DinnerSignup ---------------------------------------------------
        b.Entity<DinnerSignup>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DietaryPreference).HasMaxLength(100);
            e.Property(x => x.AllergyNotes).HasMaxLength(1000);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.EventId, x.ParticipantId }).IsUnique();
        });

        // --- DietaryRequirement (structured allergy/diet capture, §21) ------
        b.Entity<DietaryRequirement>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Surface).HasConversion<int>();
            e.Property(x => x.DietChoice).HasMaxLength(40);
            e.Property(x => x.OtherAllergens).HasMaxLength(1000);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            // One row per participant per edition PER occasion (Dinner vs
            // Speaker day-catering) — own-row scoped, upserted on save.
            e.HasIndex(x => new { x.EventId, x.ParticipantId, x.Surface }).IsUnique();
            // Catering roll-up: "all dietary rows for this occasion in the edition".
            e.HasIndex(x => new { x.EventId, x.Surface });
        });

        // --- SwagPreference -------------------------------------------------
        b.Entity<SwagPreference>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PoloSize).HasMaxLength(60);
            e.Property(x => x.JacketSize).HasMaxLength(60);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.WantsGift).HasDefaultValue(true);
            e.Property(x => x.WantsCredlyBadge).HasDefaultValue(true);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            // One preference row per participant per edition.
            e.HasIndex(x => new { x.EventId, x.ParticipantId }).IsUnique();
        });

        // --- SpeakerProfile -------------------------------------------------
        b.Entity<SpeakerProfile>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Accreditation).HasMaxLength(100);
            e.Property(x => x.Country).HasMaxLength(100);
            e.Property(x => x.Gender).HasMaxLength(40);
            // 320 = max RFC 5321 email length. Nullable: blank = no override.
            e.Property(x => x.ContactEmailOverride).HasMaxLength(320);
            // Calendar-specific email override (wizard step 1). 320 = max RFC 5321
            // email length. Nullable: blank = no calendar override.
            e.Property(x => x.CalendarEmail).HasMaxLength(320);
            e.Property(x => x.Tagline).HasMaxLength(500);
            e.Property(x => x.Biography).HasMaxLength(4000);
            e.Property(x => x.Blog).HasMaxLength(500);
            e.Property(x => x.LinkedIn).HasMaxLength(500);
            e.Property(x => x.Twitter).HasMaxLength(200);
            // HARD GATE for the outbound speaker-bio sync: defaults FALSE so no
            // speaker is ever made public in Backstage until explicitly approved.
            e.Property(x => x.SelectedForPublish).HasDefaultValue(false);
            e.Property(x => x.PhotoUrl).HasMaxLength(1000);
            // Speaker Details (§26c): Sessionize/Zoho ids, split name, skills, stored photo path.
            e.Property(x => x.SessionizeSpeakerId).HasMaxLength(80);
            e.Property(x => x.BackstageSpeakerId).HasMaxLength(80);
            e.Property(x => x.FirstName).HasMaxLength(200);
            e.Property(x => x.LastName).HasMaxLength(200);
            e.Property(x => x.MvpCategories).HasMaxLength(1000);
            e.Property(x => x.PhotoSharePointPath).HasMaxLength(1000);
            // Comma-separated set of speaker-edited bio field tokens (the
            // per-field dirty set the delta sync reads). Small; 200 is ample.
            e.Property(x => x.SpeakerEditedFields).HasMaxLength(200);
            // §38e/§58 Zoho→CEH speaker change-tracking: last-known Backstage
            // speaker snapshot the detection engine diffs against. Same field
            // sizes as the CEH-owned bio columns above.
            e.Property(x => x.BackstageName).HasMaxLength(400);
            e.Property(x => x.BackstageTagline).HasMaxLength(500);
            e.Property(x => x.BackstageBio).HasMaxLength(4000);
            e.Property(x => x.BackstageCountry).HasMaxLength(100);
            e.Property(x => x.BackstageLinkedIn).HasMaxLength(500);
            e.Property(x => x.BackstageTwitter).HasMaxLength(200);
            // Who funds the speaker's appreciation package (drives entitlements);
            // stored as int, defaults to Supported (0).
            e.Property(x => x.SpeakerFunding).HasConversion<int>();

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.EventId, x.ParticipantId }).IsUnique();
        });

        // --- ParticipantOrderOverride (per-person order-item include/exclude) -
        b.Entity<ParticipantOrderOverride>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Item).HasConversion<int>();
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.SetByEmail).HasMaxLength(320);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            // NoAction on the Participant FK: the Event root already cascade-
            // deletes both Participants and these overrides, so a second
            // Participant→Override cascade path would be a SQL Server
            // multiple-cascade-path error.
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.NoAction);

            // One override per (edition, participant, item) — upserted on edit.
            e.HasIndex(x => new { x.EventId, x.ParticipantId, x.Item }).IsUnique();
        });

        // --- Session (Sessionize session import; in-hub only) ----------------
        b.Entity<Session>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SessionizeId).IsRequired().HasMaxLength(64);
            e.Property(x => x.Title).IsRequired().HasMaxLength(400);
            e.Property(x => x.Abstract).HasMaxLength(8000);
            e.Property(x => x.Room).HasMaxLength(200);
            e.Property(x => x.Track).HasMaxLength(200);
            // Type + Length are enums stored as int (consistent with the rest of the
            // model, e.g. Participant.Role / Attendee.TicketStatus).
            e.Property(x => x.Type).HasConversion<int>();
            e.Property(x => x.Length).HasConversion<int>();
            // QR + evaluation URLs are SharePoint / form links (2000 = the cap used
            // by SponsorUploadLocation.EditLinkUrl for the same kind of URL).
            e.Property(x => x.RoomQrUrl).HasMaxLength(2000);
            e.Property(x => x.EvaluationFormUrl).HasMaxLength(2000);
            e.Property(x => x.PublicToken).HasMaxLength(64);
            // Master-class public logistics page (§6).
            e.Property(x => x.PublicSlug).HasMaxLength(64);
            e.Property(x => x.LogisticsText).HasMaxLength(8000);
            e.Property(x => x.LogisticsUpdatedByEmail).HasMaxLength(320);
            // Zoho Backstage agenda id + last-known time/location (§38e/§52). The id
            // shares the 64-char id cap used by SessionizeId; the room mirrors Room.
            e.Property(x => x.BackstageSessionId).HasMaxLength(64);
            e.Property(x => x.BackstageRoom).HasMaxLength(200);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // The Sessionize session id is the upsert key, unique within an edition.
            // Hub-added sessions carry a synthetic id (hub-<guid>) so they never
            // collide with imported ids nor with each other.
            e.HasIndex(x => new { x.EventId, x.SessionizeId }).IsUnique();
            // Session-view filters narrow by Type / Length within an edition.
            e.HasIndex(x => new { x.EventId, x.Type });
            e.HasIndex(x => new { x.EventId, x.Length });
            // "All sessions in this room" lookup (per-room QR + grouping).
            e.HasIndex(x => new { x.EventId, x.Room });
            // The public ask-page token is the bearer key for /sessions/{token}/ask.
            // Unique (filtered so the many NULLs before first-mint don't collide)
            // so a presented token resolves to exactly one session.
            e.HasIndex(x => x.PublicToken)
                .IsUnique()
                .HasFilter("[PublicToken] IS NOT NULL");
            // The public logistics page resolves a session by its public slug.
            // Filtered unique so the many NULLs (no public page yet) don't collide.
            e.HasIndex(x => x.PublicSlug)
                .IsUnique()
                .HasFilter("[PublicSlug] IS NOT NULL");
            // §38e: a Backstage agenda session maps to at most ONE CEH session per
            // edition (the change-detection match key). Filtered unique so the many
            // NULLs (sessions not yet matched to Backstage) don't collide.
            e.HasIndex(x => new { x.EventId, x.BackstageSessionId })
                .IsUnique()
                .HasFilter("[BackstageSessionId] IS NOT NULL");
        });

        // --- SessionSpeaker (Session <-> speaker, many-many) -----------------
        b.Entity<SessionSpeaker>(e =>
        {
            e.HasKey(x => x.Id);

            e.HasOne(x => x.Session).WithMany(s => s.SessionSpeakers)
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            // NoAction (not Cascade): the Event already cascade-deletes the
            // edition's participants AND its sessions, so a second cascade path to
            // Participant from the same root would be ambiguous to SQL Server.
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.NoAction);

            // A speaker is linked to a session at most once.
            e.HasIndex(x => new { x.SessionId, x.ParticipantId }).IsUnique();
            // "Sessions for a participant" lookup (speaker overview).
            e.HasIndex(x => x.ParticipantId);
        });

        // --- SessionQuestion (public attendee questions per session; hub-only) -
        // An attendee asks a question for a session BEFORE the event via a public,
        // no-login page; it lands here ONLY (never auto-public). Organizers see all;
        // a speaker linked to the session sees + answers its questions, and the
        // response is visible to the other speakers on the same session.
        b.Entity<SessionQuestion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AskerName).HasMaxLength(200);
            e.Property(x => x.AskerEmail).HasMaxLength(320);
            e.Property(x => x.QuestionText).IsRequired().HasMaxLength(2000);
            e.Property(x => x.IpHash).HasMaxLength(64);
            e.Property(x => x.ResponseText).HasMaxLength(2000);
            e.Property(x => x.RespondedByEmail).HasMaxLength(320);
            e.Property(x => x.Status).HasConversion<int>();

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Session).WithMany(s => s.Questions)
                .HasForeignKey(x => x.SessionId)
                // NoAction (not Cascade): the Event already cascade-deletes the
                // edition's sessions AND its questions, so a second cascade path
                // would be ambiguous to SQL Server.
                .OnDelete(DeleteBehavior.NoAction);
            // Responder is an optional pointer to a participant; NoAction to avoid
            // a second cascade path back to the edition root.
            e.HasOne(x => x.RespondedByParticipant).WithMany()
                .HasForeignKey(x => x.RespondedByParticipantId)
                .OnDelete(DeleteBehavior.NoAction);
            // 1:1 private-question asker (master-class landing page, FEATURE 2):
            // optional Attendee pointer. NoAction — the Event root already cascade-
            // deletes both Attendees and these questions, so a second cascade path
            // would be a SQL Server multiple-cascade-path error.
            e.HasOne(x => x.AskerAttendee).WithMany()
                .HasForeignKey(x => x.AskerAttendeeId)
                .OnDelete(DeleteBehavior.NoAction);

            // Organizer "all questions" + per-session (speaker) inbox, newest first.
            e.HasIndex(x => new { x.EventId, x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.SessionId, x.Status });
            // Soft rate-limit lookup by IP hash within an edition.
            e.HasIndex(x => new { x.EventId, x.IpHash });
            // "This attendee's own 1:1 questions for this session" landing-page lookup.
            e.HasIndex(x => new { x.SessionId, x.AskerAttendeeId });
        });

        // --- MasterClassComment (MC landing-page Q&A, FEATURE 2) -------------
        // Public WITHIN the master class: confirmed signups + the MC's speakers
        // (and organizers) post + read. Threaded via ParentCommentId. The author is
        // EITHER a Participant (speaker/organizer) OR an Attendee (confirmed signup).
        b.Entity<MasterClassComment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AuthorDisplayName).IsRequired().HasMaxLength(200);
            e.Property(x => x.Body).IsRequired().HasMaxLength(2000);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            // NoAction on Session / Participant / Attendee FKs: the Event root already
            // cascade-deletes those AND these comments, so a second cascade path would
            // be a SQL Server multiple-cascade-path error (cf. MasterClassSignup).
            e.HasOne(x => x.Session).WithMany()
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.AuthorParticipant).WithMany()
                .HasForeignKey(x => x.AuthorParticipantId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.AuthorAttendee).WithMany()
                .HasForeignKey(x => x.AuthorAttendeeId)
                .OnDelete(DeleteBehavior.NoAction);
            // A reply points at its parent comment; Restrict so deleting a parent
            // never silently cascades the whole thread.
            e.HasOne(x => x.ParentComment).WithMany()
                .HasForeignKey(x => x.ParentCommentId)
                .OnDelete(DeleteBehavior.Restrict);

            // The landing-page thread: a session's comments oldest-first.
            e.HasIndex(x => new { x.SessionId, x.CreatedAt });
        });

        // --- SessionEvaluation (public post-session attendee rating; hub-only) -
        // A HappyOrNot-style 1–5 rating + optional comment, submitted from a public,
        // no-login page (reached via the room QR) addressed by the SAME
        // Session.PublicToken as the ask page. Never shown publicly; aggregated for the
        // organizer results dashboard (per-session + per-room). One-per-attendee/session
        // is enforced softly by upserting on a per-session cookie token (VoterKey).
        b.Entity<SessionEvaluation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Comment).HasMaxLength(2000);
            // 64 chars: the per-session cookie token (URL-safe base64 of 32 bytes ≈ 43).
            e.Property(x => x.VoterKey).HasMaxLength(64);
            e.Property(x => x.IpHash).HasMaxLength(64);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Session).WithMany()
                .HasForeignKey(x => x.SessionId)
                // NoAction (not Cascade): the Event already cascade-deletes the
                // edition's sessions AND its evaluations, so a second cascade path
                // would be ambiguous to SQL Server (cf. SessionQuestion).
                .OnDelete(DeleteBehavior.NoAction);

            // The one-per-attendee/session upsert key: a (session, cookie-token) pair
            // is unique so a same-device re-rate updates in place. Filtered so the many
            // NULLs (cookie-less submits) never collide.
            e.HasIndex(x => new { x.SessionId, x.VoterKey })
                .IsUnique()
                .HasFilter("[VoterKey] IS NOT NULL");
            // Dashboard aggregation: an edition's ratings grouped by session.
            e.HasIndex(x => new { x.EventId, x.SessionId });
            // Soft rate-limit lookup by IP hash within an edition.
            e.HasIndex(x => new { x.EventId, x.IpHash });
        });

        // --- SpeakerBackstageEmailSync (Backstage email propagation queue) --
        b.Entity<SpeakerBackstageEmailSync>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.IdentityEmail).IsRequired().HasMaxLength(320);
            e.Property(x => x.DesiredEmail).IsRequired().HasMaxLength(320);
            e.Property(x => x.State).HasConversion<int>();
            e.Property(x => x.LastError).HasMaxLength(1000);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            // One propagation row per speaker per edition (upserted on change).
            e.HasIndex(x => new { x.EventId, x.ParticipantId }).IsUnique();
        });

        // --- SessionizeEndpointSetting (organizer endpoint admin + change mode) -
        b.Entity<SessionizeEndpointSetting>(e =>
        {
            e.HasKey(x => x.Id);
            // The Sessionize endpoint id (the <your-event-id> segment) is short;
            // 200 is ample. Ordinary operator config, NOT a secret.
            e.Property(x => x.EndpointId).HasMaxLength(200);
            e.Property(x => x.PreviousEndpointId).HasMaxLength(200);
            e.Property(x => x.View).HasMaxLength(40);
            e.Property(x => x.PendingChangeMode).HasConversion<int>();
            e.Property(x => x.LastUpdatedByEmail).HasMaxLength(320);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // One endpoint setting per edition (upserted on save).
            e.HasIndex(x => x.EventId).IsUnique();
        });

        // --- SponsorInfo ----------------------------------------------------
        b.Entity<SponsorInfo>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SponsorCompanyId).IsRequired().HasMaxLength(64);
            e.Property(x => x.LogoVectorPath).HasMaxLength(400);
            e.Property(x => x.LogoVectorFileName).HasMaxLength(200);
            e.Property(x => x.LogoRasterPath).HasMaxLength(400);
            e.Property(x => x.LogoRasterFileName).HasMaxLength(200);
            e.Property(x => x.CompanyDescription).HasMaxLength(1000);
            e.Property(x => x.CompanyDescriptionShort).HasMaxLength(80);
            e.Property(x => x.SocialMediaIntro).HasMaxLength(600);
            e.Property(x => x.LastUpdatedByEmail).HasMaxLength(320);
            e.Property(x => x.WebsiteUrl).HasMaxLength(400);
            e.Property(x => x.LinkedInUrl).HasMaxLength(400);
            e.Property(x => x.TwitterUrl).HasMaxLength(400);
            e.Property(x => x.EventCoordinatorFirstName).HasMaxLength(120);
            e.Property(x => x.EventCoordinatorLastName).HasMaxLength(120);
            e.Property(x => x.EventCoordinatorCompanyName).HasMaxLength(200);
            e.Property(x => x.EventCoordinatorEmail).HasMaxLength(320);
            e.Property(x => x.EventCoordinatorPhone).HasMaxLength(60);
            e.Property(x => x.ZohoContactEmail).HasMaxLength(320);
            e.Property(x => x.ZohoSponsorId).HasMaxLength(64);
            e.Property(x => x.ZohoExhibitorId).HasMaxLength(64);
            // Commercial sponsorship package (Silver/Gold/Diamond/Platinum):
            // stored as int, defaults to Silver (0 = digital / no booth).
            e.Property(x => x.SponsorPackage).HasConversion<int>();

            // Company default release ring (controlled rollout §23): stored as int,
            // defaults to Broad (3) — the company default for its contacts unless a
            // contact narrows their own ring. Existing rows upgrade with no backfill.
            // Sentinel = Broad so an explicit earlier company ring is persisted.
            e.Property(x => x.Ring)
                .HasConversion<int>()
                .HasSentinel(CommunityHub.Core.Settings.Ring.Broad)
                .HasDefaultValue(CommunityHub.Core.Settings.Ring.Broad);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId }).IsUnique();
            // The public sponsors page groups by tier; index it for the lookup.
            e.HasIndex(x => new { x.EventId, x.Tier });
        });

        // --- SponsorBoothMember ---------------------------------------------
        b.Entity<SponsorBoothMember>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SponsorCompanyId).IsRequired().HasMaxLength(64);
            e.Property(x => x.FirstName).IsRequired().HasMaxLength(120);
            e.Property(x => x.LastName).IsRequired().HasMaxLength(120);
            e.Property(x => x.Email).IsRequired().HasMaxLength(320);
            e.Property(x => x.Role).HasConversion<int>();
            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            // One row per (company, email); used to dedupe the Zoho reconcile.
            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId, x.Email }).IsUnique();
        });

        // --- SponsorBoothMaterial -------------------------------------------
        b.Entity<SponsorBoothMaterial>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SponsorCompanyId).IsRequired().HasMaxLength(64);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Url).IsRequired().HasMaxLength(1000);
            e.Property(x => x.FileName).HasMaxLength(400);
            e.Property(x => x.CreatedByEmail).HasMaxLength(320);
            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId, x.Kind });
        });

        // --- SponsorUploadAudit (§68) ---------------------------------------
        b.Entity<SponsorUploadAudit>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SponsorCompanyId).IsRequired().HasMaxLength(64);
            e.Property(x => x.Kind).IsRequired().HasMaxLength(32);
            e.Property(x => x.FileName).IsRequired().HasMaxLength(400);
            e.Property(x => x.WebUrl).HasMaxLength(2000);
            e.Property(x => x.UploadedByEmail).IsRequired().HasMaxLength(320);
            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            // Page reads the latest row per (company, kind); index that lookup.
            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId, x.Kind });
        });

        // --- SponsorUploadLocation ------------------------------------------
        b.Entity<SponsorUploadLocation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SponsorCompanyId).IsRequired().HasMaxLength(64);
            e.Property(x => x.CompanyName).IsRequired().HasMaxLength(200);
            e.Property(x => x.FolderKey).IsRequired().HasMaxLength(64);
            e.Property(x => x.Subfolder).IsRequired().HasMaxLength(120);
            e.Property(x => x.FolderPath).IsRequired().HasMaxLength(1000);
            e.Property(x => x.EditLinkUrl).HasMaxLength(2000);
            e.Property(x => x.NotifyEmailsCsv).HasMaxLength(1000);
            e.Property(x => x.NotifySubject).HasMaxLength(400);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // One row per (event, company, folder kind). Re-runs of the pull
            // engine UPSERT this row (refresh link / recipient list) instead
            // of duplicating.
            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId, x.FolderKey })
                .IsUnique();
        });

        // --- SponsorUploadFile ----------------------------------------------
        b.Entity<SponsorUploadFile>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).IsRequired().HasMaxLength(400);
            e.Property(x => x.GraphItemId).IsRequired().HasMaxLength(128);
            e.Property(x => x.ETag).HasMaxLength(128);

            e.HasOne(x => x.Location)
                .WithMany(l => l.Files)
                .HasForeignKey(x => x.SponsorUploadLocationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Graph driveItem id is the stable identity within a folder; the
            // watcher upserts by (location, GraphItemId).
            e.HasIndex(x => new { x.SponsorUploadLocationId, x.GraphItemId })
                .IsUnique();
        });

        // --- OrganizerActionItem --------------------------------------------
        b.Entity<OrganizerActionItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).IsRequired().HasMaxLength(64);
            e.Property(x => x.Summary).IsRequired().HasMaxLength(500);
            e.Property(x => x.ResolvedNotes).HasMaxLength(500);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                // NoAction (not SetNull/Cascade) because the Event already
                // cascade-deletes everything in the edition; SQL Server refuses
                // a second cascade path to Participant from the same root.
                .OnDelete(DeleteBehavior.NoAction);

            // One open action per (event, type, participant). Re-edits update the
            // existing row's Summary + UpdatedAt instead of creating a duplicate.
            e.HasIndex(x => new { x.EventId, x.Type, x.ParticipantId, x.ResolvedAt });
        });

        // --- TravelReimbursement --------------------------------------------
        b.Entity<TravelReimbursement>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OriginCity).HasMaxLength(200);
            e.Property(x => x.Explanation).HasMaxLength(2000);
            e.Property(x => x.PaidNotes).HasMaxLength(500);
            e.Property(x => x.ClaimAmountEur).HasColumnType("decimal(10,2)");

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.EventId, x.ParticipantId }).IsUnique();
        });

        // --- TravelReceipt --------------------------------------------------
        // Uploaded receipt / invoice files (bytes in DB) for a speaker's travel
        // reimbursement. Gates the Step-2 request and is attached to the ERP-inbox
        // email on submit (REQUIREMENTS §48).
        b.Entity<TravelReceipt>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(300);
            e.Property(x => x.ContentType).HasMaxLength(150);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            // Many files per (event, participant) — NOT unique.
            e.HasIndex(x => new { x.EventId, x.ParticipantId });
        });

        // --- LunchSignup ----------------------------------------------------
        b.Entity<LunchSignup>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Notes).HasMaxLength(1000);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.EventId, x.ParticipantId }).IsUnique();
        });

        // --- SurveyResponse + SurveyResponsePick ----------------------------
        // Anonymous responses to survey wizards (e.g. ELDK27 Topics). The
        // survey catalog (tracks / topics / level examples) lives in JSON
        // under CommunityHub/App_Data/Surveys/<slug>.json -- only the
        // responses are persisted. SurveySlug + TopicId are string keys
        // referencing the JSON; no FKs to a catalog table.
        b.Entity<SurveyResponse>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SurveySlug).IsRequired().HasMaxLength(80);
            e.Property(x => x.SelectedTrackId).IsRequired().HasMaxLength(80);
            e.Property(x => x.Comment).HasMaxLength(2000);
            e.Property(x => x.IpHash).HasMaxLength(64);

            // Dashboard aggregations filter by survey slug.
            e.HasIndex(x => new { x.SurveySlug, x.SubmittedAt });
            // Track-distribution aggregation.
            e.HasIndex(x => new { x.SurveySlug, x.SelectedTrackId });
        });

        b.Entity<SurveyResponsePick>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TopicId).IsRequired().HasMaxLength(80);
            e.Property(x => x.DesiredLevel).HasConversion<int>();

            e.HasOne(x => x.Response)
                .WithMany(r => r.Picks)
                .HasForeignKey(x => x.SurveyResponseId)
                .OnDelete(DeleteBehavior.Cascade);

            // One pick per (response, rank) and per (response, topic) -- a
            // respondent can't rank the same topic twice nor have two #1s.
            e.HasIndex(x => new { x.SurveyResponseId, x.Rank }).IsUnique();
            e.HasIndex(x => new { x.SurveyResponseId, x.TopicId }).IsUnique();
            // Topic-popularity aggregation.
            e.HasIndex(x => x.TopicId);
        });

        // --- SurveyState (organizer-controlled per-survey admin state) -------
        // Open/closed gate for the public survey page. Keyed by slug (the JSON
        // basename), NOT scoped to an Event — a survey is its own anonymous
        // artefact (cf. SurveyResponse). A missing row means OPEN (the default).
        b.Entity<SurveyState>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SurveySlug).IsRequired().HasMaxLength(80);
            e.Property(x => x.IsOpen).HasDefaultValue(true);
            e.Property(x => x.UpdatedByEmail).HasMaxLength(320);

            // One state row per survey — looked up + upserted by slug.
            e.HasIndex(x => x.SurveySlug).IsUnique();
        });

        // --- SponsorLead ------------------------------------------------------
        // The leads pipeline store (CONTEXT.md sponsor leads). Zoho stays the
        // source of truth for CONTENT; the hub layers processing status, AI
        // screen verdicts and reply audit on top. NOTHING is hard-deleted.
        b.Entity<SponsorLead>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SponsorCompanyId).IsRequired().HasMaxLength(64);
            e.Property(x => x.ZohoRecordId).HasMaxLength(64);
            e.Property(x => x.LeadKind).HasConversion<int>();
            e.Property(x => x.FirstName).HasMaxLength(200);
            e.Property(x => x.LastName).HasMaxLength(200);
            e.Property(x => x.FullName).HasMaxLength(400);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Phone).HasMaxLength(60);
            e.Property(x => x.Company).HasMaxLength(300);
            e.Property(x => x.JobTitle).HasMaxLength(200);
            e.Property(x => x.City).HasMaxLength(120);
            e.Property(x => x.Country).HasMaxLength(120);
            e.Property(x => x.Source).HasMaxLength(120);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.CaptureMethod).HasConversion<int>();
            e.Property(x => x.CapturedByEmail).HasMaxLength(320);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.StatusNote).HasMaxLength(500);
            e.Property(x => x.StatusChangedByEmail).HasMaxLength(320);
            e.Property(x => x.AiScreenLabel).HasMaxLength(60);
            e.Property(x => x.LastReplyByEmail).HasMaxLength(320);

            // Idempotent sync: re-pulling the same Zoho row updates in place.
            // Filtered unique so hub-local rows (no Zoho id) don't collide.
            e.HasIndex(x => new { x.EventId, x.ZohoRecordId })
                .IsUnique()
                .HasFilter("[ZohoRecordId] <> ''");
            // Per-sponsor grid + feed + digest queries.
            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId, x.CapturedAt });
            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId, x.Status });
        });

        // --- SponsorLeadNotificationPref --------------------------------------
        b.Entity<SponsorLeadNotificationPref>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SponsorCompanyId).IsRequired().HasMaxLength(64);
            e.Property(x => x.Recipients).HasMaxLength(1000);
            e.Property(x => x.Cadence).HasConversion<int>();

            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId }).IsUnique();
        });

        // --- SponsorApiKey -----------------------------------------------------
        b.Entity<SponsorApiKey>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SponsorCompanyId).IsRequired().HasMaxLength(64);
            e.Property(x => x.KeyHash).IsRequired().HasMaxLength(64);
            e.Property(x => x.KeyPrefix).IsRequired().HasMaxLength(16);
            e.Property(x => x.Label).HasMaxLength(300);
            e.Property(x => x.IssuedByEmail).HasMaxLength(320);

            // Validate/GetCurrent look up the newest non-revoked key per pair.
            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId, x.RevokedAt });
        });

        // --- GroupPhotoRegistration --------------------------------------------
        b.Entity<GroupPhotoRegistration>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CompanyName).IsRequired().HasMaxLength(200);
            e.Property(x => x.ContactName).HasMaxLength(200);
            e.Property(x => x.ContactEmail).IsRequired().HasMaxLength(320);
            e.Property(x => x.InternalParticipants).HasMaxLength(1000);
            e.Property(x => x.Location).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(1000);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.EventId, x.ScheduledAtUtc });
        });

        // --- AppGameParticipation ----------------------------------------------
        b.Entity<AppGameParticipation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SponsorCompanyId).IsRequired().HasMaxLength(64);
            e.Property(x => x.CompanyName).IsRequired().HasMaxLength(200);
            e.Property(x => x.GiftDescription).HasMaxLength(500);
            e.Property(x => x.Notes).HasMaxLength(1000);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // One participation row per sponsor per edition.
            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId }).IsUnique();
        });

        // --- SponsorTokenVersion -----------------------------------------------
        b.Entity<SponsorTokenVersion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SponsorCompanyId).IsRequired().HasMaxLength(64);
            e.Property(x => x.BumpedByEmail).HasMaxLength(320);

            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId }).IsUnique();
        });

        // --- VolunteerAvailability ------------------------------------------
        b.Entity<VolunteerAvailability>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SelectedShifts).HasMaxLength(2000);
            e.Property(x => x.PreferredRole).HasMaxLength(200);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.EventId, x.ParticipantId }).IsUnique();
        });

        // --- VolunteerDayAvailability ---------------------------------------
        b.Entity<VolunteerDayAvailability>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Level).HasConversion<int>();
            e.Property(x => x.Note).HasMaxLength(500);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.EventId, x.ParticipantId, x.Day }).IsUnique();
        });

        // --- VolunteerCategory ----------------------------------------------
        b.Entity<VolunteerCategory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.EldkLeadName).HasMaxLength(200);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // Lead (organizer) + supervisor (volunteer) are optional pointers to
            // Participant. NoAction (not Restrict/SetNull): the Event already
            // cascade-deletes the edition's participants, so SQL Server refuses a
            // second cascade path to Participant from the same root.
            e.HasOne(x => x.LeadParticipant).WithMany()
                .HasForeignKey(x => x.LeadParticipantId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.SupervisorParticipant).WithMany()
                .HasForeignKey(x => x.SupervisorParticipantId)
                .OnDelete(DeleteBehavior.NoAction);

            // Organizer tree + supervisor "is this my category" lookups.
            e.HasIndex(x => new { x.EventId, x.Name });
            e.HasIndex(x => new { x.EventId, x.SupervisorParticipantId });
        });

        // --- VolunteerSubcategory -------------------------------------------
        b.Entity<VolunteerSubcategory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                // The category already cascade-deletes its subcategories; a
                // second cascade path from Event would be ambiguous.
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Category).WithMany(c => c.Subcategories)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.EventId, x.CategoryId });
        });

        // --- VolunteerTask --------------------------------------------------
        b.Entity<VolunteerTask>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).IsRequired().HasMaxLength(300);
            // §151: reuse Description as the "detailed description"; bumped to 4000
            // to match Prerequisites/Expectations (auto-generated guidance text).
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Shift).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<int>();
            // Buckets / plan fields (2026-06-15).
            e.Property(x => x.TimeEnd).HasMaxLength(200);
            e.Property(x => x.Criticality).HasConversion<int>();
            e.Property(x => x.ResponsibleTeam).HasMaxLength(200);
            e.Property(x => x.EldkLeadName).HasMaxLength(200);
            e.Property(x => x.Prerequisites).HasMaxLength(4000);
            e.Property(x => x.Expectations).HasMaxLength(4000);
            e.Property(x => x.Instructions).HasMaxLength(4000);
            e.Property(x => x.CompletedByEmail).HasMaxLength(320);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Subcategory).WithMany(s => s.Tasks)
                .HasForeignKey(x => x.SubcategoryId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.EventId, x.SubcategoryId });
            e.HasIndex(x => new { x.EventId, x.Status });
            // §151: the stable external key the Excel round-trip upserts by — unique.
            e.HasIndex(x => x.ExternalKey).IsUnique();
        });

        // --- VolunteerTaskAssignment (Task <-> volunteer, many-many) --------
        b.Entity<VolunteerTaskAssignment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AssignedByEmail).HasMaxLength(320);
            // Volunteer self-service shift decision (2026-06-16).
            e.Property(x => x.DecisionStatus).HasConversion<int>();
            e.Property(x => x.DecisionNote).HasMaxLength(1000);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Task).WithMany(t => t.Assignments)
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.NoAction);

            // A volunteer is linked to a task at most once.
            e.HasIndex(x => new { x.TaskId, x.ParticipantId }).IsUnique();
            // "My volunteer tasks" for a participant in an edition.
            e.HasIndex(x => new { x.EventId, x.ParticipantId });
        });

        // --- VolunteerHelpRequest (help channel) ----------------------------
        b.Entity<VolunteerHelpRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Message).IsRequired().HasMaxLength(2000);
            e.Property(x => x.Response).HasMaxLength(2000);
            e.Property(x => x.RespondedByEmail).HasMaxLength(320);
            e.Property(x => x.Status).HasConversion<int>();

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            // Task / Category / requester all NoAction to avoid multiple cascade
            // paths back to the edition root (Event cascade already covers them).
            e.HasOne(x => x.Task).WithMany(t => t.HelpRequests)
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Category).WithMany(c => c.HelpRequests)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.RequestedByParticipant).WithMany()
                .HasForeignKey(x => x.RequestedByParticipantId)
                .OnDelete(DeleteBehavior.NoAction);

            // Supervisor's category-scoped help inbox, newest first.
            e.HasIndex(x => new { x.EventId, x.CategoryId, x.Status });
            // A volunteer's own raised help requests.
            e.HasIndex(x => new { x.EventId, x.RequestedByParticipantId });
        });

        // --- VolunteerBucketSupervisor (Bucket <-> supervisor, many-many) ----
        b.Entity<VolunteerBucketSupervisor>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AppointedByEmail).HasMaxLength(320);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Category).WithMany(c => c.Supervisors)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.NoAction);

            // A volunteer supervises a given bucket at most once.
            e.HasIndex(x => new { x.CategoryId, x.ParticipantId }).IsUnique();
            // "Which buckets does this volunteer supervise?" lookup.
            e.HasIndex(x => new { x.EventId, x.ParticipantId });
        });

        // --- TaskAllocationDraft (pending allocation queue, draft -> commit) -
        b.Entity<TaskAllocationDraft>(e =>
        {
            e.HasKey(x => x.Id);
            // §150 route-by-team discriminator + lifecycle stage marker (stored int).
            e.Property(x => x.TargetRole).HasConversion<int>().HasDefaultValue(ParticipantRole.Volunteer);
            e.Property(x => x.Source).HasConversion<int>().HasDefaultValue(DraftSource.Manual);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.OwnerParticipant).WithMany()
                .HasForeignKey(x => x.OwnerParticipantId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Task).WithMany(t => t.AllocationDrafts)
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.NoAction);

            // The same person is queued onto one task at most once.
            e.HasIndex(x => new { x.TaskId, x.ParticipantId }).IsUnique();
            // The organizer's whole draft queue for an edition (Commit/Discard).
            e.HasIndex(x => new { x.EventId, x.OwnerParticipantId });
        });

        // --- AllocationScenario + moves (generic stage→simulate→commit, §129) -
        b.Entity<AllocationScenario>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>().HasDefaultValue(AllocationScenarioStatus.Draft);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.CommittedByEmail).HasMaxLength(320);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.OwnerParticipant).WithMany()
                .HasForeignKey(x => x.OwnerParticipantId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.DroppedParticipant).WithMany()
                .HasForeignKey(x => x.DroppedParticipantId)
                .OnDelete(DeleteBehavior.NoAction);

            // An organizer's scenarios for an edition (list/owner scope).
            e.HasIndex(x => new { x.EventId, x.OwnerParticipantId, x.Status });
        });

        b.Entity<AllocationScenarioMove>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Op).HasConversion<int>().HasDefaultValue(AllocationMoveOp.Assign);
            e.Property(x => x.TargetRole).HasConversion<int>().HasDefaultValue(ParticipantRole.Volunteer);

            // The moves cascade-delete with their parent scenario (a scenario owns them).
            e.HasOne(x => x.Scenario).WithMany(s => s.Moves)
                .HasForeignKey(x => x.ScenarioId)
                .OnDelete(DeleteBehavior.Cascade);
            // Task/Hotel/Participant FKs are NoAction (the Event cascade already covers the
            // edition root; a scenario move is staging, not live state).
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Task).WithMany()
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Hotel).WithMany()
                .HasForeignKey(x => x.HotelId)
                .OnDelete(DeleteBehavior.NoAction);

            // Same staged move at most once per scenario.
            e.HasIndex(x => new { x.ScenarioId, x.ParticipantId, x.TaskId, x.HotelId }).IsUnique();
        });

        // --- ErpCustomerLink (e-conomic ERP customer sync, REQUIREMENTS §7a) -
        b.Entity<ErpCustomerLink>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SponsorCompanyId).IsRequired().HasMaxLength(64);
            e.Property(x => x.CompanyName).IsRequired().HasMaxLength(200);
            e.Property(x => x.ErpCustomerNumber).HasMaxLength(64);
            e.Property(x => x.Cvr).HasMaxLength(32);
            e.Property(x => x.CvrValidationReason).HasMaxLength(60);

            // One ERP link per company per edition (idempotency key).
            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId }).IsUnique();
        });

        // --- ErpOrderLink (e-conomic ERP order create from webshop, §7a) ----
        b.Entity<ErpOrderLink>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SponsorCompanyId).IsRequired().HasMaxLength(64);
            e.Property(x => x.ErpOrderNumber).HasMaxLength(64);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.CurrencyCheckResult).HasMaxLength(40);
            e.Property(x => x.FxRateApplied).HasColumnType("decimal(18,6)");

            // One ERP order per webshop order per edition (idempotency key).
            e.HasIndex(x => new { x.EventId, x.WebshopOrderId }).IsUnique();
        });

        // --- GraphicAsset (SoMe graphics + SharePoint store, REQUIREMENTS §18) -
        b.Entity<GraphicAsset>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.StableKey).IsRequired().HasMaxLength(200);
            e.Property(x => x.SponsorCompanyId).HasMaxLength(64);
            e.Property(x => x.SharePointPath).HasMaxLength(1000);
            e.Property(x => x.SharePointUrl).HasMaxLength(2000);
            e.Property(x => x.StorageItemId).HasMaxLength(128);
            e.Property(x => x.FileName).HasMaxLength(300);
            e.Property(x => x.ReleasedByEmail).HasMaxLength(320);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            // Participant / Session are optional pointers. NoAction (not
            // Cascade/SetNull): the Event already cascade-deletes the edition's
            // participants AND sessions, so a second cascade path to either from
            // the same root would be ambiguous to SQL Server.
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Session).WithMany()
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.NoAction);

            // The stable key is the idempotency identity: one graphic per key per
            // edition. Re-generation UPSERTS by it; an overrule replaces the bytes
            // behind the SAME row, keeping the link stable.
            e.HasIndex(x => new { x.EventId, x.StableKey }).IsUnique();
            // Surface queries: "my (released) speaker/session graphics", and the
            // organizer review queue (Generated-but-not-released).
            e.HasIndex(x => new { x.EventId, x.ParticipantId, x.Status });
            e.HasIndex(x => new { x.EventId, x.Type, x.Status });
        });

        // --- GraphicsAssetLocation (per-persona SharePoint links, §18) --------
        b.Entity<GraphicsAssetLocation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PersonaGroup).HasConversion<int>();
            e.Property(x => x.SiteUrl).HasMaxLength(1000);
            e.Property(x => x.DriveName).HasMaxLength(200);
            e.Property(x => x.RootFolderPath).HasMaxLength(1000);
            e.Property(x => x.BrowseUrl).HasMaxLength(2000);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.LastUpdatedByEmail).HasMaxLength(320);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // One location row per (edition, persona group). The admin page
            // UPSERTS by it.
            e.HasIndex(x => new { x.EventId, x.PersonaGroup }).IsUnique();
        });

        // --- SoMePost (LinkedIn company-page scheduling queue, §19) -----------
        b.Entity<SoMePost>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.SponsorCompanyId).HasMaxLength(64);
            // Post bodies: LinkedIn caps a company post at 3000 chars; 8000 is ample.
            e.Property(x => x.AutoText).HasMaxLength(8000);
            e.Property(x => x.ManualTextOverride).HasMaxLength(8000);
            e.Property(x => x.ImageRef).HasMaxLength(2000);
            // Tags: newline-separated handle/URN list; 2000 is generous.
            e.Property(x => x.Tags).HasMaxLength(2000);
            e.Property(x => x.ExternalPostId).HasMaxLength(200);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.Property(x => x.LastUpdatedByEmail).HasMaxLength(320);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            // Participant / Session are optional soft pointers. NoAction (not
            // Cascade/SetNull): the Event already cascade-deletes the edition's
            // participants AND sessions, so a second cascade path to either from
            // the same root would be ambiguous to SQL Server (cf. GraphicAsset).
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Session).WithMany()
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.NoAction);

            // The dispatcher's hot query: due, Active, Queued posts for an edition
            // ordered by schedule time (the "social media calendar").
            e.HasIndex(x => new { x.EventId, x.Status, x.IsActive, x.ScheduledAtUtc });
        });

        // --- SoMeSettings (per-edition SoMe queue config, §19) ----------------
        b.Entity<SoMeSettings>(e =>
        {
            e.HasKey(x => x.Id);
            // Operator config only — NOT secrets. The company-page URL/id is plain
            // config (like the Sessionize endpoint id); the LinkedIn OAuth token is
            // a Key Vault secret and is intentionally NOT a column here.
            e.Property(x => x.CompanyPageUrlOrOrgId).HasMaxLength(2000);
            e.Property(x => x.SpeakerPreAlertOrganizerEmail).HasMaxLength(320);
            e.Property(x => x.NotificationEmails).HasMaxLength(2000);
            e.Property(x => x.LastUpdatedByEmail).HasMaxLength(320);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // One settings row per edition (upserted on save).
            e.HasIndex(x => x.EventId).IsUnique();
        });

        // --- FeatureSetting (per-edition kill switches, §23) ------------------
        b.Entity<FeatureSetting>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FeatureKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.LastUpdatedByEmail).HasMaxLength(320);

            // Released-to ring (controlled rollout §23): stored as int, defaults
            // to Broad (3) so an existing row (and an upgraded DB) keeps a feature
            // visible to everyone until an organizer narrows its rollout. Sentinel
            // = Broad so an explicit earlier released ring is persisted, not reset.
            e.Property(x => x.ReleasedToRing)
                .HasConversion<int>()
                .HasSentinel(CommunityHub.Core.Settings.Ring.Broad)
                .HasDefaultValue(CommunityHub.Core.Settings.Ring.Broad);

            // Group-ring model (§23a): nullable per-feature ring OVERRIDE (null =
            // inherit the group ring) + nullable per-edition GROUP override (null =
            // catalog home group). Both stored as nullable int.
            e.Property(x => x.ReleasedToRingOverride).HasConversion<int?>();
            e.Property(x => x.GroupOverride).HasConversion<int?>();

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // One kill-switch row per (edition, feature key) — upserted on save.
            e.HasIndex(x => new { x.EventId, x.FeatureKey }).IsUnique();
        });

        // --- FeatureGroupSetting (per-edition GROUP lifecycle ring, §23a) -----
        b.Entity<FeatureGroupSetting>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.LastUpdatedByEmail).HasMaxLength(320);
            e.Property(x => x.Group).HasConversion<int>();
            e.Property(x => x.ReleasedToRing)
                .HasConversion<int>()
                .HasSentinel(CommunityHub.Core.Settings.Ring.Broad)
                .HasDefaultValue(CommunityHub.Core.Settings.Ring.Broad);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // One group-ring row per (edition, group) — upserted on save.
            e.HasIndex(x => new { x.EventId, x.Group }).IsUnique();
        });

        // --- ScheduleEntry (generic role-tagged key dates / schedule) ---------
        b.Entity<ScheduleEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Location).HasMaxLength(300);
            e.Property(x => x.Roles).HasMaxLength(200).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.LastUpdatedByEmail).HasMaxLength(320);
            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.EventId, x.StartsAt });
        });

        // --- ConfigOverride (per-edition admin-editable config, Phase 1) ------
        b.Entity<ConfigOverride>(e =>
        {
            e.HasKey(x => x.Id);

            // Section stored as int (enum); a new value is just a larger int —
            // additive, no schema change for a future section.
            e.Property(x => x.Section).HasConversion<int>();

            // The partial JSON fragment. nvarchar(max): a section override can be
            // arbitrarily large (whole sponsor task-set tree). Never a secret.
            e.Property(x => x.OverrideJson).IsRequired();

            e.Property(x => x.UpdatedByEmail).HasMaxLength(320);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // One override row per (edition, section) — upserted on save.
            e.HasIndex(x => new { x.EventId, x.Section }).IsUnique();
        });

        // --- EmailTemplateOverride (per-edition editable email templates §25h) -
        b.Entity<EmailTemplateOverride>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TemplateKey).IsRequired().HasMaxLength(100);
            // The full edited template text (subject line + body). nvarchar(max).
            e.Property(x => x.OverrideText).IsRequired();
            e.Property(x => x.UpdatedByEmail).HasMaxLength(320);
            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            // One override per (edition, template) — upserted on save.
            e.HasIndex(x => new { x.EventId, x.TemplateKey }).IsUnique();
        });

        // --- SavedSession (attendee personal "My plan", saved sessions) -------
        b.Entity<SavedSession>(e =>
        {
            e.HasKey(x => x.Id);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            // Participant + Session are NoAction (not Cascade): the Event already
            // cascade-deletes the edition's participants AND its sessions, so a
            // second cascade path to either from the same root would be ambiguous
            // to SQL Server (same reasoning as SessionSpeaker).
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Session).WithMany()
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.NoAction);

            // Idempotent toggle: a person saves a given session at most once.
            e.HasIndex(x => new { x.EventId, x.ParticipantId, x.SessionId }).IsUnique();
            // "My plan" lookup: all of a participant's saved sessions in an edition.
            e.HasIndex(x => new { x.EventId, x.ParticipantId });
        });
    }
}
