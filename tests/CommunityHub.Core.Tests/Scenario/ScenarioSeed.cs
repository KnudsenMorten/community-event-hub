using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// Rich, realistic scenario seed for the end-to-end CEH simulation tests.
///
/// Stands up ONE full event edition (ELDK27-style) with a representative cast:
/// organizers (only @expertslive.dk — the one allowed real domain), a Master
/// Class speaker + plain session speakers, sponsors with booth tasks + leads,
/// volunteers, attendees, plus the supporting rows (speaker profiles, sponsor
/// info, leads pipeline, organizer action-queue items). Everything synthetic is
/// tagged <see cref="Participant.IsTestUser"/> = true so a go-live cleanup can
/// delete it without touching real registrations.
///
/// Idempotent: <see cref="Seed"/> is a no-op if the event already exists, so a
/// test can call it repeatedly against the same context. Works against the EF
/// in-memory provider (default in these tests) or a throwaway SQLEXPRESS DB.
///
/// NO real customer / person names, secrets, real Sessionize ids or tenant ids
/// appear here — only generic descriptors and example.test addresses (plus the
/// one allowed @expertslive.dk organizer domain).
/// </summary>
public static class ScenarioSeed
{
    public const string EventCode = "ELDK27";

    // Organizer is the only role allowed a real domain (@expertslive.dk).
    public const string OrganizerEmail = "organizer@expertslive.dk";

    // Synthetic cast — example.test is reserved for documentation/tests (RFC 6761).
    public const string MasterclassSpeakerEmail = "masterclass.speaker@example.test";
    public const string SpeakerOneEmail = "speaker.one@example.test";
    public const string SpeakerTwoEmail = "speaker.two@example.test";
    public const string SponsorContactEmail = "sponsor.contact@example.test";
    public const string SponsorContact2Email = "sponsor.contact2@example.test";
    public const string VolunteerEmail = "volunteer.one@example.test";
    public const string AttendeeEmail = "attendee.one@example.test";

    // Sponsor company (external Company Manager id carried as a string).
    public const string SponsorCompanyId = "9001";
    // The canonical PUBLIC company name (company_name_public) — short marketing
    // form, the one that must win the public→legal→billing fallback chain.
    public const string SponsorPublicName = "Contoso Cloud";
    public const string SponsorLegalName = "Contoso Cloud Solutions A/S";

    /// <summary>The ids of every row the seed created, for convenient assertions.</summary>
    public sealed record SeedResult(
        int EventId,
        int OrganizerId,
        int MasterclassSpeakerId,
        int SpeakerOneId,
        int SpeakerTwoId,
        int SponsorContactId,
        int SponsorContact2Id,
        int VolunteerId,
        int AttendeeId);

    public static async Task<SeedResult> SeedAsync(
        CommunityHubDbContext db, CancellationToken ct = default)
    {
        var existing = await db.Events.FirstOrDefaultAsync(e => e.Code == EventCode, ct);
        if (existing is not null)
        {
            // Idempotent: hand back the existing ids.
            return await BuildResultAsync(db, existing.Id, ct);
        }

        var now = new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero);

