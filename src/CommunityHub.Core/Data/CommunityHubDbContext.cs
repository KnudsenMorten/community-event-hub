using CommunityHub.Core.Domain;
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
public class CommunityHubDbContext : DbContext
{
    public CommunityHubDbContext(DbContextOptions<CommunityHubDbContext> options)
        : base(options)
    {
    }

    public DbSet<Event> Events => Set<Event>();
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<LoginPin> LoginPins => Set<LoginPin>();
    public DbSet<ParticipantTask> Tasks => Set<ParticipantTask>();
    public DbSet<SentReminder> SentReminders => Set<SentReminder>();
    public DbSet<EmailLog> EmailLogs => Set<EmailLog>();
    public DbSet<Attendee> Attendees => Set<Attendee>();
    public DbSet<HotelBooking> HotelBookings => Set<HotelBooking>();
    public DbSet<Hotel> Hotels => Set<Hotel>();
    public DbSet<DinnerSignup> DinnerSignups => Set<DinnerSignup>();
    public DbSet<DietaryRequirement> DietaryRequirements => Set<DietaryRequirement>();
    public DbSet<VolunteerAvailability> VolunteerAvailabilities => Set<VolunteerAvailability>();
    public DbSet<SwagPreference> SwagPreferences => Set<SwagPreference>();
    public DbSet<LunchSignup> LunchSignups => Set<LunchSignup>();
    public DbSet<SpeakerProfile> SpeakerProfiles => Set<SpeakerProfile>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionSpeaker> SessionSpeakers => Set<SessionSpeaker>();
    public DbSet<SessionQuestion> SessionQuestions => Set<SessionQuestion>();
    public DbSet<SessionEvaluation> SessionEvaluations => Set<SessionEvaluation>();
    public DbSet<MasterClassParticipant> MasterClassParticipants => Set<MasterClassParticipant>();
    public DbSet<SpeakerBackstageEmailSync> SpeakerBackstageEmailSyncs => Set<SpeakerBackstageEmailSync>();
    public DbSet<SessionizeEndpointSetting> SessionizeEndpointSettings => Set<SessionizeEndpointSetting>();
    public DbSet<TravelReimbursement> TravelReimbursements => Set<TravelReimbursement>();
    public DbSet<OrganizerActionItem> OrganizerActionItems => Set<OrganizerActionItem>();
    public DbSet<SponsorInfo> SponsorInfos => Set<SponsorInfo>();
    public DbSet<SponsorUploadLocation> SponsorUploadLocations => Set<SponsorUploadLocation>();
    public DbSet<SponsorUploadFile> SponsorUploadFiles => Set<SponsorUploadFile>();
    public DbSet<SurveyResponse> SurveyResponses => Set<SurveyResponse>();
    public DbSet<SurveyResponsePick> SurveyResponsePicks => Set<SurveyResponsePick>();
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

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

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
            e.Property(x => x.Role).HasConversion<int>();
            e.Property(x => x.SponsorCompanyId).HasMaxLength(64);
            e.Property(x => x.CalendarFeedToken).HasMaxLength(64);
            // Onboarding lifecycle: the pre-selection gate + inbound source.
            e.Property(x => x.LifecycleState).HasConversion<int>();
            e.Property(x => x.QueueSource).HasConversion<int>();

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

            // "Everyone in this hotel" grouping query.
            e.HasIndex(x => new { x.EventId, x.HotelId });

            // Email is the login identity - unique WITHIN an edition, so the
            // same person can exist in ELDK27 and ELDK28 as separate rows.
            e.HasIndex(x => new { x.EventId, x.Email }).IsUnique();

            // Pre-selection queue: filter an edition's rows by lifecycle state.
            e.HasIndex(x => new { x.EventId, x.LifecycleState });

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
            e.Property(x => x.TicketStatus).HasConversion<int>();
            e.Property(x => x.BookingStatus).HasConversion<int>();

            e.HasOne(x => x.Event)
                .WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // Email is the reconciliation key - unique within an edition, so
            // the reconciliation job can upsert by (EventId, Email).
            e.HasIndex(x => new { x.EventId, x.Email }).IsUnique();
            // Fast filter for the organizer mismatch view.
            e.HasIndex(x => new { x.EventId, x.HasReconciliationMismatch });
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
            e.Property(x => x.Tagline).HasMaxLength(500);
            e.Property(x => x.Biography).HasMaxLength(4000);
            e.Property(x => x.Blog).HasMaxLength(500);
            e.Property(x => x.LinkedIn).HasMaxLength(500);
            e.Property(x => x.Twitter).HasMaxLength(200);
            // HARD GATE for the outbound speaker-bio sync: defaults FALSE so no
            // speaker is ever made public in Backstage until explicitly approved.
            e.Property(x => x.SelectedForPublish).HasDefaultValue(false);
            e.Property(x => x.PhotoUrl).HasMaxLength(1000);
            // Comma-separated set of speaker-edited bio field tokens (the
            // per-field dirty set the delta sync reads). Small; 200 is ample.
            e.Property(x => x.SpeakerEditedFields).HasMaxLength(200);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.EventId, x.ParticipantId }).IsUnique();
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
            // Master-class public logistics page + Zoho Booking endpoint (§ 6c).
            e.Property(x => x.PublicSlug).HasMaxLength(64);
            e.Property(x => x.LogisticsText).HasMaxLength(8000);
            e.Property(x => x.LogisticsUpdatedByEmail).HasMaxLength(320);
            e.Property(x => x.BookingEndpointUri).HasMaxLength(2000);

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
        });

        // --- MasterClassParticipant (Zoho Booking → hub, § 6c) ---------------
        b.Entity<MasterClassParticipant>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.BookingRecordId).IsRequired().HasMaxLength(128);
            e.Property(x => x.BookedEmail).IsRequired().HasMaxLength(320);
            e.Property(x => x.BookedName).HasMaxLength(200);
            e.Property(x => x.BookingStatus).HasMaxLength(60);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            // NoAction on Session + Participant: the Event already cascade-deletes
            // the edition's sessions AND participants, so a second cascade path from
            // the same root would be ambiguous to SQL Server (same reasoning as
            // SessionSpeaker).
            e.HasOne(x => x.Session).WithMany(s => s.MasterClassParticipants)
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.NoAction);

            // Idempotency key: a Zoho Booking record links to a session at most
            // once. Re-syncing the same booking updates the row in place.
            e.HasIndex(x => new { x.EventId, x.SessionId, x.BookingRecordId }).IsUnique();
            // "Booked participants for this master class" + "my master classes".
            e.HasIndex(x => new { x.EventId, x.SessionId });
            e.HasIndex(x => x.ParticipantId);
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

            // Organizer "all questions" + per-session (speaker) inbox, newest first.
            e.HasIndex(x => new { x.EventId, x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.SessionId, x.Status });
            // Soft rate-limit lookup by IP hash within an edition.
            e.HasIndex(x => new { x.EventId, x.IpHash });
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

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId }).IsUnique();
            // The public sponsors page groups by tier; index it for the lookup.
            e.HasIndex(x => new { x.EventId, x.Tier });
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
            e.Property(x => x.Description).HasMaxLength(2000);
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
    }
}
