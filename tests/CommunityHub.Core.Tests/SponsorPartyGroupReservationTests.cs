using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// REQUIREMENTS §228 — the sponsor party GROUP reservation: one reservation per company,
/// registered by any linked contact, visible to (and completing the task for) every
/// contact of that company.
/// </summary>
public class SponsorPartyGroupReservationTests
{
    private static async Task<int> SeedEventAsync(Data.CommunityHubDbContext db)
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

    private static async Task<int> SeedSponsorAsync(
        Data.CommunityHubDbContext db, int ev, string email, string name, string companyId = "c1")
    {
        var p = new Participant
        {
            EventId = ev, Email = email, FullName = name, Role = ParticipantRole.Sponsor,
            IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
            SponsorCompanyId = companyId,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    [Fact]
    public async Task Group_submit_creates_one_reservation_visible_to_every_linked_contact()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var a = await SeedSponsorAsync(db, ev, "a@co.dk", "Contact A");
        var b = await SeedSponsorAsync(db, ev, "b@co.dk", "Contact B");
        var svc = new PartyRsvpService(db);

        var r = await svc.SubmitGroupAsync("Contact A", "a@co.dk", attending: true, headCount: 5, a);
        Assert.True(r.Ok);

        // ONE row for the whole company.
        Assert.Single(db.PartyRsvps.Where(x => x.EventId == ev));

        // Contact B (who never submitted) SEES the group reservation.
        var seenByB = await svc.GetCompanyReservationAsync(ev, b);
        Assert.NotNull(seenByB);
        Assert.True(seenByB!.Attending);
        Assert.Equal(5, seenByB.HeadCount);
        Assert.Equal("Contact A", seenByB.Name);
        Assert.Equal(a, seenByB.ParticipantId);
    }

    [Fact]
    public async Task Second_contact_updates_the_same_reservation_never_a_second_row()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var a = await SeedSponsorAsync(db, ev, "a@co.dk", "Contact A");
        var b = await SeedSponsorAsync(db, ev, "b@co.dk", "Contact B");
        var svc = new PartyRsvpService(db);

        await svc.SubmitGroupAsync("Contact A", "a@co.dk", attending: true, headCount: 5, a);
        await svc.SubmitGroupAsync("Contact B", "b@co.dk", attending: true, headCount: 8, b);

        var row = Assert.Single(db.PartyRsvps.Where(x => x.EventId == ev));
        Assert.Equal(8, row.HeadCount);          // updated in place
        Assert.Equal("Contact B", row.Name);     // re-stamped to the latest submitter
        Assert.Equal(b, row.ParticipantId);
    }

    [Fact]
    public async Task Legacy_per_contact_rows_are_neutralised_so_the_group_counts_once()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var a = await SeedSponsorAsync(db, ev, "a@co.dk", "Contact A");
        var b = await SeedSponsorAsync(db, ev, "b@co.dk", "Contact B");
        var now = DateTimeOffset.UtcNow;
        // Two pre-§228 personal rows from the same company.
        db.PartyRsvps.Add(new PartyRsvp { EventId = ev, Name = "Contact A", Email = "a@co.dk", Attending = true, HeadCount = 3, ParticipantId = a, CreatedAt = now, UpdatedAt = now });
        db.PartyRsvps.Add(new PartyRsvp { EventId = ev, Name = "Contact B", Email = "b@co.dk", Attending = true, HeadCount = 4, ParticipantId = b, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();

        var svc = new PartyRsvpService(db);
        await svc.SubmitGroupAsync("Contact A", "a@co.dk", attending: true, headCount: 6, a);

        var rows = db.PartyRsvps.Where(x => x.EventId == ev).OrderBy(x => x.Id).ToList();
        Assert.Equal(2, rows.Count);                       // never hard-deleted (mirror model)
        Assert.True(rows[0].Attending);
        Assert.Equal(6, rows[0].HeadCount);                // the canonical group row
        Assert.False(rows[1].Attending);                   // stray neutralised
        Assert.Null(rows[1].HeadCount);
    }

    [Fact]
    public async Task Company_reservation_completes_every_contacts_party_task()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var a = await SeedSponsorAsync(db, ev, "a@co.dk", "Contact A");
        var b = await SeedSponsorAsync(db, ev, "b@co.dk", "Contact B");
        var now = DateTimeOffset.UtcNow;
        foreach (var pid in new[] { a, b })
            db.Tasks.Add(new ParticipantTask
            {
                EventId = ev, AssignedParticipantId = pid, Title = "Sign up for the Party",
                State = TaskState.Open, SourceKey = PartyTaskSeeder.SourceKeyFor(pid), CreatedAt = now,
            });
        await db.SaveChangesAsync();

        await new PartyRsvpService(db).SubmitGroupAsync("Contact A", "a@co.dk", true, 5, a);

        var reconciler = new FormTaskReconciler(db, TimeProvider.System);
        await reconciler.ReconcileAsync(ev, a, default);
        await reconciler.ReconcileAsync(ev, b, default);   // B never submitted anything

        Assert.All(db.Tasks.Where(t => t.EventId == ev),
            t => Assert.Equal(TaskState.Done, t.State));
    }

    [Fact(Skip = "733.1 - the per-task party/master-class cadences are RETIRED (operator 2026-07-31: the Get Started wizard is chased once every 14 days by getstarted-digest). Kept, not deleted, so re-enabling restores this coverage.")]
    public async Task Reminder_cadence_stops_for_the_whole_company_once_the_group_is_registered()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var a = await SeedSponsorAsync(db, ev, "a@co.dk", "Contact A");
        var b = await SeedSponsorAsync(db, ev, "b@co.dk", "Contact B");
        var anchor = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);
        foreach (var pid in new[] { a, b })
            db.Tasks.Add(new ParticipantTask
            {
                EventId = ev, AssignedParticipantId = pid, Title = "Sign up for the Party",
                State = TaskState.Open, SourceKey = PartyTaskSeeder.SourceKeyFor(pid), CreatedAt = anchor,
            });
        await db.SaveChangesAsync();

        var clock = new FixedTestClock(anchor.AddDays(14));
        var templates = new EmailTemplateProvider(
            Microsoft.Extensions.Options.Options.Create(
                new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));
        var builder = new AttendeePartyReminderBuilder(db, templates, clock);

        // Before anyone answers: both contacts are nagged (first window, §232).
        Assert.Equal(2, (await builder.BuildDueAsync(ev)).Count);

        // Contact A registers the GROUP → the cadence stops for BOTH contacts.
        await new PartyRsvpService(db).SubmitGroupAsync("Contact A", "a@co.dk", true, 5, a);
        Assert.Empty(await builder.BuildDueAsync(ev));
    }

    private sealed class FixedTestClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedTestClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
