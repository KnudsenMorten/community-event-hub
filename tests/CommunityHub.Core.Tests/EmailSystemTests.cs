using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for the CEH email system build-out (REQUIREMENTS §10a):
/// auto-send on activation + idempotency (10a-1), manual re-send (10a-2),
/// the EmailLog audit + name/email filter (10a-3), persona-aware scheduled
/// reminders (10a-4), secondary-email CC (10a-5), and the step-reset consume
/// (10a-6). EF Core InMemory provider + a fixed clock + a recording sender.
/// </summary>
public sealed class EmailSystemTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly FixedClock Clock = new(Now);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"emailsys-{Guid.NewGuid():N}")
            .Options);

    private static EmailTemplateProvider RealTemplates() =>
        new(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            HubUrl = "https://hub.example.test",
        }));

    private static async Task<(int eventId, Participant p)> SeedActiveAsync(
        CommunityHubDbContext db,
        ParticipantRole role = ParticipantRole.Volunteer,
        string email = "p@example.test",
        string? secondary = null,
        ParticipantLifecycleState state = ParticipantLifecycleState.Active)
    {
        var evt = new Event
        {
            Code = "T27", CommunityName = "Test Community",
            DisplayName = "Test Community 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = evt.Id, Email = email, FullName = "Test Person",
            Role = role, IsActive = state == ParticipantLifecycleState.Active,
            LifecycleState = state, SecondaryEmail = secondary,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return (evt.Id, p);
    }

    private static ParticipantEmailService NewParticipantEmail(
        CommunityHubDbContext db, CapturingEmailSender sender) =>
        new(db, RealTemplates(), sender, new EmailContextAccessor());

    // ----- 10a-1 auto-send on activation + idempotency ----------------------

    [Fact]
    public async Task Activation_auto_sends_persona_onboarding_set_with_no_approval()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedActiveAsync(
            db, ParticipantRole.Volunteer, state: ParticipantLifecycleState.Inactive);
        var sender = new CapturingEmailSender();
        var onboarding = new OnboardingEmailService(db, NewParticipantEmail(db, sender), Clock);
        var calendarInvite = new CalendarInviteEmailService(
            db, sender, new EmailContextAccessor(), Clock);
        var activation = new ParticipantActivationService(
            new PreselectionQueueService(db), onboarding, calendarInvite);

        var result = await activation.ActivateAndOnboardAsync(eventId, new[] { p.Id });

        Assert.Equal(1, result.Queue.Changed);
        // Volunteer set = getting-started (1 email; the redundant "your next steps"
        // drip email was removed per REQUIREMENTS §84/§91).
        Assert.Equal(1, result.OnboardingEmailsSent);
        // One .ics calendar invite is sent on activation (sync on by default).
        Assert.Equal(1, result.CalendarInvitesSent);
        // 1 onboarding email (Messages) + 1 ics invite = 2 total Sent rows.
        Assert.Equal(1, sender.Messages.Count);
        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal(ParticipantLifecycleState.Active,
            (await db.Participants.FindAsync(p.Id))!.LifecycleState);
    }

    [Fact]
    public async Task Onboarding_set_is_idempotent_across_a_second_activation_pass()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedActiveAsync(
            db, ParticipantRole.Speaker, state: ParticipantLifecycleState.Inactive);
        var sender = new CapturingEmailSender();
        var onboarding = new OnboardingEmailService(db, NewParticipantEmail(db, sender), Clock);
        var calendarInvite = new CalendarInviteEmailService(
            db, sender, new EmailContextAccessor(), Clock);
        var activation = new ParticipantActivationService(
            new PreselectionQueueService(db), onboarding, calendarInvite);

        await activation.ActivateAndOnboardAsync(eventId, new[] { p.Id });
        var firstCount = sender.Sent.Count;

        // A second explicit onboarding pass must send nothing more.
        var again = await onboarding.SendOnboardingSetAsync(p.Id);

        Assert.Equal(0, again);
        Assert.Equal(firstCount, sender.Sent.Count);   // no re-send
    }

    [Fact]
    public async Task Organizer_persona_set_is_single_email()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedActiveAsync(db, ParticipantRole.Organizer);
        var sender = new CapturingEmailSender();
        var onboarding = new OnboardingEmailService(db, NewParticipantEmail(db, sender), Clock);

        var sent = await onboarding.SendOnboardingSetAsync(p.Id);

        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task Attendee_has_no_onboarding_set_so_activation_sends_nothing()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedActiveAsync(db, ParticipantRole.Attendee);
        var sender = new CapturingEmailSender();
        var onboarding = new OnboardingEmailService(db, NewParticipantEmail(db, sender), Clock);

        var sent = await onboarding.SendOnboardingSetAsync(p.Id);

        Assert.Equal(0, sent);
        Assert.Empty(sender.Sent);
    }

    // ----- 10a-2 manual individual re-send ----------------------------------

    [Fact]
    public async Task Manual_resend_sends_to_one_person_and_is_not_idempotency_gated()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedActiveAsync(db, ParticipantRole.Volunteer);
        var sender = new CapturingEmailSender();
        var svc = NewParticipantEmail(db, sender);

        var to1 = await svc.SendTemplateToParticipantAsync(
            eventId, p.Id, "welcome", "manual-resend");
        var to2 = await svc.SendTemplateToParticipantAsync(
            eventId, p.Id, "welcome", "manual-resend");

        Assert.Equal(p.Email, to1);
        Assert.Equal(p.Email, to2);
        Assert.Equal(2, sender.Sent.Count);            // re-send is allowed
    }

    // ----- 10a-3 EmailLog audit + name/email filter -------------------------

    [Fact]
    public async Task Logging_decorator_writes_an_EmailLog_row_for_every_send()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedActiveAsync(db);
        var ctx = new EmailContextAccessor();
        var logger = MakeLoggingSender(db, new CapturingEmailSender(), ctx);

        using (ctx.Set(new EmailContext("welcome", eventId, p.Id, "Test Person")))
        {
            await logger.SendAsync("to@example.test", "Hi", "<p>x</p>");
        }

        var log = await db.EmailLogs.SingleAsync();
        Assert.Equal("welcome", log.Category);
        Assert.Equal("to@example.test", log.ToEmail);
        Assert.Equal(p.Id, log.ParticipantId);
        Assert.True(log.Success);
    }

    [Fact]
    public async Task Logging_decorator_records_a_failed_send_then_rethrows()
    {
        using var db = NewDb();
        var (eventId, _) = await SeedActiveAsync(db);
        var ctx = new EmailContextAccessor();
        var logger = MakeLoggingSender(db, new ThrowingEmailSender(), ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            logger.SendAsync("to@example.test", "Hi", "<p>x</p>"));

        var log = await db.EmailLogs.SingleAsync();
        Assert.False(log.Success);
        Assert.Contains("boom", log.Error);
    }

    [Fact]
    public async Task EmailLog_filters_by_name_or_email_substring()
    {
        using var db = NewDb();
        db.EmailLogs.AddRange(
            new EmailLog { EventId = 1, ToEmail = "alice@corp.test", RecipientName = "Alice Adams", Category = "welcome", Subject = "a", Success = true, SentAt = Now },
            new EmailLog { EventId = 1, ToEmail = "bob@other.test", RecipientName = "Bob Brown", Category = "welcome", Subject = "b", Success = true, SentAt = Now },
            new EmailLog { EventId = 2, ToEmail = "alice@corp.test", RecipientName = "Alice Adams", Category = "welcome", Subject = "c", Success = true, SentAt = Now });
        await db.SaveChangesAsync();

        // by email substring, edition-scoped
        var byEmail = await db.EmailLogs
            .Where(e => e.EventId == 1 && e.ToEmail.Contains("alice"))
            .ToListAsync();
        Assert.Single(byEmail);

        // by name substring
        var byName = await db.EmailLogs
            .Where(e => e.EventId == 1 && e.RecipientName!.Contains("Brown"))
            .ToListAsync();
        Assert.Single(byName);
    }

    // ----- 10a-4 persona-aware scheduled reminders --------------------------

    [Fact]
    public async Task Scheduled_task_reminder_carries_persona_and_logs_per_persona()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedActiveAsync(db, ParticipantRole.Volunteer);
        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId, AssignedParticipantId = p.Id,
            // §81: reminders fire only on the due day, so seed a task due today.
            Title = "Do the thing", DueDate = DateOnly.FromDateTime(Now.UtcDateTime),
            State = TaskState.Open,
        });
        await db.SaveChangesAsync();

        var builder = new TaskReminderBuilder(
            db, RealTemplates(), Clock, new SponsorRecipientResolver(db));
        var due = await builder.BuildDueAsync(eventId);

        var msg = Assert.Single(due);
        Assert.Equal("Volunteer", msg.Persona);
        Assert.Equal(p.Id, msg.ParticipantId);
    }

    // ----- 10a-5 secondary-email CC -----------------------------------------

    [Fact]
    public async Task Secondary_email_is_added_as_cc_on_a_participant_send()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedActiveAsync(
            db, ParticipantRole.Volunteer, secondary: "backup@example.test");
        var sender = new CapturingEmailSender();
        var svc = NewParticipantEmail(db, sender);

        await svc.SendTemplateToParticipantAsync(eventId, p.Id, "welcome", "manual-resend");

        var cc = Assert.Single(sender.CcSent).Cc;
        Assert.Contains("backup@example.test", cc);
    }

    [Fact]
    public async Task No_secondary_email_means_no_cc()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedActiveAsync(db, ParticipantRole.Volunteer, secondary: null);
        var sender = new CapturingEmailSender();
        var svc = NewParticipantEmail(db, sender);

        await svc.SendTemplateToParticipantAsync(eventId, p.Id, "welcome", "manual-resend");

        Assert.Empty(Assert.Single(sender.CcSent).Cc);
    }

    [Fact]
    public void Routing_keeps_speaker_override_as_To_and_secondary_as_cc()
    {
        var (to, cc) = ParticipantEmailService.ResolveRouting(
            identityEmail: "id@sessionize.test",
            speakerOverride: "preferred@speaker.test",
            secondaryEmail: "backup@speaker.test");

        Assert.Equal("preferred@speaker.test", to);     // override wins the To
        Assert.Equal(new[] { "backup@speaker.test" }, cc); // secondary is CC
    }

    // ----- 10a-6 step-reset consume -----------------------------------------

    [Fact]
    public async Task Step_reset_consume_emails_the_person_and_resolves_the_item()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedActiveAsync(db, ParticipantRole.Speaker);
        var actions = new OrganizerActionItemService(db, Clock);
        var onboarding = new OnboardingService(db, actions, Clock);
        await onboarding.MarkStepCompleteAsync(eventId, p.Id, OnboardingStep.Hotel);
        await onboarding.ResetStepAsync(eventId, p.Id, OnboardingStep.Hotel);  // raises action

        var sender = new CapturingEmailSender();
        var svc = new OnboardingStepResetEmailService(
            db, NewParticipantEmail(db, sender), actions);

        var consumed = await svc.SendPendingAsync(eventId);

        Assert.Equal(1, consumed);
        Assert.Single(sender.Sent);
        // The action item is now resolved (consumed exactly once).
        var open = await db.OrganizerActionItems.CountAsync(a =>
            a.Type == OrganizerActionItemService.TypeOnboardingStepReset
            && a.ResolvedAt == null);
        Assert.Equal(0, open);

        // A re-run finds nothing open -> idempotent.
        var again = await svc.SendPendingAsync(eventId);
        Assert.Equal(0, again);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public void Step_label_is_extracted_from_the_action_summary()
    {
        var label = OnboardingStepResetEmailService.ExtractStepLabel(
            "Onboarding step re-opened: Hotel — please remind Test Person to complete it.");
        Assert.Equal("Hotel", label);
    }

    // ----- helpers ----------------------------------------------------------

    private static LoggingEmailSender MakeLoggingSender(
        CommunityHubDbContext db, IEmailSender inner, IEmailContextAccessor ctx)
    {
        // A scope factory that always hands back the SAME in-memory DbContext, so
        // the test can read the rows the decorator wrote.
        var scopes = new SingleContextScopeFactory(db);
        // The allowlist is now fail-closed (empty => send to nobody), so a realistic
        // test must configure it. All test addresses use @example.test, so allow that
        // domain — a send to a test recipient is then "allowed" (Success=true), which
        // is what these decorator tests exercise.
        return new LoggingEmailSender(
            inner, scopes, ctx,
            Options.Create(new EmailOptions()), Clock, log: null);
    }

    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }

    /// <summary>An <see cref="IServiceScopeFactory"/> whose every scope resolves the
    /// one DbContext the test owns.</summary>
    private sealed class SingleContextScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private readonly CommunityHubDbContext _db;
        public SingleContextScopeFactory(CommunityHubDbContext db) => _db = db;
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) =>
            serviceType == typeof(CommunityHubDbContext) ? _db : null;
        public void Dispose() { /* the test owns the context lifetime */ }
    }
}
