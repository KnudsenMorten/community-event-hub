using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using CommunityHub.Forms;
using CommunityHub.Pages;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §253 G11 — an organizer ROLE change is no longer a bare field write: the
/// <see cref="RoleChangeTaskReconciler"/> prunes the OLD role's auto-seeded tasks
/// (wizard-step mirrors + dated <c>speakerdl:</c> deadlines that would otherwise keep
/// firing due-day reminders) and seeds + reconciles the NEW role's steps, wired into
/// <see cref="EditParticipantModel"/>'s save.
///
/// §252 pass-2 orphan (a) — the party calendar-invite email carries the
/// open-not-download Google/Outlook "open calendar entry" links
/// (<see cref="CalendarLinkBuilder"/>), verified on the real rendered HTML.
/// </summary>
public sealed class RoleChangeAndCalendarLinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 8, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingSender : IEmailSender
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();
        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        { Sent.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }
        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
        { Sent.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default)
        { Sent.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }
        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody, string icsContent, string icsFileName, CancellationToken ct = default)
        { Sent.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default)
        { Sent.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"rolechg-{Guid.NewGuid():N}")
            .Options);

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

    private static async Task<(int eventId, Participant org)> SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "T27", CommunityName = "Test Community", DisplayName = "Test Community 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        var org = new Participant
        {
            EventId = evt.Id, Email = "org@example.test", FullName = "Org Person",
            Role = ParticipantRole.Organizer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(org);
        await db.SaveChangesAsync();
        return (evt.Id, org);
    }

    private static RoleChangeTaskReconciler NewReconciler(CommunityHubDbContext db)
    {
        var clock = new FixedClock();
        return new RoleChangeTaskReconciler(
            db,
            new WizardStepTaskSeeder(db, new SpeakerWizardService(db), new RoleWizardService(db), clock),
            new FormTaskReconciler(db, clock));
    }

    // =====================================================================
    //  G11 — the reconciler itself
    // =====================================================================

    [Fact]
    public async Task RoleChange_speaker_to_volunteer_prunes_speaker_tasks_and_seeds_volunteer_steps()
    {
        using var db = NewDb();
        var (eventId, _) = await SeedAsync(db);
        var p = new Participant
        {
            EventId = eventId, Email = "ex@x.dk", FullName = "Ex Speaker",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.Tasks.AddRange(
            // A dated speaker deadline — the row that used to keep firing reminders.
            new ParticipantTask
            {
                EventId = eventId, AssignedParticipantId = p.Id, Title = "Upload final presentation",
                DueDate = new DateOnly(2026, 7, 7), State = TaskState.Open,
                SourceKey = $"speakerdl:{p.Id}:upload-final-presentation",
            },
            // A speaker-only wizard mirror task.
            new ParticipantTask
            {
                EventId = eventId, AssignedParticipantId = p.Id, Title = "Help to promote your session(s)",
                State = TaskState.Open, SourceKey = $"promote:{p.Id}",
            },
            // A MANUAL organizer task — must never be pruned.
            new ParticipantTask
            {
                EventId = eventId, AssignedParticipantId = p.Id, Title = "Return the badge printer",
                State = TaskState.Open, SourceKey = null,
            });
        await db.SaveChangesAsync();

        // The role change has been SAVED (the page saves first), then reconcile.
        p.Role = ParticipantRole.Volunteer;
        await db.SaveChangesAsync();
        var (removed, created) = await NewReconciler(db)
            .ReconcileAsync(eventId, p.Id, ParticipantRole.Speaker, ParticipantRole.Volunteer);

        Assert.Equal(2, removed);
        Assert.True(created > 0);

        var keys = await db.Tasks
            .Where(t => t.EventId == eventId && t.AssignedParticipantId == p.Id)
            .Select(t => t.SourceKey)
            .ToListAsync();
        // Speaker-only tasks are GONE (the ex-speaker's speakerdl reminders stop firing).
        Assert.DoesNotContain(keys, k => k != null && k.StartsWith("speakerdl:"));
        Assert.DoesNotContain($"promote:{p.Id}", keys);
        // The manual organizer task survives.
        Assert.Contains(null, keys);
        // The NEW role's wizard steps are seeded (volunteer wizard: profile + accept + …).
        Assert.Contains(WizardStepTaskKeys.Profile(p.Id), keys);
        Assert.Contains(WizardStepTaskKeys.Accept(p.Id), keys);
        Assert.Contains(WizardStepTaskKeys.Availability(p.Id), keys);
    }

    [Fact]
    public async Task RoleChange_volunteer_to_speaker_prunes_availability_but_keeps_party_task()
    {
        using var db = NewDb();
        var (eventId, _) = await SeedAsync(db);
        var p = new Participant
        {
            EventId = eventId, Email = "vol@x.dk", FullName = "Vol Unteer",
            Role = ParticipantRole.Volunteer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.Tasks.AddRange(
            new ParticipantTask
            {
                EventId = eventId, AssignedParticipantId = p.Id, Title = "Complete your day availability",
                State = TaskState.Open, SourceKey = WizardStepTaskKeys.Availability(p.Id),
            },
            new ParticipantTask
            {
                EventId = eventId, AssignedParticipantId = p.Id, Title = "Sign up for the Party",
                State = TaskState.Open, SourceKey = Core.Config.PartyTaskSeeder.SourceKeyFor(p.Id),
            });
        await db.SaveChangesAsync();

        p.Role = ParticipantRole.Speaker;
        await db.SaveChangesAsync();
        await NewReconciler(db)
            .ReconcileAsync(eventId, p.Id, ParticipantRole.Volunteer, ParticipantRole.Speaker);

        var keys = await db.Tasks
            .Where(t => t.EventId == eventId && t.AssignedParticipantId == p.Id)
            .Select(t => t.SourceKey!)
            .ToListAsync();
        // The volunteer-only availability step task is pruned; the party task (every
        // role gets one) survives.
        Assert.DoesNotContain(WizardStepTaskKeys.Availability(p.Id), keys);
        Assert.Contains(Core.Config.PartyTaskSeeder.SourceKeyFor(p.Id), keys);
    }

    // =====================================================================
    //  G11 — wired into the EditParticipant save
    // =====================================================================

    [Fact]
    public async Task EditParticipant_role_change_runs_the_task_reconciliation()
    {
        using var db = NewDb();
        var (eventId, org) = await SeedAsync(db);
        var target = new Participant
        {
            EventId = eventId, Email = "sp@x.dk", FullName = "Sp Eaker",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(target);
        await db.SaveChangesAsync();
        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId, AssignedParticipantId = target.Id, Title = "Hotel",
            DueDate = new DateOnly(2026, 8, 1), State = TaskState.Open,
            SourceKey = $"speakerdl:{target.Id}:hotel",
        });
        await db.SaveChangesAsync();

        var clock = new FixedClock();
        var http = new DefaultHttpContext { User = Session(org) };
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions()));
        var model = new EditParticipantModel(
            db, accessor,
            new WelcomeEmailService(db, templates, new CapturingSender(), clock),
            new AttendeeOneDayWelcomeEmailService(db, templates, new CapturingSender(), clock),
            clock,
            NewReconciler(db))
        {
            PageContext = new PageContext { HttpContext = http },
            Id = target.Id,
            Email = "sp@x.dk",
            FullName = "Sp Eaker",
            Role = ParticipantRole.Media,      // speaker → media
            IsActive = true,
        };

        await model.OnPostAsync(default);

        var reloaded = await db.Participants.FindAsync(target.Id);
        Assert.Equal(ParticipantRole.Media, reloaded!.Role);
        // The ex-speaker's dated speakerdl task is pruned by the save itself.
        Assert.False(await db.Tasks.AnyAsync(t => t.AssignedParticipantId == target.Id
            && t.SourceKey!.StartsWith("speakerdl:")));
        // The new role's wizard steps were seeded.
        Assert.True(await db.Tasks.AnyAsync(t => t.AssignedParticipantId == target.Id
            && t.SourceKey == WizardStepTaskKeys.Profile(target.Id)));
    }

    // =====================================================================
    //  G12 — TasksTable grid + export hide deactivated assignees by default
    // =====================================================================

    [Fact]
    public async Task TasksTable_hides_tasks_of_deactivated_assignees_by_default()
    {
        using var db = NewDb();
        var (eventId, org) = await SeedAsync(db);
        var active = new Participant
        {
            EventId = eventId, Email = "a@x.dk", FullName = "Act Ive",
            Role = ParticipantRole.Volunteer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        var gone = new Participant
        {
            EventId = eventId, Email = "g@x.dk", FullName = "Gon Er",
            Role = ParticipantRole.Volunteer, IsActive = false,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.AddRange(active, gone);
        await db.SaveChangesAsync();
        db.Tasks.AddRange(
            new ParticipantTask { EventId = eventId, AssignedParticipantId = active.Id, Title = "Kept", State = TaskState.Open },
            new ParticipantTask { EventId = eventId, AssignedParticipantId = gone.Id, Title = "Hidden", State = TaskState.Open },
            new ParticipantTask { EventId = eventId, AssignedParticipantId = null, Title = "Unassigned", State = TaskState.Open });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(org) };
        var model = new TasksTableModel(
            db, new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http)), new FixedClock())
        {
            PageContext = new PageContext { HttpContext = http },
        };

        // Default: the drop-out's task is hidden; unassigned rows stay visible.
        await model.OnGetAsync(default);
        Assert.Equal(new[] { "Kept", "Unassigned" },
            model.Tasks.Select(t => t.Title).OrderBy(t => t).ToArray());

        // Opt-in: include deactivated assignees (e.g. to clean them up).
        model.ShowInactiveAssignees = true;
        await model.OnGetAsync(default);
        Assert.Equal(3, model.Tasks.Count);
    }

    // =====================================================================
    //  §252 orphan (a) — party invite email carries the open-calendar links
    // =====================================================================

    [Fact]
    public async Task Party_invite_email_contains_google_and_outlook_open_links()
    {
        using var db = NewDb();
        var (eventId, _) = await SeedAsync(db);
        var me = new Participant
        {
            EventId = eventId, Email = "party@x.dk", FullName = "Party Person",
            Role = ParticipantRole.Volunteer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(me);
        await db.SaveChangesAsync();

        var sender = new CapturingSender();
        var http = new DefaultHttpContext { User = Session(me) };
        var model = new PartyModel(
            new PartyRsvpService(db),
            new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http)),
            new CalendarInviteEmailService(db, sender, new EmailContextAccessor(), new FixedClock()),
            NullLogger<PartyModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = http },
        };

        await model.OnPostInviteAsync(default);

        var m = Assert.Single(sender.Sent);
        Assert.Equal("party@x.dk", m.To);
        // Open-not-download calendar links (operator ask), small links in the intro.
        Assert.Contains("calendar.google.com/calendar/render?action=TEMPLATE", m.Html);
        Assert.Contains("outlook.office.com/calendar", m.Html);
        Assert.Contains(">Google Calendar</a>", m.Html);
        Assert.Contains(">Outlook</a>", m.Html);
    }
}