        var evt = new Event
        {
            Code = EventCode,
            CommunityName = "Community Events Demo",
            DisplayName = "Community Events Demo 2027",
            StartDate = new DateOnly(2027, 2, 4),
            EndDate = new DateOnly(2027, 2, 5),
            PreDayDate = new DateOnly(2027, 2, 3),
            VenueName = "Demo Conference Center",
            HubHostname = "hub.example.test",
            IsActive = true,
            CreatedAt = now,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync(ct);

        Participant Add(string email, string name, ParticipantRole role,
            string? companyId = null, bool active = true)
        {
            var p = new Participant
            {
                EventId = evt.Id,
                Email = email,
                FullName = name,
                Role = role,
                SponsorCompanyId = companyId,
                IsActive = active,
                IsTestUser = true,        // every seeded row is synthetic
                CreatedAt = now,
            };
            db.Participants.Add(p);
            return p;
        }

        var organizer = Add(OrganizerEmail, "Demo Organizer", ParticipantRole.Organizer);
        // The "Master Class" speaker is now a plain Speaker; the pre-day nuance
        // lives on SpeakerProfile.SpeakingPreDay (set on their profile below).
        var mc = Add(MasterclassSpeakerEmail, "Masterclass Mentor", ParticipantRole.Speaker);
        var s1 = Add(SpeakerOneEmail, "Session Speaker One", ParticipantRole.Speaker);
        var s2 = Add(SpeakerTwoEmail, "Session Speaker Two", ParticipantRole.Speaker);
        var sp = Add(SponsorContactEmail, "Sponsor Contact", ParticipantRole.Sponsor, SponsorCompanyId);
        var sp2 = Add(SponsorContact2Email, "Second Sponsor Contact", ParticipantRole.Sponsor, SponsorCompanyId);
        var vol = Add(VolunteerEmail, "Helpful Volunteer", ParticipantRole.Volunteer);
        var att = Add(AttendeeEmail, "Curious Attendee", ParticipantRole.Attendee);
        await db.SaveChangesAsync(ct);

        // --- Speaker profiles (hub-collected + Sessionize-imported fields) ----
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = evt.Id, ParticipantId = mc.Id, CreatedAt = now,
            Tagline = "Master Class lead", Biography = "Veteran practitioner.",
            // Pre-day (Master Class) speaker — drives the masterclass-only
            // milestone + pre-day lunch entitlement.
            SpeakingPreDay = true,
        });
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = evt.Id, ParticipantId = s1.Id, CreatedAt = now,
            Tagline = "Cloud engineer",
        });

        // --- Sponsor company facts + canonical public name --------------------
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = evt.Id,
            SponsorCompanyId = SponsorCompanyId,
            CompanyDescriptionShort = SponsorPublicName,
        });

        // The leads grid resolves the company display name from the upload
        // location / lead rows; seed an upload location carrying the PUBLIC name
        // so company-name resolution can be asserted GUI-side.
        db.SponsorUploadLocations.Add(new SponsorUploadLocation
        {
            EventId = evt.Id,
            SponsorCompanyId = SponsorCompanyId,
            CompanyName = SponsorPublicName,
            FolderKey = "logo",
            Subfolder = "logo",
            FolderPath = "/Sponsors/9001/logo",
        });

        // --- Booth tasks for the sponsor company (deliverables) ---------------
        // Mandatory deliverables + one optional paid add-on, scoped to the
        // sponsor company so any linked contact can manage them.
        void BoothTask(string title, string slug, DateOnly due, bool mandatory, TaskState state)
            => db.Tasks.Add(new ParticipantTask
            {
                EventId = evt.Id,
                AssignedParticipantId = null,     // sponsor-company task, not a person
                SponsorCompanyId = SponsorCompanyId,
                Title = title,
                DueDate = due,
                State = state,
                IsMandatory = mandatory,
                SourceKey = $"booth:{SponsorCompanyId}:{slug}",
                CreatedAt = now,
            });

        BoothTask("Upload company logo", "logo", new DateOnly(2026, 12, 1), true, TaskState.Open);
        BoothTask("Provide booth layout", "layout", new DateOnly(2027, 1, 10), true, TaskState.Open);
        BoothTask("Submit session description", "session-desc", new DateOnly(2027, 1, 15), true, TaskState.Done);
        BoothTask("Order attendee-bag insert", "bag-insert", new DateOnly(2027, 1, 20), false, TaskState.Open);

        // --- Leads pipeline (Zoho-sourced content; hub-local status) ----------
        void Lead(string zohoId, string fullName, string company, SponsorLeadStatus status, int daysAgo)
            => db.SponsorLeads.Add(new SponsorLead
            {
                EventId = evt.Id,
                SponsorCompanyId = SponsorCompanyId,
                ZohoRecordId = zohoId,
                LeadKind = SponsorLeadKind.Lead,
                FullName = fullName,
                Company = company,
                Email = fullName.Replace(' ', '.').ToLowerInvariant() + "@example.test",
                Status = status,
                CapturedAt = now.AddDays(-daysAgo),
                LastSyncedAt = now,
            });

        Lead("zoho-lead-001", "Prospect Alpha", "Alpha Industries", SponsorLeadStatus.Open, 1);
        Lead("zoho-lead-002", "Prospect Beta", "Beta Corp", SponsorLeadStatus.Open, 3);
        Lead("zoho-lead-003", "Prospect Gamma", "Gamma Ltd", SponsorLeadStatus.Processed, 10);
        Lead("zoho-lead-004", "Spam Bot", "n/a", SponsorLeadStatus.Junk, 2);

        // --- Volunteer + attendee supporting rows -----------------------------
        // (The volunteer wizard / attendee reconciliation create their own rows
        //  in the scenario tests; seed an attendee with a clean reconciliation.)
        db.Attendees.Add(new Core.Domain.Attendee
        {
            EventId = evt.Id,
            Email = AttendeeEmail,
            FirstName = "Curious", LastName = "Attendee",
            TicketStatus = TicketStatus.TwoDay,
            BookingStatus = MasterClassBookingStatus.Booked,
            MasterClassName = "Demo Master Class",
            HasReconciliationMismatch = false,
            CreatedAt = now,
        });
        // One mismatch attendee so the organizer dashboard shows a non-zero count.
        db.Attendees.Add(new Core.Domain.Attendee
        {
            EventId = evt.Id,
            Email = "mismatch.attendee@example.test",
            FirstName = "Mismatch", LastName = "Attendee",
            TicketStatus = TicketStatus.TwoDay,
            BookingStatus = MasterClassBookingStatus.NotBooked,
            HasReconciliationMismatch = true,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);

        return await BuildResultAsync(db, evt.Id, ct);
    }

    private static async Task<SeedResult> BuildResultAsync(
        CommunityHubDbContext db, int eventId, CancellationToken ct)
    {
        var people = await db.Participants
            .Where(p => p.EventId == eventId)
            .ToDictionaryAsync(p => p.Email, p => p.Id, ct);

        int Id(string email) => people.TryGetValue(email, out var id) ? id : 0;

        return new SeedResult(
            eventId,
            Id(OrganizerEmail),
            Id(MasterclassSpeakerEmail),
            Id(SpeakerOneEmail),
            Id(SpeakerTwoEmail),
            Id(SponsorContactEmail),
            Id(SponsorContact2Email),
            Id(VolunteerEmail),
            Id(AttendeeEmail));
    }
}
