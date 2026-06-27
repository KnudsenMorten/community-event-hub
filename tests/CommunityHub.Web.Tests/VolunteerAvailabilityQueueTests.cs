using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Pages.Volunteer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the volunteer "My Availability" page applying the §59 delta-approval
/// queue to volunteers (§45). Mirrors the §38e "first submission applies, a later CHANGE
/// is queued" pattern:
/// <list type="bullet">
/// <item>A FIRST-time availability submission (no rows yet) APPLIES directly — no delta.</item>
/// <item>A later EDIT does NOT overwrite — it ENQUEUEs a Volunteer Update delta and keeps
/// the current (approved) availability until an organizer approves it.</item>
/// <item>Approve APPLIES the queued new availability; reject KEEPS the old.</item>
/// </list>
/// Drives the real <see cref="AvailabilityModel"/> over a fake volunteer session + in-memory
/// DB. FAKE names only.
/// </summary>
public sealed class VolunteerAvailabilityQueueTests
{
    private const int EventId = 31;
    private static readonly DateOnly Day1 = new(2027, 2, 9);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"volavail-{Guid.NewGuid():N}")
            .Options);

    private sealed class NoOpEmailSender : IEmailSender
    {
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string f, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static ClaimsPrincipal Session(Participant p)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        };
        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static AvailabilityModel NewModel(CommunityHubDbContext db, HttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        // A non-existent config path → the loader returns defaults (no extra volunteer days);
        // the editable days come from the seeded Event's StartDate..EndDate.
        var cfg = new EventEditionConfigLoader();
        var cfgOptions = new EventConfigOptions { EventConfigPath = "config/does-not-exist.json" };
        var queue = new SyncDeltaQueueService(db);
        return new AvailabilityModel(
            db, accessor, cfg, cfgOptions, new NoOpEmailSender(), queue,
            NullLogger<AvailabilityModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static async Task<Participant> SeedVolunteerAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "VA27", CommunityName = "C", DisplayName = "VA 2027",
            StartDate = Day1, EndDate = Day1, IsActive = true,
        });
        await db.SaveChangesAsync();

        var vol = new Participant
        {
            EventId = EventId, FullName = "Vera Volunteer", Email = "vera@example.test",
            Role = ParticipantRole.Volunteer, IsActive = true,
        };
        db.Participants.Add(vol);
        await db.SaveChangesAsync();
        return vol;
    }

    private static AvailabilityModel WithInputs(AvailabilityModel m, string slot, string? note = null)
    {
        m.Inputs = new List<AvailabilityModel.DayInput>
        {
            new() { Day = Day1, Slot = slot, Note = note },
        };
        return m;
    }

    [Fact]
    public async Task First_submission_applies_directly_with_no_delta()
    {
        using var db = NewDb();
        var vol = await SeedVolunteerAsync(db);
        var http = new DefaultHttpContext { User = Session(vol) };

        var model = WithInputs(NewModel(db, http), "Full day");
        var result = await model.OnPostAsync(default);

        Assert.IsType<RedirectToPageResult>(result);
        // Applied directly to the volunteer's day row.
        var row = db.VolunteerDayAvailabilities.Single();
        Assert.Equal(VolunteerAvailabilityLevel.Full, row.Level);
        // No delta was enqueued.
        Assert.Empty(db.SyncDeltas);
    }

    [Fact]
    public async Task Chosen_slot_persists_exclusively_and_reselects_on_load()
    {
        // Bug fix #1: the per-day options are radios sharing one name, so exactly ONE slot
        // is ever chosen for a day. This proves the server round-trip the radios rely on:
        // the picked slot persists (Level + tagged Note) and re-resolves to the SAME single
        // option on load — there is never more than one stored availability per (day).
        using var db = NewDb();
        var vol = await SeedVolunteerAsync(db);
        var http = new DefaultHttpContext { User = Session(vol) };

        await WithInputs(NewModel(db, http), "Morning 9–12", "need 13:00 free").OnPostAsync(default);

        var row = Assert.Single(db.VolunteerDayAvailabilities);   // exactly one row for the day
        Assert.Equal(VolunteerAvailabilityLevel.Half, row.Level); // Morning = Half
        Assert.Contains("[Morning 9–12]", row.Note);
        // The stored (Level, Note) re-selects the SAME single option (radio re-check on load).
        var reselected = CommunityHub.Core.Volunteers.VolunteerDayOptions.Resolve(Day1, row.Level, row.Note);
        Assert.Equal("Morning 9–12", reselected.Slot);
    }

    [Fact]
    public async Task A_later_edit_enqueues_a_delta_and_does_not_overwrite()
    {
        using var db = NewDb();
        var vol = await SeedVolunteerAsync(db);
        var http = new DefaultHttpContext { User = Session(vol) };

        // First submission (Full) applies.
        await WithInputs(NewModel(db, http), "Full day").OnPostAsync(default);

        // Later EDIT (Full → Morning) is QUEUED, not applied.
        var editResult = await WithInputs(NewModel(db, http), "Morning 9–12", "need 13:00 free").OnPostAsync(default);

        Assert.IsType<RedirectToPageResult>(editResult);
        // The stored availability is UNCHANGED (still Full) while the change is pending.
        var row = db.VolunteerDayAvailabilities.Single();
        Assert.Equal(VolunteerAvailabilityLevel.Full, row.Level);
        // A Volunteer Update delta is now pending for this volunteer.
        var delta = Assert.Single(db.SyncDeltas);
        Assert.Equal(SyncDeltaEntityType.Volunteer, delta.EntityType);
        Assert.Equal(SyncDeltaChangeKind.Update, delta.ChangeKind);
        Assert.Equal(SyncDeltaStatus.Pending, delta.Status);
        Assert.Equal(vol.Id.ToString(), delta.EntityId);
        Assert.Equal("Vera Volunteer", delta.EntityLabel);
    }

    [Fact]
    public async Task Approving_a_queued_edit_applies_the_new_availability()
    {
        using var db = NewDb();
        var vol = await SeedVolunteerAsync(db);
        var http = new DefaultHttpContext { User = Session(vol) };

        await WithInputs(NewModel(db, http), "Full day").OnPostAsync(default);
        await WithInputs(NewModel(db, http), "Morning 9–12", "need 13:00 free").OnPostAsync(default);

        var delta = db.SyncDeltas.Single();
        var queue = new SyncDeltaQueueService(db);
        var result = await queue.ApproveAsync(delta.Id, "olivia@example.test");

        Assert.True(result.Applied);
        var row = db.VolunteerDayAvailabilities.Single();
        Assert.Equal(VolunteerAvailabilityLevel.Half, row.Level); // Morning = Half
        Assert.Contains("Morning 9–12", row.Note);
        Assert.Contains("need 13:00 free", row.Note);
        // Volunteer was not deleted.
        Assert.NotNull(await db.Participants.FindAsync(vol.Id));
    }

    [Fact]
    public async Task Rejecting_a_queued_edit_keeps_the_old_availability()
    {
        using var db = NewDb();
        var vol = await SeedVolunteerAsync(db);
        var http = new DefaultHttpContext { User = Session(vol) };

        await WithInputs(NewModel(db, http), "Full day").OnPostAsync(default);
        await WithInputs(NewModel(db, http), "Not able to help").OnPostAsync(default);

        var delta = db.SyncDeltas.Single();
        var queue = new SyncDeltaQueueService(db);
        var result = await queue.RejectAsync(delta.Id, "olivia@example.test", "Spoke to volunteer");

        Assert.False(result.Applied);
        var row = db.VolunteerDayAvailabilities.Single();
        Assert.Equal(VolunteerAvailabilityLevel.Full, row.Level); // unchanged
    }
}
