using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.4 — the daily run: publish what changed, mail on the schedule, and never mail an external
/// contact before the operator has approved the reports.
/// </summary>
/// <remarks>
/// <para>🔒 The three things that would be expensive to get wrong, each with a test: a hotel mailed
/// every day, an external recipient mailed before approval, and a run that stops at the first
/// failure so the venue never gets the other ten files.</para>
///
/// <para>NO real names — Ada / Grace only.</para>
/// </remarks>
public sealed class LogisticsRunServiceTests
{
    private const int EventId = 1;
    private static readonly DateOnly EventStart = new(2027, 2, 10);
    private static readonly DateOnly PreDay = new(2027, 2, 9);

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class CapturingSender : IEmailSender
    {
        public List<(string To, string Subject, IReadOnlyCollection<EmailAttachment> Files)> Sent { get; } = new();
        public Exception? Throws { get; set; }

        public Task SendAsync(string to, string subject, string html, CancellationToken ct = default) =>
            SendWithAttachmentsAsync(to, subject, html, [], ct);

        public Task SendAsync(string to, string subject, string html,
            IReadOnlyCollection<string>? cc, CancellationToken ct = default) =>
            SendWithAttachmentsAsync(to, subject, html, [], ct);

        public Task SendAsync(string to, string subject, string html, string text,
            CancellationToken ct = default) =>
            SendWithAttachmentsAsync(to, subject, html, [], ct);

        public Task SendAsync(string to, string subject, string html, string text,
            IReadOnlyCollection<string>? cc, CancellationToken ct = default) =>
            SendWithAttachmentsAsync(to, subject, html, [], ct);

        public Task SendWithIcsAsync(string to, string subject, string html, string ics,
            string icsFileName, CancellationToken ct = default) =>
            SendWithAttachmentsAsync(to, subject, html, [], ct);

        public Task SendWithAttachmentsAsync(
            string to, string subject, string html,
            IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default)
        {
            if (Throws is not null) throw Throws;
            Sent.Add((to, subject, attachments));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStore : ISharePointFileStore
    {
        private readonly Dictionary<string, Dictionary<string, byte[]>> _folders = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Uploads { get; } = new();
        public string? FailUploadsFor { get; set; }

        public bool CanStore => true;
        public bool CanRead => true;

        // §6.3 — the fake hands back a WebUrl because the real Graph store does (both on upload and
        // in a listing). A fake that returns "" would let a change that never records a link pass,
        // and the organizer page would show every file as unopenable.
        private static string UrlFor(string folder, string name) =>
            $"https://sp.test/{folder}/{name}";

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string folder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                _folders.TryGetValue(folder, out var f)
                    ? f.Keys.Select(n => new SharePointFileRef($"{folder}|{n}", n, UrlFor(folder, n))).ToList()
                    : []);

        public Task<StoredFile> UploadToFolderAsync(
            string folder, string fileName, byte[] content, string contentType, CancellationToken ct = default)
        {
            if (FailUploadsFor is not null && fileName.Contains(FailUploadsFor, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Graph said no");

            Uploads.Add(fileName);
            if (!_folders.TryGetValue(folder, out var f)) _folders[folder] = f = new(StringComparer.OrdinalIgnoreCase);
            f[fileName] = content;
            return Task.FromResult(
                new StoredFile($"{folder}/{fileName}", UrlFor(folder, fileName), "id"));
        }

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);
        public Task<StoredFile> StoreAsync(string p, byte[] c, string t, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteFromFolderAsync(string f, string n, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyPurchases : ISponsorPurchaseSummary
    {
        public Task<SponsorPurchaseSummary> ByCategoryAsync(string c, CancellationToken ct = default) =>
            Task.FromResult(new SponsorPurchaseSummary([], false));
        public Task<SponsorPurchaseSummary> ByProductAsync(long id, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static CommunityHubDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase(name).Options);

    private static async Task SeedAsync(CommunityHubDbContext db, DateOnly? preDay = null)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "ELDK 2027",
            StartDate = EventStart, EndDate = EventStart, PreDayDate = preDay ?? PreDay, IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> AddHotelGuestAsync(
        CommunityHubDbContext db, string hotelName, string guest, string? contact = "hotel@example.test")
    {
        var h = await db.Hotels.FirstOrDefaultAsync(x => x.Name == hotelName);
        if (h is null)
        {
            h = new Hotel { EventId = EventId, Name = hotelName, ContactEmail = contact };
            db.Hotels.Add(h);
            await db.SaveChangesAsync();
        }

        var p = new Participant
        {
            EventId = EventId, FullName = guest, Email = $"{guest.Replace(' ', '.')}@example.test",
            Role = ParticipantRole.Speaker, IsActive = true, HotelId = h.Id,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        db.HotelBookings.Add(new HotelBooking
        {
            EventId = EventId, ParticipantId = p.Id, NeedsRoom = true,
            CheckInDate = PreDay, CheckOutDate = EventStart,
        });
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static (LogisticsRunService Svc, CapturingSender Mail, FakeStore Store, LogisticsRecipients Rec)
        NewService(CommunityHubDbContext db, MovableClock clock, LogisticsRecipients? recipients = null)
    {
        var paths = TestDocLibrary.Resolver();
        var store = new FakeStore();
        var mail = new CapturingSender();
        var rec = recipients ?? new LogisticsRecipients();

        var svc = new LogisticsRunService(
            db,
            new DocLibraryFilePublisher(store, paths),
            new SwagLogisticsProducer(db),
            new FoodLogisticsProducer(db),
            new LunchLogisticsProducer(db),
            new ExpoLogisticsProducer(new EmptyPurchases()),
            new HotelLogisticsProducer(db),
            rec,
            mail,
            clock);

        return (svc, mail, store, rec);
    }

    private static MovableClock ClockDaysBefore(int days) =>
        new(new DateTimeOffset(PreDay.AddDays(-days).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

    /// <summary>
    /// Someone who needs a room and is placed in NO hotel is in nobody's rooming list — so the run
    /// has to name them, or they are invisible.
    /// </summary>
    /// <remarks>
    /// 🔒 §774 — this is the gap a correct run cannot show. Every file was produced correctly and
    /// the person is in none of them, so the run looks like a success and the empty hotel folder
    /// reads as "no hotel work to do". Found by audit: <c>UnplacedAsync</c> existed, was tested,
    /// and was called by nothing in production.
    /// </remarks>
    [Fact]
    public async Task Someone_who_needs_a_room_but_is_placed_in_no_hotel_is_NAMED_by_the_run()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);

        // Grace is placed in a hotel; Ada asked for a room and was placed nowhere.
        await AddHotelGuestAsync(db, "Hotel One", "Grace Hopper");

        var ada = new Participant
        {
            EventId = EventId, FullName = "Ada Lovelace", Email = "ada@example.test",
            Role = ParticipantRole.Speaker, IsActive = true, HotelId = null,
        };
        db.Participants.Add(ada);
        await db.SaveChangesAsync();
        db.HotelBookings.Add(new HotelBooking
        {
            EventId = EventId, ParticipantId = ada.Id, NeedsRoom = true,
            CheckInDate = PreDay, CheckOutDate = EventStart,
        });
        await db.SaveChangesAsync();

        var (svc, _, _, _) = NewService(db, ClockDaysBefore(60));

        var result = await svc.RunAsync(EventId);

        // The run SUCCEEDED — that is the whole point. Ada is not a failure or a skip.
        Assert.True(result.Ran);
        Assert.Empty(result.Failures);

        Assert.Equal(["Ada Lovelace"], result.Unplaced);
        // Grace has a hotel, so she is in a file and must NOT be reported as a gap.
        Assert.DoesNotContain("Grace Hopper", result.Unplaced);
    }

    /// <summary>Nobody unplaced ⇒ nothing reported. A gap report that always fires is noise.</summary>
    [Fact]
    public async Task Everybody_placed_means_the_run_reports_no_gap()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);
        await AddHotelGuestAsync(db, "Hotel One", "Grace Hopper");

        var (svc, _, _, _) = NewService(db, ClockDaysBefore(60));

        var result = await svc.RunAsync(EventId);

        Assert.Empty(result.Unplaced);
    }

    // =====================================================================
    //  §6.3 — what the organizer page reads
    // =====================================================================

    /// <summary>
    /// Every published file records a headline figure and a link, or the §6.3 page has a row it can
    /// neither explain nor open.
    /// </summary>
    [Fact]
    public async Task A_published_file_records_its_headline_figure_and_its_link()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);
        await AddHotelGuestAsync(db, "Hotel One", "Grace Hopper");
        var (svc, _, _, _) = NewService(db, ClockDaysBefore(60));

        await svc.RunAsync(EventId);

        var states = await db.LogisticsFileStates.ToListAsync();
        Assert.NotEmpty(states);
        Assert.All(states, s => Assert.False(string.IsNullOrWhiteSpace(s.Headline)));
        Assert.All(states, s => Assert.False(string.IsNullOrWhiteSpace(s.WebUrl)));

        // The rooming list counts GUESTS, and the one guest seeded is the one reported.
        var hotel = states.Single(s => s.FileName.Contains("hotel", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("1 guest", hotel.Headline);
    }

    /// <summary>
    /// 🔒 A run that changed nothing still records that it RAN. "Not run since Friday" and "ran and
    /// had nothing to do" need opposite responses from the organizer, and a page that only records
    /// eventful runs shows them identically.
    /// </summary>
    [Fact]
    public async Task Every_run_records_its_status_including_the_one_that_changed_nothing()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);
        var clock = ClockDaysBefore(60);
        var (svc, _, _, _) = NewService(db, clock);

        await svc.RunAsync(EventId);
        var first = await db.LogisticsRunSummaries.SingleAsync();
        Assert.True(first.Ok);
        Assert.True(first.Published > 0);

        clock.Advance(TimeSpan.FromDays(1));
        await svc.RunAsync(EventId);

        // Still ONE row — a status, not a history — and it moved to the second run.
        var second = await db.LogisticsRunSummaries.SingleAsync();
        Assert.Equal(clock.GetUtcNow(), second.RanAt);
        Assert.Equal(0, second.Published);
        Assert.True(second.Unchanged > 0);
        Assert.True(second.Ok);
    }

    /// <summary>An unplaced room-needer reaches the page's problem list, not just the log.</summary>
    [Fact]
    public async Task The_run_status_names_the_people_awaiting_a_hotel()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);

        var ada = new Participant
        {
            EventId = EventId, FullName = "Ada Lovelace", Email = "ada@example.test",
            Role = ParticipantRole.Speaker, IsActive = true, HotelId = null,
        };
        db.Participants.Add(ada);
        await db.SaveChangesAsync();
        db.HotelBookings.Add(new HotelBooking
        {
            EventId = EventId, ParticipantId = ada.Id, NeedsRoom = true,
            CheckInDate = PreDay, CheckOutDate = EventStart,
        });
        await db.SaveChangesAsync();

        var (svc, _, _, _) = NewService(db, ClockDaysBefore(60));
        await svc.RunAsync(EventId);

        var summary = await db.LogisticsRunSummaries.SingleAsync();
        Assert.Contains("Ada Lovelace", summary.Problems);
        // 🔒 Still a healthy run. Colouring an unplaced guest as a FAILURE would train the organizer
        // to ignore the colour that means the job is broken.
        Assert.True(summary.Ok);
    }

    /// <summary>
    /// "Generate now" rewrites the file even though nothing changed — that is what the button is
    /// for: repairing a file somebody edited or deleted by hand.
    /// </summary>
    [Fact]
    public async Task Generate_now_rewrites_an_unchanged_file()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);
        var (svc, _, store, _) = NewService(db, ClockDaysBefore(60));

        await svc.RunAsync(EventId);
        var target = (await db.LogisticsFileStates.FirstAsync()).FileName;
        var before = store.Uploads.Count;

        var result = await svc.RegenerateOneAsync(EventId, target);

        Assert.True(result.Ok);
        Assert.Equal(before + 1, store.Uploads.Count);
        Assert.False(string.IsNullOrWhiteSpace(result.Headline));
    }

