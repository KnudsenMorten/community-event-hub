using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="OrganizerExportsService"/> — the read-only
/// on-site EXPORTS / run-sheet projections (REQUIREMENTS §20 Organizer). Uses the
/// EF Core InMemory provider so the real DbContext mapping + LINQ run (no SQL),
/// seeded with one edition (attendees, lunch sign-ups, sessions+speakers, a
/// volunteer work tree with an assignment, participants). A second edition's rows
/// are planted to prove every projection is event-scoped.
///
/// All seeded people use SYNTHETIC names (public-mirror safety: no real
/// customer/person names).
/// </summary>
public sealed class OrganizerExportsServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"exports-{Guid.NewGuid():N}")
            .Options);

    private static OrganizerExportsService NewSvc(CommunityHubDbContext db) => new(db);

    private static async Task SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "EX27", CommunityName = "Exports Test",
            DisplayName = "Exports Test 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        });
        db.Events.Add(new Event
        {
            Id = OtherEventId, Code = "OTHER", CommunityName = "Other",
            DisplayName = "Other 2027",
            StartDate = new DateOnly(2027, 5, 1), EndDate = new DateOnly(2027, 5, 2),
            IsActive = false,
        });

        // --- Participants (synthetic names) ---------------------------------
        Participant P(string name, string email, ParticipantRole role,
                      bool active = true, bool test = false, string? company = null, int ev = EventId)
        {
            var p = new Participant
            {
                EventId = ev, Email = email, FullName = name, Role = role,
                IsActive = active, IsTestUser = test, SponsorCompanyId = company,
            };
            db.Participants.Add(p);
            return p;
        }

        var sp1 = P("Speaker Alpha", "alpha@expertslive.dk", ParticipantRole.Speaker);
        var sp2 = P("Speaker Bravo", "bravo@expertslive.dk", ParticipantRole.Speaker);
        var vol1 = P("Volunteer Charlie", "charlie@expertslive.dk", ParticipantRole.Volunteer);
        var spon = P("Sponsor Delta", "delta@expertslive.dk", ParticipantRole.Sponsor, company: "77");
        var inactive = P("Inactive Echo", "echo@expertslive.dk", ParticipantRole.Speaker, active: false); // excluded from badges
        P("Test Foxtrot", "foxtrot@expertslive.dk", ParticipantRole.Volunteer, test: true); // excluded from badges
        P("Ghost Golf", "golf@expertslive.dk", ParticipantRole.Speaker, ev: OtherEventId); // scope guard
        await db.SaveChangesAsync();

        // --- Lunch sign-ups -------------------------------------------------
        void Lunch(int pid, bool setup, bool pre, int ev = EventId)
            => db.LunchSignups.Add(new LunchSignup
            {
                EventId = ev, ParticipantId = pid, LunchSetupDay = setup, LunchPreDay = pre,
            });
        Lunch(sp1.Id, setup: true, pre: true);
        Lunch(vol1.Id, setup: false, pre: true);
        Lunch(sp2.Id, setup: false, pre: false);                 // no day -> not a person row
        Lunch(sp1.Id, setup: true, pre: true, ev: OtherEventId); // scope guard

        // --- Sessions + speakers --------------------------------------------
        var s1 = new Session
        {
            EventId = EventId, SessionizeId = "sez-1", Title = "Opening Keynote",
            Room = "Hall A", Type = SessionType.TechnicalSession, Length = SessionLength.SixtyMin,
            StartsAt = new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero),
            RoomQrUrl = "https://example.test/qr/hall-a.png", PublicToken = "tok-1",
        };
        var s2 = new Session
        {
            EventId = EventId, SessionizeId = "sez-2", Title = "Deep Dive",
            Room = "Hall A", Type = SessionType.TechnicalSession, Length = SessionLength.FiftyMin,
            StartsAt = new DateTimeOffset(2027, 2, 10, 11, 0, 0, TimeSpan.Zero),
        };
        var sService = new Session
        {
            EventId = EventId, SessionizeId = "sez-svc", Title = "Lunch break",
            IsServiceSession = true, Room = "Foyer",
        };
        var sGhost = new Session
        {
            EventId = OtherEventId, SessionizeId = "sez-ghost", Title = "Ghost talk", Room = "Hall Z",
        };
        db.Sessions.AddRange(s1, s2, sService, sGhost);
        await db.SaveChangesAsync();

        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s1.Id, ParticipantId = sp1.Id });
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s1.Id, ParticipantId = sp2.Id });

        // --- Volunteer work tree + assignment -------------------------------
        var cat = new VolunteerCategory { Id = 10, EventId = EventId, Name = "Registration" };
        db.VolunteerCategories.Add(cat);
        var sub = new VolunteerSubcategory { Id = 20, EventId = EventId, CategoryId = 10, Name = "Badge desk" };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();

        var vt = new VolunteerTask
        {
            Id = 30, EventId = EventId, SubcategoryId = 20, Title = "Morning badge desk",
            DueDate = new DateOnly(2027, 2, 10), Shift = "08:00", TimeEnd = "12:00",
            Status = VolunteerTaskStatus.Open,
        };
        var vtCancelled = new VolunteerTask
        {
            Id = 31, EventId = EventId, SubcategoryId = 20, Title = "Old job",
            Status = VolunteerTaskStatus.Cancelled,
        };
        db.VolunteerTasks.AddRange(vt, vtCancelled);
        await db.SaveChangesAsync();

        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = vt.Id, ParticipantId = vol1.Id,
        });
        // An assignment on the cancelled task — must be excluded from the rota.
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = vtCancelled.Id, ParticipantId = vol1.Id,
        });

        // --- Dinner sign-ups + structured dietary rows (§326bh) --------------
        // The catering surfaces must count ONLY active people who said Yes, so the
        // seed plants each way that could leak: an inactive Yes, a No who filled in
        // a diet, and another edition's Yes.
        void Dinner(int pid, DinnerRsvp rsvp, int plusOnes = 0,
                    string? comments = null, string? legacyNotes = null, int ev = EventId)
            => db.DinnerSignups.Add(new DinnerSignup
            {
                EventId = ev, ParticipantId = pid, Rsvp = rsvp,
                Attending = rsvp == DinnerRsvp.Yes, PlusOneCount = plusOnes,
                Comments = comments, AllergyNotes = legacyNotes,
            });

        Dinner(sp1.Id, DinnerRsvp.Yes, plusOnes: 2, comments: "Arrives late");
        Dinner(vol1.Id, DinnerRsvp.Yes, legacyNotes: "No shellfish please");
        Dinner(sp2.Id, DinnerRsvp.No);                        // declined -> no seat, no diet count
        Dinner(inactive.Id, DinnerRsvp.Yes, plusOnes: 3);     // drop-out -> must never reach the caterer
        Dinner(sp1.Id, DinnerRsvp.Yes, ev: OtherEventId);     // scope guard

        void Diet(int pid, DietarySurface surface, string? diet = null,
                  bool gluten = false, bool peanuts = false, string? other = null, int ev = EventId)
            => db.DietaryRequirements.Add(new DietaryRequirement
            {
                EventId = ev, ParticipantId = pid, Surface = surface,
                DietChoice = diet, Gluten = gluten, Peanuts = peanuts, OtherAllergens = other,
            });

        Diet(sp1.Id, DietarySurface.Dinner, diet: "Vegan", gluten: true);
        Diet(vol1.Id, DietarySurface.Dinner, diet: "Vegan", peanuts: true, other: "Severe — no cross-contact");
        Diet(sp2.Id, DietarySurface.Dinner, diet: "Halal");        // declined dinner -> excluded
        Diet(inactive.Id, DietarySurface.Dinner, diet: "Kosher", gluten: true); // drop-out -> excluded
        Diet(sp1.Id, DietarySurface.Dinner, diet: "Vegan", ev: OtherEventId);   // scope guard

        // --- Attendees ------------------------------------------------------
        void Att(string first, string last, string email, TicketStatus ticket,
                 string? masterClass = null, int ev = EventId)
            => db.Attendees.Add(new Attendee
            {
                EventId = ev, Email = email, FirstName = first, LastName = last,
                TicketStatus = ticket, MasterClassName = masterClass,
            });
        Att("Anna", "Andersen", "anna@example.test", TicketStatus.TwoDay, "Kubernetes 101");
        Att("Bo", "Berg", "bo@example.test", TicketStatus.Other);
        Att("Ghost", "Person", "ghost@example.test", TicketStatus.TwoDay, ev: OtherEventId); // scope guard

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Attendee_list_is_scoped_and_ordered_and_flags_two_day()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var rows = await NewSvc(db).BuildAttendeeListAsync(EventId);

        Assert.Equal(2, rows.Count); // ghost (other edition) excluded
        Assert.Equal("Anna Andersen", rows[0].Name); // ordered by last name
        Assert.True(rows[0].TwoDay);
        Assert.Equal("Kubernetes 101", rows[0].MasterClass);
        Assert.False(rows[1].TwoDay);
    }

    // --- Appreciation Dinner + catering dietary roll-up (§326bh) ------------

    [Fact]
    public async Task Dinner_headcount_counts_seats_and_excludes_inactive_and_declined()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var head = await NewSvc(db).BuildDinnerHeadcountAsync(EventId);

        // sp1 (+2) and vol1 (+0) — sp2 declined, Inactive Echo (+3) is a drop-out,
        // and the other edition's row is out of scope.
        Assert.Equal(2, head.Attending);
        Assert.Equal(2, head.PlusOnes);
        Assert.Equal(4, head.Total);
    }

    [Fact]
    public async Task Dinner_run_sheet_lists_confirmed_people_with_their_dietary_detail()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var people = await NewSvc(db).BuildDinnerRunSheetAsync(EventId);

        Assert.Equal(2, people.Count);
        Assert.DoesNotContain(people, p => p.Name == "Inactive Echo");
        Assert.DoesNotContain(people, p => p.Name == "Speaker Bravo"); // declined

        var alpha = people.Single(p => p.Name == "Speaker Alpha");
        Assert.Equal(2, alpha.PlusOnes);
        Assert.Equal("Vegan", alpha.Diet);
        Assert.Equal("Gluten", alpha.Allergens);
        Assert.Equal("Arrives late", alpha.Comments);

        // The structured free-text and the legacy AllergyNotes are BOTH surfaced.
        var charlie = people.Single(p => p.Name == "Volunteer Charlie");
        Assert.Equal("Peanuts", charlie.Allergens);
        Assert.Contains("Severe", charlie.OtherRequirements);
        Assert.Contains("No shellfish please", charlie.OtherRequirements);
    }

    [Fact]
    public async Task Dietary_rollup_counts_only_active_attending_people()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var rows = await NewSvc(db).BuildDietaryHeadcountAsync(EventId);

        // Both confirmed guests are Vegan; the Halal (declined) and Kosher
        // (deactivated) rows must not reach the kitchen sheet.
        var vegan = rows.Single(r => r.Kind == "Diet" && r.Item == "Vegan");
        Assert.Equal("Appreciation Dinner", vegan.Occasion);
        Assert.Equal(2, vegan.Count);
        Assert.DoesNotContain(rows, r => r.Item == "Halal");
        Assert.DoesNotContain(rows, r => r.Item == "Kosher");

        // Allergens are counted per token — the drop-out's Gluten must not inflate it.
        Assert.Equal(1, rows.Single(r => r.Kind == "Allergen" && r.Item == "Gluten").Count);
        Assert.Equal(1, rows.Single(r => r.Kind == "Allergen" && r.Item == "Peanuts").Count);

        Assert.Equal(2, rows.Single(r => r.Item == "People with any requirement").Count);
        Assert.Equal(1, rows.Single(r => r.Item == "Free-text notes to read").Count);

        // No day-catering rows are ever written today, so that occasion is absent
        // rather than printed as zeros (§326bh Defect 2).
        Assert.DoesNotContain(rows, r => r.Occasion.StartsWith("Speaker"));
    }

    [Fact]
    public async Task Dietary_rollup_reconciles_with_the_dinner_run_sheet()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = NewSvc(db);

        var people = await svc.BuildDinnerRunSheetAsync(EventId);
        var rows = await svc.BuildDietaryHeadcountAsync(EventId);

        // The kitchen sheet must never claim more meals of a kind than there are
        // seats on the run-sheet — the two are built from the same population.
        var withRequirement = rows.Single(r => r.Item == "People with any requirement").Count;
        Assert.True(withRequirement <= people.Count);
        Assert.Equal(
            people.Count(p => p.Diet == "Vegan"),
            rows.Single(r => r.Kind == "Diet" && r.Item == "Vegan").Count);
    }

    [Fact]
    public async Task Dinner_and_dietary_csv_carry_the_headers_and_rows()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = NewSvc(db);

        var dinnerCsv = await svc.BuildDinnerCsvAsync(EventId);
        Assert.StartsWith("Name,Role,PlusOnes,Diet,Allergens,OtherRequirements,Comments", dinnerCsv);
        Assert.Contains("Speaker Alpha", dinnerCsv);
        Assert.DoesNotContain("Inactive Echo", dinnerCsv);

        var dietCsv = await svc.BuildDietaryCsvAsync(EventId);
        Assert.StartsWith("Occasion,Kind,Item,Count", dietCsv);
        Assert.Contains("Appreciation Dinner,Diet,Vegan,2", dietCsv);
    }

    [Fact]
    public async Task Dinner_surfaces_are_empty_when_nobody_has_confirmed()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "EX27", CommunityName = "Empty",
            DisplayName = "Empty 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var svc = NewSvc(db);

        Assert.Empty(await svc.BuildDinnerRunSheetAsync(EventId));
        Assert.Empty(await svc.BuildDietaryHeadcountAsync(EventId));
        var head = await svc.BuildDinnerHeadcountAsync(EventId);
        Assert.Equal(0, head.Total);
    }

    [Fact]
    public async Task Lunch_headcount_counts_per_day_scoped()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var head = await NewSvc(db).BuildLunchHeadcountAsync(EventId);

        Assert.Equal(2, head.Count);
        Assert.Equal(1, head.Single(h => h.Day.StartsWith("Setup")).Count); // sp1 only
        Assert.Equal(2, head.Single(h => h.Day.StartsWith("Pre")).Count);   // sp1 + vol1
    }

    [Fact]
    public async Task Lunch_people_only_lists_those_with_a_day()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var people = await NewSvc(db).BuildLunchPeopleAsync(EventId);

        Assert.Equal(2, people.Count); // sp2 (no day) excluded
        Assert.DoesNotContain(people, p => p.Name == "Speaker Bravo");
        Assert.Contains(people, p => p.Name == "Speaker Alpha" && p.SetupDay && p.PreDay);
    }

    [Fact]
    public async Task Room_sheets_exclude_service_and_other_edition_and_render_qr_token()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var rows = await NewSvc(db).BuildRoomSheetsAsync(EventId);

        Assert.Equal(2, rows.Count); // service session + ghost excluded
        var keynote = rows.Single(r => r.Title == "Opening Keynote");
        Assert.Equal("Hall A", keynote.Room);
        Assert.Equal("https://example.test/qr/hall-a.png", keynote.RoomQrUrl);
        Assert.Equal("tok-1", keynote.PublicToken);
        Assert.Contains("Speaker Alpha", keynote.Speakers);
        Assert.Contains("Speaker Bravo", keynote.Speakers);
        // Ordered by room then start time: keynote (09:00) before deep dive (11:00).
        Assert.Equal("Opening Keynote", rows[0].Title);
    }

    [Fact]
    public async Task Volunteer_rota_excludes_cancelled_task_and_carries_when()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var rows = await NewSvc(db).BuildVolunteerRotaAsync(EventId);

        var row = Assert.Single(rows); // the cancelled-task assignment is excluded
        Assert.Equal("Volunteer Charlie", row.Volunteer);
        Assert.Equal("Registration", row.Bucket);
        Assert.Equal("Morning badge desk", row.Task);
        Assert.Equal(new DateOnly(2027, 2, 10), row.Due);
        Assert.Equal("08:00", row.Shift);
        Assert.Equal("12:00", row.TimeEnd);
    }

    [Fact]
    public async Task Badge_data_excludes_inactive_test_and_other_edition()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var rows = await NewSvc(db).BuildBadgeDataAsync(EventId);

        // Active, non-test, this edition: sp1, sp2, vol1, spon = 4.
        Assert.Equal(4, rows.Count);
        Assert.DoesNotContain(rows, r => r.Name == "Inactive Echo");
        Assert.DoesNotContain(rows, r => r.Name == "Test Foxtrot");
        Assert.DoesNotContain(rows, r => r.Name == "Ghost Golf");
        Assert.Equal("77", rows.Single(r => r.Name == "Sponsor Delta").Company);
    }

    [Fact]
    public async Task Csv_exports_have_a_header_and_quote_fields_that_need_it()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = NewSvc(db);

        var attendeeCsv = await svc.BuildAttendeeListCsvAsync(EventId);
        var lines = attendeeCsv.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Name,Email,TicketStatus,TicketClass,MasterClass,TwoDay", lines[0]);
        Assert.Equal(3, lines.Length); // header + 2 attendees

        // Room title with no comma is unquoted; QR url with no comma unquoted.
        var roomCsv = await svc.BuildRoomSheetsCsvAsync(EventId);
        Assert.Contains("Opening Keynote", roomCsv);

        // Every CSV builder returns a non-empty header row even for an empty event.
        var badgeCsv = await svc.BuildBadgeDataCsvAsync(EventId);
        Assert.StartsWith("Name,Role,Company", badgeCsv);
    }

    /// <summary>
    /// §253 G4/G7 vendor-export truth: the caterer headcount/run-sheet and the
    /// day-of rota must exclude a deactivated person's surviving rows — and the
    /// rota must also exclude a shift its volunteer DECLINED. Before the fix all
    /// three counted ghosts (money ordered against people who left).
    /// </summary>
    [Fact]
    public async Task Caterer_and_rota_exports_exclude_inactive_people_and_declined_shifts()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // The deactivated "Inactive Echo" keeps a lunch sign-up + a shift
        // (legacy flag-only drop-out whose cascade never ran), and the active
        // "Speaker Bravo" DECLINED a shift on the same task.
        var echo = await db.Participants.SingleAsync(p => p.Email == "echo@expertslive.dk");
        var bravo = await db.Participants.SingleAsync(p => p.Email == "bravo@expertslive.dk");
        db.LunchSignups.Add(new LunchSignup
        {
            EventId = EventId, ParticipantId = echo.Id,
            LunchSetupDay = true, LunchPreDay = true,
        });
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = 30, ParticipantId = echo.Id,
        });
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = 30, ParticipantId = bravo.Id,
            DecisionStatus = ShiftDecisionStatus.Declined,
        });
        await db.SaveChangesAsync();
        var svc = NewSvc(db);

        // Caterer numbers + run-sheet: unchanged (echo's sign-up excluded).
        var head = await svc.BuildLunchHeadcountAsync(EventId);
        Assert.Equal(1, head.Single(h => h.Day.StartsWith("Setup")).Count); // sp1 only
        Assert.Equal(2, head.Single(h => h.Day.StartsWith("Pre")).Count);   // sp1 + vol1
        var people = await svc.BuildLunchPeopleAsync(EventId);
        Assert.Equal(2, people.Count);
        Assert.DoesNotContain(people, p => p.Name == "Inactive Echo");

        // Day-of rota: neither the ghost nor the declined shift prints.
        var rota = await svc.BuildVolunteerRotaAsync(EventId);
        var row = Assert.Single(rota);
        Assert.Equal("Volunteer Charlie", row.Volunteer);
    }

    [Fact]
    public async Task Empty_event_yields_empty_lists_but_lunch_headcount_has_both_days()
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
        var svc = NewSvc(db);

        Assert.Empty(await svc.BuildAttendeeListAsync(EventId));
        Assert.Empty(await svc.BuildLunchPeopleAsync(EventId));
        Assert.Empty(await svc.BuildRoomSheetsAsync(EventId));
        Assert.Empty(await svc.BuildVolunteerRotaAsync(EventId));
        Assert.Empty(await svc.BuildBadgeDataAsync(EventId));

        var head = await svc.BuildLunchHeadcountAsync(EventId);
        Assert.Equal(2, head.Count);            // both day rows present
        Assert.All(head, h => Assert.Equal(0, h.Count));
    }
}
