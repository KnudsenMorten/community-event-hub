using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Entitlements;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1386 — hotel logistics refinement: priority placement, Reserved / Confirmed / Cancelled, the
/// per-night overview with forecast, the Excel 97-2003 sheet + the import of the hotel's returned copy,
/// the change log + hotel-ops mail, deadline warnings and the apply-once contract seed.
/// FAKE names and domains only (public-mirror safety).
/// </summary>
public sealed class HotelLogisticsRefinementTests
{
    private const int EventId = 1;
    private static readonly DateOnly N8 = new(2027, 2, 8);
    private static readonly DateOnly N9 = new(2027, 2, 9);
    private static readonly DateOnly N10 = new(2027, 2, 10);
    private const string Review = "ops@crew.example";

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-08T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Mail : IEmailSender
    {
        public List<(string To, string Subject, int Attachments)> Sent { get; } = new();
        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
        { Sent.Add((toEmail, subject, 0)); return Task.CompletedTask; }
        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken cancellationToken = default)
            => SendAsync(toEmail, subject, htmlBody, cancellationToken);
        public Task SendAsync(string toEmail, string subject, string htmlBody, EmailReplyTo? replyTo, CancellationToken cancellationToken = default)
            => SendAsync(toEmail, subject, htmlBody, cancellationToken);
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default)
            => SendAsync(toEmail, subject, htmlBody, cancellationToken);
        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody, string icsContent, string icsFileName, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not used");
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken cancellationToken = default)
        { Sent.Add((toEmail, subject, attachments.Count)); return Task.CompletedTask; }
    }

    private sealed class World
    {
        public required CommunityHubDbContext Db { get; init; }
        public required Clock Clock { get; init; }
        public required Mail Mail { get; init; }
        public required HotelChangeLogService Log { get; init; }
        public required HotelAllotmentService Allotments { get; init; }
        public required HotelPlacementService Placement { get; init; }
        public required HotelOverviewService Overview { get; init; }
        public required HotelAgreementSheet Sheet { get; init; }
        public required HotelSheetImportService Import { get; init; }
        public required HotelOpsNotifier Notifier { get; init; }
        public required HotelContractSeeder Seeder { get; init; }
        public int MainId { get; set; }
        public int BufferId { get; set; }
    }

    private static async Task<World> NewWorldAsync()
    {
        var db = new CommunityHubDbContext(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"h1386-{Guid.NewGuid():N}").Options);
        var clock = new Clock();
        var mail = new Mail();
        var log = new HotelChangeLogService(db, clock);
        var allot = new HotelAllotmentService(db, clock, log);
        var placement = new HotelPlacementService(db, clock, log);
        var overview = new HotelOverviewService(db);
        var sheet = new HotelAgreementSheet(db, new HotelCoverageService(db, clock));
        var w = new World
        {
            Db = db, Clock = clock, Mail = mail, Log = log, Allotments = allot, Placement = placement,
            Overview = overview, Sheet = sheet,
            Import = new HotelSheetImportService(db, clock, log),
            Notifier = new HotelOpsNotifier(db, clock, mail, sheet, overview, allot, log,
                new LogisticsRecipients { ReviewMailbox = Review }),
            Seeder = new HotelContractSeeder(db, allot, placement, log, clock),
        };

        db.Events.Add(new Event
        {
            Id = EventId, Code = "H27", CommunityName = "H", DisplayName = "Hotel Test 2027",
            StartDate = N9, EndDate = N10, IsActive = true,
        });
        var main = new Hotel
        {
            EventId = EventId, Name = "Hotel Alpha", Priority = 1, AgreementNumber = "1000001",
            AgreementLabel = "TEST 2027", ContactEmail = "rooms@hotel.example",
            NotifyEmails = "ops@crew.example, lead@crew.example, rooms@hotel.example, someone@elsewhere.example",
        };
        var buffer = new Hotel { EventId = EventId, Name = "Hotel Alpha (buffer)", Priority = 2 };
        db.Hotels.AddRange(main, buffer);
        await db.SaveChangesAsync();
        w.MainId = main.Id;
        w.BufferId = buffer.Id;
        return w;
    }

    private static async Task<Participant> PersonAsync(World w, string name, ParticipantRole role = ParticipantRole.Volunteer,
        DateOnly? inD = null, DateOnly? outD = null, bool needs = true, int? hotelId = null, bool booking = true)
    {
        var p = new Participant
        {
            EventId = EventId, FullName = name, Email = $"{name.Replace(' ', '.').ToLowerInvariant()}@guest.example",
            Role = role, IsActive = true, LifecycleState = ParticipantLifecycleState.Active, HotelId = hotelId,
        };
        w.Db.Participants.Add(p);
        await w.Db.SaveChangesAsync();
        if (booking)
        {
            w.Db.HotelBookings.Add(new HotelBooking
            {
                EventId = EventId, ParticipantId = p.Id, NeedsRoom = needs,
                CheckInDate = needs ? inD ?? N8 : null, CheckOutDate = needs ? outD ?? N10 : null,
            });
            await w.Db.SaveChangesAsync();
        }
        return p;
    }

    // ---- placement + state ------------------------------------------------------------------

    [Fact]
    public async Task A_new_reservation_is_placed_in_the_priority_1_hotel_and_logged()
    {
        var w = await NewWorldAsync();
        var p = await PersonAsync(w, "Ann Able");

        await w.Placement.OnParticipantSavedAsync(EventId, p.Id, HotelBookingSnapshot.None, null);

        Assert.Equal(w.MainId, (await w.Db.Participants.FindAsync(p.Id))!.HotelId);
        var kinds = await w.Db.HotelChangeLog.Select(e => e.Kind).ToListAsync();
        Assert.Contains(HotelChangeKinds.BookingCreated, kinds);
        Assert.Contains(HotelChangeKinds.Placed, kinds);
    }

    [Fact]
    public async Task Bulk_placement_places_only_reserved_unassigned_active_non_sponsors()
    {
        var w = await NewWorldAsync();
        var a = await PersonAsync(w, "Ann Able");
        var s = await PersonAsync(w, "Sam Sponsor", ParticipantRole.Sponsor);
        var n = await PersonAsync(w, "Ned None", needs: false);
        var already = await PersonAsync(w, "Bea Buffer", hotelId: w.BufferId);

        var placed = await w.Placement.PlaceUnassignedInPrimaryAsync(EventId, "org@crew.example");

        Assert.Equal(1, placed);
        Assert.Equal(w.MainId, (await w.Db.Participants.FindAsync(a.Id))!.HotelId);
        Assert.Null((await w.Db.Participants.FindAsync(s.Id))!.HotelId);
        Assert.Null((await w.Db.Participants.FindAsync(n.Id))!.HotelId);
        Assert.Equal(w.BufferId, (await w.Db.Participants.FindAsync(already.Id))!.HotelId);
    }

    [Fact]
    public async Task Dropping_the_room_cancels_the_reservation_and_keeps_it_on_the_hotel_sheet()
    {
        var w = await NewWorldAsync();
        var p = await PersonAsync(w, "Ann Able");
        p.HotelId = w.MainId;
        var b = await w.Db.HotelBookings.SingleAsync(x => x.ParticipantId == p.Id);
        var before = HotelBookingSnapshot.Of(b);
        b.NeedsRoom = false; b.CheckInDate = null; b.CheckOutDate = null;
        await w.Db.SaveChangesAsync();

        await w.Placement.OnParticipantSavedAsync(EventId, p.Id, before, null);

        b = await w.Db.HotelBookings.SingleAsync(x => x.ParticipantId == p.Id);
        Assert.Equal(HotelConfirmationState.Cancelled, b.ConfirmationState);
        Assert.Equal(w.MainId, b.CancelledHotelId);
        var rows = await w.Sheet.RowsAsync(EventId, w.MainId);
        Assert.Equal(HotelConfirmationState.Cancelled, Assert.Single(rows).State);
        // …and it is no longer demand.
        var ov = await w.Overview.BuildAsync(EventId, includeNotAnswered: false);
        Assert.All(ov.Nights, n => Assert.Equal(0, n.Reserved + n.Confirmed));
    }

    [Fact]
    public async Task Needing_the_room_again_makes_it_Reserved_and_clears_the_old_numbers()
    {
        var w = await NewWorldAsync();
        var p = await PersonAsync(w, "Ann Able");
        p.HotelId = w.MainId;
        p.HotelConfirmationNumber = "C-1";
        var b = await w.Db.HotelBookings.SingleAsync(x => x.ParticipantId == p.Id);
        b.ConfirmationState = HotelConfirmationState.Cancelled;
        b.ConfirmationNumber = "C-1";
        b.CancellationNumber = "X-1";
        await w.Db.SaveChangesAsync();

        await w.Placement.OnParticipantSavedAsync(EventId, p.Id,
            new HotelBookingSnapshot(true, false, null, null, null), null);

        b = await w.Db.HotelBookings.SingleAsync(x => x.ParticipantId == p.Id);
        Assert.Equal(HotelConfirmationState.NotConfirmed, b.ConfirmationState);
        Assert.Null(b.ConfirmationNumber);
        Assert.Null(b.CancellationNumber);
        Assert.Null((await w.Db.Participants.FindAsync(p.Id))!.HotelConfirmationNumber);
    }

    [Fact]
    public async Task Deactivating_a_placed_guest_cancels_the_room_at_that_hotel()
    {
        var w = await NewWorldAsync();
        var p = await PersonAsync(w, "Ann Able");
        p.HotelId = w.MainId;
        await w.Db.SaveChangesAsync();

        var svc = new ParticipantDeactivationService(w.Db, w.Clock, new CommunityHub.Core.Audit.AuditTrailService(w.Db, w.Clock));
        await svc.DeactivateAsync(EventId, p.Id, "test");

        var b = await w.Db.HotelBookings.SingleAsync(x => x.ParticipantId == p.Id);
        Assert.Equal(HotelConfirmationState.Cancelled, b.ConfirmationState);
        Assert.Equal(w.MainId, b.CancelledHotelId);
        Assert.Contains(await w.Db.HotelChangeLog.ToListAsync(),
            e => e.Kind == HotelChangeKinds.BookingCancelled && e.HotelId == w.MainId && e.AffectsGuestList);
    }

    // ---- overview + forecast ------------------------------------------------------------------

    [Fact]
    public async Task Overview_counts_allocated_reserved_confirmed_and_forecast_per_night()
    {
        var w = await NewWorldAsync();
        await w.Allotments.SetAllotmentsAsync(EventId, w.MainId, new Dictionary<DateOnly, int?> { [N8] = 2, [N9] = 1 });
        await w.Allotments.SetAllotmentsAsync(EventId, w.BufferId, new Dictionary<DateOnly, int?> { [N9] = 1 });
        var a = await PersonAsync(w, "Ann Able", hotelId: null);                 // 8→10, unplaced
        var c = await PersonAsync(w, "Cid Conf", inD: N9, outD: N10);            // 9→10
        c.HotelId = w.MainId;
        var cb = await w.Db.HotelBookings.SingleAsync(x => x.ParticipantId == c.Id);
        cb.ConfirmationState = HotelConfirmationState.Confirmed;
        await w.Db.SaveChangesAsync();
        await w.Overview.SetForecastAsync(EventId, N9, 3, "3 more speakers", "org@crew.example", w.Log, w.Clock);

        var ov = await w.Overview.BuildAsync(EventId, includeNotAnswered: false);
        var n8 = ov.Nights.Single(n => n.Night == N8);
        var n9 = ov.Nights.Single(n => n.Night == N9);

        Assert.Equal((2, 0, 1, 0, 0), (n8.PrimaryAllocated, n8.BufferAllocated, n8.Reserved, n8.Confirmed, n8.ForecastExtra));
        Assert.Equal(1, n8.Result);                        // 2 held, 1 needed → 1 too many
        Assert.Equal((1, 1, 1, 1, 3), (n9.PrimaryAllocated, n9.BufferAllocated, n9.Reserved, n9.Confirmed, n9.ForecastExtra));
        Assert.Equal(-3, n9.Result);                       // 2 held, 5 needed → gap 3
        Assert.Equal(-4, n9.PrimaryResult);
        Assert.Equal(1, ov.UnplacedPeople);
    }

    [Fact]
    public async Task Not_answered_lists_crew_without_a_booking_and_skips_sponsors_and_attendees()
    {
        var w = await NewWorldAsync();
        await PersonAsync(w, "Vic Volunteer", booking: false);
        await PersonAsync(w, "Sue Sponsor", ParticipantRole.Sponsor, booking: false);
        await PersonAsync(w, "Ari Attendee", ParticipantRole.Attendee, booking: false);
        await PersonAsync(w, "Ann Able");

        var ov = await w.Overview.BuildAsync(EventId);

        Assert.Equal("Vic Volunteer", Assert.Single(ov.NotAnswered).FullName);
    }

    // ---- spreadsheet ------------------------------------------------------------------------

    [Fact]
    public async Task The_sheet_is_a_real_xls_with_the_agreement_title_and_the_twelve_columns_and_round_trips()
    {
        var w = await NewWorldAsync();
        var p = await PersonAsync(w, "Jan Ketil Testsen");
        p.HotelId = w.MainId;
        await w.Db.SaveChangesAsync();

        var file = await w.Sheet.BuildAsync(EventId, w.MainId);

        Assert.NotNull(file);
        Assert.EndsWith(".xls", file!.FileName);
        // BIFF8 / OLE2 compound document signature: D0 CF 11 E0.
        Assert.Equal(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }, file.Content.Take(4).ToArray());

        using var wb = new NPOI.HSSF.UserModel.HSSFWorkbook(new MemoryStream(file.Content));
        var ws = wb.GetSheetAt(0);
        Assert.Equal("Group Agreement number: 1000001 (TEST 2027)", ws.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(HotelAgreementSheet.Columns, Enumerable.Range(0, 12).Select(i => ws.GetRow(2).GetCell(i).StringCellValue).ToArray());
        Assert.Equal("Jan Ketil", ws.GetRow(3).GetCell(0).StringCellValue);
        Assert.Equal("Testsen", ws.GetRow(3).GetCell(1).StringCellValue);
        Assert.Equal("Reserved", ws.GetRow(3).GetCell(11).StringCellValue);

        var parsed = HotelAgreementSheet.Parse(new MemoryStream(file.Content));
        Assert.Equal(p.Email, Assert.Single(parsed).Email);
    }

    [Fact]
    public async Task The_sheet_lists_only_this_hotels_guests()
    {
        var w = await NewWorldAsync();
        var a = await PersonAsync(w, "Ann Able");
        var b = await PersonAsync(w, "Bo Buffer");
        a.HotelId = w.MainId; b.HotelId = w.BufferId;
        await w.Db.SaveChangesAsync();

        Assert.Equal(a.Id, Assert.Single(await w.Sheet.RowsAsync(EventId, w.MainId)).ParticipantId);
    }

    [Fact]
    public void Parse_refuses_a_file_that_is_not_a_spreadsheet()
    {
        Assert.Throws<InvalidDataException>(() => HotelAgreementSheet.Parse(new MemoryStream("not excel"u8.ToArray())));
    }

    // ---- import -----------------------------------------------------------------------------

    [Fact]
    public async Task Import_previews_then_applies_confirmation_and_cancellation_numbers_without_mail()
    {
        var w = await NewWorldAsync();
        var a = await PersonAsync(w, "Ann Able");
        var b = await PersonAsync(w, "Søren Østergaard");
        a.HotelId = w.MainId; b.HotelId = w.MainId;
        await w.Db.SaveChangesAsync();

        var rows = new List<HotelSheetImportRow>
        {
            new(4, "Ann", "Able", a.Email.ToUpperInvariant(), "CONF-1", null, "Reserved"),
            new(5, "Soren", "Ostergaard", "", null, "CANC-9", "Cancelled"),   // matched by name
            new(6, "No", "Body", "nobody@guest.example", "CONF-X", null, null),
        };
        var preview = await w.Import.PreviewAsync(EventId, w.MainId, rows);

        Assert.Equal(2, preview.Count(l => l.HasChange));
        Assert.False(preview.Single(l => l.RowNumber == 6).Matched);
        Assert.Equal("name", preview.Single(l => l.RowNumber == 5).MatchedBy);
        Assert.Empty(await w.Db.HotelChangeLog.ToListAsync());   // preview writes nothing

        var n = await w.Import.ApplyAsync(EventId, w.MainId, preview.Where(l => l.HasChange)
            .Select(l => new HotelImportApproval(l.ParticipantId!.Value, l.NewConfirmation, l.NewCancellation)).ToList(), "org@crew.example");

        Assert.Equal(2, n);
        var ab = await w.Db.HotelBookings.SingleAsync(x => x.ParticipantId == a.Id);
        Assert.Equal((HotelConfirmationState.Confirmed, "CONF-1"), (ab.ConfirmationState, ab.ConfirmationNumber));
        Assert.Equal("CONF-1", (await w.Db.Participants.FindAsync(a.Id))!.HotelConfirmationNumber);
        var bb = await w.Db.HotelBookings.SingleAsync(x => x.ParticipantId == b.Id);
        Assert.Equal((HotelConfirmationState.Cancelled, "CANC-9"), (bb.ConfirmationState, bb.CancellationNumber));
        // Import lines are logged but never mailed.
        Assert.All(await w.Db.HotelChangeLog.ToListAsync(), e => Assert.NotNull(e.NotifiedAt));
        Assert.Empty(w.Mail.Sent);
    }

    [Fact]
    public async Task Import_ignores_an_approval_for_someone_not_at_this_hotel()
    {
        var w = await NewWorldAsync();
        var other = await PersonAsync(w, "Bo Buffer");
        other.HotelId = w.BufferId;
        await w.Db.SaveChangesAsync();

        var n = await w.Import.ApplyAsync(EventId, w.MainId, new[] { new HotelImportApproval(other.Id, "CONF-1", null) }, "org@crew.example");

        Assert.Equal(0, n);
        Assert.Null((await w.Db.HotelBookings.SingleAsync(x => x.ParticipantId == other.Id)).ConfirmationNumber);
    }

    // ---- hotel-ops mail ---------------------------------------------------------------------

    [Fact]
    public async Task Recipients_are_internal_only_never_the_hotel_or_another_domain()
    {
        var w = await NewWorldAsync();
        var main = await w.Db.Hotels.FindAsync(w.MainId);

        Assert.Equal(new[] { "ops@crew.example", "lead@crew.example" }, w.Notifier.RecipientsFor(main));
        Assert.Equal(new[] { Review }, w.Notifier.RecipientsFor(await w.Db.Hotels.FindAsync(w.BufferId)));
    }

    [Fact]
    public async Task Changes_wait_for_the_quiet_period_then_go_out_once_with_the_sheet_attached()
    {
        var w = await NewWorldAsync();
        var p = await PersonAsync(w, "Ann Able");
        await w.Placement.OnParticipantSavedAsync(EventId, p.Id, HotelBookingSnapshot.None, null);

        var first = await w.Notifier.RunAsync(EventId);
        Assert.True(first.Waiting);
        Assert.Empty(w.Mail.Sent);

        w.Clock.Now = w.Clock.Now.AddMinutes(HotelOpsNotifier.QuietMinutes + 1);
        var second = await w.Notifier.RunAsync(EventId);

        Assert.Equal(1, second.ChangeMails);
        Assert.Equal(2, w.Mail.Sent.Count);                                  // ops@ + lead@
        Assert.All(w.Mail.Sent, m => Assert.Equal(1, m.Attachments));
        Assert.DoesNotContain(w.Mail.Sent, m => m.To.Contains("hotel.example") || m.To.Contains("guest.example"));

        var third = await w.Notifier.RunAsync(EventId);
        Assert.Equal(0, third.ChangeMails);
        Assert.Equal(2, w.Mail.Sent.Count);
    }

    [Fact]
    public async Task A_deadline_warning_is_sent_once_per_lead_and_a_late_start_sends_only_the_nearest()
    {
        var w = await NewWorldAsync();
        await w.Allotments.SetAllotmentsAsync(EventId, w.MainId, new Dictionary<DateOnly, int?> { [N9] = 10 });
        await w.Allotments.SaveCutoffAsync(EventId, w.MainId, new DateOnly(2026, 11, 13), "Release 20%", 20, null, default, 14);
        foreach (var e in w.Db.HotelChangeLog) e.NotifiedAt = w.Clock.Now;   // ignore the allocation change mail
        await w.Db.SaveChangesAsync();

        // 5 days before: the 14-day and 7-day points were missed — one warning, not two.
        var r1 = await w.Notifier.RunAsync(EventId);
        Assert.Equal(1, r1.DeadlineWarnings);
        var r2 = await w.Notifier.RunAsync(EventId);
        Assert.Equal(0, r2.DeadlineWarnings);

        w.Clock.Now = w.Clock.Now.AddDays(2);   // 3 days before
        var r3 = await w.Notifier.RunAsync(EventId);
        Assert.Equal(1, r3.DeadlineWarnings);
        Assert.Contains(w.Mail.Sent, m => m.Subject.Contains("Release 20%"));
    }

    [Fact]
    public async Task The_original_reservation_freezes_after_the_first_deadline_and_not_before()
    {
        var w = await NewWorldAsync();
        await w.Allotments.SetAllotmentsAsync(EventId, w.MainId, new Dictionary<DateOnly, int?> { [N9] = 10 });
        await w.Allotments.SaveCutoffAsync(EventId, w.MainId, new DateOnly(2026, 11, 9), "Free cancellation", null, null);

        await w.Notifier.RunAsync(EventId);
        Assert.Null((await w.Db.HotelAllotments.SingleAsync()).OriginalRooms);

        w.Clock.Now = DateTimeOffset.Parse("2026-11-10T10:00:00Z");
        await w.Notifier.RunAsync(EventId);
        Assert.Equal(10, (await w.Db.HotelAllotments.SingleAsync()).OriginalRooms);
    }

    // ---- seed -------------------------------------------------------------------------------

    [Fact]
    public async Task The_contract_seed_applies_once_and_never_overwrites_later_edits()
    {
        var w = await NewWorldAsync();
        var seed = new HotelContractSeed
        {
            Key = "test-1",
            PlaceUnassignedInPrimary = true,
            Hotels =
            {
                new HotelSeedEntry { Name = "Hotel Alpha", Priority = 1, AgreementNumber = "2000002",
                    Allotments = { ["2027-02-09"] = 120 },
                    Cutoffs = { new HotelSeedCutoff { Date = "2026-12-07", Label = "Free cancellation", WarnDaysBefore = 21 } } },
                new HotelSeedEntry { Name = "New Buffer Hotel", Priority = 2, BookingReference = "REF-1",
                    Allotments = { ["2027-02-09"] = 5 } },
            },
        };
        var p = await PersonAsync(w, "Ann Able");

        Assert.NotNull(await w.Seeder.ApplyAsync(EventId, seed));

        Assert.Equal("2000002", (await w.Db.Hotels.FindAsync(w.MainId))!.AgreementNumber);
        Assert.Equal(120, (await w.Db.HotelAllotments.SingleAsync(a => a.HotelId == w.MainId)).RoomsAllotted);
        var created = await w.Db.Hotels.SingleAsync(h => h.Name == "New Buffer Hotel");
        Assert.Equal((2, "REF-1"), (created.Priority, created.BookingReference));
        Assert.Equal(21, (await w.Db.HotelCutoffs.SingleAsync()).WarnDaysBefore);
        Assert.Equal(w.MainId, (await w.Db.Participants.FindAsync(p.Id))!.HotelId);

        // An organizer changes the allocation; the seed must not put it back.
        await w.Allotments.SetAllotmentsAsync(EventId, w.MainId, new Dictionary<DateOnly, int?> { [N9] = 100 });
        Assert.Null(await w.Seeder.ApplyAsync(EventId, seed));
        Assert.Equal(100, (await w.Db.HotelAllotments.SingleAsync(a => a.HotelId == w.MainId)).RoomsAllotted);
    }

    [Fact]
    public void The_shipped_edition_config_carries_a_readable_seed_with_a_priority_1_and_a_priority_2_hotel()
    {
        var path = Path.Combine(RepoRoot(), "config", "hotel.eldk27.json");
        var seed = HotelContractSeeder.ReadSeed(path);

        Assert.NotNull(seed);
        Assert.False(string.IsNullOrWhiteSpace(seed!.Key));
        Assert.Contains(seed.Hotels, h => h.Priority == 1 && h.Allotments.Count > 0 && h.Cutoffs.Count > 0);
        Assert.Contains(seed.Hotels, h => h.Priority == 2 && h.Allotments.Count > 0);
        Assert.All(seed.Hotels.SelectMany(h => h.Cutoffs), c => Assert.True(DateOnly.TryParse(c.Date, out _)));
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CommunityHub.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void Leads_are_the_hotels_own_then_7_3_1()
    {
        Assert.Equal(new[] { 14, 7, 3, 1 }, HotelOpsNotifier.LeadsFor(14));
        Assert.Equal(new[] { 3, 1 }, HotelOpsNotifier.LeadsFor(3));
    }

    [Fact]
    public void Name_split_and_fold()
    {
        Assert.Equal(("Jan Ketil", "Testsen"), HotelAgreementSheet.SplitName("Jan Ketil  Testsen"));
        Assert.Equal(HotelSheetImportService.Fold("Søren Østergaard"), HotelSheetImportService.Fold("soren  ostergaard"));
    }
}