    /// <summary>
    /// 🔒 A forced regenerate NEVER mails — not on the spot, and not by making the next run think
    /// the data changed. The button must not be a way to reach an external hotel contact.
    /// </summary>
    [Fact]
    public async Task Generate_now_sends_NOTHING_and_does_not_trigger_the_next_on_change_mail()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);
        await AddHotelGuestAsync(db, "Hotel One", "Grace Hopper");

        // Inside the 3-week window, so the hotel file is on the ON-CHANGE schedule.
        var clock = ClockDaysBefore(5);
        var (svc, mail, _, _) = NewService(db, clock);

        await svc.RunAsync(EventId);
        var sentAfterFirstRun = mail.Sent.Count;

        var hotelFile = (await db.LogisticsFileStates
            .FirstAsync(s => s.FileName.Contains("hotel"))).FileName;

        var result = await svc.RegenerateOneAsync(EventId, hotelFile);
        Assert.True(result.Ok);
        Assert.Equal(sentAfterFirstRun, mail.Sent.Count);      // the button itself sent nothing

        clock.Advance(TimeSpan.FromDays(1));
        await svc.RunAsync(EventId);

        // ...and the next run does not mail the hotel a rooming list identical to the one it has.
        Assert.Equal(sentAfterFirstRun, mail.Sent.Count);
    }

    /// <summary>
    /// Asking for a file this edition does not produce is answered honestly, not with a cheerful
    /// "regenerated" about a file that is not coming back.
    /// </summary>
    [Fact]
    public async Task Generate_now_refuses_a_file_the_producers_do_not_build()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);
        var (svc, _, store, _) = NewService(db, ClockDaysBefore(60));

        var result = await svc.RegenerateOneAsync(EventId, "eldk27-not-a-real-file.xlsx");

        Assert.False(result.Ok);
        Assert.Contains("not one of the files", result.Error);
        Assert.Empty(store.Uploads);
    }

    [Fact]
    public async Task A_first_run_publishes_the_files_and_remembers_their_keys()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);
        var (svc, _, store, _) = NewService(db, ClockDaysBefore(60));

        var result = await svc.RunAsync(EventId);

        Assert.True(result.Ran);
        Assert.True(result.Published > 0);
        Assert.NotEmpty(store.Uploads);
        Assert.Equal(result.Published, await db.LogisticsFileStates.CountAsync());
    }

    /// <summary>
    /// 🔒 THE IDEMPOTENCY THE WHOLE SCHEDULE RESTS ON. A second run over unchanged data rewrites
    /// nothing — otherwise the "on change" hotel mail fires daily.
    /// </summary>
    [Fact]
    public async Task A_second_run_over_unchanged_data_writes_NOTHING()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);
        var clock = ClockDaysBefore(60);
        var (svc, _, store, _) = NewService(db, clock);

        await svc.RunAsync(EventId);
        var firstUploads = store.Uploads.Count;

        clock.Advance(TimeSpan.FromDays(1));
        var second = await svc.RunAsync(EventId);

        Assert.Equal(firstUploads, store.Uploads.Count);   // no new writes
        Assert.Equal(0, second.Published);
        Assert.True(second.Unchanged > 0);
    }

    /// <summary>
    /// 🔒 §770.4 — nothing reaches an external address until he approves the reports.
    /// </summary>
    [Fact]
    public async Task Every_mail_goes_to_the_REVIEW_mailbox_while_the_reports_are_unapproved()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);
        await AddHotelGuestAsync(db, "Scandic Copenhagen", "Ada Lovelace");

        var rec = new LogisticsRecipients
        {
            VenueOperations = "venue@example.test",
            LogisticsOrganizer = "logistics@example.test",
            // ApprovedForRealRecipients stays FALSE — the shipped default.
        };
        var (svc, mail, _, _) = NewService(db, ClockDaysBefore(5), rec);

        await svc.RunAsync(EventId);

        Assert.NotEmpty(mail.Sent);
        Assert.All(mail.Sent, m => Assert.Equal("mok@expertslive.dk", m.To));
        Assert.DoesNotContain(mail.Sent, m => m.To.Contains("venue@") || m.To.Contains("hotel@"));
    }

    /// <summary>
    /// 🔒 A hotel hears from us only when its OWN list moved. A weekly identical rooming list is how
    /// a venue learns to ignore CEH.
    /// </summary>
    [Fact]
    public async Task A_hotel_is_mailed_when_its_list_CHANGES_and_not_again_when_it_does_not()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);
        await AddHotelGuestAsync(db, "Scandic Copenhagen", "Ada Lovelace");

        var clock = ClockDaysBefore(5);      // inside the 3-week window
        var (svc, mail, _, _) = NewService(db, clock);

        await svc.RunAsync(EventId);
        var afterFirst = mail.Sent.Count(m => m.Files.Any(f => f.FileName.Contains("hotel")));
        Assert.Equal(1, afterFirst);

        clock.Advance(TimeSpan.FromDays(1));
        await svc.RunAsync(EventId);
        Assert.Equal(afterFirst, mail.Sent.Count(m => m.Files.Any(f => f.FileName.Contains("hotel"))));

        // A new guest arrives ⇒ that hotel's list moved ⇒ exactly one more mail.
        await AddHotelGuestAsync(db, "Scandic Copenhagen", "Grace Hopper");
        clock.Advance(TimeSpan.FromDays(1));
        await svc.RunAsync(EventId);
        Assert.Equal(afterFirst + 1, mail.Sent.Count(m => m.Files.Any(f => f.FileName.Contains("hotel"))));
    }

    /// <summary>
    /// 🔒 §6.4: the hotel mail starts three weeks out. Before that a rooming list is still churning,
    /// and a hotel does not want a mail every time a speaker is placed.
    /// </summary>
    [Fact]
    public async Task A_hotel_is_NOT_mailed_before_the_three_week_window_opens()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);
        await AddHotelGuestAsync(db, "Scandic Copenhagen", "Ada Lovelace");

        var (svc, mail, _, _) = NewService(db, ClockDaysBefore(40));   // outside the window

        await svc.RunAsync(EventId);

        Assert.DoesNotContain(mail.Sent, m => m.Files.Any(f => f.FileName.Contains("hotel")));
    }

    [Fact]
    public async Task The_weekly_files_are_mailed_once_a_week_not_once_a_day()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);
        var clock = ClockDaysBefore(60);
        var (svc, mail, _, _) = NewService(db, clock);

        await svc.RunAsync(EventId);
        var first = mail.Sent.Count;
        Assert.True(first > 0);

        clock.Advance(TimeSpan.FromDays(3));
        await svc.RunAsync(EventId);
        Assert.Equal(first, mail.Sent.Count);          // still inside the week

        clock.Advance(TimeSpan.FromDays(5));
        await svc.RunAsync(EventId);
        Assert.True(mail.Sent.Count > first);          // a week has passed
    }

    /// <summary>
    /// ⚠️ One file's failure must not stop the run — the other ten still have to reach the venue.
    /// </summary>
    [Fact]
    public async Task ONE_file_failing_does_not_stop_the_others()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);
        var (svc, _, store, _) = NewService(db, ClockDaysBefore(60));
        store.FailUploadsFor = "award";

        var result = await svc.RunAsync(EventId);

        Assert.True(result.Ran);
        Assert.True(result.Published > 0);
        Assert.Contains(result.Failures, f => f.Contains("award") && f.Contains("Graph said no"));
    }

    /// <summary>
    /// A publish that succeeded but whose MAIL failed is reported, and the state keeps its old
    /// stamp so the next run tries again rather than treating it as sent.
    /// </summary>
    [Fact]
    public async Task A_failed_MAIL_is_reported_and_retried_next_run()
    {
        var name = $"run-{Guid.NewGuid():N}";
        using var db = NewDb(name);
        await SeedAsync(db);
        var clock = ClockDaysBefore(60);
        var (svc, mail, _, _) = NewService(db, clock);
        mail.Throws = new InvalidOperationException("SMTP refused");

        var first = await svc.RunAsync(EventId);
        Assert.Contains(first.Failures, f => f.Contains("mail failed"));
        Assert.All(await db.LogisticsFileStates.ToListAsync(), s => Assert.Null(s.LastMailedAt));

        mail.Throws = null;
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await svc.RunAsync(EventId);
        Assert.True(second.Mailed > 0);      // retried, not written off as sent
    }
}
