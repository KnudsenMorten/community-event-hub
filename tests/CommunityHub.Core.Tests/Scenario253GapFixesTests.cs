using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;
using TR = CommunityHub.Core.Reminders.AttendeeTicketSyncService.TicketRow;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §253 lifecycle-matrix regression tests for the G9–G16 gap family:
/// <list type="bullet">
///   <item>G9 — every reminder builder + the shared participant-email transport skip
///   DEACTIVATED participants (due-day task reminders, MC biweekly nag, step-reset).</item>
///   <item>G10 — the travel-reimbursement deadline is entitlement-gated: a
///   sponsor-funded speaker never gets the travel task, and an already-seeded one is
///   pruned (so its due-day reminder can never fire).</item>
///   <item>G11 — an ex-speaker's speakerdl: tasks are swept by the nightly seeder.</item>
///   <item>G13 — the attendee-list export excludes soft-cancelled mirror rows.</item>
///   <item>G14 — the public agenda excludes deactivated speakers.</item>
///   <item>G15 — a participant hard-delete leaves no ghost PartyRsvp / volunteer
///   availability rows (the FK holes that used to ghost or 500).</item>
///   <item>G16 — a re-purchased attendee is RE-ENGAGED (stale party "No" cleared,
///   §241 invite re-armed, pendingmc chaser ledger cleared, tasks reopened).</item>
///   <item>§252 orphan (a) — the Master-Class confirmation email carries the
///   open-not-download Google/Outlook calendar links.</item>
/// </list>
/// EF InMemory + the real shipped templates + fixed clocks. FAKE names only.
/// </summary>
public sealed class Scenario253GapFixesTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 8, 0, 0, TimeSpan.Zero);
    private static readonly FixedClock Clock = new(Now);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"g253-{Guid.NewGuid():N}")
            .Options);

    private static EmailTemplateProvider RealTemplates() =>
        new(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            PrivateTemplateDirectory = Path.Combine(Path.GetTempPath(), "ceh-no-private-g253"),
            HubUrl = "https://hub.example.test",
        }));

    private sealed class NoOpContext : IEmailContextAccessor
    {
        public EmailContext? Current => null;
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) => new D();
    }

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "T27", CommunityName = "Test Community", DisplayName = "Test Community 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        return evt.Id;
    }

    private static async Task<Participant> SeedPersonAsync(
        CommunityHubDbContext db, int eventId, string email, ParticipantRole role,
        bool isActive = true, string name = "Test Person")
    {
        var p = new Participant
        {
            EventId = eventId, Email = email, FullName = name, Role = role,
            IsActive = isActive, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    // =====================================================================
    //  G9 — reminder builders skip DEACTIVATED participants
    // =====================================================================

    [Fact]
    public async Task G9_TaskReminderBuilder_skips_deactivated_assignee_but_reminds_active()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var active = await SeedPersonAsync(db, eventId, "active@x.dk", ParticipantRole.Speaker);
        var gone = await SeedPersonAsync(db, eventId, "gone@x.dk", ParticipantRole.Speaker, isActive: false);
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        db.Tasks.AddRange(
            new ParticipantTask { EventId = eventId, AssignedParticipantId = active.Id, Title = "Upload deck", DueDate = today, State = TaskState.Open },
            new ParticipantTask { EventId = eventId, AssignedParticipantId = gone.Id, Title = "Upload deck", DueDate = today, State = TaskState.Open });
        await db.SaveChangesAsync();

        var due = await new TaskReminderBuilder(db, RealTemplates(), Clock, new SponsorRecipientResolver(db))
            .BuildDueAsync(eventId);

        var msg = Assert.Single(due);
        Assert.Equal("active@x.dk", msg.RecipientEmail);
    }

    [Fact(Skip = "733.1 - the per-task party/master-class cadences are RETIRED (operator 2026-07-31: the Get Started wizard is chased once every 14 days by getstarted-digest). Kept, not deleted, so re-enabling restores this coverage.")]
    public async Task G9_MasterClass_biweekly_nag_stops_for_organizer_deactivated_attendee()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        // Organizer-DEACTIVATED attendee whose TICKET is still Active — the §234
        // mirror gate passes, so only the new IsActive gate can stop the nag.
        var p = await SeedPersonAsync(db, eventId, "nag@x.dk", ParticipantRole.Attendee, isActive: false);
        db.Attendees.Add(new Attendee
        {
            EventId = eventId, BackstageTicketId = "T1", Email = "nag@x.dk",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
        });
        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId, AssignedParticipantId = p.Id, Title = "Select your Master Class",
            State = TaskState.Open, SourceKey = AttendeeMasterClassTaskSeeder.SourceKeyFor(p.Id),
            CreatedAt = Now.AddDays(-30),   // well past the first 2-week window
        });
        await db.SaveChangesAsync();

        var due = await new AttendeeMasterClassReminderBuilder(db, RealTemplates(), Clock)
            .BuildDueAsync(eventId);
        Assert.Empty(due);

        // Control: the SAME state with the participant active DOES nag.
        p.IsActive = true;
        await db.SaveChangesAsync();
        due = await new AttendeeMasterClassReminderBuilder(db, RealTemplates(), Clock)
            .BuildDueAsync(eventId);
        Assert.Single(due);
    }

    [Fact]
    public async Task G9_ParticipantEmailService_transport_refuses_deactivated_recipient()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var p = await SeedPersonAsync(db, eventId, "off@x.dk", ParticipantRole.Volunteer, isActive: false);
        var sender = new CapturingEmailSender();
        var svc = new ParticipantEmailService(db, RealTemplates(), sender, new EmailContextAccessor());

        var to = await svc.SendTemplateToParticipantAsync(eventId, p.Id, "welcome", "manual-resend");

        Assert.Null(to);
        Assert.Empty(sender.Sent);

        // Control: reactivated → the same call sends.
        p.IsActive = true;
        await db.SaveChangesAsync();
        to = await svc.SendTemplateToParticipantAsync(eventId, p.Id, "welcome", "manual-resend");
        Assert.Equal("off@x.dk", to);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task G9_StepReset_reminder_skips_deactivated_participant_and_keeps_item_open()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var p = await SeedPersonAsync(db, eventId, "reset@x.dk", ParticipantRole.Volunteer, isActive: false);
        db.OrganizerActionItems.Add(new OrganizerActionItem
        {
            EventId = eventId, ParticipantId = p.Id,
            Type = OrganizerActionItemService.TypeOnboardingStepReset,
            Summary = "Onboarding step re-opened: Hotel — please remind them",
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var svc = new OnboardingStepResetEmailService(
            db,
            new ParticipantEmailService(db, RealTemplates(), sender, new EmailContextAccessor()),
            new OrganizerActionItemService(db, Clock));

        var consumed = await svc.SendPendingAsync(eventId);

        Assert.Equal(0, consumed);
        Assert.Empty(sender.Sent);
        // The item is NOT consumed: if the person is reactivated the reminder still fires.
        Assert.Single(db.OrganizerActionItems.Where(a => a.ResolvedAt == null));

        p.IsActive = true;
        await db.SaveChangesAsync();
        consumed = await svc.SendPendingAsync(eventId);
        Assert.Equal(1, consumed);
        Assert.Single(sender.Sent);
    }

    // =====================================================================
    //  G10 — sponsor-funded speaker: NO travel task / reminder (end-to-end)
    // =====================================================================

    private static SpeakerDeadlineSeeder NewSeeder(CommunityHubDbContext db, string configPath) =>
        new(db, new SpeakerDeadlineOptions { ConfigPath = configPath }, Clock);

    private static string WriteDeadlineConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), $"g253-deadlines-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
        {
          "deadlines": [
            { "title": "Submit travel reimbursement", "dueDate": "2026-07-07", "nonDenmarkOnly": true },
            { "title": "Upload final presentation", "dueDate": "2026-07-07" },
            { "title": "Help to promote your session(s)", "dueDate": "2027-01-15" }
          ]
        }
        """);
        return path;
    }

    [Fact]
    public async Task G10_sponsor_category_speaker_gets_no_travel_task_and_no_travel_reminder()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var speaker = await SeedPersonAsync(db, eventId, "sf@x.se", ParticipantRole.Speaker);
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = eventId, ParticipantId = speaker.Id,
            Category = SpeakerCategory.Sponsor, Country = "SE",   // non-DK
        });
        // Simulate the pre-fix state: the travel task was already (wrongly) seeded.
        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId, AssignedParticipantId = speaker.Id,
            Title = "Submit travel reimbursement", DueDate = DateOnly.FromDateTime(Now.UtcDateTime),
            State = TaskState.Open, SourceKey = $"speakerdl:{speaker.Id}:submit-travel-reimbursement",
        });
        await db.SaveChangesAsync();

        var config = WriteDeadlineConfig();
        try
        {
            await NewSeeder(db, config).SeedAsync(eventId);

            // §1082 — the projection is over LIVE tasks only. A pruned row is now RETIRED rather
            // than deleted (closed, labelled, kept for audit), so "the speaker does not have this
            // task" means it is not OPEN to them — which is the property §299 6.2 is about and the
            // one that stops the reminder firing. Filtering here rather than asserting absence keeps
            // the test about entitlement instead of about deletion mechanics.
            var keys = await db.Tasks
                .Where(t => t.EventId == eventId && t.AssignedParticipantId == speaker.Id)
                .Where(CommunityHub.Core.Tasks.TaskClosure.NotSystemClosed)
                .Select(t => t.SourceKey!)
                .ToListAsync();
            // §299 6.2: a SPONSOR-category speaker gets no travel task (pruned, never re-seeded).
            Assert.DoesNotContain(keys, k => k.Contains("travel"));

            // §456/§458 (operator 2026-07-27) SUPERSEDE the two rules this test used to assert:
            //   - it required NO presentation deadline at all. He decided the FINAL upload is the
            //     one deliverable an exhibitor-speaker owns ("only task relevant for a sponsor
            //     (exhibitor) speaker is the task for upload final presentation"), so final is now
            //     seeded and only the PREVIEW stays excluded;
            //   - it required the Help-Promote deadline to be PRESENT (the §314 exemption). He
            //     reversed that: "sponsor speaker category should not get the task 'Help promote'".
            //     Its menu entry is hidden too (§457), so keeping the task would have meant e-mail
            //     reminders for a page they can no longer reach.
            Assert.Contains(keys, k => k.Contains("final") && k.Contains("presentation"));
            Assert.DoesNotContain(keys, k => k.Contains("preview"));
            Assert.DoesNotContain(keys, k => k.Contains("promote"));

            // End-to-end: the reminder pipeline produces NO travel reminder either.
            var due = await new TaskReminderBuilder(db, RealTemplates(), Clock, new SponsorRecipientResolver(db))
                .BuildDueAsync(eventId);
            Assert.DoesNotContain(due, m => m.Subject.Contains("travel", StringComparison.OrdinalIgnoreCase));

            // Control: a COMMUNITY (ELDK-funded) non-DK speaker still gets the travel
            // task AND the presentation upload.
            var community = await SeedPersonAsync(db, eventId, "sup@x.se", ParticipantRole.Speaker, name: "Sup Ported");
            db.SpeakerProfiles.Add(new SpeakerProfile
            {
                EventId = eventId, ParticipantId = community.Id,
                Category = SpeakerCategory.Community, Country = "SE",
            });
            await db.SaveChangesAsync();
            await NewSeeder(db, config).SeedAsync(eventId);
            Assert.True(await db.Tasks.AnyAsync(t =>
                t.AssignedParticipantId == community.Id
                && t.SourceKey == $"speakerdl:{community.Id}:submit-travel-reimbursement"));
            Assert.True(await db.Tasks.AnyAsync(t =>
                t.AssignedParticipantId == community.Id
                && t.SourceKey == $"speakerdl:{community.Id}:upload-final-presentation"));

            // §299 6.2: a GUEST speaker keeps the presentation upload but is excluded
            // from the travel task automatically (no TravelReimbursement entitlement).
            var guest = await SeedPersonAsync(db, eventId, "guest@x.se", ParticipantRole.Speaker, name: "Gwen Guest");
            db.SpeakerProfiles.Add(new SpeakerProfile
            {
                EventId = eventId, ParticipantId = guest.Id,
                Category = SpeakerCategory.Guest, Country = "SE",
            });
            await db.SaveChangesAsync();
            await NewSeeder(db, config).SeedAsync(eventId);
            var guestKeys = await db.Tasks
                .Where(t => t.EventId == eventId && t.AssignedParticipantId == guest.Id)
                .Select(t => t.SourceKey!)
                .ToListAsync();
            Assert.Contains($"speakerdl:{guest.Id}:upload-final-presentation", guestKeys);
            Assert.DoesNotContain(guestKeys, k => k.Contains("travel"));
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public async Task Masterclass_only_deadline_follows_DERIVED_preday_not_the_retired_flag()
    {
        // §299 C5: the masterclassOnly deadline gate derives from the speaker's LINKED
        // sessions (SpeakerDayScope), never the retired SpeakingPreDay flag. The
        // main-day speaker here has the legacy flag ON but no pre-day session — they
        // must NOT get the masterclass-only deadline; the MC-linked speaker (flag OFF)
        // must.
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var mcSpeaker = await SeedPersonAsync(db, eventId, "mc@x.dk", ParticipantRole.Speaker);
        var mainOnly = await SeedPersonAsync(db, eventId, "main@x.dk", ParticipantRole.Speaker, name: "Main Only");
        db.SpeakerProfiles.AddRange(
            new SpeakerProfile
            {
                EventId = eventId, ParticipantId = mcSpeaker.Id,
                Category = SpeakerCategory.Community, SpeakingPreDay = false, // legacy flag OFF
            },
            new SpeakerProfile
            {
                EventId = eventId, ParticipantId = mainOnly.Id,
                Category = SpeakerCategory.Community, SpeakingPreDay = true,  // legacy flag ON (must be ignored)
            });
        db.Sessions.Add(new Session
        {
            EventId = eventId, SessionizeId = "mc-1", Title = "Deep dive",
            Type = SessionType.MasterClass,
        });
        await db.SaveChangesAsync();
        var mcSession = await db.Sessions.SingleAsync(s => s.SessionizeId == "mc-1");
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = mcSession.Id, ParticipantId = mcSpeaker.Id });
        await db.SaveChangesAsync();

        var config = Path.Combine(Path.GetTempPath(), $"g253-mconly-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, """
        {
          "deadlines": [
            { "title": "Prepare master class materials", "dueDate": "2027-01-05", "masterclassOnly": true }
          ]
        }
        """);
        try
        {
            await NewSeeder(db, config).SeedAsync(eventId);

            Assert.True(await db.Tasks.AnyAsync(t =>
                t.AssignedParticipantId == mcSpeaker.Id
                && t.SourceKey == $"speakerdl:{mcSpeaker.Id}:prepare-master-class-materials"));

            // 🔒 §708 — ASSERT THE GATE, NOT "NO TASKS AT ALL".
            //
            // This used to assert `mainOnly` held NO tasks whatsoever, which was true only while the
            // JSON config was a speaker's ONLY source of tasks. Since the migration the registry
            // also seeds the eight code-defined speaker tasks, so a main-day speaker legitimately
            // holds some (the presentation uploads and Help Promote — no entitlements here, so no
            // logistics). The rule under test is the masterclass-only GATE, and it is unchanged:
            // this speaker does not present on the pre-day, so they must not get THAT deadline.
            Assert.False(await db.Tasks.AnyAsync(t =>
                t.AssignedParticipantId == mainOnly.Id
                && t.SourceKey == $"speakerdl:{mainOnly.Id}:prepare-master-class-materials"));
        }
        finally
        {
            File.Delete(config);
        }
    }

    // =====================================================================
    //  G11 — the nightly seeder sweeps an EX-SPEAKER's speakerdl: tasks
    // =====================================================================

    [Fact]
    public async Task G11_seeder_sweeps_speakerdl_tasks_of_a_participant_no_longer_a_speaker()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var ex = await SeedPersonAsync(db, eventId, "ex@x.dk", ParticipantRole.Volunteer, name: "Ex Speaker");
        var still = await SeedPersonAsync(db, eventId, "still@x.dk", ParticipantRole.Speaker, name: "Still Speaking");
        db.Tasks.AddRange(
            new ParticipantTask
            {
                EventId = eventId, AssignedParticipantId = ex.Id, Title = "Upload final presentation",
                DueDate = DateOnly.FromDateTime(Now.UtcDateTime), State = TaskState.Open,
                SourceKey = $"speakerdl:{ex.Id}:upload-final-presentation",
            },
            new ParticipantTask
            {
                EventId = eventId, AssignedParticipantId = still.Id, Title = "Upload final presentation",
                DueDate = DateOnly.FromDateTime(Now.UtcDateTime), State = TaskState.Open,
                SourceKey = $"speakerdl:{still.Id}:upload-final-presentation",
            });
        await db.SaveChangesAsync();

        var config = WriteDeadlineConfig();
        try
        {
            await NewSeeder(db, config).SeedAsync(eventId);

            // §1082 — the ex-speaker's dated speakerdl task is RETIRED, not deleted (operator
            // 2026-08-13: *"you dont delete, but close them (as they were inactive)"*). The property
            // that matters is unchanged and asserted below: its due-day reminder can never fire
            // again. What changed is that the row survives for audit — an ex-speaker has often
            // already DONE some of these, and deleting erased that they did.
            var exRows = await db.Tasks
                .Where(t => t.AssignedParticipantId == ex.Id && t.SourceKey!.StartsWith("speakerdl:"))
                .ToListAsync();
            Assert.NotEmpty(exRows);                                   // kept, not erased
            Assert.All(exRows, t => Assert.Equal(TaskState.Done, t.State));
            Assert.All(exRows, t => Assert.Equal(
                TaskClosedReason.RetiredFromCatalog, t.ClosedReason));  // system-closed, not "they did it"
            Assert.True(await db.Tasks.AnyAsync(t => t.AssignedParticipantId == still.Id
                && t.SourceKey == $"speakerdl:{still.Id}:upload-final-presentation"));

            var due = await new TaskReminderBuilder(db, RealTemplates(), Clock, new SponsorRecipientResolver(db))
                .BuildDueAsync(eventId);
            Assert.DoesNotContain(due, m => m.RecipientEmail == "ex@x.dk");
        }
        finally
        {
            File.Delete(config);
        }
    }

    // =====================================================================
    //  G13 — attendee-list export excludes soft-cancelled tickets
    // =====================================================================

    [Fact]
    public async Task G13_attendee_list_export_excludes_cancelled_mirror_rows()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        db.Attendees.AddRange(
            new Attendee
            {
                EventId = eventId, BackstageTicketId = "T1", Email = "in@x.dk",
                FirstName = "Ina", LastName = "List", FullName = "Ina List",
                TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
            },
            new Attendee
            {
                EventId = eventId, BackstageTicketId = "T2", Email = "out@x.dk",
                FirstName = "Otto", LastName = "Gone", FullName = "Otto Gone",
                TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Cancelled,
                CancelledAt = Now,
            });
        await db.SaveChangesAsync();

        var svc = new OrganizerExportsService(db);
        var rows = await svc.BuildAttendeeListAsync(eventId);
        var row = Assert.Single(rows);
        Assert.Equal("in@x.dk", row.Email);

        var csv = await svc.BuildAttendeeListCsvAsync(eventId);
        Assert.Contains("in@x.dk", csv);
        Assert.DoesNotContain("out@x.dk", csv);
    }

    // =====================================================================
    //  G14 — public agenda excludes DEACTIVATED speakers
    // =====================================================================

    [Fact]
    public async Task G14_public_agenda_excludes_inactive_speaker_but_keeps_active_cospeaker()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var activeSp = await SeedPersonAsync(db, eventId, "on@x.dk", ParticipantRole.Speaker, name: "Vis Ible");
        var ghost = await SeedPersonAsync(db, eventId, "ghost@x.dk", ParticipantRole.Speaker,
            isActive: false, name: "Gho Stly");
        var s = new Session
        {
            EventId = eventId, Title = "Co-talk", Type = SessionType.TechnicalSession,
            StartsAt = new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.FromHours(1)),
            EndsAt = new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.FromHours(1)),
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        db.SessionSpeakers.AddRange(
            new SessionSpeaker { SessionId = s.Id, ParticipantId = activeSp.Id },
            new SessionSpeaker { SessionId = s.Id, ParticipantId = ghost.Id });
        await db.SaveChangesAsync();

        var view = await new PublicAgendaService(db).BuildAsync();

        Assert.NotNull(view);
        var item = view!.Days.SelectMany(d => d.Items).Single(i => i.Title == "Co-talk");
        Assert.Contains("Vis Ible", item.Speakers);
        Assert.DoesNotContain("Gho Stly", item.Speakers);
        // The session keeps its LINK to the deactivated speaker (alert-only, §56/§58).
        Assert.Equal(2, await db.SessionSpeakers.CountAsync(x => x.SessionId == s.Id));
    }

    // =====================================================================
    //  G15 — hard-delete leaves no ghost PartyRsvp / availability rows
    // =====================================================================

    [Fact]
    public async Task G15_participant_hard_delete_cleans_party_rsvp_and_volunteer_availability()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var p = await SeedPersonAsync(db, eventId, "vol@x.dk", ParticipantRole.Volunteer);
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = eventId, Name = p.FullName, Email = p.Email,
            Attending = true, ParticipantId = p.Id, UpdatedAt = Now,
        });
        db.VolunteerAvailabilities.Add(new VolunteerAvailability
        {
            EventId = eventId, ParticipantId = p.Id, SelectedShifts = "shift-1",
        });
        db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
        {
            EventId = eventId, ParticipantId = p.Id, Day = new DateOnly(2027, 2, 9),
        });
        await db.SaveChangesAsync();

        db.Participants.Remove(p);
        await db.SaveChangesAsync();   // used to ghost the RSVP / FK-error on availability

        Assert.Empty(db.PartyRsvps.Where(r => r.EventId == eventId));
        Assert.Empty(db.VolunteerAvailabilities.Where(a => a.EventId == eventId));
        Assert.Empty(db.VolunteerDayAvailabilities.Where(a => a.EventId == eventId));
    }

    // =====================================================================
    //  G16 — re-purchase re-engages: party re-ask + invite/chaser re-arm
    // =====================================================================

    [Fact]
    public async Task G16_new_ticket_after_full_cancellation_clears_stale_party_no_and_ledgers()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        // 1. Active 2-day ticket + provisioned login + answered party YES + chaser ledger.
        await sync.SyncAsync(eventId, new[] { new TR("T1", "Bo", "Back", "bo@x.dk", TicketStatus.TwoDay, "2-day") });
        var p = await SeedPersonAsync(db, eventId, "bo@x.dk", ParticipantRole.Attendee, name: "Bo Back");
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = eventId, Name = "Bo Back", Email = "bo@x.dk",
            Attending = true, ParticipantId = p.Id, UpdatedAt = Now.AddDays(-30),
        });
        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId, AssignedParticipantId = p.Id, Title = "Sign up for the Party",
            State = TaskState.Done, CompletedAt = Now.AddDays(-30),
            SourceKey = PartyTaskSeeder.SourceKeyFor(p.Id),
        });
        db.SentReminders.Add(new SentReminder
        {
            EventId = eventId, RecipientEmail = "bo@x.dk",
            ReminderType = "pending-master-class-selection", OccasionKey = "pendingmc:bo@x.dk",
        });
        var mirrorRow = await db.Attendees.SingleAsync(a => a.BackstageTicketId == "T1");
        mirrorRow.MasterClassInviteSentAt = Now.AddDays(-30);   // §241 invite already sent once
        await db.SaveChangesAsync();

        // 2. CANCEL (§326as: Zoho flags T1 not_attending): party flips to No, login locked.
        await sync.SyncAsync(eventId,
            new[] { new TR("T1", "Bo", "Back", "bo@x.dk", TicketStatus.TwoDay, "2-day", CancelledUpstream: true) });
        var rsvp = await db.PartyRsvps.SingleAsync(r => r.Email == "bo@x.dk");
        Assert.False(rsvp.Attending);
        Assert.False((await db.Participants.SingleAsync(x => x.Id == p.Id)).IsActive);

        // 3. RE-PURCHASE: NEW ticket id, same email.
        await sync.SyncAsync(eventId, new[] { new TR("T2", "Bo", "Back", "bo@x.dk", TicketStatus.TwoDay, "2-day") });

        // Login restored.
        Assert.True((await db.Participants.SingleAsync(x => x.Id == p.Id)).IsActive);
        // The stale auto-cancelled party "No" is DELETED → they start UNANSWERED.
        Assert.Empty(db.PartyRsvps.Where(r => r.EventId == eventId));
        // The party task is REOPENED so the biweekly cadence resumes.
        var task = await db.Tasks.SingleAsync(t => t.SourceKey == PartyTaskSeeder.SourceKeyFor(p.Id));
        Assert.Equal(TaskState.Open, task.State);
        Assert.Null(task.CompletedAt);
        // The once-ever pendingmc chaser ledger row is cleared → the chaser can re-fire.
        Assert.Empty(db.SentReminders.Where(s => s.OccasionKey == "pendingmc:bo@x.dk"));
        // The NEW active row is invite-eligible again (§241 = the 2-day welcome).
        var newRow = await db.Attendees.SingleAsync(a => a.BackstageTicketId == "T2");
        Assert.Null(newRow.MasterClassInviteSentAt);
        var eligible = await new MasterClassSignupService(db).EligibleNotInvitedIdsAsync(eventId);
        Assert.Contains(newRow.Id, eligible);
    }

    [Fact]
    public async Task G16_same_ticket_reappearance_rearms_the_selection_invite()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        await sync.SyncAsync(eventId, new[] { new TR("T1", "Re", "Turn", "re@x.dk", TicketStatus.TwoDay, "2-day") });
        var row = await db.Attendees.SingleAsync(a => a.BackstageTicketId == "T1");
        row.MasterClassInviteSentAt = Now.AddDays(-20);   // invited before the cancellation
        await db.SaveChangesAsync();

        // §326as: cancel = Zoho flags the SAME ticket not_attending; re-purchase = it
        // goes back to attending.
        await sync.SyncAsync(eventId,
            new[] { new TR("T1", "Re", "Turn", "re@x.dk", TicketStatus.TwoDay, "2-day", CancelledUpstream: true) });
        await sync.SyncAsync(eventId, new[] { new TR("T1", "Re", "Turn", "re@x.dk", TicketStatus.TwoDay, "2-day") }); // reappear

        row = await db.Attendees.SingleAsync(a => a.BackstageTicketId == "T1");
        Assert.Equal(MirrorState.Active, row.MirrorState);
        // No confirmed seat (released at cancel) ⇒ the invite stamp is CLEARED so the
        // §241 sweep re-welcomes them.
        Assert.Null(row.MasterClassInviteSentAt);
    }

    [Fact]
    public async Task G16_genuine_pre_cancellation_party_No_is_preserved_on_repurchase()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        await sync.SyncAsync(eventId, new[] { new TR("T1", "No", "Thanks", "no@x.dk", TicketStatus.TwoDay, "2-day") });
        // A REAL "No" answered long before the cancellation (UpdatedAt in the past).
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = eventId, Name = "No Thanks", Email = "no@x.dk",
            Attending = false, HeadCount = null, UpdatedAt = Now.AddDays(-60),
        });
        await db.SaveChangesAsync();

        // §326as: cancel via Zoho's own status (rsvp already No — untouched).
        await sync.SyncAsync(eventId,
            new[] { new TR("T1", "No", "Thanks", "no@x.dk", TicketStatus.TwoDay, "2-day", CancelledUpstream: true) });
        await sync.SyncAsync(eventId, new[] { new TR("T2", "No", "Thanks", "no@x.dk", TicketStatus.TwoDay, "2-day") }); // new ticket

        // The deliberate answer SURVIVES — only cancel-time auto-"No" rows are cleared.
        var rsvp = await db.PartyRsvps.SingleAsync(r => r.Email == "no@x.dk");
        Assert.False(rsvp.Attending);
    }

    // =====================================================================
    //  §252 orphan (a) — MC confirmation carries the open-calendar links
    // =====================================================================

    [Fact]
    public async Task Orphan_MC_confirmation_email_contains_google_and_outlook_open_links()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        // §257: verify the attached-invite path (gated behind the auto-invite switch).
        (await db.Events.FindAsync(eventId))!.AutoCalendarInvitesEnabled = true;
        var s = new Session { EventId = eventId, Title = "Deep Dive MC", Type = SessionType.MasterClass, MasterClassCapacity = 5 };
        db.Sessions.Add(s);
        var a = new Attendee
        {
            EventId = eventId, Email = "cal@x.dk", FirstName = "Cal", LastName = "Link",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
        };
        db.Attendees.Add(a);
        await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var signups = new MasterClassSignupService(db);
        var svc = new MasterClassEmailService(db, sender, new NoOpContext(), signups, RealTemplates());
        Assert.True((await signups.SignUpAsync(eventId, a.Id, s.Id)).Ok);
        var id = (await signups.SignupIdAsync(eventId, a.Id, s.Id))!.Value;

        await svc.SendConfirmedAsync(id, "https://hub.test");

        var m = Assert.Single(sender.IcsMessages);
        // §341-4 (operator 2026-07-26) — REVERSED ASSERTION, deliberately. The Google/Outlook
        // "add it online" links and the "your calendar invitation is attached" line are GONE
        // from the confirmation. They were the fallback for AutoCalendarInvitesEnabled=false
        // (the default), but the operator's decision is that the mail must not talk about
        // calendar invites at all — attendees add it from the hub button instead. Asserting
        // their ABSENCE so a future edit cannot quietly reinstate them.
        Assert.DoesNotContain("calendar.google.com", m.Html);
        Assert.DoesNotContain("outlook.office.com/calendar", m.Html);
        Assert.DoesNotContain("invitation is attached", m.Html);
    }
}
