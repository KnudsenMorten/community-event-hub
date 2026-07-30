using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Forms;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §413 — a COMPLETED Master Class step must be leaveable (operator 2026-07-27: <i>"when i click
/// 'Save &amp; Exit' nothing happens … it fails even though the user already have selected a master
/// class"</i>).
///
/// <para><b>The defect.</b> The step refused any save that posted no radio, regardless of state. So
/// an attendee who already held a CONFIRMED seat and pressed Save &amp; next or Save &amp; exit got
/// <i>"Please choose a Master Class"</i> rendered directly above <i>"✓ You're confirmed for Test
/// Master Class"</i> — the screen contradicting itself, and no way out of a step that was already
/// done. Save &amp; exit (§393) is gated on a successful outcome, so it silently did nothing, which
/// is what he saw.</para>
///
/// <para><b>The rule now pinned:</b> posting no radio is "nothing to change", not "no seat". This
/// step is data-backed — the seat is already persisted — so an existing confirmed signup advances.
/// Only an attendee with NO seat is asked to choose, which is the case the message was written
/// for.</para>
/// </summary>
public sealed class MasterClassStepSaveTests
{
    private const int EventId = 1;

    [Fact]
    public async Task An_attendee_who_ALREADY_holds_a_seat_can_save_without_re_picking()
    {
        await using var db = NewDb();
        var (email, attendeeId, sessionId) = await SeedAsync(db);

        // They already hold the seat — exactly the state in his screenshot.
        db.MasterClassSignups.Add(new MasterClassSignup
        {
            EventId = EventId, AttendeeId = attendeeId, SessionId = sessionId,
            Status = MasterClassSignupStatus.Confirmed, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var model = new MasterClassFormModel { SessionId = 0 };   // nothing posted
        var ms = new ModelStateDictionary();

        var outcome = await Service(db).SaveAsync(model, EventId, email, "https://hub.test", ms, default);

        Assert.Equal(WizardStepOutcome.Advance, outcome);
        Assert.True(ms.IsValid);
        Assert.NotNull(model.Confirmed);   // and the step still renders their seat
    }

    [Fact]
    public async Task An_attendee_with_NO_seat_is_still_asked_to_choose()
    {
        // The guard must not be removed wholesale — someone who has genuinely not picked yet needs
        // the prompt, which is the case the message was written for.
        await using var db = NewDb();
        var (email, _, _) = await SeedAsync(db);

        var model = new MasterClassFormModel { SessionId = 0 };
        var ms = new ModelStateDictionary();

        var outcome = await Service(db).SaveAsync(model, EventId, email, "https://hub.test", ms, default);

        Assert.Equal(WizardStepOutcome.Invalid, outcome);
        Assert.False(ms.IsValid);
    }

    [Fact]
    public async Task A_non_two_day_attendee_is_NOT_RELEVANT_rather_than_invalid()
    {
        // Unchanged behaviour, pinned alongside so the three outcomes stay distinct: a 1-day
        // attendee has no Master Class to choose and must never be blocked by this step.
        await using var db = NewDb();
        var (email, _, _) = await SeedAsync(db, ticket: TicketStatus.Other);

        var outcome = await Service(db).SaveAsync(
            new MasterClassFormModel { SessionId = 0 }, EventId, email, "https://hub.test",
            new ModelStateDictionary(), default);

        Assert.Equal(WizardStepOutcome.NotRelevant, outcome);
    }

    // ---- harness ----------------------------------------------------------

    private sealed class NoOpSender : IEmailSender
    {
        public Task SendAsync(string t, string s, string h, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string t, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string t, string s, string h, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithIcsAsync(string t, string s, string h, string ics, string fn, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithAttachmentsAsync(string t, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static MasterClassFormService Service(CommunityHubDbContext db)
    {
        var signups = new MasterClassSignupService(db);
        return new MasterClassFormService(
            signups,
            new MasterClassEmailService(db, new NoOpSender(), new EmailContextAccessor(), signups));
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"mc-step-{Guid.NewGuid():N}").Options);

    private static async Task<(string Email, int AttendeeId, int SessionId)> SeedAsync(
        CommunityHubDbContext db, TicketStatus ticket = TicketStatus.TwoDay)
    {
        const string email = "attendee@example.test";
        db.Events.Add(new Event { Id = EventId, Code = "TEST", IsActive = true });

        var session = new Session
        {
            EventId = EventId, Title = "Test Master Class",
            Type = SessionType.MasterClass, MasterClassCapacity = 10,
        };
        db.Sessions.Add(session);

        var attendee = new Attendee
        {
            EventId = EventId, Email = email, FullName = "Test Attendee",
            FirstName = "Test", LastName = "Attendee",
            BackstageTicketId = "t-1", TicketStatus = ticket,
        };
        db.Attendees.Add(attendee);
        await db.SaveChangesAsync();
        return (email, attendee.Id, session.Id);
    }
}
