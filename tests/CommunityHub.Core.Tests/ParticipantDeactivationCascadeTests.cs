using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Tests.Scenario;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The §253 G1 deactivation cascade — the fix for the "no deactivation cascade"
/// root cause the scenario matrix confirmed on every organizer path (D1–D4).
/// Proves, PER ROLE (volunteer / speaker / sponsor contact / media / event
/// partner), that ONE <see cref="ParticipantDeactivationService.DeactivateAsync"/>:
///   • flips BOTH IsActive and LifecycleState + stamps the G8 tombstone;
///   • cancels the party RSVP (§209 semantics — row kept, Attending=false,
///     HeadCount=null) and the headcount drops;
///   • releases the hotel room-block claim (NeedsRoom off, placement cleared) so
///     the block math + confirmed-invite audience exclude the person;
///   • drops the lunch / swag / dinner counts (via the §253 G2/G4/G5/G6
///     active-only filters);
///   • closes the person's open tasks and vacates their shift assignments so
///     coverage shows the gap;
///   • writes an audit entry.
/// Plus: re-activation restores NOTHING (no silent booking resurrection), the
/// sponsor GROUP party reservation survives only while the company has another
/// ACTIVE contact, and the whole-company withdrawal (G8b) cascades. FAKE names.
/// </summary>
public sealed class ParticipantDeactivationCascadeTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"cascade-{Guid.NewGuid():N}")
            .Options);

    private static ParticipantDeactivationService Sut(CommunityHubDbContext db) =>
        new(db, new FixedClock(), new AuditTrailService(db, new FixedClock()));

    private static VolunteerStructureService.ActorContext Organizer() =>
        new(999, "org@example.test", ParticipantRole.Organizer, EventId);

    // ----- seeding -----------------------------------------------------------

    private static async Task<Event> SeedEventAsync(CommunityHubDbContext db)
    {
        var e = new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9),
        };
        db.Events.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    private static async Task<Participant> SeedPersonAsync(
        CommunityHubDbContext db, ParticipantRole role, string email,
        string? companyId = null)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = email.Split('@')[0],
            Role = role, SponsorCompanyId = companyId,
            IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    /// <summary>Give the person the full logistics footprint the cascade must clear.</summary>
    private static async Task<(Hotel Hotel, VolunteerTask Shift, ParticipantTask Task)>
        SeedFullFootprintAsync(CommunityHubDbContext db, Participant p)
    {
        var hotel = new Hotel { EventId = EventId, Name = "Block Hotel", RoomBlockSize = 10 };
        db.Hotels.Add(hotel);
        await db.SaveChangesAsync();

        p.HotelId = hotel.Id;
        db.HotelBookings.Add(new HotelBooking
        {
            EventId = EventId, ParticipantId = p.Id, NeedsRoom = true,
            CheckInDate = new DateOnly(2027, 2, 8), CheckOutDate = new DateOnly(2027, 2, 11),
        });
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = EventId, Name = p.FullName, Email = p.Email,
            Attending = true, ParticipantId = p.Id,
            CreatedAt = Now, UpdatedAt = Now,
        });
        db.LunchSignups.Add(new LunchSignup
        {
            EventId = EventId, ParticipantId = p.Id,
            LunchSetupDay = true, LunchPreDay = true,
        });
        db.SwagPreferences.Add(new SwagPreference
        {
            EventId = EventId, ParticipantId = p.Id,
            WantsPolo = true, PoloSize = "L", WantsJacket = true, JacketSize = "M",
        });
        db.DinnerSignups.Add(new DinnerSignup
        {
            EventId = EventId, ParticipantId = p.Id, Attending = true, PlusOneCount = 1,
        });
        var task = new ParticipantTask
        {
            EventId = EventId, AssignedParticipantId = p.Id,
            Title = "Upload deck", DueDate = new DateOnly(2027, 1, 20),
            State = TaskState.Open,
        };
        db.Tasks.Add(task);

        var cat = new VolunteerCategory { EventId = EventId, Name = "Registration" };
        db.VolunteerCategories.Add(cat);
        await db.SaveChangesAsync();
        var sub = new VolunteerSubcategory { EventId = EventId, CategoryId = cat.Id, Name = "Desk" };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();
        var shift = new VolunteerTask
        {
            EventId = EventId, SubcategoryId = sub.Id, Title = "Desk shift",
            ResourcesNeeded = 1,
        };
        db.VolunteerTasks.Add(shift);
        await db.SaveChangesAsync();
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = shift.Id, ParticipantId = p.Id,
        });
        await db.SaveChangesAsync();
        return (hotel, shift, task);
    }

    // ----- per-role cascade --------------------------------------------------

    [Theory]
    [InlineData(ParticipantRole.Volunteer)]
    [InlineData(ParticipantRole.Speaker)]
    [InlineData(ParticipantRole.Sponsor)]
    [InlineData(ParticipantRole.Media)]
    [InlineData(ParticipantRole.EventPartner)]
    public async Task Deactivation_cascades_every_logistics_surface_for_role(ParticipantRole role)
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var p = await SeedPersonAsync(db, role, $"{role}@example.test".ToLowerInvariant(),
            companyId: role == ParticipantRole.Sponsor ? "42" : null);
        var (_, shift, task) = await SeedFullFootprintAsync(db, p);

        var result = await Sut(db).DeactivateAsync(
            EventId, p.Id, "test", "org@example.test");

        // Flags + tombstone (kills the D1/D4 flag-only divergence).
        Assert.True(result.Found);
        Assert.False(p.IsActive);
        Assert.Equal(ParticipantLifecycleState.Inactive, p.LifecycleState);
        Assert.NotNull(p.DeactivatedByOrganizerAt);

        // Party: RSVP row kept but cancelled (§209 semantics); headcount drops.
        var rsvp = await db.PartyRsvps.SingleAsync(r => r.EventId == EventId);
        Assert.False(rsvp.Attending);
        Assert.Null(rsvp.HeadCount);
        var (_, attending) = await new PartyRsvpService(db).CountsAsync(EventId);
        Assert.Equal(0, attending);

        // Hotel: room-block claim released, placement cleared; block math clean.
        var booking = await db.HotelBookings.SingleAsync(h => h.ParticipantId == p.Id);
        Assert.False(booking.NeedsRoom);
        Assert.Null(p.HotelId);
        var block = await new HotelRoomBlockService(db).BuildAsync(EventId);
        Assert.Equal(0, block.TotalAssignedNeedingRoom);
        Assert.Equal(0, block.UnassignedNeedingRoom);

        // Hotel: no CONFIRMED calendar invite goes to the drop-out (§253 H3).
        var hotels = new HotelManagementService(db, new FixedClock());
        var hotelId = await db.Hotels.Select(h => h.Id).SingleAsync();
        Assert.Empty(await hotels.ListReserversForInviteAsync(EventId, hotelId));

        // Lunch: headcount + caterer run-sheet drop to zero (§253 G4).
        var exports = new OrganizerExportsService(db);
        Assert.All(await exports.BuildLunchHeadcountAsync(EventId), r => Assert.Equal(0, r.Count));
        Assert.Empty(await exports.BuildLunchPeopleAsync(EventId));

        // Tasks: closed (Done), so the due-date reminder track goes quiet (§81) — but §332
        // LABELS the closure, so the dashboards stop reading abandoned work as completed.
        var closed = await db.Tasks.SingleAsync(t => t.Id == task.Id);
        Assert.Equal(TaskState.Done, closed.State);
        Assert.Equal(TaskClosedReason.AbandonedOnDeactivation, closed.ClosedReason);

        // Shifts: vacated + coverage honestly shows the gap (§253 G7).
        Assert.Empty(await db.VolunteerTaskAssignments.Where(a => a.ParticipantId == p.Id).ToListAsync());
        var coverage = await new VolunteerAllocationService(
                db, new FixedClock(), new FeatureGateService(db), new RingResolver(db))
            .LoadCoverageAsync(Organizer());
        var line = Assert.Single(coverage, c => c.TaskId == shift.Id);
        Assert.Equal(0, line.AssignedCount);
        Assert.False(line.IsCovered);

        // Audited.
        Assert.True(await db.AuditEntries.AnyAsync(a =>
            a.EventId == EventId
            && a.Action == ParticipantDeactivationService.ActionDeactivate
            && a.TargetId == p.Id.ToString()));
    }

    /// <summary>
    /// §332 (the §326bj bug the operator's "Task completion 40 / 390" screenshot showed):
    /// a drop-out's untouched tasks were counted as COMPLETED on every dashboard, because the
    /// cascade closes them as Done. They must now leave BOTH sides of the ratio — the
    /// percentage has to describe work that is still real.
    /// </summary>
    [Fact]
    public async Task Abandoned_tasks_leave_both_sides_of_the_completion_ratio()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var stays = await SeedPersonAsync(db, ParticipantRole.Speaker, "stays@example.test");
        var leaves = await SeedPersonAsync(db, ParticipantRole.Speaker, "leaves@example.test");

        // Two people, two tasks each. The one who stays finished one of theirs.
        foreach (var (person, done) in new[] { (stays, true), (leaves, false) })
        {
            db.Tasks.Add(new ParticipantTask
            {
                EventId = EventId, AssignedParticipantId = person.Id, Title = "Upload deck",
                State = done ? TaskState.Done : TaskState.Open,
                CompletedAt = done ? Now : null,
            });
            db.Tasks.Add(new ParticipantTask
            {
                EventId = EventId, AssignedParticipantId = person.Id, Title = "Send bio",
                State = TaskState.Open,
            });
        }
        await db.SaveChangesAsync();

        var before = await new OrganizerOverviewService(db, new FixedClock()).BuildAsync(EventId);
        Assert.Equal(new[] { 1, 4 }, new[] { before.TaskOverall.Done, before.TaskOverall.Total });

        await Sut(db).DeactivateAsync(EventId, leaves.Id, "withdrew");

        // The two abandoned tasks are gone from the ratio entirely — NOT counted as done
        // (the old bug would have read 3/4 = 75% "completion" for work nobody did).
        var after = await new OrganizerOverviewService(db, new FixedClock()).BuildAsync(EventId);
        Assert.Equal(new[] { 1, 2 }, new[] { after.TaskOverall.Done, after.TaskOverall.Total });
        Assert.Equal(50, after.TaskOverall.Percent);

        // Same for the per-speaker milestone view: the withdrawn speaker no longer reads as
        // having cleared "Send bio".
        var bio = Assert.Single(after.SpeakerMilestones, m => m.Milestone == "Send bio");
        Assert.Equal(0, bio.Done);
        Assert.Equal(1, bio.Total);
    }

    /// <summary>
    /// §331 — the shift assignments are HARD-deleted and reactivation restores nothing, so
    /// the audit entry's Detail is the only surviving record of what the person held. It
    /// must name the task, its date and its shift label, not just a count.
    /// </summary>
    [Fact]
    public async Task Deactivation_records_which_shifts_were_vacated_in_the_audit_detail()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var p = await SeedPersonAsync(db, ParticipantRole.Volunteer, "vol@example.test");
        var (_, shift, _) = await SeedFullFootprintAsync(db, p);
        shift.DueDate = new DateOnly(2027, 2, 10);
        shift.Shift = "08:00-12:00";
        await db.SaveChangesAsync();

        await Sut(db).DeactivateAsync(EventId, p.Id, "test", "org@example.test");

        var entry = await db.AuditEntries.SingleAsync(a =>
            a.Action == ParticipantDeactivationService.ActionDeactivate);
        Assert.NotNull(entry.Detail);
        Assert.Contains("Desk shift", entry.Detail);
        Assert.Contains("2027-02-10", entry.Detail);
        Assert.Contains("08:00-12:00", entry.Detail);
        Assert.Contains("Registration", entry.Detail);   // the bucket it belonged to
    }

    /// <summary>Nothing to vacate ⇒ no noise in Detail (it stays null).</summary>
    [Fact]
    public async Task Deactivation_leaves_the_audit_detail_null_when_no_shifts_were_held()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var p = await SeedPersonAsync(db, ParticipantRole.Speaker, "spk@example.test");

        await Sut(db).DeactivateAsync(EventId, p.Id, "test", "org@example.test");

        var entry = await db.AuditEntries.SingleAsync(a =>
            a.Action == ParticipantDeactivationService.ActionDeactivate);
        Assert.Null(entry.Detail);
    }

    [Fact]
    public async Task Deactivation_is_idempotent_and_converges_a_flag_only_legacy_row()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var p = await SeedPersonAsync(db, ParticipantRole.Volunteer, "vol@example.test");
        await SeedFullFootprintAsync(db, p);

        // Simulate the OLD flag-only deactivation (pre-cascade data shape).
        p.IsActive = false;
        await db.SaveChangesAsync();

        var first = await Sut(db).DeactivateAsync(EventId, p.Id, "converge");
        var second = await Sut(db).DeactivateAsync(EventId, p.Id, "re-run");

        // First run reports + clears the live footprint; the re-run double-applies nothing.
        Assert.Equal(1, first.PartyRsvpsCancelled);
        Assert.Equal(1, first.HotelBookingsReleased);
        Assert.Equal(1, first.TasksClosed);
        Assert.Equal(1, first.ShiftAssignmentsVacated);
        Assert.True(second.AlreadyInactive);
        Assert.Equal(0, second.PartyRsvpsCancelled);
        Assert.Equal(0, second.HotelBookingsReleased);
        Assert.Equal(0, second.TasksClosed);
        Assert.Equal(0, second.ShiftAssignmentsVacated);
    }

    // ----- re-activation restores nothing -------------------------------------

    [Fact]
    public async Task Reactivation_clears_flags_and_tombstone_but_restores_no_bookings()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var p = await SeedPersonAsync(db, ParticipantRole.Speaker, "spk@example.test");
        var (_, _, task) = await SeedFullFootprintAsync(db, p);
        var sut = Sut(db);
        await sut.DeactivateAsync(EventId, p.Id, "test");

        var ok = await sut.ReactivateAsync(EventId, p.Id, "org@example.test");

        Assert.True(ok.Found);
        Assert.True(p.IsActive);
        Assert.Equal(ParticipantLifecycleState.Active, p.LifecycleState);
        Assert.Null(p.DeactivatedByOrganizerAt);

        // Logistics resurrect NOTHING: the person re-RSVPs / the organizer re-books.
        Assert.False((await db.PartyRsvps.SingleAsync()).Attending);
        Assert.False((await db.HotelBookings.SingleAsync()).NeedsRoom);
        Assert.Null(p.HotelId);
        Assert.Empty(await db.VolunteerTaskAssignments.Where(a => a.ParticipantId == p.Id).ToListAsync());

        // §332 — TASKS are the deliberate exception. They were force-closed to Done by the
        // cascade (never by the person), so a returning participant gets their untouched work
        // back and is chased for it again instead of coming back "finished" forever.
        var back = await db.Tasks.SingleAsync(t => t.Id == task.Id);
        Assert.Equal(TaskState.Open, back.State);
        Assert.Null(back.CompletedAt);
        Assert.Null(back.ClosedReason);
        Assert.Equal(1, ok.TasksReopened);

        // Safe no-op on an already-active row.
        Assert.True((await sut.ReactivateAsync(EventId, p.Id)).Found);
    }

    [Fact]
    public async Task Cascade_is_edition_scoped_and_notfound_safe()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var p = await SeedPersonAsync(db, ParticipantRole.Media, "media@example.test");

        var wrongEvent = await Sut(db).DeactivateAsync(EventId + 1, p.Id, "wrong edition");

        Assert.False(wrongEvent.Found);
        Assert.True(p.IsActive);   // untouched
        Assert.False((await Sut(db).ReactivateAsync(EventId + 1, p.Id)).Found);
    }

    // ----- sponsor GROUP party reservation (§228 × §253 G3) -------------------

    [Fact]
    public async Task Group_reservation_survives_while_another_active_contact_exists_then_cancels()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var a = await SeedPersonAsync(db, ParticipantRole.Sponsor, "a@corp.test", companyId: "42");
        var b = await SeedPersonAsync(db, ParticipantRole.Sponsor, "b@corp.test", companyId: "42");
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = EventId, Name = a.FullName, Email = a.Email,
            Attending = true, HeadCount = 5, ParticipantId = a.Id,
            CreatedAt = Now, UpdatedAt = Now,
        });
        await db.SaveChangesAsync();
        var sut = Sut(db);
        var party = new PartyRsvpService(db);

        // Contact A (the reserving contact) leaves — B is still active, so the
        // company's group answer is KEPT and re-pointed to B.
        var first = await sut.DeactivateAsync(EventId, a.Id, "left company");
        Assert.True(first.GroupReservationKeptForCompany);
        var rsvp = await db.PartyRsvps.SingleAsync();
        Assert.True(rsvp.Attending);
        Assert.Equal(5, rsvp.HeadCount);
        Assert.Equal(b.Id, rsvp.ParticipantId);
        Assert.Equal(1, (await party.CountsAsync(EventId)).Attending);

        // The LAST contact leaves — now the group reservation is cancelled.
        var second = await sut.DeactivateAsync(EventId, b.Id, "company gone");
        Assert.False(second.GroupReservationKeptForCompany);
        Assert.Equal(1, second.PartyRsvpsCancelled);
        rsvp = await db.PartyRsvps.SingleAsync();
        Assert.False(rsvp.Attending);
        Assert.Null(rsvp.HeadCount);
        Assert.Equal(0, (await party.CountsAsync(EventId)).Attending);
    }

    // ----- party counts belt-and-braces (§253 G3) -----------------------------

    [Fact]
    public async Task Party_counts_exclude_inactive_participants_but_keep_anonymous_rows()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var ghost = await SeedPersonAsync(db, ParticipantRole.Volunteer, "ghost@example.test");
        ghost.IsActive = false;   // legacy flag-only drop-out, cascade never ran
        db.PartyRsvps.AddRange(
            new PartyRsvp
            {
                EventId = EventId, Name = "Ghost", Email = ghost.Email,
                Attending = true, ParticipantId = ghost.Id, CreatedAt = Now, UpdatedAt = Now,
            },
            new PartyRsvp
            {
                EventId = EventId, Name = "Anon", Email = "anon@public.test",
                Attending = true, ParticipantId = null, CreatedAt = Now, UpdatedAt = Now,
            });
        await db.SaveChangesAsync();

        var svc = new PartyRsvpService(db);
        var (total, attending) = await svc.CountsAsync(EventId);
        var rows = await svc.GetAllAsync(EventId);

        // The anonymous public RSVP stays; the inactive participant's row is out.
        Assert.Equal(1, total);
        Assert.Equal(1, attending);
        Assert.Equal("anon@public.test", Assert.Single(rows).Email);
    }

    // ----- whole-company withdrawal (§253 G8b) --------------------------------

    [Fact]
    public async Task Company_withdrawal_cascades_contacts_group_rsvp_and_counts()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var a = await SeedPersonAsync(db, ParticipantRole.Sponsor, "a@corp.test", companyId: "42");
        var b = await SeedPersonAsync(db, ParticipantRole.Sponsor, "b@corp.test", companyId: "42");
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "42",
            SponsorPackage = SponsorPackage.Gold,
        });
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = EventId, Name = a.FullName, Email = a.Email,
            Attending = true, HeadCount = 8, ParticipantId = a.Id,
            CreatedAt = Now, UpdatedAt = Now,
        });
        db.Tasks.Add(new ParticipantTask
        {
            EventId = EventId, AssignedParticipantId = b.Id,
            Title = "Upload logo", State = TaskState.Open,
        });
        await db.SaveChangesAsync();

        var result = await Sut(db).WithdrawSponsorCompanyAsync(EventId, "42", "org@example.test");

        Assert.True(result.Found);
        Assert.Equal(2, result.ContactsDeactivated);

        var info = await db.SponsorInfos.SingleAsync();
        Assert.Equal(SponsorStatus.Withdrawn, info.Status);
        Assert.NotNull(info.WithdrawnAt);

        // Every contact deactivated + tombstoned; group HeadCount gone; task closed.
        Assert.All(await db.Participants.Where(p => p.SponsorCompanyId == "42").ToListAsync(),
            p => { Assert.False(p.IsActive); Assert.NotNull(p.DeactivatedByOrganizerAt); });
        var rsvp = await db.PartyRsvps.SingleAsync();
        Assert.False(rsvp.Attending);
        Assert.Null(rsvp.HeadCount);
        Assert.Equal(0, (await new PartyRsvpService(db).CountsAsync(EventId)).Attending);
        Assert.Equal(TaskState.Done, (await db.Tasks.SingleAsync()).State);

        // Withdrawn company leaves the public sponsors page (§253 G8b / X7).
        var pub = await new PublicSponsorsService(db).BuildAsync();
        Assert.NotNull(pub);
        Assert.Equal(0, pub!.TotalCount);

        // Idempotent: a second withdrawal changes nothing and stays Found.
        var again = await Sut(db).WithdrawSponsorCompanyAsync(EventId, "42");
        Assert.True(again.Found);
        Assert.Equal(0, again.ContactsDeactivated);
    }

    // ----- §470: a deactivated speaker still living in Zoho Backstage ----------

    private static (ParticipantDeactivationService Sut, CapturingEmailSender Sender)
        SutWithNotifier(CommunityHubDbContext db)
    {
        var sender = new CapturingEmailSender();
        var alerts = new Core.Email.EngineAlertSender(
            sender, new Core.Email.EmailContextAccessor(), new FixedClock(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Core.Email.EngineAlertSender>.Instance);
        return (new ParticipantDeactivationService(
                    db, new FixedClock(), new AuditTrailService(db, new FixedClock()),
                    allocation: null, zohoNotifier: new Core.Email.ZohoChangeNotifier(alerts)),
                sender);
    }

    [Fact]
    public async Task Deactivating_a_speaker_who_exists_in_Zoho_asks_organizers_to_remove_them_by_hand()
    {
        // §470 — the Backstage speaker API is CREATE-ONLY (§26c): CEH cannot delete the speaker,
        // so without this mail they silently stay on the PUBLIC agenda after withdrawing.
        using var db = NewDb();
        await SeedEventAsync(db);
        var speaker = await SeedPersonAsync(db, ParticipantRole.Speaker, "gone@example.test");
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId, ParticipantId = speaker.Id, BackstageSpeakerId = "bs-777",
        });
        await db.SaveChangesAsync();

        var (sut, sender) = SutWithNotifier(db);
        await sut.DeactivateAsync(EventId, speaker.Id, "left the programme");

        var mail = Assert.Single(sender.Messages);
        Assert.Contains("bs-777", mail.Html);          // WHICH speaker to remove
        Assert.Contains("MANUALLY", mail.Html);        // and that it must be done by hand
    }

    [Fact]
    public async Task Deactivating_a_speaker_NOT_in_Zoho_sends_nothing()
    {
        // No Backstage id ⇒ nothing to clean up ⇒ no mail. Organizers must not be trained to
        // ignore a notice that fires when there is nothing to do.
        using var db = NewDb();
        await SeedEventAsync(db);
        var speaker = await SeedPersonAsync(db, ParticipantRole.Speaker, "never-synced@example.test");
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId, ParticipantId = speaker.Id, BackstageSpeakerId = null,
        });
        await db.SaveChangesAsync();

        var (sut, sender) = SutWithNotifier(db);
        await sut.DeactivateAsync(EventId, speaker.Id, "left the programme");

        Assert.Empty(sender.Messages);
    }

    [Fact]
    public async Task Re_running_the_cascade_on_an_already_inactive_speaker_does_not_re_mail()
    {
        // The cascade is deliberately idempotent and re-applies on a re-run; the NOTICE must not.
        // Otherwise every re-run mails the organizers about the same person again.
        using var db = NewDb();
        await SeedEventAsync(db);
        var speaker = await SeedPersonAsync(db, ParticipantRole.Speaker, "twice@example.test");
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId, ParticipantId = speaker.Id, BackstageSpeakerId = "bs-888",
        });
        await db.SaveChangesAsync();

        var (sut, sender) = SutWithNotifier(db);
        await sut.DeactivateAsync(EventId, speaker.Id, "left");
        await sut.DeactivateAsync(EventId, speaker.Id, "left again");

        Assert.Single(sender.Messages);
    }
}
