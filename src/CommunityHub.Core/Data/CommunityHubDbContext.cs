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
    public DbSet<Attendee> Attendees => Set<Attendee>();
    public DbSet<HotelBooking> HotelBookings => Set<HotelBooking>();
    public DbSet<DinnerSignup> DinnerSignups => Set<DinnerSignup>();
    public DbSet<VolunteerAvailability> VolunteerAvailabilities => Set<VolunteerAvailability>();
    public DbSet<SwagPreference> SwagPreferences => Set<SwagPreference>();
    public DbSet<LunchSignup> LunchSignups => Set<LunchSignup>();
    public DbSet<SpeakerProfile> SpeakerProfiles => Set<SpeakerProfile>();
    public DbSet<TravelReimbursement> TravelReimbursements => Set<TravelReimbursement>();
    public DbSet<OrganizerActionItem> OrganizerActionItems => Set<OrganizerActionItem>();
    public DbSet<SponsorInfo> SponsorInfos => Set<SponsorInfo>();
    public DbSet<SponsorUploadLocation> SponsorUploadLocations => Set<SponsorUploadLocation>();
    public DbSet<SponsorUploadFile> SponsorUploadFiles => Set<SponsorUploadFile>();
    public DbSet<SurveyResponse> SurveyResponses => Set<SurveyResponse>();
    public DbSet<SurveyResponsePick> SurveyResponsePicks => Set<SurveyResponsePick>();

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
            e.Property(x => x.Role).HasConversion<int>();
            e.Property(x => x.SponsorCompanyId).HasMaxLength(64);

            e.HasOne(x => x.Event)
                .WithMany(ev => ev.Participants)
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // Email is the login identity - unique WITHIN an edition, so the
            // same person can exist in ELDK27 and ELDK28 as separate rows.
            e.HasIndex(x => new { x.EventId, x.Email }).IsUnique();
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
            e.Property(x => x.Tagline).HasMaxLength(500);
            e.Property(x => x.Biography).HasMaxLength(4000);
            e.Property(x => x.Blog).HasMaxLength(500);
            e.Property(x => x.LinkedIn).HasMaxLength(500);
            e.Property(x => x.Twitter).HasMaxLength(200);

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany()
                .HasForeignKey(x => x.ParticipantId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.EventId, x.ParticipantId }).IsUnique();
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

            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.EventId, x.SponsorCompanyId }).IsUnique();
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
    }
}
