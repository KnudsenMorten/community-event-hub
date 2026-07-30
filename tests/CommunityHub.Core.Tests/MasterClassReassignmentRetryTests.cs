using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §234 3 — the ticket-reassignment VALIDATION email is no longer fire-and-forget.
/// The sync jobs persist the intent (<see cref="Attendee.ReassignmentValidationPendingSince"/>)
/// BEFORE sending; the marker clears ONLY on an actually-DELIVERED send (the
/// <see cref="IEmailDeliveryOutcome"/> seam — a ring-dropped send keeps it), so the next
/// sync run retries. The inherited-MC title is recomputed at send time from the current
/// confirmed signup.
/// </summary>
public sealed class MasterClassReassignmentRetryTests
{
    private sealed class NoOpContext : IEmailContextAccessor
    {
        public EmailContext? Current => null;
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) => new D();
    }

    /// <summary>A fixed delivered-vs-dropped seam (what BrevoEmailSender would report).</summary>
    private sealed class FixedOutcome : IEmailDeliveryOutcome
    {
        public FixedOutcome(bool delivered, string? reason = null)
        { LastSendDelivered = delivered; LastDropReason = delivered ? null : reason ?? "ring-drop"; }
        public bool LastSendDelivered { get; }
        public string? LastDropReason { get; }
        public void Record(bool delivered, string? reason = null) { }
    }

    private static async Task<(int ev, int mc, int att)> SeedAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        var e = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(e); await db.SaveChangesAsync();
        var s = new Session { EventId = e.Id, Title = "Deep Dive MC", Type = SessionType.MasterClass, MasterClassCapacity = 5 };
        db.Sessions.Add(s); await db.SaveChangesAsync();
        var a = new Attendee
        {
            EventId = e.Id, BackstageTicketId = "t-1", Email = "new@x.dk",
            FirstName = "New", LastName = "Holder", TicketStatus = TicketStatus.TwoDay,
        };
        db.Attendees.Add(a); await db.SaveChangesAsync();
        return (e.Id, s.Id, a.Id);
    }

    private static MasterClassEmailService Build(
        CommunityHub.Core.Data.CommunityHubDbContext db,
        CapturingEmailSender sender, IEmailDeliveryOutcome? outcome)
        => new(db, sender, new NoOpContext(), new MasterClassSignupService(db), outcome: outcome);

    [Fact]
    public async Task Pending_marker_is_persisted_idempotently_and_lists_with_the_recomputed_title()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc, att) = await SeedAsync(db);
        db.MasterClassSignups.Add(new MasterClassSignup
        { EventId = ev, SessionId = mc, AttendeeId = att, Status = MasterClassSignupStatus.Confirmed });
        await db.SaveChangesAsync();
        var email = Build(db, new CapturingEmailSender(), outcome: null);

        await email.MarkReassignmentValidationsPendingAsync(ev, new[] { att });
        var since = (await db.Attendees.FindAsync(att))!.ReassignmentValidationPendingSince;
        Assert.NotNull(since);

        // Idempotent: a second mark keeps the ORIGINAL timestamp.
        await email.MarkReassignmentValidationsPendingAsync(ev, new[] { att });
        Assert.Equal(since, (await db.Attendees.FindAsync(att))!.ReassignmentValidationPendingSince);

        // The pending list recomputes the inherited title from the CURRENT confirmed seat.
        var pending = Assert.Single(await email.GetPendingReassignmentValidationsAsync(ev));
        Assert.Equal(att, pending.AttendeeId);
        Assert.Equal("Deep Dive MC", pending.InheritedMcTitle);

        // No confirmed seat ⇒ null title (the email asks them to choose one).
        db.MasterClassSignups.RemoveRange(db.MasterClassSignups);
        await db.SaveChangesAsync();
        pending = Assert.Single(await email.GetPendingReassignmentValidationsAsync(ev));
        Assert.Null(pending.InheritedMcTitle);
    }

    [Fact]
    public async Task Delivered_send_clears_the_marker_and_reports_sent()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _, att) = await SeedAsync(db);
        var sender = new CapturingEmailSender();
        var email = Build(db, sender, new FixedOutcome(delivered: true));
        await email.MarkReassignmentValidationsPendingAsync(ev, new[] { att });

        Assert.True(await email.SendReassignmentValidationAsync(att, "Deep Dive MC", "https://hub.test"));
        Assert.Single(sender.Sent);
        Assert.Null((await db.Attendees.FindAsync(att))!.ReassignmentValidationPendingSince);
        Assert.Empty(await email.GetPendingReassignmentValidationsAsync(ev));
    }

    [Fact]
    public async Task Ring_dropped_send_keeps_the_marker_so_the_next_run_retries()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _, att) = await SeedAsync(db);
        var sender = new CapturingEmailSender();
        var email = Build(db, sender, new FixedOutcome(delivered: false));
        await email.MarkReassignmentValidationsPendingAsync(ev, new[] { att });

        // The transport was invoked but reported a drop ⇒ NOT sent, marker kept.
        Assert.False(await email.SendReassignmentValidationAsync(att, "Deep Dive MC", "https://hub.test"));
        Assert.NotNull((await db.Attendees.FindAsync(att))!.ReassignmentValidationPendingSince);
        Assert.Single(await email.GetPendingReassignmentValidationsAsync(ev));
    }

    [Fact]
    public async Task Cancelled_attendees_are_not_listed_for_retry()
    {
        // A ticket cancelled AFTER the reassignment was detected must not be emailed;
        // the marker stays dormant and resumes only if the ticket reactivates.
        using var db = ScenarioFixture.NewDb();
        var (ev, _, att) = await SeedAsync(db);
        var email = Build(db, new CapturingEmailSender(), outcome: null);
        await email.MarkReassignmentValidationsPendingAsync(ev, new[] { att });

        var a = await db.Attendees.FindAsync(att);
        a!.MirrorState = MirrorState.Cancelled;
        await db.SaveChangesAsync();
        Assert.Empty(await email.GetPendingReassignmentValidationsAsync(ev));

        a.MirrorState = MirrorState.Active;
        await db.SaveChangesAsync();
        Assert.Single(await email.GetPendingReassignmentValidationsAsync(ev));
    }

    [Fact]
    public async Task Legacy_wiring_without_the_outcome_seam_behaves_as_before()
    {
        // Null seam (old tests/constructions): a non-throwing send counts as sent and
        // clears the marker — no behaviour change for existing callers.
        using var db = ScenarioFixture.NewDb();
        var (ev, _, att) = await SeedAsync(db);
        var email = Build(db, new CapturingEmailSender(), outcome: null);
        await email.MarkReassignmentValidationsPendingAsync(ev, new[] { att });

        Assert.True(await email.SendReassignmentValidationAsync(att, null, "https://hub.test"));
        Assert.Null((await db.Attendees.FindAsync(att))!.ReassignmentValidationPendingSince);
    }
}
