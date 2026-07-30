using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Resources;
using CommunityHub.Forms.Steps;
using CommunityHub.Notify;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §434 — the hotel calendar invitation must read honestly in BOTH states.
///
/// <para>Operator 2026-07-27: <i>"this calendar invite must have different subject to 'ELDK27 Hotel
/// Reservation' - and then it must include address to hotel once it is ready and assigned to
/// person"</i>, then <i>"it just says 'your assigned hotel' but it should state something different
/// when its not assigned yet"</i>.</para>
///
/// <para><b>What was wrong.</b> One string served both states. With no hotel assigned the invite
/// went out with the subject <c>"ELDK27 Hotel — your assigned hotel"</c> and a LOCATION field
/// reading <c>"your assigned hotel"</c>. In a calendar that is not a placeholder — the entry looks
/// like it names a hotel and the location looks filled in, so nothing tells you the organizers have
/// not picked one. Worse, it is the field a phone offers to navigate you to.</para>
/// </summary>
public sealed class HotelInviteWordingTests
{
    private const int EventId = 501;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-07-27T10:00:00Z");
    }

    /// <summary>Captures what the invite WOULD have carried, without sending anything.</summary>
    private sealed class SpyEmailSender : IEmailSender
    {
        public string? Subject; public string? Ics;
        public Task SendAsync(string to, string subject, string html, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendAsync(string to, string subject, string html, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendAsync(string to, string subject, string html, string textBody, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendWithIcsAsync(string to, string subject, string html, string ics,
            string fileName, CancellationToken ct = default)
        {
            Subject = subject; Ics = ics;
            return Task.CompletedTask;
        }
        public Task SendWithAttachmentsAsync(string to, string subject, string html,
            IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"hotelinvite-{Guid.NewGuid():N}").Options);

    private static IStringLocalizer<SharedResource> Loc()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions { ResourcesPath = "" }), NullLoggerFactory.Instance);
        return new StringLocalizer<SharedResource>(factory);
    }

    private static async Task<(HotelFormService Svc, SpyEmailSender Spy, int Pid)> BuildAsync(
        CommunityHubDbContext db, Hotel? assigned)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "ELDK27", Code = "ELDK27",
            IsActive = true, CalendarSyncEnabled = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        if (assigned is not null) db.Hotels.Add(assigned);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = EventId, Email = "s@x.dk", FullName = "Test Speaker",
            Role = ParticipantRole.Speaker, HotelId = assigned?.Id,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        db.HotelBookings.Add(new HotelBooking
        {
            EventId = EventId, ParticipantId = p.Id, NeedsRoom = true,
            CheckInDate = new DateOnly(2027, 2, 9), CheckOutDate = new DateOnly(2027, 2, 10),
        });
        await db.SaveChangesAsync();

        var spy = new SpyEmailSender();
        var clock = new FixedClock();
        var invite = new CalendarInviteEmailService(
            db, spy, new EmailContextAccessor(), clock,
            Options.Create(new EmailOptions { FromAddress = "info@x.dk", FromDisplayName = "X", EventCode = "ELDK27" }));

        var svc = new HotelFormService(
            db, clock,
            new OrganizerActionItemService(db, clock), Loc(),
            NullLogger<HotelFormService>.Instance, invite);

        return (svc, spy, p.Id);
    }

    [Fact]
    public async Task With_no_hotel_assigned_it_never_claims_there_is_one()
    {
        using var db = NewDb();
        var (svc, spy, pid) = await BuildAsync(db, assigned: null);

        var (ok, _) = await svc.SendInviteEmailAsync(EventId, pid, default);
        Assert.True(ok);

        // THE reported wording. It must appear in neither the subject nor the location.
        Assert.DoesNotContain("your assigned hotel", spy.Subject!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("your assigned hotel", spy.Ics!, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("ELDK27 Hotel Reservation", spy.Subject);
        Assert.Contains("not assigned yet", spy.Ics!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Once_assigned_the_subject_names_the_hotel_and_the_location_carries_the_address()
    {
        using var db = NewDb();
        var (svc, spy, pid) = await BuildAsync(db, assigned: new Hotel
        {
            EventId = EventId, Name = "AC Hotel Bella Sky",
            Address = "Center Boulevard 5, 2300 København S",
        });

        var (ok, _) = await svc.SendInviteEmailAsync(EventId, pid, default);
        Assert.True(ok);

        Assert.Equal("ELDK27 Hotel Reservation — AC Hotel Bella Sky", spy.Subject);
        // The address is what a phone routes you from, so it has to reach the LOCATION field.
        Assert.Contains("Center Boulevard 5", spy.Ics!);
        Assert.Contains("AC Hotel Bella Sky", spy.Ics!);
        Assert.DoesNotContain("not assigned yet", spy.Ics!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Both_states_share_one_uid_so_assigning_a_hotel_updates_the_same_entry()
    {
        // This is what makes "this calendar entry updates itself" true rather than a promise:
        // the re-send after assignment must land on the entry already in the person's calendar,
        // not add a second hotel booking beside it.
        using var dbA = NewDb();
        var (svcA, spyA, pidA) = await BuildAsync(dbA, assigned: null);
        await svcA.SendInviteEmailAsync(EventId, pidA, default);

        using var dbB = NewDb();
        var (svcB, spyB, pidB) = await BuildAsync(dbB, assigned: new Hotel
        {
            EventId = EventId, Name = "AC Hotel Bella Sky", Address = "Center Boulevard 5",
        });
        await svcB.SendInviteEmailAsync(EventId, pidB, default);

        Assert.Equal(UidOf(spyA.Ics!), UidOf(spyB.Ics!));
    }

    private static string UidOf(string ics) =>
        ics.Split('\n').First(l => l.StartsWith("UID:", StringComparison.Ordinal)).Trim();
}
