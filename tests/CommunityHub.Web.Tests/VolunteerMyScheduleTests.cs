using System.Security.Claims;
using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Volunteers;
using CommunityHub.Pages.Volunteer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the volunteer unified "My schedule" page (REQUIREMENTS Top-8 #8 /
/// §193): the per-task "Add Reminder" handler e-mails the signed-in volunteer a
/// calendar INVITATION for their own assigned task, and sends nothing for a task
/// they aren't assigned to. Drives the real <see cref="MyScheduleModel"/> over a
/// fake HttpContext. FAKE names only.
/// </summary>
public sealed class VolunteerMyScheduleTests
{
    /// <summary>Captures the last calendar-invite send so a test can assert on it.</summary>
    private sealed class CapturingEmailSender : IEmailSender
    {
        public string? LastTo { get; private set; }
        public string? LastIcs { get; private set; }
        public int IcsSends { get; private set; }

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody, string icsContent, string icsFileName, CancellationToken ct = default)
        {
            LastTo = toEmail; LastIcs = icsContent; IcsSends++;
            return Task.CompletedTask;
        }
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vol-mysched-{Guid.NewGuid():N}")
            .Options);

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static ClaimsPrincipal Session(Participant p)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static MyScheduleModel NewModel(
        CommunityHubDbContext db, DefaultHttpContext http, IEmailSender? sender = null)
    {
        http.Request.Host = new HostString("ceh.example.test");
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var structure = new VolunteerStructureService(db, TimeProvider.System);
        var actions = new OrganizerActionItemService(db, TimeProvider.System);
        var invite = new CalendarInviteEmailService(
            db, sender ?? new CapturingEmailSender(), new EmailContextAccessor(), TimeProvider.System);
        return new MyScheduleModel(
            db,
            accessor,
            new VolunteerScheduleBuilder(db, structure),
            structure,
            new VolunteerShiftService(db, TimeProvider.System, actions),
            helpNotify: null!,                 // not reached by these handlers
            invite,
            NullLogger<MyScheduleModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private sealed record Seed(Participant Volunteer, Participant Other, int TaskId);

    private static async Task<Seed> SeedAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            Code = "ELDK27", DisplayName = "Test Edition", CommunityName = "Test Community",
            VenueName = "Test Venue", StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 9, 2),
            CalendarSyncEnabled = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var eventId = ev.Id;

        var vol = new Participant
        {
            EventId = eventId, Email = "vol@example.test", FullName = "Vol Unteer",
            Role = ParticipantRole.Volunteer, IsActive = true,
        };
        var other = new Participant
        {
            EventId = eventId, Email = "other@example.test", FullName = "Other Vol",
            Role = ParticipantRole.Volunteer, IsActive = true,
        };
        db.Participants.AddRange(vol, other);
        await db.SaveChangesAsync();

        var cat = new VolunteerCategory { EventId = eventId, Name = "Registration" };
        db.VolunteerCategories.Add(cat);
        await db.SaveChangesAsync();
        var sub = new VolunteerSubcategory { EventId = eventId, CategoryId = cat.Id, Name = "Desk" };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();
        var task = new VolunteerTask
        {
            EventId = eventId, SubcategoryId = sub.Id, Title = "Staff the desk",
            DueDate = new DateOnly(2026, 9, 1), Instructions = "Hand out badges.",
        };
        db.VolunteerTasks.Add(task);
        await db.SaveChangesAsync();

        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = eventId, TaskId = task.Id, ParticipantId = vol.Id,
        });
        await db.SaveChangesAsync();

        return new Seed(vol, other, task.Id);
    }

    [Fact]
    public async Task Own_assigned_task_emails_a_calendar_invite_to_the_volunteer()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        var http = new DefaultHttpContext { User = Session(seed.Volunteer) };
        var sender = new CapturingEmailSender();
        var model = NewModel(db, http, sender);

        var result = await model.OnPostAddReminderAsync(seed.TaskId, default);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(1, sender.IcsSends);
        // The invite goes to the volunteer's (primary, here) calendar address.
        Assert.Equal("vol@example.test", sender.LastTo);
        Assert.StartsWith("BEGIN:VCALENDAR", sender.LastIcs);
        Assert.Contains("METHOD:REQUEST", sender.LastIcs);
        Assert.Contains("Volunteer: Staff the desk", sender.LastIcs);
    }

    [Fact]
    public async Task Task_not_assigned_to_me_sends_nothing()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        // Sign in as the OTHER volunteer (no assignment to the task).
        var http = new DefaultHttpContext { User = Session(seed.Other) };
        var sender = new CapturingEmailSender();
        var model = NewModel(db, http, sender);

        var result = await model.OnPostAddReminderAsync(seed.TaskId, default);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(0, sender.IcsSends);
    }

    [Fact]
    public async Task OnGet_loads_schedule_for_the_signed_in_volunteer()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        var http = new DefaultHttpContext { User = Session(seed.Volunteer) };
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        var entry = Assert.Single(model.Schedule.Entries);
        Assert.Equal("Staff the desk", entry.Title);
        Assert.Equal("Hand out badges.", entry.Instructions);
        Assert.Equal(ShiftDecisionStatus.None, entry.Decision);
    }

    // ---------------------------------------------------------------------
    //  Shift confirm/decline handlers (merged here from the old MyShifts page,
    //  §234 Wave 3a): declining my OWN shift persists the decision and raises
    //  a coordinator reassign signal; a shift I'm not assigned to is forbidden.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Decline_my_own_shift_persists_and_signals_coordinator()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);
        var model = NewModel(db, new DefaultHttpContext { User = Session(seed.Volunteer) });

        var result = await model.OnPostDeclineAsync(seed.TaskId, "Cannot make it", default);

        Assert.IsType<RedirectToPageResult>(result);
        var a = await db.VolunteerTaskAssignments.SingleAsync(
            x => x.TaskId == seed.TaskId && x.ParticipantId == seed.Volunteer.Id);
        Assert.Equal(ShiftDecisionStatus.Declined, a.DecisionStatus);

        var open = await db.OrganizerActionItems.CountAsync(
            x => x.Type == OrganizerActionItemService.TypeVolunteerShiftReassign && x.ResolvedAt == null);
        Assert.Equal(1, open);
    }

    [Fact]
    public async Task Decline_a_shift_not_mine_is_forbidden()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);
        // Sign in as the OTHER volunteer (not assigned to the task).
        var model = NewModel(db, new DefaultHttpContext { User = Session(seed.Other) });

        var result = await model.OnPostDeclineAsync(seed.TaskId, "not mine", default);

        Assert.IsType<ForbidResult>(result);
        var a = await db.VolunteerTaskAssignments.SingleAsync(
            x => x.TaskId == seed.TaskId && x.ParticipantId == seed.Volunteer.Id);
        Assert.Equal(ShiftDecisionStatus.None, a.DecisionStatus);
    }

    // ---------------------------------------------------------------------
    //  Volunteer status dropdown / server guard: Cancelled ("No longer
    //  needed") is a coordinator/supervisor-only state and must never be
    //  selectable by — nor accepted from — a volunteer's own surface.
    // ---------------------------------------------------------------------

    [Fact]
    public void Volunteer_selectable_statuses_exclude_Cancelled()
    {
        // MySchedule is the single volunteer surface (§234 Wave 3a — the old
        // MyTasks/MyShifts pages are permanent redirects here), and its dropdown
        // is built from this list, so the rendered options can never offer
        // Cancelled.
        Assert.DoesNotContain(VolunteerTaskStatus.Cancelled, MyScheduleModel.VolunteerSelectableStatuses);

        Assert.Equal(
            new[] { VolunteerTaskStatus.Open, VolunteerTaskStatus.InProgress, VolunteerTaskStatus.Done },
            MyScheduleModel.VolunteerSelectableStatuses);
    }

    [Fact]
    public async Task Volunteer_cannot_set_Cancelled_via_post_handler_server_side()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        var http = new DefaultHttpContext { User = Session(seed.Volunteer) };
        var model = NewModel(db, http);

        // A forged POST of Cancelled (e.g. crafted request, not from the dropdown)
        // must be rejected before the task is touched.
        var result = await model.OnPostSetStatusAsync(seed.TaskId, VolunteerTaskStatus.Cancelled, default);

        Assert.IsType<RedirectToPageResult>(result);
        // The task status is unchanged (still the seeded default Open).
        var task = await db.VolunteerTasks.AsNoTracking().FirstAsync(t => t.Id == seed.TaskId);
        Assert.Equal(VolunteerTaskStatus.Open, task.Status);
    }

    [Fact]
    public async Task Volunteer_can_still_set_an_allowed_status_via_post_handler()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        var http = new DefaultHttpContext { User = Session(seed.Volunteer) };
        var model = NewModel(db, http);

        var result = await model.OnPostSetStatusAsync(seed.TaskId, VolunteerTaskStatus.Done, default);

        Assert.IsType<RedirectToPageResult>(result);
        var task = await db.VolunteerTasks.AsNoTracking().FirstAsync(t => t.Id == seed.TaskId);
        Assert.Equal(VolunteerTaskStatus.Done, task.Status);
    }
}
