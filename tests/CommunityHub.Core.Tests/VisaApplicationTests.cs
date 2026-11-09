using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Navigation;
using CommunityHub.Core.Visa;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1374 — the visa form: only flagged people may submit, required fields and limits are enforced,
/// the organizer's application ID survives the applicant's edits, and the organizer list, Excel and PDF
/// carry every field. Operator 2026-10-28: <i>"instead of a survey, it can be a simple form"</i>.
/// </summary>
public sealed class VisaApplicationTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"visa-{Guid.NewGuid():N}").Options);

    private static async Task<(CommunityHubDbContext Db, Participant Flagged, Participant Other)> SeedAsync()
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        var flagged = new Participant { EventId = EventId, Email = "spk@example.com", FullName = "Asha Rao", Role = ParticipantRole.Speaker, NeedsVisa = true };
        var other = new Participant { EventId = EventId, Email = "vol@example.com", FullName = "Bo Berg", Role = ParticipantRole.Volunteer };
        db.Participants.AddRange(flagged, other);
        await db.SaveChangesAsync();
        return (db, flagged, other);
    }

    private static VisaApplication Complete() => new()
    {
        FirstNames = "Asha", LastName = "Rao", DateOfBirth = new DateOnly(1990, 5, 1), Gender = "Female",
        Nationality = "IN", HomeStreet = "MG Road 1", HomePostalCode = "560001", HomeCity = "Bengaluru",
        HomeCountry = "IN", Phone = "+91 1234", Email = "Asha@Example.com",
        CompanyName = "Contoso, Ltd", CompanyStreet = "Tech Park 2", CompanyPostalCode = "560002",
        CompanyCity = "Bengaluru", CompanyCountry = "IN", CompanyEmail = "info@contoso.example",
        CompanyWebsite = "https://contoso.example", DepartmentAndRole = "Cloud, architect",
        VisitedBefore = false, CompanyActivity = "IT consulting", ContactOrigin = "Call for speakers",
        RelationshipDuration = "This is the first contact", FinancialScope = "None",
        ArrivalDate = new DateOnly(2027, 2, 7), LengthOfVisitDays = 5, Entries = "Single",
        Purpose = "Speaking at the conference.", TravelPaidBy = "My employer", StayPaidBy = "The event organizer",
        Accommodation = "Conference hotel", HostGuarantees = true,
    };

    [Fact]
    public async Task Only_a_flagged_participant_can_submit()
    {
        var (db, _, other) = await SeedAsync();
        var svc = new VisaApplicationService(db);

        Assert.False(await svc.MaySeeFormAsync(EventId, other.Id));
        var errors = await svc.SubmitAsync(EventId, other.Id, Complete());

        Assert.NotEmpty(errors);
        Assert.Empty(db.VisaApplications);
    }

    [Fact]
    public async Task Missing_required_fields_are_named_and_nothing_is_saved()
    {
        var (db, flagged, _) = await SeedAsync();
        var input = Complete();
        input.LastName = " ";
        input.Accommodation = null;
        input.CompanyPhone = null;            // optional
        input.SpecialCircumstances = null;    // optional

        var errors = await new VisaApplicationService(db).SubmitAsync(EventId, flagged.Id, input);

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("Last name"));
        Assert.Contains(errors, e => e.Contains("Where they will stay"));
        Assert.Empty(db.VisaApplications);
    }

    [Fact]
    public async Task Limits_are_enforced()
    {
        var (db, flagged, _) = await SeedAsync();
        var input = Complete();
        input.Purpose = new string('x', VisaFormOptions.LongTextMax + 1);
        input.LengthOfVisitDays = 0;

        var errors = await new VisaApplicationService(db).SubmitAsync(EventId, flagged.Id, input);

        Assert.Contains(errors, e => e.Contains("Purpose"));
        Assert.Contains(errors, e => e.Contains("Length of visit"));
    }

    [Fact]
    public async Task A_resubmit_keeps_the_first_submit_time_and_the_organizers_application_id()
    {
        var (db, flagged, _) = await SeedAsync();
        var svc = new VisaApplicationService(db);

        Assert.Empty(await svc.SubmitAsync(EventId, flagged.Id, Complete()));
        var first = (await svc.GetAsync(EventId, flagged.Id))!.SubmittedAt;
        Assert.NotNull(first);
        Assert.Equal("asha@example.com", (await svc.GetAsync(EventId, flagged.Id))!.Email);

        Assert.True(await svc.SetApplicationIdAsync(EventId, flagged.Id, " DK-12345 "));

        var edit = Complete();
        edit.HomeCity = "Aarhus";
        edit.ApplicationId = "forged";        // posted by the applicant — must be ignored
        Assert.Empty(await svc.SubmitAsync(EventId, flagged.Id, edit));

        var saved = (await svc.GetAsync(EventId, flagged.Id))!;
        Assert.Equal(first, saved.SubmittedAt);
        Assert.Equal("DK-12345", saved.ApplicationId);
        Assert.Equal("Aarhus", saved.HomeCity);
        Assert.Single(db.VisaApplications);

        Assert.True(await svc.SetApplicationIdAsync(EventId, flagged.Id, ""));
        Assert.Null((await svc.GetAsync(EventId, flagged.Id))!.ApplicationId);
    }

    [Fact]
    public async Task The_list_shows_everyone_flagged_and_anyone_who_answered()
    {
        var (db, flagged, other) = await SeedAsync();
        var svc = new VisaApplicationService(db);
        Assert.Single(await svc.ListAsync(EventId));               // flagged, not submitted
        Assert.Contains(await svc.CandidatesAsync(EventId), c => c.Id == other.Id);

        Assert.Empty(await svc.SubmitAsync(EventId, flagged.Id, Complete()));
        Assert.True(await svc.SetNeedsVisaAsync(EventId, flagged.Id, false));

        var rows = await svc.ListAsync(EventId);
        var row = Assert.Single(rows);                              // answers are never hidden
        Assert.True(row.Submitted);
        Assert.False(row.NeedsVisa);
        Assert.False(await svc.MaySeeFormAsync(EventId, flagged.Id));
    }

    [Fact]
    public async Task Excel_has_a_column_per_field_and_quotes_commas()
    {
        var (db, flagged, _) = await SeedAsync();
        var svc = new VisaApplicationService(db);
        Assert.Empty(await svc.SubmitAsync(EventId, flagged.Id, Complete()));

        var csv = VisaApplicationService.BuildCsv(await svc.ListAsync(EventId));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Equal(6 + VisaApplicationService.Fields.Count, lines[0].Split(',').Length);
        Assert.Contains("\"Contoso, Ltd\"", lines[1]);
        Assert.Contains("India", lines[1]);                         // ISO code exported as the country name
    }

    [Fact]
    public async Task The_pdf_renders_for_one_applicant_and_for_nobody()
    {
        var (db, flagged, _) = await SeedAsync();
        var svc = new VisaApplicationService(db);
        Assert.Empty(await svc.SubmitAsync(EventId, flagged.Id, Complete()));
        await svc.SetApplicationIdAsync(EventId, flagged.Id, "DK-1");

        var pdf = VisaApplicationPdf.Render(await svc.ListAsync(EventId), "ELDK 2027", DateTimeOffset.UtcNow);
        var empty = VisaApplicationPdf.Render(Array.Empty<VisaRow>(), "ELDK 2027", DateTimeOffset.UtcNow);

        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(empty, 0, 4));
    }

    private static ParticipantTask? VisaTask(CommunityHubDbContext db, int participantId) =>
        db.Tasks.SingleOrDefault(t => t.SourceKey == VisaApplicationService.TaskSourceKeyFor(participantId));

    [Fact]
    public async Task A_flagged_person_gets_an_undated_visa_task_that_submitting_completes()
    {
        var (db, flagged, other) = await SeedAsync();
        var svc = new VisaApplicationService(db);

        await svc.SyncTasksAsync(EventId);
        await svc.SyncTasksAsync(EventId);                            // idempotent

        var task = VisaTask(db, flagged.Id);
        Assert.NotNull(task);
        Assert.Single(db.Tasks.Where(t => t.SourceKey!.StartsWith(VisaApplicationService.TaskKeyPrefix)));
        Assert.Null(task!.DueDate);                                   // "no deadline" ⇒ no reminder mail
        Assert.Equal(TaskState.Open, task.State);
        Assert.Null(VisaTask(db, other.Id));                          // not flagged ⇒ no task

        Assert.Empty(await svc.SubmitAsync(EventId, flagged.Id, Complete()));
        Assert.Equal(TaskState.Done, VisaTask(db, flagged.Id)!.State);
    }

    [Fact]
    public async Task Removing_the_flag_retires_an_open_task_and_re_adding_reopens_it()
    {
        var (db, _, other) = await SeedAsync();
        var svc = new VisaApplicationService(db);

        Assert.True(await svc.SetNeedsVisaAsync(EventId, other.Id, true));
        Assert.Equal(TaskState.Open, VisaTask(db, other.Id)!.State);

        Assert.True(await svc.SetNeedsVisaAsync(EventId, other.Id, false));
        var retired = VisaTask(db, other.Id)!;
        Assert.Equal(TaskState.Done, retired.State);
        Assert.Equal(TaskClosedReason.RetiredFromCatalog, retired.ClosedReason);   // closed, never deleted

        Assert.True(await svc.SetNeedsVisaAsync(EventId, other.Id, true));
        var back = VisaTask(db, other.Id)!;
        Assert.Equal(TaskState.Open, back.State);
        Assert.Null(back.ClosedReason);
    }

    [Fact]
    public async Task The_stay_payer_and_the_host_guarantee_are_fixed_answers()
    {
        var (db, flagged, _) = await SeedAsync();
        var input = Complete();
        input.StayPaidBy = "Myself";                                   // posted by hand — overruled
        input.HostGuarantees = true;

        Assert.Empty(await new VisaApplicationService(db).SubmitAsync(EventId, flagged.Id, input));

        Assert.Equal(VisaFormOptions.StayPaidByOrganizer, db.VisaApplications.Single().StayPaidBy);
        Assert.False(db.VisaApplications.Single().HostGuarantees);
    }

    [Fact]
    public async Task The_hotel_choices_are_this_editions_hotels_with_their_address()
    {
        var (db, flagged, _) = await SeedAsync();
        var hotel = new Hotel { EventId = EventId, Name = "Bella Sky Copenhagen", Address = "Center Blvd. 5, 2300 København S" };
        db.Hotels.AddRange(hotel, new Hotel { EventId = 99, Name = "Other edition hotel" });
        await db.SaveChangesAsync();
        flagged.HotelId = hotel.Id;
        await db.SaveChangesAsync();
        var svc = new VisaApplicationService(db);

        var choice = Assert.Single(await svc.HotelChoicesAsync(EventId));
        Assert.Equal("Bella Sky Copenhagen, Center Blvd. 5, 2300 København S", choice.Value);
        Assert.Equal(choice.Value, await svc.AssignedHotelValueAsync(EventId, flagged.Id));
    }

    [Theory]
    [InlineData(ParticipantRole.Speaker)]
    [InlineData(ParticipantRole.Volunteer)]
    public void The_menu_shows_the_visa_form_only_when_flagged(ParticipantRole role)
    {
        Assert.DoesNotContain(NavBuilder.Build(role).AllItems, i => i.Href == "/Forms/Visa");
        Assert.Contains(NavBuilder.Build(role, needsVisa: true).AllItems, i => i.Href == "/Forms/Visa");
    }
}
