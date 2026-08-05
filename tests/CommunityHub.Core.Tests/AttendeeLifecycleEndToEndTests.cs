using CommunityHub.Core.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using TR = CommunityHub.Core.Reminders.AttendeeTicketSyncService.TicketRow;

namespace CommunityHub.Core.Tests;

/// <summary>
/// DEEP end-to-end ATTENDEE-lifecycle test for BOTH ticket types (1-day / 2-day-with-Master-Class).
/// Chains the FULL journey the operator depends on for ~1500 attendees and asserts the DB state +
/// the produced emails/tasks/reminders at every step, validating the just-merged §206–§210 work
/// plus the existing attendee flow:
/// sync/provision → welcome email + §169 magic-link → task seeding → Get-Started stepper →
/// party RSVP (explicit Yes/No, un-answer reopens) → Master-Class selection + waitlist →
/// 2-week reminders (no 1-Dec gate) → §210 confirmed-seat 09:00–16:00 invite →
/// §209 reassignment / cancellation seat+party resets → idempotency.
///
/// <para>EF in-memory over a NAMED shared store (so the §169 magic-link service, which opens its
/// own DI scope, sees the same data), the REAL shipped email templates, an ephemeral
/// DataProtection provider, and a deterministic mutable clock. FAKE names/data only.</para>
/// </summary>
public sealed class AttendeeLifecycleEndToEndTests
{
    private const string Origin = "https://hub.example";
    private static readonly DateTimeOffset Base = new(2026, 6, 30, 9, 0, 0, TimeSpan.Zero);

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset now) => _now = now;
        public void Set(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class NoOpContext : IEmailContextAccessor
    {
        public EmailContext? Current => null;
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) => new D();
    }

    /// <summary>Everything one journey needs, wired exactly like the hosted graph.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public required ServiceProvider Sp { get; init; }
        public required IServiceScope Scope { get; init; }
        public required CommunityHubDbContext Db { get; init; }
        public required MutableClock Clock { get; init; }
        public required CapturingEmailSender Sender { get; init; }
        public required EmailTemplateProvider Templates { get; init; }
        public required MasterClassSignupService Mc { get; init; }
        public required AttendeeTicketSyncService Sync { get; init; }
        public required AttendeeWelcomeProvisioningService Provisioning { get; init; }
        public required AttendeeOneDayWelcomeEmailService OneDayWelcome { get; init; }
        public required MasterClassEmailService McEmail { get; init; }
        public required AttendeeMasterClassTaskSeeder McTaskSeeder { get; init; }
        public required PartyTaskSeeder PartyTaskSeeder { get; init; }
        public required FormTaskReconciler Reconciler { get; init; }
        public required PartyRsvpService Party { get; init; }
        public required AttendeePartyReminderBuilder PartyReminder { get; init; }
        public required AttendeeMasterClassReminderBuilder McReminder { get; init; }

        public int EventId { get; set; }

        public async ValueTask DisposeAsync() { Scope.Dispose(); await Sp.DisposeAsync(); }
    }

    private static Harness NewHarness()
    {
        var dbName = $"attendee-e2e-{Guid.NewGuid():N}";
        var clock = new MutableClock(Base);
        var sp = new ServiceCollection()
            .AddDbContext<CommunityHubDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton<IDataProtectionProvider>(DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-attendee-e2e"))))
            .AddSingleton<TimeProvider>(clock)
            .AddScoped<IEmailMagicLinkService, EmailMagicLinkService>()
            .BuildServiceProvider();

        var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
        var sender = new CapturingEmailSender();
        var templates = new EmailTemplateProvider(
            Options.Create(new EmailTemplateOptions
            {
                TemplateDirectory = RepoPaths.EmailTemplates(),
                PrivateTemplateDirectory = Path.Combine(Path.GetTempPath(), "ceh-no-private-attendee-e2e"),
                HubUrl = Origin,
            }),
            sp.GetRequiredService<IServiceScopeFactory>(),
            emailContext: null);

        var mc = new MasterClassSignupService(db);
        var ctx = new NoOpContext();
        return new Harness
        {
            Sp = sp, Scope = scope, Db = db, Clock = clock, Sender = sender, Templates = templates,
            Mc = mc,
            Sync = new AttendeeTicketSyncService(db, mc),
            Provisioning = new AttendeeWelcomeProvisioningService(
                db, clock, NullLogger<AttendeeWelcomeProvisioningService>.Instance),
            OneDayWelcome = new AttendeeOneDayWelcomeEmailService(db, templates, sender, clock, ctx),
            McEmail = new MasterClassEmailService(db, sender, ctx, mc, templates),
            McTaskSeeder = new AttendeeMasterClassTaskSeeder(db, clock),
            PartyTaskSeeder = new PartyTaskSeeder(db, clock),
            Reconciler = new FormTaskReconciler(db, clock),
            Party = new PartyRsvpService(db),
            PartyReminder = new AttendeePartyReminderBuilder(db, templates, clock),
            McReminder = new AttendeeMasterClassReminderBuilder(db, templates, clock),
        };
    }

    private static async Task<(int ev, int session)> SeedEditionAsync(Harness h, int capacity)
    {
        var ev = new Event
        {
            CommunityName = "Test Community", DisplayName = "Test Community 2027", Code = "TC27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        h.Db.Events.Add(ev);
        await h.Db.SaveChangesAsync();
        h.EventId = ev.Id;
        var s = new Session
        {
            EventId = ev.Id, Title = "Intune", Type = SessionType.MasterClass, MasterClassCapacity = capacity,
        };
        h.Db.Sessions.Add(s);
        await h.Db.SaveChangesAsync();
        return (ev.Id, s.Id);
    }

    /// <summary>Run the sync for a single incoming ticket and return the attendee row id.
    /// NOTE: a single-ticket pull SOFT-CANCELS every OTHER attendee not in the pull (by design,
    /// §128) — so when a test needs MULTIPLE coexisting attendees, seed the extra rows directly
    /// with <see cref="SeedAttendeeRowAsync"/> instead of calling this twice.</summary>
    private static async Task<int> SyncOneAsync(
        Harness h, string ticketId, string first, string last, string email, TicketStatus status, string cls)
    {
        await h.Sync.SyncAsync(h.EventId, new[] { new TR(ticketId, first, last, email, status, cls) });
        return h.Db.Attendees.Single(a => a.BackstageTicketId == ticketId).Id;
    }

    /// <summary>Seed an active attendee row directly (no sync) — for tests that need several
    /// attendees to coexist without the single-ticket-pull soft-cancel side effect.</summary>
    private static async Task<int> SeedAttendeeRowAsync(
        Harness h, string ticketId, string first, string last, string email, TicketStatus status)
    {
        var a = new Attendee
        {
            EventId = h.EventId, BackstageTicketId = ticketId, Email = email.ToLowerInvariant(),
            FirstName = first, LastName = last, FullName = $"{first} {last}",
            TicketStatus = status, MirrorState = MirrorState.Active,
        };
        h.Db.Attendees.Add(a);
        await h.Db.SaveChangesAsync();
        return a.Id;
    }

    private PartyRsvp? PartyRow(Harness h, string email) =>
        h.Db.PartyRsvps.AsNoTracking()
            .FirstOrDefault(r => r.EventId == h.EventId && r.Email.ToLower() == email.ToLowerInvariant());

    private static Task<ParticipantTask?> TaskByKey(Harness h, string key) =>
        h.Db.Tasks.FirstOrDefaultAsync(t => t.EventId == h.EventId && t.SourceKey == key);

    // =====================================================================
    //  2-DAY (Master-Class) ticket journey
    // =====================================================================

    [Fact]
    public async Task TwoDay_01_sync_creates_active_attendee_with_twoday_status()
    {
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 50);

        await SyncOneAsync(h, "T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day + Master Class");

        var a = h.Db.Attendees.Single();
        Assert.Equal(TicketStatus.TwoDay, a.TicketStatus);
        Assert.Equal(MirrorState.Active, a.MirrorState);
        Assert.Equal("ada@x.dk", a.Email);
        Assert.Null(a.CancelledAt);
    }

    [Fact]
    public async Task TwoDay_02_provision_creates_login_capable_attendee_participant()
    {
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 50);
        await SyncOneAsync(h, "T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day");

        var created = await h.Provisioning.ProvisionAsync(h.EventId);

        var pid = Assert.Single(created);
        var p = await h.Db.Participants.FindAsync(pid);
        Assert.NotNull(p);
        Assert.Equal(ParticipantRole.Attendee, p!.Role);
        Assert.True(p.IsActive);
        Assert.Equal(ParticipantLifecycleState.Active, p.LifecycleState);
        Assert.Equal("ada@x.dk", p.Email);

        // Idempotent: a re-run provisions nobody new.
        Assert.Empty(await h.Provisioning.ProvisionAsync(h.EventId));
    }

    [Fact]
    public async Task TwoDay_03_selection_invite_is_the_first_contact_and_carries_magic_link()
    {
        // FINDING-context: a 2-day holder has NO standalone "welcome" email — provisioning mints
        // only the login identity; the first attendee-facing send is the Master-Class SELECTION
        // INVITE (gated by Attendee.MasterClassInviteSentAt), which is the de-facto 2-day welcome.
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 50);
        var aid = await SyncOneAsync(h, "T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day");
        var pid = (await h.Provisioning.ProvisionAsync(h.EventId)).Single();

        Assert.True(await h.McEmail.SendSelectionInviteAsync(aid, Origin));

        var m = Assert.Single(h.Sender.Messages);
        Assert.Equal("ada@x.dk", m.To);
        Assert.Contains("Choose your Master Class", m.Subject);
        Assert.Contains("/Forms/Wizard?step=masterclass", m.Html);              // 351-7: magic-link CTA into the hub
        // §169: the recipient's standing personal magic-link GRANT was minted (the selection
        // invite's CTA is the MyMasterClass deep-link, so the /go/ rewrite rides the hub CTA token;
        // the minted grant is the durable signal the attendee can one-click sign in for a year).
        Assert.NotNull(await h.Db.MagicLinkGrants.FirstOrDefaultAsync(g => g.ParticipantId == pid));
        Assert.NotNull(h.Db.Attendees.Find(aid)!.MasterClassInviteSentAt);

        // New-only: a re-send without force is suppressed.
        Assert.False(await h.McEmail.SendSelectionInviteAsync(aid, Origin));
    }

    [Fact]
    public async Task TwoDay_03b_selection_invite_is_a_full_welcome_with_party_and_getstarted_cta()
    {
        // §215: the 2-day FIRST email is a full welcome — welcome intro + Party info +
        // a PRIMARY Get-Started CTA (magic-link target = /Forms/Wizard, so they land
        // signed-in on Get-Started), while KEEPING the Master-Class selection deep-link.
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 50);
        var aid = await SyncOneAsync(h, "T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day");
        await h.Provisioning.ProvisionAsync(h.EventId);   // so the §169 magic link binds

        Assert.True(await h.McEmail.SendSelectionInviteAsync(aid, Origin));
        var m = Assert.Single(h.Sender.Messages);

        // (a) WELCOME intro.
        Assert.Contains("Welcome to", m.Html);
        Assert.Contains("Test Community 2027", m.Html);                 // {{eventDisplayName}}

        // (b) PARTY info — the 16:00–18:30 / 9 Feb / Bella Center window + RSVP-in-hub.
        Assert.Contains("16:00", m.Html);
        Assert.Contains("9 Feb", m.Html);
        Assert.Contains("Bella Center", m.Html);

        // (c) PRIMARY CTA = the Get-Started wizard, reached through THIS attendee's §169
        //     auto-login magic-link (…/go/{token}/Forms/Wizard), so they land signed-in.
        Assert.Matches(@$"{System.Text.RegularExpressions.Regex.Escape(Origin)}/go/[^""/]+/Forms/Wizard", m.Html);

        // The Master-Class selection deep-link is KEPT (secondary).
        Assert.Contains("/Forms/Wizard?step=masterclass", m.Html); // 351-7: magic-link CTA into the hub
        Assert.Contains("Choose my Master Class", m.Html);
        Assert.Contains("Choose your Master Class", m.Subject);          // subject unchanged
    }

    [Fact]
    public async Task TwoDay_04_seeds_masterclass_and_party_tasks_with_correct_links()
    {
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 50);
        await SyncOneAsync(h, "T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day");
        var pid = (await h.Provisioning.ProvisionAsync(h.EventId)).Single();

        Assert.Equal(1, await h.McTaskSeeder.SeedAsync(h.EventId));
        Assert.Equal(1, await h.PartyTaskSeeder.SeedAsync(h.EventId));

        var mcTask = await TaskByKey(h, AttendeeMasterClassTaskSeeder.SourceKeyFor(pid));
        Assert.NotNull(mcTask);
        Assert.Equal(TaskState.Open, mcTask!.State);
        Assert.Null(mcTask.DueDate);                              // cadence is the 2-week reminder
        Assert.Contains("/Attendee", mcTask.Description!);        // links to the MC chooser

        var partyTask = await TaskByKey(h, PartyTaskSeeder.SourceKeyFor(pid));
        Assert.NotNull(partyTask);
        Assert.Equal(TaskState.Open, partyTask!.State);
        Assert.Null(partyTask.DueDate);                          // §177 attendee task: no due date
        Assert.Equal("Sign up for the Party", partyTask.Title);

        // Idempotent re-seed.
        Assert.Equal(0, await h.McTaskSeeder.SeedAsync(h.EventId));
        Assert.Equal(0, await h.PartyTaskSeeder.SeedAsync(h.EventId));
    }

    // (Get-Started stepper for 2-day = [masterclass, party] is validated headlessly in the Web
    //  GUI test AttendeeLifecycleGuiTests.GetStarted_page_renders_two_day_stepper — the
    //  AttendeeWizardService lives in the web project, not Core.)

    [Fact]
    public async Task TwoDay_06_party_no_then_yes_then_unanswer_reopens_task()
    {
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 50);
        await SyncOneAsync(h, "T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day");
        var pid = (await h.Provisioning.ProvisionAsync(h.EventId)).Single();
        await h.PartyTaskSeeder.SeedAsync(h.EventId);

        // Unanswered → task OPEN + no row.
        Assert.Equal(TaskState.Open, (await TaskByKey(h, PartyTaskSeeder.SourceKeyFor(pid)))!.State);
        Assert.Null(PartyRow(h, "ada@x.dk"));

        // Submit NO → row Attending=false; task Done.
        Assert.True((await h.Party.SubmitAsync("Ada Tester", "ada@x.dk", attending: false, ipHash: null, participantId: pid)).Ok);
        await h.Reconciler.ReconcileAsync(h.EventId, pid, default);
        Assert.False(PartyRow(h, "ada@x.dk")!.Attending);
        Assert.Equal(TaskState.Done, (await TaskByKey(h, PartyTaskSeeder.SourceKeyFor(pid)))!.State);

        // Change to YES → Attending=true; the calendar-invite path is available (window resolves).
        Assert.True((await h.Party.SubmitAsync("Ada Tester", "ada@x.dk", attending: true, ipHash: null, participantId: pid)).Ok);
        Assert.True(PartyRow(h, "ada@x.dk")!.Attending);
        var party = await h.Party.GetActivePartyAsync();
        var (startUtc, endUtc) = PartyRsvpService.WindowUtc(party!);
        Assert.True(endUtc > startUtc);

        // Un-answer (remove the row) → reconciler reopens the task so the cadence nags again.
        h.Db.PartyRsvps.RemoveRange(h.Db.PartyRsvps.Where(r => r.ParticipantId == pid));
        await h.Db.SaveChangesAsync();
        await h.Reconciler.ReconcileAsync(h.EventId, pid, default);
        var reopened = (await TaskByKey(h, PartyTaskSeeder.SourceKeyFor(pid)))!;
        Assert.Equal(TaskState.Open, reopened.State);
        Assert.Null(reopened.CompletedAt);
    }

    [Fact]
    public async Task TwoDay_07_masterclass_select_confirms_decrements_seat_and_marks_task_done()
    {
        await using var h = NewHarness();
        var (_, session) = await SeedEditionAsync(h, capacity: 2);
        var aid = await SyncOneAsync(h, "T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day");
        var pid = (await h.Provisioning.ProvisionAsync(h.EventId)).Single();
        await h.McTaskSeeder.SeedAsync(h.EventId);

        var r = await h.Mc.SignUpAsync(h.EventId, aid, session);
        Assert.True(r.Ok);
        Assert.Equal(MasterClassSignupStatus.Confirmed, r.Signup!.Status);

        var opt = (await h.Mc.ListMasterClassesAsync(h.EventId)).Single(o => o.SessionId == session);
        Assert.Equal(1, opt.Confirmed);
        Assert.Equal(1, opt.Free);                               // capacity 2, one taken

        await h.Reconciler.ReconcileAsync(h.EventId, pid, default);
        Assert.Equal(TaskState.Done, (await TaskByKey(h, AttendeeMasterClassTaskSeeder.SourceKeyFor(pid)))!.State);
    }

    [Fact]
    public async Task TwoDay_08_masterclass_full_puts_next_attendee_on_waitlist()
    {
        await using var h = NewHarness();
        var (_, session) = await SeedEditionAsync(h, capacity: 1);
        var a1 = await SeedAttendeeRowAsync(h, "T1", "Ada", "One", "a1@x.dk", TicketStatus.TwoDay);
        var a2 = await SeedAttendeeRowAsync(h, "T2", "Bo", "Two", "a2@x.dk", TicketStatus.TwoDay);

        Assert.Equal(MasterClassSignupStatus.Confirmed, (await h.Mc.SignUpAsync(h.EventId, a1, session)).Signup!.Status);
        var second = await h.Mc.SignUpAsync(h.EventId, a2, session);
        Assert.True(second.Ok);
        Assert.Equal(MasterClassSignupStatus.Waitlisted, second.Signup!.Status);

        var opt = (await h.Mc.ListMasterClassesAsync(h.EventId)).Single(o => o.SessionId == session);
        Assert.Equal(1, opt.Confirmed);
        Assert.Equal(1, opt.Waitlisted);
        Assert.Equal(0, opt.Free);
    }

    [Fact]
    public async Task TwoDay_09_confirmed_email_is_full_day_0900_1600_with_doors_text_and_subject_pointer()
    {
        await using var h = NewHarness();
        var (_, session) = await SeedEditionAsync(h, capacity: 5);
        // §257: verify the attached-invite path (gated behind the auto-invite switch).
        (await h.Db.Events.FindAsync(h.EventId))!.AutoCalendarInvitesEnabled = true;
        await h.Db.SaveChangesAsync();
        var aid = await SyncOneAsync(h, "T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day");
        await h.Provisioning.ProvisionAsync(h.EventId);
        await h.Mc.SignUpAsync(h.EventId, aid, session);
        var sigId = (await h.Mc.SignupIdAsync(h.EventId, aid, session))!.Value;

        await h.McEmail.SendConfirmedAsync(sigId, Origin);

        var ics = Assert.Single(h.Sender.IcsMessages);
        // §341-1 (operator 2026-07-26): the subject no longer points at a calendar invite,
        // because the mail no longer mentions one. The come-early callout below stays.
        Assert.DoesNotContain("see the calendar invite", ics.Subject);
        Assert.Contains("important details inside", ics.Subject);
        // 369: the registration/breakfast call-out was REMOVED at the operator request.
        Assert.DoesNotContain("Registration &amp; breakfast", ics.Html);
        // §210b calendar invite: the WHOLE master-class day, 08:00–16:00 local, in the venue zone.
        Assert.NotNull(h.Sender.LastIcs);
        Assert.Contains("BEGIN:VTIMEZONE", h.Sender.LastIcs!);
        Assert.Contains("DTSTART;TZID=", h.Sender.LastIcs!);
        Assert.Contains("T080000", h.Sender.LastIcs!);
        Assert.Contains("T160000", h.Sender.LastIcs!);
        Assert.DoesNotContain("T090000", h.Sender.LastIcs!);
    }

    [Fact(Skip = "733.1 - the per-task party/master-class cadences are RETIRED (operator 2026-07-31: the Get Started wizard is chased once every 14 days by getstarted-digest). Kept, not deleted, so re-enabling restores this coverage.")]
    public async Task TwoDay_10_reminders_fire_until_answered_then_stop_no_date_gate()
    {
        await using var h = NewHarness();
        var (_, session) = await SeedEditionAsync(h, capacity: 5);
        var aid = await SyncOneAsync(h, "T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day");
        var pid = (await h.Provisioning.ProvisionAsync(h.EventId)).Single();
        await h.McTaskSeeder.SeedAsync(h.EventId);
        await h.PartyTaskSeeder.SeedAsync(h.EventId);

        // §232: the day of welcome is QUIET — the welcome itself is the day-0 nudge.
        Assert.Empty(await h.PartyReminder.BuildDueAsync(h.EventId));
        Assert.Empty(await h.McReminder.BuildDueAsync(h.EventId));

        // +14 days → the FIRST reminder for both. §707.11: the occasion is the DAY (was `:wk1`),
        // and "due" is decided by lastSent + interval rather than a calendar window.
        h.Clock.Set(Base.AddDays(14));
        var stamp = $":{Base.AddDays(14):yyyyMMdd}";
        Assert.EndsWith(stamp, (await h.PartyReminder.BuildDueAsync(h.EventId)).Single().OccasionKey);
        Assert.EndsWith(stamp, (await h.McReminder.BuildDueAsync(h.EventId)).Single().OccasionKey);

        // Answer both → both cadences STOP.
        await h.Party.SubmitAsync("Ada Tester", "ada@x.dk", attending: true, ipHash: null, participantId: pid);
        await h.Mc.SignUpAsync(h.EventId, aid, session);
        await h.Reconciler.ReconcileAsync(h.EventId, pid, default);

        Assert.Empty(await h.PartyReminder.BuildDueAsync(h.EventId));
        Assert.Empty(await h.McReminder.BuildDueAsync(h.EventId));
    }

    [Fact]
    public async Task TwoDay_11_reassignment_transfers_held_seat_and_resets_party()
    {
        await using var h = NewHarness();
        var (_, session) = await SeedEditionAsync(h, capacity: 1);   // cap 1 proves no double-count
        var aid = await SyncOneAsync(h, "T1", "Old", "Holder", "old@x.dk", TicketStatus.TwoDay, "2-day");
        await h.Mc.SignUpAsync(h.EventId, aid, session);
        var sigId = (await h.Mc.SignupIdAsync(h.EventId, aid, session))!.Value;
        h.Db.PartyRsvps.Add(new PartyRsvp { EventId = h.EventId, Name = "Old", Email = "old@x.dk", Attending = true });
        await h.Db.SaveChangesAsync();

        var r = await h.Sync.SyncAsync(h.EventId, new[]
        {
            new TR("T1", "New", "Person", "new@x.dk", TicketStatus.TwoDay, "2-day"),
        });

        Assert.Equal(1, r.Reassigned);
        var att = h.Db.Attendees.Find(aid)!;
        Assert.Equal("new@x.dk", att.Email);                     // same row, new identity
        Assert.Equal(MirrorState.Active, att.MirrorState);
        Assert.NotNull(att.MasterClassInviteSentAt);             // "validate inherited MC" flag

        // Seat KEPT + transferred + still HELD (never released) — exactly one Confirmed signup.
        var sig = Assert.Single(h.Db.MasterClassSignups);
        Assert.Equal(sigId, sig.Id);
        Assert.Equal(aid, sig.AttendeeId);
        Assert.Equal(MasterClassSignupStatus.Confirmed, sig.Status);
        Assert.Equal("Intune", Assert.Single(r.Reassignments).InheritedMcTitle);

        // OLD holder's party declined (kept, not deleted); NEW holder UNANSWERED.
        Assert.False(PartyRow(h, "old@x.dk")!.Attending);
        Assert.Null(PartyRow(h, "new@x.dk"));

        // No double-count: cap 1 is still full, a fresh attendee can only WAITLIST.
        // (Seed directly — a single-ticket re-sync would soft-cancel the just-reassigned T1.)
        var other = await SeedAttendeeRowAsync(h, "T2", "Cy", "Three", "other@x.dk", TicketStatus.TwoDay);
        Assert.Equal(MasterClassSignupStatus.Waitlisted, (await h.Mc.SignUpAsync(h.EventId, other, session)).Signup!.Status);
    }

    [Fact]
    public async Task TwoDay_12_cancellation_releases_seat_frees_capacity_and_clears_party()
    {
        await using var h = NewHarness();
        var (_, session) = await SeedEditionAsync(h, capacity: 1);
        var holder = await SyncOneAsync(h, "T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day");
        await h.Mc.SignUpAsync(h.EventId, holder, session);
        h.Db.PartyRsvps.Add(new PartyRsvp { EventId = h.EventId, Name = "Ada", Email = "ada@x.dk", Attending = true });
        await h.Db.SaveChangesAsync();

        // §326as: Zoho flags T1 not_attending and keeps it in the feed → soft-cancel.
        var r = await h.Sync.SyncAsync(h.EventId,
            new[] { new TR("T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day", CancelledUpstream: true) },
            Array.Empty<AttendeeTicketSyncService.OrderRow>());

        Assert.Equal(1, r.Cancelled);
        var a = h.Db.Attendees.Find(holder)!;
        Assert.Equal(MirrorState.Cancelled, a.MirrorState);
        Assert.NotNull(a.CancelledAt);
        Assert.Empty(h.Db.MasterClassSignups.Where(s => s.AttendeeId == holder));   // seat RELEASED
        Assert.False(PartyRow(h, "ada@x.dk")!.Attending);                           // party cleared

        // Freed capacity: a fresh attendee can now CONFIRM the released seat.
        var next = await SyncOneAsync(h, "T9", "Bo", "New", "bo@x.dk", TicketStatus.TwoDay, "2-day");
        Assert.Equal(MasterClassSignupStatus.Confirmed, (await h.Mc.SignUpAsync(h.EventId, next, session)).Signup!.Status);
    }

    [Fact]
    public async Task TwoDay_13_resync_seed_reconcile_are_idempotent()
    {
        await using var h = NewHarness();
        var (_, session) = await SeedEditionAsync(h, capacity: 5);
        var aid = await SyncOneAsync(h, "T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day");
        var pid = (await h.Provisioning.ProvisionAsync(h.EventId)).Single();
        await h.McTaskSeeder.SeedAsync(h.EventId);
        await h.PartyTaskSeeder.SeedAsync(h.EventId);
        await h.Mc.SignUpAsync(h.EventId, aid, session);
        await h.Party.SubmitAsync("Ada", "ada@x.dk", attending: true, ipHash: null, participantId: pid);
        await h.Reconciler.ReconcileAsync(h.EventId, pid, default);

        // Re-run everything a second time.
        var r2 = await h.Sync.SyncAsync(h.EventId, new[] { new TR("T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day") });
        Assert.Empty(await h.Provisioning.ProvisionAsync(h.EventId));
        await h.McTaskSeeder.SeedAsync(h.EventId);
        await h.PartyTaskSeeder.SeedAsync(h.EventId);
        await h.Reconciler.ReconcileAsync(h.EventId, pid, default);

        Assert.Equal(0, r2.Created);
        Assert.Equal(1, r2.Updated);
        Assert.Single(h.Db.Participants);
        Assert.Single(h.Db.MasterClassSignups);
        Assert.Single(h.Db.PartyRsvps);
        Assert.Equal(2, await h.Db.Tasks.CountAsync());           // exactly the MC + party task
        Assert.Equal(TaskState.Done, (await TaskByKey(h, PartyTaskSeeder.SourceKeyFor(pid)))!.State);
        Assert.Equal(TaskState.Done, (await TaskByKey(h, AttendeeMasterClassTaskSeeder.SourceKeyFor(pid)))!.State);
    }

    [Fact]
    public async Task TwoDay_14_cancellation_locks_out_the_login_participant()
    {
        // §216: cancelling the only ticket DEACTIVATES the attendee's login Participant
        // (IsActive=false → cannot sign in), on top of the §209 seat/party resets.
        await using var h = NewHarness();
        var (_, session) = await SeedEditionAsync(h, capacity: 5);
        var holder = await SyncOneAsync(h, "T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day");
        var pid = (await h.Provisioning.ProvisionAsync(h.EventId)).Single();
        await h.Mc.SignUpAsync(h.EventId, holder, session);
        h.Db.PartyRsvps.Add(new PartyRsvp { EventId = h.EventId, Name = "Ada", Email = "ada@x.dk", Attending = true });
        await h.Db.SaveChangesAsync();

        // Provisioned login starts ACTIVE (can sign in).
        Assert.True((await h.Db.Participants.FindAsync(pid))!.IsActive);

        // §326as: Zoho flags T1 not_attending (the row stays in the feed) → soft-cancel.
        var cancelledPull = new[]
        {
            new TR("T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day", CancelledUpstream: true),
        };
        var r = await h.Sync.SyncAsync(h.EventId, cancelledPull, Array.Empty<AttendeeTicketSyncService.OrderRow>());
        Assert.Equal(1, r.Cancelled);

        // §216: the login Participant is now LOCKED OUT (deactivated) — same row, no delete.
        var p = await h.Db.Participants.FindAsync(pid);
        Assert.NotNull(p);
        Assert.False(p!.IsActive);
        Assert.Equal(ParticipantLifecycleState.Active, p.LifecycleState);   // lifecycle untouched
        Assert.Single(h.Db.Participants);                                    // no duplicate identity

        // §209 still holds: MC seat released + party cleared + mirror Cancelled.
        Assert.Equal(MirrorState.Cancelled, h.Db.Attendees.Find(holder)!.MirrorState);
        Assert.Empty(h.Db.MasterClassSignups.Where(s => s.AttendeeId == holder));
        Assert.False(PartyRow(h, "ada@x.dk")!.Attending);

        // Idempotent: the same not_attending row arriving again cancels nothing more and
        // leaves the login locked (it must NOT look like a re-purchase).
        var r2 = await h.Sync.SyncAsync(h.EventId, cancelledPull, Array.Empty<AttendeeTicketSyncService.OrderRow>());
        Assert.Equal(0, r2.Cancelled);
        Assert.False((await h.Db.Participants.FindAsync(pid))!.IsActive);
    }

    [Fact]
    public async Task TwoDay_15_repurchase_reactivates_the_same_participant_and_restores_login()
    {
        // §216: a previously-cancelled attendee whose ticket becomes active again is
        // reconciled onto the EXISTING Attendee + Participant row and login is RESTORED.
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 5);
        var holder = await SyncOneAsync(h, "T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day");
        var pid = (await h.Provisioning.ProvisionAsync(h.EventId)).Single();

        // Cancel (§326as: not_attending) → locked out.
        await h.Sync.SyncAsync(h.EventId,
            new[] { new TR("T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day", CancelledUpstream: true) },
            Array.Empty<AttendeeTicketSyncService.OrderRow>());
        Assert.False((await h.Db.Participants.FindAsync(pid))!.IsActive);

        // RE-PURCHASE: the SAME stable ticket id reappears active.
        var r = await h.Sync.SyncAsync(h.EventId, new[]
        {
            new TR("T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day"),
        });
        Assert.Equal(1, r.Reactivated);

        // Reconciled onto the EXISTING rows (no new identities) + login RESTORED.
        Assert.Equal(MirrorState.Active, h.Db.Attendees.Find(holder)!.MirrorState);
        Assert.Single(h.Db.Attendees);
        var p = await h.Db.Participants.FindAsync(pid);
        Assert.True(p!.IsActive);
        Assert.Single(h.Db.Participants);                                    // SAME participant

        // Idempotent: re-running the active pull keeps login on and adds no rows.
        await h.Sync.SyncAsync(h.EventId, new[]
        {
            new TR("T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day"),
        });
        Assert.True((await h.Db.Participants.FindAsync(pid))!.IsActive);
        Assert.Single(h.Db.Participants);
    }

    [Fact]
    public async Task TwoDay_16_cancelling_one_of_two_tickets_keeps_login_active()
    {
        // §216 nuance: lockout only when NO active ticket remains. An email holding a
        // second active ticket stays signed-in-capable when one ticket is cancelled.
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 5);
        // Two active tickets for the SAME email (seed directly to avoid the single-pull
        // soft-cancel side effect), plus the login Participant.
        await SeedAttendeeRowAsync(h, "T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay);
        await SeedAttendeeRowAsync(h, "T2", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay);
        var pid = (await h.Provisioning.ProvisionAsync(h.EventId)).Single();   // one participant for the email

        // §326as: T1 comes back flagged not_attending; T2 is still attending.
        var r = await h.Sync.SyncAsync(h.EventId, new[]
        {
            new TR("T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day", CancelledUpstream: true),
            new TR("T2", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day"),
        });
        Assert.Equal(1, r.Cancelled);

        // The email still holds an ACTIVE ticket → login stays ACTIVE.
        Assert.True((await h.Db.Participants.FindAsync(pid))!.IsActive);

        // Now the second ticket is cancelled too → locked out.
        var r2 = await h.Sync.SyncAsync(h.EventId, new[]
        {
            new TR("T1", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day", CancelledUpstream: true),
            new TR("T2", "Ada", "Tester", "ada@x.dk", TicketStatus.TwoDay, "2-day", CancelledUpstream: true),
        }, Array.Empty<AttendeeTicketSyncService.OrderRow>());
        Assert.Equal(1, r2.Cancelled);
        Assert.False((await h.Db.Participants.FindAsync(pid))!.IsActive);
    }

    // =====================================================================
    //  1-DAY ticket journey
    // =====================================================================

    [Fact]
    public async Task OneDay_01_sync_creates_active_other_ticket_attendee()
    {
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 50);
        await SyncOneAsync(h, "O1", "Eve", "Day", "eve@x.dk", TicketStatus.Other, "1-day");

        var a = h.Db.Attendees.Single();
        Assert.Equal(TicketStatus.Other, a.TicketStatus);
        Assert.Equal(MirrorState.Active, a.MirrorState);
    }

    [Fact]
    public async Task OneDay_02_provision_succeeds_but_no_welcome_mail_is_sent()
    {
        // §299 OPEN-26 (operator 2026-07-23): 1-day attendees get NO welcome mail — the §208
        // send path is retired. Provisioning still creates the participant (so a ring-1 tester
        // can validate the 1-day experience), but the welcome service is inert: no mail, no
        // WelcomeWithLoginSentAt stamp.
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 50);
        await SyncOneAsync(h, "O1", "Eve", "Day", "eve@x.dk", TicketStatus.Other, "1-day");

        var created = await h.Provisioning.ProvisionOneDayAsync(h.EventId);
        var pid = Assert.Single(created);
        var p = await h.Db.Participants.FindAsync(pid);
        Assert.Equal(ParticipantRole.Attendee, p!.Role);
        Assert.True(p.IsActive);

        Assert.False(await h.OneDayWelcome.SendForProvisioningAsync(pid));
        Assert.Empty(h.Sender.Messages);
        Assert.Null(p.WelcomeWithLoginSentAt);
    }

    [Fact]
    public async Task OneDay_03_seeds_no_tasks_at_all()
    {
        // §299 7.1 (operator 2026-07-23): a 1-day holder gets NO tasks — not the master-class
        // task (never had it) and no longer the party task either.
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 50);
        await SyncOneAsync(h, "O1", "Eve", "Day", "eve@x.dk", TicketStatus.Other, "1-day");
        var pid = (await h.Provisioning.ProvisionOneDayAsync(h.EventId)).Single();

        Assert.Equal(0, await h.McTaskSeeder.SeedAsync(h.EventId));   // NOT a 2-day holder
        Assert.Equal(0, await h.PartyTaskSeeder.SeedAsync(h.EventId)); // 1-day: no party task

        Assert.Null(await TaskByKey(h, PartyTaskSeeder.SourceKeyFor(pid)));
        Assert.Null(await TaskByKey(h, AttendeeMasterClassTaskSeeder.SourceKeyFor(pid)));
        Assert.Equal(0, await h.Db.Tasks.CountAsync());
    }

    [Fact]
    public async Task OneDay_05_no_party_task_means_no_reminders_ever()
    {
        // §299 7.1: with no party task seeded for a 1-day holder there is nothing for the
        // week-cadence party reminder (or the MC reminder) to ever pick up.
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 50);
        await SyncOneAsync(h, "O1", "Eve", "Day", "eve@x.dk", TicketStatus.Other, "1-day");
        var pid = (await h.Provisioning.ProvisionOneDayAsync(h.EventId)).Single();
        Assert.Equal(0, await h.PartyTaskSeeder.SeedAsync(h.EventId));

        Assert.Empty(await h.PartyReminder.BuildDueAsync(h.EventId));
        Assert.Empty(await h.McReminder.BuildDueAsync(h.EventId));
        h.Clock.Set(Base.AddDays(14));
        Assert.Empty(await h.PartyReminder.BuildDueAsync(h.EventId));
        Assert.Empty(await h.McReminder.BuildDueAsync(h.EventId));
        h.Clock.Set(Base.AddDays(28));
        Assert.Empty(await h.PartyReminder.BuildDueAsync(h.EventId));
        _ = pid; // provisioning still creates the participant; only tasks are withheld
    }

    [Fact]
    public async Task OneDay_06_reassignment_resets_party_old_inactive_new_unanswered()
    {
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 50);
        var aid = await SyncOneAsync(h, "O1", "Old", "One", "old1@x.dk", TicketStatus.Other, "1-day");
        h.Db.PartyRsvps.Add(new PartyRsvp { EventId = h.EventId, Name = "Old", Email = "old1@x.dk", Attending = true });
        await h.Db.SaveChangesAsync();

        var r = await h.Sync.SyncAsync(h.EventId, new[] { new TR("O1", "New", "One", "new1@x.dk", TicketStatus.Other, "1-day") });

        Assert.Equal(1, r.Reassigned);
        var att = h.Db.Attendees.Find(aid)!;
        Assert.Equal("new1@x.dk", att.Email);
        Assert.Equal(MirrorState.Active, att.MirrorState);
        Assert.Empty(h.Db.MasterClassSignups);                   // no MC for a 1-day ticket
        Assert.False(PartyRow(h, "old1@x.dk")!.Attending);       // old declined
        Assert.Null(PartyRow(h, "new1@x.dk"));                   // new unanswered
    }

    [Fact]
    public async Task OneDay_07_cancellation_resets_party_and_marks_cancelled()
    {
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 50);
        var holder = await SyncOneAsync(h, "O1", "Eve", "Day", "eve@x.dk", TicketStatus.Other, "1-day");
        h.Db.PartyRsvps.Add(new PartyRsvp { EventId = h.EventId, Name = "Eve", Email = "eve@x.dk", Attending = true });
        await h.Db.SaveChangesAsync();

        var r = await h.Sync.SyncAsync(h.EventId,
            new[] { new TR("O1", "Eve", "Day", "eve@x.dk", TicketStatus.Other, "1-day", CancelledUpstream: true) },
            Array.Empty<AttendeeTicketSyncService.OrderRow>());

        Assert.Equal(1, r.Cancelled);
        Assert.Equal(MirrorState.Cancelled, h.Db.Attendees.Find(holder)!.MirrorState);
        Assert.Empty(h.Db.MasterClassSignups);
        Assert.False(PartyRow(h, "eve@x.dk")!.Attending);
    }

    [Fact]
    public async Task OneDay_08_resync_and_reprovision_are_idempotent()
    {
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 50);
        await SyncOneAsync(h, "O1", "Eve", "Day", "eve@x.dk", TicketStatus.Other, "1-day");
        var pid = (await h.Provisioning.ProvisionOneDayAsync(h.EventId)).Single();
        await h.OneDayWelcome.SendForProvisioningAsync(pid);
        await h.PartyTaskSeeder.SeedAsync(h.EventId);

        Assert.Empty(await h.Provisioning.ProvisionOneDayAsync(h.EventId));   // no new participant
        Assert.False(await h.OneDayWelcome.SendForProvisioningAsync(pid));    // welcome retired (OPEN-26)
        Assert.Equal(0, await h.PartyTaskSeeder.SeedAsync(h.EventId));        // 1-day: never a party task
        Assert.Single(h.Db.Participants);
        Assert.Empty(h.Sender.Messages);   // OPEN-26: no 1-day welcome mail at all
        Assert.Equal(0, await h.Db.Tasks.CountAsync());  // §299 7.1: no tasks for a 1-day holder
    }

    /// <summary>
    /// §498/§499 — the operator's screenshot: an INACTIVE attendee still listed under "Awaiting a
    /// Master Class choice", with the page claiming <i>"the hub is already reminding them"</i>.
    /// Someone whose ticket was cancelled or reassigned has left; chasing them is mail to a person
    /// who is gone.
    ///
    /// <para>Deliberately asserted ALONGSIDE an active attendee in the identical state, so this
    /// proves the reminder stopped for the drop-out specifically — not that the cadence was broken
    /// for everybody, which a one-person test would have happily passed.</para>
    /// </summary>
    [Fact(Skip = "733.1 - the per-task party/master-class cadences are RETIRED (operator 2026-07-31: the Get Started wizard is chased once every 14 days by getstarted-digest). Kept, not deleted, so re-enabling restores this coverage.")]
    public async Task A_dropped_out_attendee_is_not_reminded_but_an_active_one_still_is()
    {
        await using var h = NewHarness();
        await SeedEditionAsync(h, capacity: 10);

        // Seeded directly, NOT via two SyncOneAsync calls: a single-ticket pull soft-cancels every
        // attendee absent from it (§128), so calling it twice would quietly cancel Ada and the test
        // would then "pass" for entirely the wrong reason.
        await SeedAttendeeRowAsync(h, "T1", "Ada", "Stays", "ada@x.dk", TicketStatus.TwoDay);
        await SeedAttendeeRowAsync(h, "T2", "Bo", "Leaves", "bo@x.dk", TicketStatus.TwoDay);
        await h.Provisioning.ProvisionAsync(h.EventId);
        await h.McTaskSeeder.SeedAsync(h.EventId);

        // Both are due their first nudge two weeks in.
        h.Clock.Set(Base.AddDays(14));
        Assert.Equal(2, (await h.McReminder.BuildDueAsync(h.EventId)).Count);

        // Bo's ticket is cancelled / reassigned. AttendeeTicketSyncService closes the login gate
        // and deliberately LEAVES LifecycleState Active — the exact fingerprint §499 keys on to
        // tell "was in, now out" from "not yet in".
        var bo = await h.Db.Participants.SingleAsync(p => p.Email == "bo@x.dk");
        bo.IsActive = false;
        await h.Db.SaveChangesAsync();

        var due = await h.McReminder.BuildDueAsync(h.EventId);
        Assert.Single(due);
        Assert.DoesNotContain(due, m => m.RecipientEmail.Contains("bo@", StringComparison.OrdinalIgnoreCase));
    }
}
