using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Sponsors;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="CommandCenterService"/> — the organizer
/// command-center landing (REQUIREMENTS §20 Organizer). The command center is the
/// <i>actionable</i> sibling of <see cref="OrganizerOverviewService"/> (§11): it
/// answers "is the event on track and what do I do next" with registrations,
/// onboarding completion % per persona, hotel/swag/lunch/dinner headcounts,
/// session + sponsor status, and a "what needs my attention" / today's-tasks
/// call-out — every number a read-only aggregate over entities that already exist.
///
/// Uses the EF Core InMemory provider so the real DbContext mapping + LINQ run
/// (no SQL), seeded with one rich edition plus a second edition whose rows are
/// planted to prove every number is event-scoped. A separate empty-event test
/// proves the snapshot is a safe all-zero / all-clear state (no divide-by-zero).
/// </summary>
public sealed class CommandCenterServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    // Fixed "today" so overdue / due-today maths is deterministic.
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2027, 1, 15);
    private static readonly DateOnly Past = new(2027, 1, 1);
    private static readonly DateOnly Future = new(2027, 2, 1);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"cmdcenter-{Guid.NewGuid():N}")
            .Options);

    private static CommandCenterService NewSvc(CommunityHubDbContext db)
    {
        var clock = new FixedClock(Now);
        var actions = new OrganizerActionItemService(db, clock);
        var onboarding = new OnboardingService(db, actions, clock);
        var speakerReadiness = new SpeakerReadinessService(db);
        var sponsorDeliverables = new SponsorDeliverablesService(db);
        var syncHealth = new SyncHealthService(db, clock);
        return new CommandCenterService(
            db, onboarding, speakerReadiness, sponsorDeliverables, syncHealth, clock);
    }

    /// <summary>Mark every onboarding step done — satisfies any persona's required subset.</summary>
    private static void MarkFullyOnboarded(Participant p)
    {
        p.OnboardingCompleted_Bio = true;
        p.OnboardingCompleted_Picture = true;
        p.OnboardingCompleted_Hotel = true;
        p.OnboardingCompleted_Appreciation = true;
        p.OnboardingCompleted_Swag = true;
    }

    /// <summary>Seed one fully-populated edition; returns participant ids by handle.</summary>
    private static async Task<Dictionary<string, int>> SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "CC27", CommunityName = "Command Center Test",
            DisplayName = "Command Center 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        });
        // A second edition so scoping can be asserted.
        db.Events.Add(new Event
        {
            Id = OtherEventId, Code = "OTHER", CommunityName = "Other",
            DisplayName = "Other 2027",
            StartDate = new DateOnly(2027, 5, 1), EndDate = new DateOnly(2027, 5, 2),
            IsActive = false,
        });

        var ids = new Dictionary<string, int>();
        Participant P(string handle, ParticipantRole role, bool active = true,
                      bool onboarded = false, int ev = EventId)
        {
            var p = new Participant
            {
                EventId = ev, Email = $"{handle}@expertslive.dk",
                FullName = handle, Role = role, IsActive = active,
                // Only Active rows enter the onboarding overview pipeline.
                LifecycleState = ParticipantLifecycleState.Active,
            };
            if (onboarded) MarkFullyOnboarded(p);
            db.Participants.Add(p);
            return p;
        }

        // Speakers: 1 of 2 fully onboarded -> persona Speaker = 50%.
        var sp1 = P("sp1", ParticipantRole.Speaker, onboarded: true);
        var sp2 = P("sp2", ParticipantRole.Speaker, onboarded: false);
        // Volunteers: vol1 onboarded, vol2 not, volPending is a pending application.
        var vol1 = P("vol1", ParticipantRole.Volunteer, onboarded: true);
        var vol2 = P("vol2", ParticipantRole.Volunteer, onboarded: false);
        var volPending = P("volp", ParticipantRole.Volunteer, active: false, onboarded: false);
        var spon = P("spon", ParticipantRole.Sponsor, onboarded: true);
        // Sponsor onboarding is wizard-driven (company info + logos), not the
        // Appreciation/Swag flags MarkFullyOnboarded sets — link spon to its
        // company so the enriched SponsorInfo "42" below makes it complete.
        spon.SponsorCompanyId = "42";
        var org = P("org", ParticipantRole.Organizer, onboarded: true);
        // Other-edition participant — must never leak into EventId counts.
        P("ghost", ParticipantRole.Speaker, ev: OtherEventId, onboarded: true);
        await db.SaveChangesAsync();

        foreach (var (h, p) in new[]
        {
            ("sp1", sp1), ("sp2", sp2), ("vol1", vol1), ("vol2", vol2),
            ("volp", volPending), ("spon", spon), ("org", org),
        })
        {
            ids[h] = p.Id;
        }

        // --- Tasks (the today's / overdue call-out + sponsor task %) ---------
        void Task(int? assignee, string title, TaskState state, DateOnly? due,
                  string? sourceKey = null, int ev = EventId)
            => db.Tasks.Add(new ParticipantTask
            {
                EventId = ev, AssignedParticipantId = assignee, Title = title,
                State = state, DueDate = due, SourceKey = sourceKey,
            });

        Task(sp2.Id, "Submit slides", TaskState.Open, Past);        // overdue + assigned
        Task(null, "Book photographer", TaskState.Open, Today);     // due today + unassigned
        Task(sp1.Id, "Confirm bio", TaskState.Done, Past);          // done -> not in list
        Task(vol1.Id, "Bring lanyards", TaskState.Open, Future);    // future -> not in list
        // Sponsor tasks (sourceKey "sponsor:*"): 1 of 2 done -> 50%.
        Task(spon.Id, "Upload logo", TaskState.Done, Future, "sponsor:42:logo");
        Task(null, "Booth layout", TaskState.Open, Past, "sponsor:42:booth"); // overdue too
        // Other-edition task — scope guard (must not count as overdue here).
        Task(null, "Ghost task", TaskState.Open, Past, ev: OtherEventId);

        // --- Headcounts -----------------------------------------------------
        db.HotelBookings.Add(new HotelBooking { EventId = EventId, ParticipantId = sp1.Id, NeedsRoom = true });
        db.HotelBookings.Add(new HotelBooking { EventId = EventId, ParticipantId = sp2.Id, NeedsRoom = false });
        db.HotelBookings.Add(new HotelBooking { EventId = OtherEventId, ParticipantId = sp1.Id, NeedsRoom = true }); // scope guard

        db.SwagPreferences.Add(new SwagPreference { EventId = EventId, ParticipantId = sp1.Id, WantsPolo = true, WantsJacket = false, WantsGift = false });
        db.SwagPreferences.Add(new SwagPreference { EventId = EventId, ParticipantId = vol1.Id, WantsPolo = false, WantsJacket = false, WantsGift = true });
        db.SwagPreferences.Add(new SwagPreference { EventId = EventId, ParticipantId = vol2.Id, WantsPolo = false, WantsJacket = false, WantsGift = false }); // wants nothing
        db.SwagPreferences.Add(new SwagPreference { EventId = OtherEventId, ParticipantId = sp1.Id, WantsPolo = false, WantsJacket = true, WantsGift = false }); // scope guard

        db.LunchSignups.Add(new LunchSignup { EventId = EventId, ParticipantId = vol1.Id, LunchSetupDay = true });
        db.LunchSignups.Add(new LunchSignup { EventId = EventId, ParticipantId = vol2.Id, LunchPreDay = true });
        db.LunchSignups.Add(new LunchSignup { EventId = OtherEventId, ParticipantId = vol1.Id, LunchSetupDay = true }); // scope guard

        // Dinner headcount = attendees (Attending) + plus-ones: (1 + 2) for sp1 = 3.
        db.DinnerSignups.Add(new DinnerSignup { EventId = EventId, ParticipantId = sp1.Id, Attending = true, PlusOneCount = 2 });
        db.DinnerSignups.Add(new DinnerSignup { EventId = EventId, ParticipantId = sp2.Id, Attending = false, PlusOneCount = 5 }); // not attending -> 0
        db.DinnerSignups.Add(new DinnerSignup { EventId = OtherEventId, ParticipantId = sp1.Id, Attending = true, PlusOneCount = 9 }); // scope guard

        // --- Sessions (scheduled = has StartsAt + Room; service sessions excluded) ---
        db.Sessions.Add(new Session { EventId = EventId, Title = "Keynote", StartsAt = Now, Room = "Hall A" });   // scheduled
        db.Sessions.Add(new Session { EventId = EventId, Title = "Deep dive", StartsAt = null, Room = null });    // unscheduled
        db.Sessions.Add(new Session { EventId = EventId, Title = "Lunch break", IsServiceSession = true });       // excluded
        db.Sessions.Add(new Session { EventId = OtherEventId, Title = "Ghost session", StartsAt = Now, Room = "X" }); // scope guard

        // --- Sponsors -------------------------------------------------------
        // Company info + logos present ⇒ spon's wizard onboarding is complete (Silver, no booth).
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "42",
            WebsiteUrl = "https://co42.test",
            LogoRasterPath = "uploads/sponsors/42/logo.png",
        });
        db.SponsorInfos.Add(new SponsorInfo { EventId = OtherEventId, SponsorCompanyId = "99" }); // scope guard

        // --- Attendees (with a reconciliation mismatch) ---------------------
        db.Attendees.Add(new Attendee { EventId = EventId, Email = "a1@expertslive.dk", FirstName = "A", LastName = "1" });
        db.Attendees.Add(new Attendee { EventId = EventId, Email = "a2@expertslive.dk", FirstName = "A", LastName = "2", HasReconciliationMismatch = true });
        db.Attendees.Add(new Attendee { EventId = OtherEventId, Email = "ghost@expertslive.dk", FirstName = "G", LastName = "host", HasReconciliationMismatch = true }); // scope guard

        // --- Volunteer work tree (unassigned coverage + open help) ----------
        var cat = new VolunteerCategory { Id = 10, EventId = EventId, Name = "Registration" };
        db.VolunteerCategories.Add(cat);
        var sub = new VolunteerSubcategory { Id = 20, EventId = EventId, CategoryId = 10, Name = "Badge desk" };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();

        var vtAssigned = new VolunteerTask { Id = 30, EventId = EventId, SubcategoryId = 20, Title = "Morning badge desk" };
        var vtOpen = new VolunteerTask { Id = 31, EventId = EventId, SubcategoryId = 20, Title = "Afternoon badge desk" };   // unassigned
        var vtCancelled = new VolunteerTask { Id = 32, EventId = EventId, SubcategoryId = 20, Title = "Old job", Status = VolunteerTaskStatus.Cancelled };
        var vtGhost = new VolunteerTask { Id = 33, EventId = OtherEventId, SubcategoryId = 20, Title = "Ghost vol task" };    // scope guard
        db.VolunteerTasks.AddRange(vtAssigned, vtOpen, vtCancelled, vtGhost);
        await db.SaveChangesAsync();

        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = vtAssigned.Id, ParticipantId = vol1.Id,
        });

        db.VolunteerHelpRequests.Add(new VolunteerHelpRequest
        {
            EventId = EventId, TaskId = vtAssigned.Id, CategoryId = cat.Id,
            RequestedByParticipantId = vol1.Id, Message = "Need lanyards",
            Status = VolunteerHelpStatus.Open,
        });
        db.VolunteerHelpRequests.Add(new VolunteerHelpRequest
        {
            EventId = EventId, TaskId = vtAssigned.Id, CategoryId = cat.Id,
            RequestedByParticipantId = vol1.Id, Message = "Resolved one",
            Status = VolunteerHelpStatus.Resolved,
        });
        db.VolunteerHelpRequests.Add(new VolunteerHelpRequest
        {
            EventId = OtherEventId, TaskId = vtGhost.Id, CategoryId = cat.Id,
            RequestedByParticipantId = vol1.Id, Message = "Ghost help",
            Status = VolunteerHelpStatus.Open,
        });

        // --- Organizer action items (open + resolved) -----------------------
        db.OrganizerActionItems.Add(new OrganizerActionItem
        {
            EventId = EventId, Type = "test", ParticipantId = sp1.Id,
            Summary = "Open item", CreatedAt = Now,
        });
        db.OrganizerActionItems.Add(new OrganizerActionItem
        {
            EventId = EventId, Type = "test", ParticipantId = sp2.Id,
            Summary = "Resolved item", CreatedAt = Now, ResolvedAt = Now,
        });
        db.OrganizerActionItems.Add(new OrganizerActionItem
        {
            EventId = OtherEventId, Type = "test", ParticipantId = sp1.Id,
            Summary = "Ghost item", CreatedAt = Now,
        });

        await db.SaveChangesAsync();
        return ids;
    }

    [Fact]
    public async Task Registrations_and_attendees_scoped_to_event()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var s = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal("Command Center 2027", s.EventDisplayName);
        Assert.Equal(7, s.TotalParticipants);     // ghost (other edition) excluded
        Assert.Equal(6, s.ActiveParticipants);    // volp inactive
        Assert.Equal(2, s.TotalAttendees);        // ghost attendee excluded
    }

    [Fact]
    public async Task Onboarding_overall_and_per_persona_percentages()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var s = await NewSvc(db).BuildAsync(EventId);

        // 7 active-pipeline people; 4 fully onboarded (sp1, vol1, spon, org) = 57%.
        // volp is Inactive-... actually Active LifecycleState but onboarded:false; counts as a row.
        Assert.Equal(57, s.OnboardingOverallPercent);   // round(4/7*100) = 57

        var speaker = Assert.Single(s.OnboardingByPersona, p => p.Persona == PersonaGroup.Speaker);
        Assert.Equal(2, speaker.Total);
        Assert.Equal(1, speaker.Completed);             // sp1 done, sp2 not
        Assert.Equal(50, speaker.Percent);
        Assert.Equal(1, speaker.Outstanding);

        var volunteer = Assert.Single(s.OnboardingByPersona, p => p.Persona == PersonaGroup.Volunteer);
        Assert.Equal(3, volunteer.Total);               // vol1 + vol2 + volp
        Assert.Equal(1, volunteer.Completed);           // only vol1
    }

    [Fact]
    public async Task Headcounts_hotel_swag_lunch_dinner_scoped()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var s = await NewSvc(db).BuildAsync(EventId);

        int Head(string key) => Assert.Single(s.Headcounts, h => h.Key == key).Count;
        Assert.Equal(1, Head("Hotel"));   // only NeedsRoom == true, other-edition excluded
        Assert.Equal(2, Head("Swag"));    // sp1 + vol1 want something; vol2 wants nothing
        Assert.Equal(2, Head("Lunch"));   // vol1 + vol2 signed up; other-edition excluded
        Assert.Equal(3, Head("Dinner"));  // sp1 attending (1) + 2 plus-ones; sp2 not attending
    }

    [Fact]
    public async Task Sessions_and_sponsor_status_scoped()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var s = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(2, s.SessionsTotal);         // service session excluded, other-edition excluded
        Assert.Equal(1, s.SessionsScheduled);     // only the keynote has StartsAt + Room
        Assert.Equal(1, s.SessionsUnscheduled);

        Assert.Equal(1, s.SponsorsTotal);         // other-edition sponsor excluded
        Assert.Equal(2, s.SponsorTasksTotal);
        Assert.Equal(1, s.SponsorTasksDone);
        Assert.Equal(50, s.SponsorTasksPercent);
    }

    [Fact]
    public async Task Attention_call_out_overdue_today_and_tiles()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var s = await NewSvc(db).BuildAsync(EventId);

        // Overdue (Submit slides + Booth layout) + due-today (Book photographer).
        Assert.Equal(2, s.TasksOverdue);
        Assert.Equal(1, s.TasksDueToday);
        // The list carries exactly the overdue + today rows, overdue flagged.
        Assert.Equal(3, s.OverdueAndTodayTasks.Count);
        Assert.Equal(2, s.OverdueAndTodayTasks.Count(t => t.IsOverdue));
        var dueToday = Assert.Single(s.OverdueAndTodayTasks, t => !t.IsOverdue);
        Assert.Equal("Book photographer", dueToday.Title);
        Assert.Null(dueToday.Assignee);   // unassigned

        int Tile(string key) => Assert.Single(s.AttentionTiles, t => t.Key == key).Count;
        Assert.Equal(2, Tile("OverdueTasks"));
        Assert.Equal(1, Tile("DueToday"));
        Assert.Equal(1, Tile("UnassignedVolunteerTasks"));    // vtOpen
        Assert.Equal(1, Tile("OpenHelpRequests"));            // resolved + other-edition excluded
        Assert.Equal(1, Tile("PendingVolunteers"));           // volp inactive
        Assert.Equal(1, Tile("ReconciliationMismatches"));    // a2 only
        Assert.Equal(1, Tile("OpenActionItems"));             // resolved + other-edition excluded
        Assert.Equal(1, Tile("UnscheduledSessions"));

        // Every attention tile deep-links somewhere (no dead ends) and is flagged.
        Assert.All(s.AttentionTiles, t =>
        {
            Assert.True(t.IsAttention);
            Assert.StartsWith("/Organizer/", t.LinkPage);
        });

        // With outstanding work, the snapshot is NOT all-clear.
        Assert.False(s.AllClear);
    }

    [Fact]
    public async Task Snapshot_is_pure_read_only_and_repeatable()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = NewSvc(db);

        var a = await svc.BuildAsync(EventId);
        var b = await svc.BuildAsync(EventId);

        // Same inputs -> same aggregates; building it never mutated the store.
        Assert.Equal(a.TotalParticipants, b.TotalParticipants);
        Assert.Equal(a.OnboardingOverallPercent, b.OnboardingOverallPercent);
        Assert.Equal(a.TasksOverdue, b.TasksOverdue);
        Assert.Equal(a.SponsorTasksPercent, b.SponsorTasksPercent);
    }

    [Fact]
    public async Task Other_edition_returns_its_own_scoped_snapshot()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // Scope guard from the OTHER side: the second edition sees only its rows.
        var s = await NewSvc(db).BuildAsync(OtherEventId);

        Assert.Equal("Other 2027", s.EventDisplayName);
        Assert.Equal(1, s.TotalParticipants);     // only the ghost speaker
        Assert.Equal(1, s.SponsorsTotal);         // only company 99
        Assert.Equal(1, Assert.Single(s.Headcounts, h => h.Key == "Hotel").Count);
    }

    [Fact]
    public async Task Empty_event_is_a_safe_all_clear_snapshot()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "EMPTY", CommunityName = "Empty",
            DisplayName = "Empty 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var s = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal("Empty 2027", s.EventDisplayName);
        Assert.Equal(0, s.TotalParticipants);
        Assert.Equal(0, s.TotalAttendees);
        Assert.Equal(0, s.OnboardingOverallPercent);    // no divide-by-zero
        Assert.Empty(s.OnboardingByPersona);
        Assert.Equal(0, s.SponsorTasksPercent);         // no divide-by-zero
        Assert.Equal(0, s.SessionsTotal);
        Assert.Equal(0, s.TasksOverdue);
        Assert.Equal(0, s.TasksDueToday);
        Assert.Empty(s.OverdueAndTodayTasks);
        // Every attention tile reads 0 and the snapshot reports a calm all-clear.
        Assert.All(s.AttentionTiles, t => Assert.Equal(0, t.Count));
        Assert.True(s.AllClear);

        // §131 live event-day surfaces are all-zero / never-synced on an empty edition.
        Assert.Equal(0, s.ExpectedAttendees);
        Assert.Equal(0, s.CheckedInCount);
        Assert.Equal(0, s.CheckedInPercent);          // no divide-by-zero
        Assert.Equal(0, s.MasterClassCount);
        Assert.Equal(0, s.MasterClassFillPercent);    // no divide-by-zero
        Assert.Equal(0, s.SpeakerTotal);
        Assert.Equal(0, s.SponsorCompaniesTotal);
        Assert.Equal(SyncHealthStatus.NeverSynced, s.SyncStatus);
        Assert.Null(s.SyncLastSuccessAt);
    }

    [Fact]
    public async Task EventDay_checkin_masterclass_and_ops_summaries_scoped()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "CC27", CommunityName = "CC", DisplayName = "CC 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.Events.Add(new Event
        {
            Id = OtherEventId, Code = "OTH", CommunityName = "Oth", DisplayName = "Oth 2027",
            StartDate = new DateOnly(2027, 5, 1), EndDate = new DateOnly(2027, 5, 2), IsActive = false,
        });

        // --- Attendees / check-in (active mirror is the expected base) ----------
        // 3 active (2 checked in), 1 cancelled-but-checked-in (excluded), 1 other-edition.
        db.Attendees.Add(new Attendee { EventId = EventId, Email = "a1@x", FirstName = "A", LastName = "1", MirrorState = MirrorState.Active, CheckedInAt = Now });
        db.Attendees.Add(new Attendee { EventId = EventId, Email = "a2@x", FirstName = "A", LastName = "2", MirrorState = MirrorState.Active, CheckedInAt = null });
        db.Attendees.Add(new Attendee { EventId = EventId, Email = "a3@x", FirstName = "A", LastName = "3", MirrorState = MirrorState.Active, CheckedInAt = Now });
        db.Attendees.Add(new Attendee { EventId = EventId, Email = "ax@x", FirstName = "A", LastName = "X", MirrorState = MirrorState.Cancelled, CheckedInAt = Now });
        db.Attendees.Add(new Attendee { EventId = OtherEventId, Email = "g@x", FirstName = "G", LastName = "h", MirrorState = MirrorState.Active, CheckedInAt = Now });

        // --- Master classes (fill + waitlist) ----------------------------------
        // MC1 cap 2: 2 confirmed -> full. MC2 cap 5: 1 confirmed + 1 offered + 3 waitlisted.
        var mc1 = new Session { EventId = EventId, Title = "MC One", Type = SessionType.MasterClass, MasterClassCapacity = 2 };
        var mc2 = new Session { EventId = EventId, Title = "MC Two", Type = SessionType.MasterClass, MasterClassCapacity = 5 };
        var mcGhost = new Session { EventId = OtherEventId, Title = "Ghost MC", Type = SessionType.MasterClass, MasterClassCapacity = 9 };
        db.Sessions.AddRange(mc1, mc2, mcGhost);
        await db.SaveChangesAsync();

        int seat = 1;
        void Signup(Session mc, MasterClassSignupStatus st, int ev = EventId) =>
            db.MasterClassSignups.Add(new MasterClassSignup
            {
                EventId = ev, SessionId = mc.Id, AttendeeId = seat++, Status = st,
            });
        Signup(mc1, MasterClassSignupStatus.Confirmed);
        Signup(mc1, MasterClassSignupStatus.Confirmed);
        Signup(mc2, MasterClassSignupStatus.Confirmed);
        Signup(mc2, MasterClassSignupStatus.Offered);
        Signup(mc2, MasterClassSignupStatus.Waitlisted);
        Signup(mc2, MasterClassSignupStatus.Waitlisted);
        Signup(mc2, MasterClassSignupStatus.Waitlisted);
        Signup(mcGhost, MasterClassSignupStatus.Waitlisted, OtherEventId); // scope guard

        // --- Speaker readiness roster (§134): one un-ready speaker ---------------
        var sp = new Participant
        {
            EventId = EventId, Email = "sp@x", FullName = "Speaker One",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(sp);
        await db.SaveChangesAsync();
        db.SpeakerProfiles.Add(new SpeakerProfile { EventId = EventId, ParticipantId = sp.Id });

        // --- Sponsor deliverables board (§135): one incomplete company ----------
        db.SponsorInfos.Add(new SponsorInfo { EventId = EventId, SponsorCompanyId = "77" });

        // --- Sync health (§132): a fresh successful sync -> In-sync -------------
        db.SyncRuns.Add(new SyncRun
        {
            EventId = EventId, Key = SyncRun.AttendeeBackstageKey,
            LastSuccessAt = Now.AddHours(-1), LastWebhookAt = Now.AddMinutes(-5),
            Summary = "ok",
        });
        db.SyncRuns.Add(new SyncRun
        {
            EventId = OtherEventId, Key = SyncRun.AttendeeBackstageKey,
            LastSuccessAt = Now.AddDays(-30), // stale on the other edition (scope guard)
        });
        await db.SaveChangesAsync();

        var s = await NewSvc(db).BuildAsync(EventId);

        // Check-in: active mirror only; cancelled + other-edition excluded.
        Assert.Equal(3, s.ExpectedAttendees);
        Assert.Equal(2, s.CheckedInCount);
        Assert.Equal(67, s.CheckedInPercent);   // round(2/3*100)

        // Master classes: scoped, capacity summed, full counted, waitlist + offers split.
        Assert.Equal(2, s.MasterClassCount);    // ghost excluded
        Assert.True(s.MasterClassHasCapacity);
        Assert.Equal(7, s.MasterClassCapacity); // 2 + 5
        Assert.Equal(3, s.MasterClassConfirmed);
        Assert.Equal(1, s.MasterClassOffered);
        Assert.Equal(3, s.MasterClassWaitlisted); // ghost waitlist excluded
        Assert.Equal(4, s.MasterClassSeatsTaken); // 3 confirmed + 1 offered
        Assert.Equal(1, s.MasterClassFull);       // MC1 only
        Assert.Equal(57, s.MasterClassFillPercent); // round(4/7*100)

        // Speaker readiness summary (one speaker, not ready: only "other to-dos" satisfied).
        Assert.Equal(1, s.SpeakerTotal);
        Assert.Equal(0, s.SpeakerReady);
        Assert.InRange(s.SpeakerReadinessAvgPercent, 1, 99);

        // Sponsor deliverables summary (one incomplete, not overdue).
        Assert.Equal(1, s.SponsorCompaniesTotal);
        Assert.Equal(0, s.SponsorCompaniesComplete);
        Assert.Equal(0, s.SponsorCompaniesAtRisk);

        // Sync-health badge: scoped to this edition's fresh run.
        Assert.Equal(SyncHealthStatus.InSync, s.SyncStatus);
        Assert.Equal(Now.AddHours(-1), s.SyncLastSuccessAt);
    }
}
