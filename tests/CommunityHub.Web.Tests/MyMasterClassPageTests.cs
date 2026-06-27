using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §139: the reworked attendee Master Class self-service page model
/// (<see cref="MyMasterClassModel"/>). Proves, over the real page model + service:
///   • the 3-section data is populated (current confirmed seat + waitlists; ALL classes
///     with Available/FULL availability for section 2);
///   • <c>OnPostSwitchAsync</c> performs the ATOMIC switch (old seat cancelled, new seat
///     confirmed);
///   • the safety-net: a switch that loses the race (target filled mid-session) redirects
///     carrying <see cref="MasterClassSignupService.NowFullError"/> and the attendee KEEPS
///     their original seat.
/// Resolution is via the emailed magic-link token (no signed-in participant needed).
/// FAKE names only.
/// </summary>
public sealed class MyMasterClassPageTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"mymc-{Guid.NewGuid():N}")
            .Options);

    private sealed class NoParticipant : ICurrentParticipantAccessor
    {
        public CurrentParticipant? Current => null;
    }

    private sealed class NoOpSender : IEmailSender
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody, string ics, string icsName, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpContext : IEmailContextAccessor
    {
        public EmailContext? Current => null;
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) => new D();
    }

    private static MyMasterClassModel NewModel(CommunityHubDbContext db)
    {
        var svc = new MasterClassSignupService(db);
        var promo = new MasterClassPromotionEmailService(db, new NoOpSender(), new NoOpContext(), svc);
        var email = new MasterClassEmailService(db, new NoOpSender(), new NoOpContext(), svc);
        return new MyMasterClassModel(svc, new NoParticipant(), promo, email)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private sealed record Seed(int EventId, int McA, int McB, int AttId, string Token);

    private static async Task<Seed> SeedAsync(CommunityHubDbContext db, int capA, int capB, string token = "tok-abc")
    {
        var e = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(e); await db.SaveChangesAsync();
        var a = new Session { EventId = e.Id, Title = "MC A", Type = SessionType.MasterClass, MasterClassCapacity = capA };
        var b = new Session { EventId = e.Id, Title = "MC B", Type = SessionType.MasterClass, MasterClassCapacity = capB };
        db.Sessions.AddRange(a, b); await db.SaveChangesAsync();
        var att = new Attendee
        {
            EventId = e.Id, Email = "p@x.dk", FirstName = "Pat", LastName = "Lee",
            TicketStatus = TicketStatus.TwoDay, SelfServiceToken = token,
        };
        db.Attendees.Add(att); await db.SaveChangesAsync();
        return new Seed(e.Id, a.Id, b.Id, att.Id, token);
    }

    private static async Task<int> OtherAttendee(CommunityHubDbContext db, int ev, string email)
    {
        var a = new Attendee { EventId = ev, Email = email, FirstName = "F", LastName = "X", TicketStatus = TicketStatus.TwoDay };
        db.Attendees.Add(a); await db.SaveChangesAsync(); return a.Id;
    }

    [Fact]
    public async Task Get_populates_the_three_sections_with_availability()
    {
        using var db = NewDb();
        var s = await SeedAsync(db, capA: 5, capB: 1);
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(s.EventId, s.AttId, s.McA);                          // confirmed in A (section 1)
        await svc.SignUpAsync(s.EventId, await OtherAttendee(db, s.EventId, "x@x.dk"), s.McB);  // B now full

        var model = NewModel(db);
        await model.OnGetAsync(s.Token, null, null, null, false, default);

        Assert.False(model.InvalidLink);
        Assert.True(model.Eligible);
        // Section 1: the confirmed seat.
        Assert.NotNull(model.Confirmed);
        Assert.Equal(s.McA, model.Confirmed!.SessionId);
        // Section 2: ALL classes listed with availability; B is FULL, A still has room.
        var a = model.Options.Single(o => o.SessionId == s.McA);
        var b = model.Options.Single(o => o.SessionId == s.McB);
        Assert.False(a.IsFull);
        Assert.Equal(4, a.Free);     // 5 cap - 1 taken
        Assert.True(b.IsFull);
    }

    [Fact]
    public async Task Switch_handler_moves_the_seat_atomically()
    {
        using var db = NewDb();
        var s = await SeedAsync(db, capA: 5, capB: 5);
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(s.EventId, s.AttId, s.McA);   // confirmed in A

        var model = NewModel(db);
        var result = await model.OnPostSwitchAsync(s.Token, s.McB, default);

        Assert.IsType<RedirectToPageResult>(result);
        var sig = Assert.Single(await svc.GetForAttendeeAsync(s.EventId, s.AttId));
        Assert.Equal(s.McB, sig.SessionId);
        Assert.Equal(MasterClassSignupStatus.Confirmed, sig.Status);
    }

    [Fact]
    public async Task Switch_losing_the_race_redirects_with_full_error_and_keeps_old_seat()
    {
        using var db = NewDb();
        var s = await SeedAsync(db, capA: 5, capB: 1);
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(s.EventId, s.AttId, s.McA);                                       // confirmed in A
        await svc.SignUpAsync(s.EventId, await OtherAttendee(db, s.EventId, "x@x.dk"), s.McB);  // B fills (1/1)

        var model = NewModel(db);
        var result = await model.OnPostSwitchAsync(s.Token, s.McB, default);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(MasterClassSignupService.NowFullError, redirect.RouteValues!["msg"]);

        // Old seat in A survived — the switch was all-or-nothing.
        var sig = Assert.Single(await svc.GetForAttendeeAsync(s.EventId, s.AttId));
        Assert.Equal(s.McA, sig.SessionId);
        Assert.Equal(MasterClassSignupStatus.Confirmed, sig.Status);
    }

    [Fact]
    public async Task Invalid_token_renders_invalid_link()
    {
        using var db = NewDb();
        await SeedAsync(db, 5, 5);
        var model = NewModel(db);
        await model.OnGetAsync("not-a-token", null, null, null, false, default);
        Assert.True(model.InvalidLink);
    }

    [Fact] // §140: a FULL class surfaces how many people are on its waitlist (McOption.Waitlisted).
    public async Task Full_class_surfaces_the_waitlist_count()
    {
        using var db = NewDb();
        var s = await SeedAsync(db, capA: 5, capB: 1);
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(s.EventId, await OtherAttendee(db, s.EventId, "x@x.dk"), s.McB);  // B full (1/1)
        await svc.SignUpAsync(s.EventId, await OtherAttendee(db, s.EventId, "y@x.dk"), s.McB);  // waitlist
        await svc.SignUpAsync(s.EventId, await OtherAttendee(db, s.EventId, "z@x.dk"), s.McB);  // waitlist

        var model = NewModel(db);
        await model.OnGetAsync(s.Token, null, null, null, false, default);

        var b = model.Options.Single(o => o.SessionId == s.McB);
        Assert.True(b.IsFull);
        Assert.Equal(2, b.Waitlisted);   // shown in the GUI as "2 on waitlist"
    }

    [Fact] // §140: the reminder toggle tags its redirect so the flash anchors by that button.
    public async Task Flash_after_reminder_toggle_anchors_to_the_reminder_scope()
    {
        using var db = NewDb();
        var s = await SeedAsync(db, capA: 5, capB: 5);
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(s.EventId, s.AttId, s.McA);   // confirmed (so a reminder toggle is possible)

        var model = NewModel(db);
        var result = await model.OnPostToggleReminderAsync(s.Token, true, default);
        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("reminder", redirect.RouteValues!["scope"]);

        // Re-render carrying that scope → the flash is placed inline at the reminder toggle.
        var model2 = NewModel(db);
        await model2.OnGetAsync(s.Token, "We'll remind you about a month before.", "reminder", null, false, default);
        Assert.True(model2.FlashAtReminder);
        Assert.True(model2.FlashPlacedInline);
    }

    [Fact] // §140: a session-anchored flash is placed inline (no faint top banner).
    public async Task Session_anchored_flash_is_placed_inline()
    {
        using var db = NewDb();
        var s = await SeedAsync(db, capA: 5, capB: 5);
        var model = NewModel(db);
        // A give-up confirmation tagged with the freed class (McB exists in Options).
        await model.OnGetAsync(s.Token, "Done — your Master Class place was updated.",
            null, s.McB, false, default);
        Assert.True(model.FlashPlacedInline);
    }
}
