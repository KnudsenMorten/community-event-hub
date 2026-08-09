using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §243 (operator 2026-07-07: "if ticket is cancelled, then they must get email that their
/// ticket was cancelled if they are a 2-day master class holder") — the ticket-cancelled
/// notification sweep the sync jobs run
/// (<see cref="MasterClassEmailService.SendPendingTicketCancellationsAsync"/>):
/// <list type="bullet">
/// <item>audience = soft-cancelled ACTIVE-no-more 2-DAY holders only (1-day cancellations
/// and active tickets are never mailed) within the 7-day sweep window;</item>
/// <item>ONCE per cancellation via the SentReminder ledger keyed on CancelledAt — a
/// REAPPEARED ticket that is cancelled AGAIN mails again (fresh occasion);</item>
/// <item>a ring-dropped send is NOT ledgered (delivered-vs-dropped seam) so the next sync
/// run retries it once rings widen;</item>
/// <item>ring-gated per recipient: EmailContext FeatureKey <c>welcome-email</c> + the
/// recipient's provisioned Participant id, NO AttendeeWelcome tag (not a welcome — the
/// §217 ceiling does not apply).</item>
/// </list>
/// </summary>
public sealed class MasterClassTicketCancellationEmailTests
{
    private sealed class RecordingContext : IEmailContextAccessor
    {
        public EmailContext? Current { get; private set; }
        public EmailContext? LastSet { get; private set; }
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) { Current = c; LastSet = c; return new D(); }
    }

    private sealed class MutableOutcome : IEmailDeliveryOutcome
    {
        public bool LastSendDelivered { get; set; } = true;
        public string? LastDropReason => LastSendDelivered ? null : "ring-drop";
        public void Record(bool delivered, string? reason = null) { }
    }

    private static EmailTemplateProvider RealTemplates() =>
        new(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            HubUrl = "https://hub.example.test",
        }));

    private static async Task<int> SeedEventAsync(CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        var e = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
    }

    /// <summary>
    /// 🔒 §707.35b — the audience is decided by the TICKET CLASS, so every seed must carry one.
    /// It used to be decided by <see cref="TicketStatus"/>, which is why these seeds set only that;
    /// see the §707.34b regression test below for why that was wrong.
    /// </summary>
    private const string TwoDayClass = "2-day (Pre-day + Main Event)";
    private const string OneDayClass = "1-day (Main Event)";

    private static async Task<Attendee> SeedAttendeeAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, int ev, string email,
        TicketStatus status = TicketStatus.TwoDay,
        MirrorState mirror = MirrorState.Cancelled,
        DateTimeOffset? cancelledAt = null,
        string? ticketClassName = TwoDayClass,
        string? ticketClassId = null)
    {
        var a = new Attendee
        {
            EventId = ev, BackstageTicketId = Guid.NewGuid().ToString("N"),
            Email = email, FirstName = "Pat", LastName = "Lee",
            TicketStatus = status, MirrorState = mirror,
            TicketClassName = ticketClassName, TicketClassId = ticketClassId,
            CancelledAt = mirror == MirrorState.Cancelled
                ? (cancelledAt ?? DateTimeOffset.UtcNow) : null,
        };
        db.Attendees.Add(a);
        await db.SaveChangesAsync();
        return a;
    }

    private static MasterClassEmailService NewService(
        CommunityHub.Core.Data.CommunityHubDbContext db, CapturingEmailSender sender,
        IEmailContextAccessor? ctx = null, IEmailDeliveryOutcome? outcome = null,
        EmailTemplateProvider? templates = null) =>
        new(db, sender, ctx ?? new RecordingContext(), new MasterClassSignupService(db),
            templates: templates, outcome: outcome);

    // ------------------------------------------------------------------
    // Audience + once-per-cancellation dedup.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sweep_mails_only_cancelled_two_day_holders_and_only_once()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var target = await SeedAttendeeAsync(db, ev, "gone2day@x.dk");
        // §242: a 1-day cancellation gets no mail — decided by the CLASS, not TicketStatus.
        await SeedAttendeeAsync(db, ev, "gone1day@x.dk",
            status: TicketStatus.Other, ticketClassName: OneDayClass);
        await SeedAttendeeAsync(db, ev, "active@x.dk", mirror: MirrorState.Active);      // still active
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        Assert.Equal(1, await svc.SendPendingTicketCancellationsAsync(ev));
        var m = Assert.Single(sender.Messages);
        Assert.Equal("gone2day@x.dk", m.To);
        Assert.Contains("cancelled", m.Subject, StringComparison.OrdinalIgnoreCase);

        // Ledgered: the next sweep (10 minutes later) sends nothing.
        Assert.Equal(1, await db.SentReminders.CountAsync(
            r => r.ReminderType == MasterClassEmailService.TicketCancelledTemplate));
        Assert.Equal(0, await svc.SendPendingTicketCancellationsAsync(ev));
        Assert.Single(sender.Messages);

        // §326bc: the occasion keys on the cancellation DATE, not the tick. CancelledAt is
        // re-stamped on every cancel, so a tick-granular key changed on every flap and
        // re-mailed the whole cohort; a date-granular key absorbs that while still allowing
        // a genuine later cancellation to notify again.
        var row = await db.SentReminders.SingleAsync();
        Assert.Equal($"cancelled:{(await db.Attendees.FindAsync(target.Id))!.CancelledAt!.Value.UtcDateTime:yyyy-MM-dd}",
            row.OccasionKey);
    }

    /// <summary>
    /// 🔒 §707.34b REGRESSION — THE CASE THAT WAS NEVER COVERED, and the one that happens in
    /// production every time someone cancels in Zoho.
    /// </summary>
    /// <remarks>
    /// The sweep used to require <c>MirrorState == Cancelled &amp;&amp; TicketStatus == TwoDay</c>.
    /// Those are MUTUALLY EXCLUSIVE by construction: `AttendeeTicketSyncService.FromBackstage` maps
    /// any ticket Zoho reports as not-attending to <see cref="TicketStatus.None"/>, and the SAME
    /// `status_string = "not_attending"` is what makes the row Cancelled. One upstream field drove
    /// both conditions in opposite directions ⇒ a 2-day attendee cancelled in Zoho was NEVER told.
    ///
    /// <para>Every existing test in this file seeded <c>TicketStatus.TwoDay</c> ALONGSIDE
    /// <c>Cancelled</c> — a combination the real sync cannot produce — which is exactly why the bug
    /// survived a suite that looked thorough. Verified on PROD 2026-07-30: row 7312 sat at
    /// MirrorState 1 / TicketStatus 0 with no mail sent.</para>
    /// </remarks>
    [Fact]
    public async Task A_zoho_cancelled_two_day_holder_is_notified_even_though_TicketStatus_was_zeroed()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        // Exactly what the sync leaves behind: cancelled, and TicketStatus zeroed by Apply().
        await SeedAttendeeAsync(db, ev, "cancelled-in-zoho@x.dk",
            status: TicketStatus.None, mirror: MirrorState.Cancelled);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        Assert.Equal(1, await svc.SendPendingTicketCancellationsAsync(ev));
        Assert.Equal("cancelled-in-zoho@x.dk", Assert.Single(sender.Messages).To);
    }

    /// <summary>
    /// 🔒 §707.35b — the CLASS ID is authoritative, the display NAME is only a fallback. Operator
    /// 2026-07-30: *"ticket class has a unique number, dont use the displayname of the ticket
    /// class"*. A name is editable in Zoho (and WAS edited, §447), so a rename must never be able to
    /// change who receives mail.
    /// </summary>
    [Fact]
    public async Task A_one_day_class_is_never_notified_however_its_name_reads()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedAttendeeAsync(db, ev, "oneday@x.dk",
            status: TicketStatus.None, ticketClassName: OneDayClass);
        // A blank/unknown class FAILS CLOSED — no class, no mail.
        await SeedAttendeeAsync(db, ev, "unknownclass@x.dk",
            status: TicketStatus.None, ticketClassName: null);
        var sender = new CapturingEmailSender();

        Assert.Equal(0, await NewService(db, sender).SendPendingTicketCancellationsAsync(ev));
        Assert.Empty(sender.Messages);
    }

    /// <summary>
    /// §707.35b — the id decides, and it OVERRULES the name. Pinned on the policy directly (the
    /// service reads the edition config, which these in-memory tests do not wire).
    /// The real ids: 2-day <c>14880000003485482</c>, 1-day <c>14880000003485481</c> — the 1-day is
    /// deliberately absent from the config, so anything unlisted is non-2-day.
    /// </summary>
    [Fact]
    public void The_ticket_class_id_decides_and_beats_a_misleading_name()
    {
        var twoDayIds = new[] { "14880000003485482" };

        // Id says 2-day while the NAME says 1-day ⇒ 2-day. A rename cannot change the verdict.
        Assert.True(MasterClassTicketPolicy.IncludesMasterClass(
            "14880000003485482", OneDayClass, twoDayIds));
        // Id says 1-day while the NAME says 2-day ⇒ NOT 2-day.
        Assert.False(MasterClassTicketPolicy.IncludesMasterClass(
            "14880000003485481", TwoDayClass, twoDayIds));
        // No id (historic rows, or a payload without one) ⇒ documented fallback to the markers.
        Assert.True(MasterClassTicketPolicy.IncludesMasterClass(null, TwoDayClass, twoDayIds));
        Assert.False(MasterClassTicketPolicy.IncludesMasterClass(null, OneDayClass, twoDayIds));
    }

    [Fact]
    public async Task Reappearance_then_a_second_cancellation_mails_again()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var a = await SeedAttendeeAsync(db, ev, "p@x.dk",
            cancelledAt: DateTimeOffset.UtcNow.AddDays(-1));
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        Assert.Equal(1, await svc.SendPendingTicketCancellationsAsync(ev));

        // The ticket REAPPEARS (§128: MirrorState → Active, CancelledAt cleared)...
        var row = (await db.Attendees.FindAsync(a.Id))!;
        row.MirrorState = MirrorState.Active;
        row.CancelledAt = null;
        await db.SaveChangesAsync();
        Assert.Equal(0, await svc.SendPendingTicketCancellationsAsync(ev));

        // ...and is cancelled AGAIN later ⇒ a fresh occasion ⇒ one more mail.
        row.MirrorState = MirrorState.Cancelled;
        row.CancelledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        Assert.Equal(1, await svc.SendPendingTicketCancellationsAsync(ev));
        Assert.Equal(2, sender.Messages.Count);
    }

    [Fact]
    public async Task Cancellations_older_than_the_sweep_window_are_never_backfilled()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedAttendeeAsync(db, ev, "old@x.dk", cancelledAt: DateTimeOffset.UtcNow.AddDays(
            -(MasterClassEmailService.TicketCancelledSweepWindowDays + 1)));
        var sender = new CapturingEmailSender();

        Assert.Equal(0, await NewService(db, sender).SendPendingTicketCancellationsAsync(ev));
        Assert.Empty(sender.Messages);
    }

    // ------------------------------------------------------------------
    // Delivered-vs-dropped seam: ring-dropped ⇒ retried, then settles.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Ring_dropped_notice_is_not_ledgered_and_the_next_sweep_retries_it()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedAttendeeAsync(db, ev, "p@x.dk");
        var sender = new CapturingEmailSender();
        var outcome = new MutableOutcome { LastSendDelivered = false };
        var svc = NewService(db, sender, outcome: outcome);

        Assert.Equal(0, await svc.SendPendingTicketCancellationsAsync(ev));   // dropped
        Assert.Single(sender.Messages);                                        // dispatched though
        Assert.Equal(0, await db.SentReminders.CountAsync());                  // NOT ledgered

        outcome.LastSendDelivered = true;                                      // rings widened
        Assert.Equal(1, await svc.SendPendingTicketCancellationsAsync(ev));    // auto-retried
        Assert.Equal(1, await db.SentReminders.CountAsync());
        Assert.Equal(0, await svc.SendPendingTicketCancellationsAsync(ev));    // settled
    }

    // ------------------------------------------------------------------
    // Ring gating context + template render.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Notice_is_ring_gated_on_the_person_via_welcome_email_without_the_welcome_cap()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedAttendeeAsync(db, ev, "p@x.dk");
        // The provisioned (now §216-locked-out) login participant.
        var p = new Participant
        {
            EventId = ev, Email = "p@x.dk", FullName = "Pat Lee",
            Role = ParticipantRole.Attendee, IsActive = false,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var ctx = new RecordingContext();
        Assert.Equal(1, await NewService(db, new CapturingEmailSender(), ctx)
            .SendPendingTicketCancellationsAsync(ev));

        var c = ctx.LastSet;
        Assert.NotNull(c);
        Assert.Equal("welcome-email", c!.FeatureKey);   // ring-gated like attendee mail
        Assert.Equal(p.Id, c.ParticipantId);            // ...keyed on the PERSON
        Assert.False(c.AttendeeWelcome);                // NOT a welcome — no §217 ceiling
    }

    [Fact]
    public async Task Notice_renders_from_the_masterclass_cancelled_ticket_template()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedAttendeeAsync(db, ev, "p@x.dk");
        var sender = new CapturingEmailSender();

        Assert.Equal(1, await NewService(db, sender, templates: RealTemplates())
            .SendPendingTicketCancellationsAsync(ev));

        var m = Assert.Single(sender.Messages);
        Assert.Contains("C 2027", m.Subject);                       // {{eventDisplayName}}
        Assert.Contains("cancelled", m.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pat", m.Html);                             // {{firstName}}
        Assert.Contains("seat held by that ticket has been released", m.Html);

        // 🔒 §707.24 — the sign-in sentence is CONDITIONAL now. This attendee's only ticket was
        // cancelled, so they really are locked out and the mail may say so. It used to say this
        // unconditionally, which lied to anyone still holding a second active ticket.
        Assert.Contains("sign-in has been closed", m.Html);
        Assert.DoesNotContain("{{", m.Html);                        // no unresolved token
    }

    /// <summary>
    /// 🔴 §1015 — the sign-in line is a BULLET, not a paragraph after the list.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-09, with a screenshot: *"the event hub sign-in has been closed should
    /// be in the bullet above"*. Both statements answer the same "That means:" lead-in, so a
    /// <c>&lt;p&gt;</c> sitting after <c>&lt;/ul&gt;</c> — directly beneath a ONE-item list — read as
    /// a new, unrelated thought exactly where the eye expects item two.</para>
    ///
    /// <para>🔑 <b>The assertion is STRUCTURAL, deliberately.</b> The previous test asserted the
    /// words were present, and they were present the whole time it was wrong — the defect was
    /// entirely in where they sat. Checking the text again would pin nothing.</para>
    /// </remarks>
    [Theory]
    [InlineData(true)]    // still holds another ticket ⇒ "stays open"
    [InlineData(false)]   // fully cancelled ⇒ "has been closed"
    public async Task The_sign_in_line_is_a_bullet_inside_the_list(bool keepsAnotherTicket)
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedAttendeeAsync(db, ev, "p@x.dk");
        if (keepsAnotherTicket)
        {
            // A SECOND, still-active ticket on the same address flips the wording to "stays open".
            // Both wordings ride the same token, so both must be list items or the fix is half done.
            await SeedAttendeeAsync(db, ev, "p@x.dk", mirror: MirrorState.Active);
        }
        var sender = new CapturingEmailSender();

        await NewService(db, sender, templates: RealTemplates()).SendPendingTicketCancellationsAsync(ev);
        var html = Assert.Single(sender.Messages).Html;

        var phrase = keepsAnotherTicket ? "sign-in stays open" : "sign-in has been closed";
        var at = html.IndexOf(phrase, StringComparison.Ordinal);
        Assert.True(at > 0, $"'{phrase}' is missing from the mail entirely.");

        // It is INSIDE the unordered list: the last <ul> before it is not yet closed.
        var listEnd = html.IndexOf("</ul>", StringComparison.Ordinal);
        Assert.True(listEnd > at,
            "the sign-in line renders AFTER </ul> — it is a paragraph again, not a bullet.");

        // …and it is a real <li>, not a <p> that merely happens to sit inside the list markup.
        var liStart = html.LastIndexOf("<li", at, StringComparison.Ordinal);
        var pStart = html.LastIndexOf("<p", at, StringComparison.Ordinal);
        Assert.True(liStart > pStart, "the sign-in line is still wrapped in a <p>, not an <li>.");
    }
}
