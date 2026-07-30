using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Forms;
using CommunityHub.Pages;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §206 Party form (authenticated-only, explicit Yes/No, no default) + §207/§208 attendee
/// Get-Started stepper (Master Class + Party for 2-day; Party for 1-day). Offline page-model
/// + service tests over EF in-memory.
/// </summary>
public sealed class PartyAndAttendeeWizardTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"party-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-30T10:00:00Z");
    }

    private sealed class NoOpSender : IEmailSender
    {
        public Task SendAsync(string t, string s, string h, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string t, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string t, string s, string h, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithIcsAsync(string t, string s, string h, string ics, string fn, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithAttachmentsAsync(string t, string s, string h, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpContext : IEmailContextAccessor
    {
        public EmailContext? Current => null;
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) => new D();
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static ClaimsPrincipal Session(Participant p) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new Claim(ClaimTypes.Email, p.Email),
            new Claim(ClaimTypes.Name, p.FullName),
            new Claim(ClaimTypes.Role, p.Role.ToString()),
            new Claim("EventId", p.EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme));

    private static PartyModel NewPartyModel(CommunityHubDbContext db, Participant? signedIn)
    {
        var http = new DefaultHttpContext();
        if (signedIn is not null) http.User = Session(signedIn);
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var calendar = new CalendarInviteEmailService(db, new NoOpSender(), new NoOpContext(), new FixedClock());
        return new PartyModel(new PartyRsvpService(db), accessor, calendar,
            NullLogger<PartyModel>.Instance)
        {
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext
            {
                HttpContext = http,
            },
        };
    }

    private static async Task<(int ev, Participant p)> SeedAsync(
        CommunityHubDbContext db, ParticipantRole role = ParticipantRole.Attendee)
    {
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "T 2027", Code = "T27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = ev.Id, Email = "p@x.dk", FullName = "Person One",
            Role = role, IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return (ev.Id, p);
    }

    // ----- §206 explicit Yes/No, no default ------------------------------

    [Fact]
    public async Task Unanswered_party_form_is_incomplete_no_row_created()
    {
        using var db = NewDb();
        var (ev, p) = await SeedAsync(db);
        var model = NewPartyModel(db, p);
        model.Attending = null;   // neither Yes nor No actively chosen

        await model.OnPostAsync(default);

        Assert.False(model.SubmittedOk);
        Assert.NotNull(model.ErrorMessage);
        Assert.False(await db.PartyRsvps.AnyAsync(r => r.EventId == ev));   // no default RSVP row
    }

    [Fact]
    public async Task Explicit_yes_creates_attending_row_and_enables_invite()
    {
        using var db = NewDb();
        var (ev, p) = await SeedAsync(db);
        var model = NewPartyModel(db, p);
        model.Attending = true;   // actively chose Yes

        await model.OnPostAsync(default);

        Assert.True(model.SubmittedOk);
        Assert.True(model.Attending);   // the view shows the calendar-invite button on Yes
        var row = await db.PartyRsvps.SingleAsync(r => r.EventId == ev && r.ParticipantId == p.Id);
        Assert.True(row.Attending);
    }

    [Fact]
    public async Task Explicit_no_creates_declined_row()
    {
        using var db = NewDb();
        var (ev, p) = await SeedAsync(db);
        var model = NewPartyModel(db, p);
        model.Attending = false;   // actively chose No

        await model.OnPostAsync(default);

        Assert.True(model.SubmittedOk);
        var row = await db.PartyRsvps.SingleAsync(r => r.EventId == ev && r.ParticipantId == p.Id);
        Assert.False(row.Attending);
    }

    [Fact]
    public async Task Yes_invite_handler_sends_calendar_invite_for_the_window()
    {
        using var db = NewDb();
        var (ev, p) = await SeedAsync(db);
        // CalendarSyncEnabled must be on for the invite to actually send.
        var evRow = await db.Events.FirstAsync(e => e.Id == ev);
        evRow.CalendarSyncEnabled = true;
        await db.SaveChangesAsync();

        var captured = new List<(string To, string Ics)>();
        var sender = new CapturingIcsSender(captured);
        var calendar = new CalendarInviteEmailService(db, sender, new NoOpContext(), new FixedClock());
        var http = new DefaultHttpContext { User = Session(p) };
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var model = new PartyModel(new PartyRsvpService(db), accessor, calendar, NullLogger<PartyModel>.Instance)
        {
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = http },
        };

        await model.OnPostInviteAsync(default);

        Assert.True(model.SubmittedOk);
        var sent = Assert.Single(captured);
        Assert.Equal("p@x.dk", sent.To);
        Assert.Contains("BEGIN:VEVENT", sent.Ics);
    }

    private sealed class CapturingIcsSender(List<(string To, string Ics)> captured) : IEmailSender
    {
        public Task SendAsync(string t, string s, string h, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string t, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string t, string s, string h, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithIcsAsync(string t, string s, string h, string ics, string fn, CancellationToken ct = default)
        { captured.Add((t, ics)); return Task.CompletedTask; }
        public Task SendWithAttachmentsAsync(string t, string s, string h, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ----- §207/§208 attendee stepper ------------------------------------

    [Fact]
    public async Task Two_day_attendee_stepper_has_masterclass_then_party()
    {
        using var db = NewDb();
        var (ev, p) = await SeedAsync(db);
        db.Attendees.Add(new Attendee
        {
            EventId = ev, BackstageTicketId = "t1", Email = p.Email,
            FullName = p.FullName, TicketStatus = TicketStatus.TwoDay,
        });
        await db.SaveChangesAsync();

        var view = await new AttendeeWizardService(db).BuildAsync(ev, p.Id);
        Assert.Equal(new[] { "masterclass", "party" }, view.Steps.Select(s => s.Key).ToArray());
        Assert.All(view.Steps, s => Assert.False(s.Done));   // nothing done yet
        // §410: an attendee's only tasks ARE wizard mirrors (party-form, masterclass-form), so the
        // deadlines step must not appear — it would list their own wizard back at them.
        Assert.DoesNotContain(view.Steps, s => s.Key == "deadlines");
    }

    [Fact]
    public async Task One_day_attendee_stepper_has_party_only()
    {
        using var db = NewDb();
        var (ev, p) = await SeedAsync(db);
        db.Attendees.Add(new Attendee
        {
            EventId = ev, BackstageTicketId = "t1", Email = p.Email,
            FullName = p.FullName, TicketStatus = TicketStatus.Other,
        });
        await db.SaveChangesAsync();

        var view = await new AttendeeWizardService(db).BuildAsync(ev, p.Id);
        Assert.Equal(new[] { "party" }, view.Steps.Select(s => s.Key).ToArray());
    }

    [Fact]
    public async Task Party_step_done_once_an_rsvp_row_exists()
    {
        using var db = NewDb();
        var (ev, p) = await SeedAsync(db);
        db.Attendees.Add(new Attendee
        {
            EventId = ev, BackstageTicketId = "t1", Email = p.Email,
            FullName = p.FullName, TicketStatus = TicketStatus.Other,
        });
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = ev, ParticipantId = p.Id, Name = p.FullName, Email = p.Email, Attending = false,
        });
        await db.SaveChangesAsync();

        var view = await new AttendeeWizardService(db).BuildAsync(ev, p.Id);
        Assert.True(view.Steps.Single(s => s.Key == "party").Done);
    }
}
