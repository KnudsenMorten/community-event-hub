using System.Globalization;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;

namespace CommunityHub.Core.Organizer;

/// <summary>§1386 — one guest row on a hotel's spreadsheet.</summary>
public sealed record HotelSheetRow(
    int ParticipantId, string FirstName, string LastName, string Email,
    DateOnly? Arrival, DateOnly? Departure,
    string? ConfirmationNumber, string? CancellationNumber,
    int GuestsInRoom, int Rooms, int EldkPaymentDays, int GuestSelfPayment,
    HotelConfirmationState State);

/// <summary>§1386 — a hotel's spreadsheet, ready to download or attach.</summary>
public sealed record HotelSheetFile(int HotelId, string HotelName, string FileName, byte[] Content, int Rows)
{
    /// <summary>Excel 97-2003.</summary>
    public const string ContentType = "application/vnd.ms-excel";
}

/// <summary>§1386 — one parsed row from the spreadsheet the HOTEL sends back.</summary>
public sealed record HotelSheetImportRow(
    int RowNumber, string FirstName, string LastName, string Email,
    string? ConfirmationNumber, string? CancellationNumber, string? StateText);

/// <summary>
/// §1386 — the hotel's guest spreadsheet in <b>Excel 97-2003 (.xls)</b> (operator 2026-11-08: <i>"we
/// need to be able to generate an excel spreadsheet in 2003 format to hotel AC Bella Sky Copenhagen"</i>),
/// and the parser for the copy the hotel returns with its confirmation and cancellation numbers.
/// </summary>
/// <remarks>
/// <para><b>Layout.</b> Row 1 = the title ("Group Agreement number: … (…)"), row 3 = the column
/// headers, guests from row 4. The column names are the operator's, verbatim — the hotel fills two of
/// them in and sends the file back, so they are also the import's contract.</para>
/// <para><b>Who is on it.</b> Everyone PLACED at this hotel who needs a room (Reserved / Confirmed),
/// plus every CANCELLED reservation that was held at this hotel — the hotel has to see a
/// cancellation to release the room and to return its cancellation number.</para>
/// <para>🔒 One file per hotel (§770.11): a sheet never lists another hotel's guests.</para>
/// </remarks>
public sealed class HotelAgreementSheet
{
    public static readonly string[] Columns =
    {
        "First Name", "Last Name", "Email", "Arrival Date", "Departure Date",
        "Confirmation Number", "Cancellation Number", "Number of Guests in room", "Number of Rooms",
        "ELDK Payment Days", "Guest Self-payment", "Reservation State",
    };

    private readonly CommunityHubDbContext _db;
    private readonly Entitlements.HotelCoverageService _coverage;

    public HotelAgreementSheet(CommunityHubDbContext db, Entitlements.HotelCoverageService coverage)
    {
        _db = db;
        _coverage = coverage;
    }

    /// <summary>The rows for one hotel, ordered by last name.</summary>
    public async Task<IReadOnlyList<HotelSheetRow>> RowsAsync(int eventId, int hotelId, CancellationToken ct = default)
    {
        var raw = await _db.HotelBookings.AsNoTracking()
            .Where(b => b.EventId == eventId
                        && !b.Participant.IsTestUser
                        && (
                            // live reservation placed here
                            (b.Participant.HotelId == hotelId && b.NeedsRoom && b.Participant.IsActive
                             && b.ConfirmationState != HotelConfirmationState.Cancelled)
                            // a cancellation the hotel must hear about
                            || (b.ConfirmationState == HotelConfirmationState.Cancelled
                                && (b.CancelledHotelId == hotelId
                                    || (b.CancelledHotelId == null && b.Participant.HotelId == hotelId)))))
            .Select(b => new
            {
                b.ParticipantId, b.Participant.FullName, b.Participant.Email,
                b.CheckInDate, b.CheckOutDate, b.RoomShareWith,
                b.ConfirmationNumber, b.CancellationNumber, b.ConfirmationState,
                b.GuestsInRoom, b.RoommateParticipantId,
                PersonNumber = b.Participant.HotelConfirmationNumber,
            })
            .ToListAsync(ct);
        if (raw.Count == 0) return Array.Empty<HotelSheetRow>();

        var ids = raw.Select(r => r.ParticipantId).ToList();
        var names = await _db.SpeakerProfiles.AsNoTracking()
            .Where(s => s.EventId == eventId && ids.Contains(s.ParticipantId))
            .Select(s => new { s.ParticipantId, s.FirstName, s.LastName })
            .ToListAsync(ct);
        var nameById = names.GroupBy(n => n.ParticipantId).ToDictionary(g => g.Key, g => g.First());

        var live = raw.Where(r => r.ConfirmationState != HotelConfirmationState.Cancelled).Select(r => r.ParticipantId).ToList();
        var coverage = await _coverage.ResolveManyAsync(eventId, live, ct);

        // §1386 — a shared room is ONE room: the pair's lower id carries "1 room", the other "0"; both default to 2 guests.
        var liveSet = live.ToHashSet();
        var mateOf = new Dictionary<int, int>();
        foreach (var r in raw.Where(r => liveSet.Contains(r.ParticipantId) && r.RoommateParticipantId is int m && liveSet.Contains(m) && m != r.ParticipantId))
        {
            mateOf[r.ParticipantId] = r.RoommateParticipantId!.Value;
            mateOf.TryAdd(r.RoommateParticipantId!.Value, r.ParticipantId);
        }

        return raw.Select(r =>
            {
                var (first, last) = nameById.TryGetValue(r.ParticipantId, out var sp)
                                    && !string.IsNullOrWhiteSpace(sp.FirstName) && !string.IsNullOrWhiteSpace(sp.LastName)
                    ? (sp.FirstName!.Trim(), sp.LastName!.Trim())
                    : SplitName(r.FullName);
                var cancelled = r.ConfirmationState == HotelConfirmationState.Cancelled;
                var cov = !cancelled && coverage.TryGetValue(r.ParticipantId, out var c) ? c.Result : default;
                var confirmation = !string.IsNullOrWhiteSpace(r.ConfirmationNumber) ? r.ConfirmationNumber : r.PersonNumber;
                return new HotelSheetRow(
                    r.ParticipantId, first, last, r.Email ?? string.Empty,
                    r.CheckInDate, r.CheckOutDate,
                    string.IsNullOrWhiteSpace(confirmation) ? null : confirmation.Trim(),
                    string.IsNullOrWhiteSpace(r.CancellationNumber) ? null : r.CancellationNumber.Trim(),
                    GuestsInRoom: r.GuestsInRoom is int g && g > 0 ? g
                        : mateOf.ContainsKey(r.ParticipantId) ? 2 : HotelBooking.EffectiveGuestsInRoom(null, r.RoomShareWith),
                    Rooms: mateOf.TryGetValue(r.ParticipantId, out var mate) && mate < r.ParticipantId ? 0 : 1,
                    EldkPaymentDays: cancelled ? 0 : cov.Covered,
                    GuestSelfPayment: cancelled ? 0 : cov.OwnExpense,
                    r.ConfirmationState);
            })
            .OrderBy(r => r.LastName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.FirstName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ParticipantId)
            .ToList();
    }

    /// <summary>Build the .xls for one hotel. Null when the hotel does not exist in the edition.</summary>
    public async Task<HotelSheetFile?> BuildAsync(int eventId, int hotelId, CancellationToken ct = default)
    {
        var hotel = await _db.Hotels.AsNoTracking().FirstOrDefaultAsync(h => h.Id == hotelId && h.EventId == eventId, ct);
        if (hotel is null) return null;
        var rows = await RowsAsync(eventId, hotelId, ct);
        var bytes = Render(hotel.SheetTitle, rows);
        return new HotelSheetFile(hotel.Id, hotel.Name, FileNameFor(hotel), bytes, rows.Count);
    }

    public static string FileNameFor(Hotel hotel)
    {
        var safe = new string(hotel.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray())
            .Replace("--", "-").Trim('-');
        var agreement = string.IsNullOrWhiteSpace(hotel.AgreementNumber) ? "" : "-" + hotel.AgreementNumber.Trim();
        return $"hotel-{safe}{agreement}.xls";
    }

    /// <summary>Render rows to Excel 97-2003 bytes. Public + static so tests can round-trip it.</summary>
    public static byte[] Render(string title, IReadOnlyList<HotelSheetRow> rows)
    {
        using var wb = new HSSFWorkbook();
        var ws = wb.CreateSheet("Guests");

        var bold = wb.CreateFont();
        bold.IsBold = true;
        var titleFont = wb.CreateFont();
        titleFont.IsBold = true;
        titleFont.FontHeightInPoints = 14;

        var titleStyle = wb.CreateCellStyle();
        titleStyle.SetFont(titleFont);
        var headerStyle = wb.CreateCellStyle();
        headerStyle.SetFont(bold);
        headerStyle.FillForegroundColor = NPOI.HSSF.Util.HSSFColor.Grey25Percent.Index;
        headerStyle.FillPattern = FillPattern.SolidForeground;
        var dateStyle = wb.CreateCellStyle();
        dateStyle.DataFormat = wb.CreateDataFormat().GetFormat("dd-mm-yyyy");

        var t = ws.CreateRow(0).CreateCell(0);
        t.SetCellValue(title);
        t.CellStyle = titleStyle;

        var header = ws.CreateRow(2);
        for (var i = 0; i < Columns.Length; i++)
        {
            var c = header.CreateCell(i);
            c.SetCellValue(Columns[i]);
            c.CellStyle = headerStyle;
        }

        var r = 3;
        foreach (var g in rows)
        {
            var row = ws.CreateRow(r++);
            row.CreateCell(0).SetCellValue(g.FirstName);
            row.CreateCell(1).SetCellValue(g.LastName);
            row.CreateCell(2).SetCellValue(g.Email);
            SetDate(row.CreateCell(3), g.Arrival, dateStyle);
            SetDate(row.CreateCell(4), g.Departure, dateStyle);
            row.CreateCell(5).SetCellValue(g.ConfirmationNumber ?? string.Empty);
            row.CreateCell(6).SetCellValue(g.CancellationNumber ?? string.Empty);
            row.CreateCell(7).SetCellValue(g.GuestsInRoom);
            row.CreateCell(8).SetCellValue(g.Rooms);
            row.CreateCell(9).SetCellValue(g.EldkPaymentDays);
            row.CreateCell(10).SetCellValue(g.GuestSelfPayment);
            row.CreateCell(11).SetCellValue(HotelReservationStateText.Of(g.State));
        }

        for (var i = 0; i < Columns.Length; i++) ws.SetColumnWidth(i, (i == 2 ? 34 : 18) * 256);
        ws.CreateFreezePane(0, 3);

        using var ms = new MemoryStream();
        wb.Write(ms, leaveOpen: true);
        return ms.ToArray();
    }

    private static void SetDate(ICell cell, DateOnly? d, ICellStyle style)
    {
        if (d is null) { cell.SetCellValue(string.Empty); return; }
        cell.SetCellValue(d.Value.ToDateTime(TimeOnly.MinValue));
        cell.CellStyle = style;
    }

    /// <summary>
    /// Parse a spreadsheet the hotel returned — .xls or .xlsx. The header row is FOUND (any of the
    /// first 15 rows that has an "Email" column), so a hotel that adds a row above it, or moves
    /// columns about, still imports. Throws <see cref="InvalidDataException"/> on an unreadable file
    /// or one without the Email column.
    /// </summary>
    public static IReadOnlyList<HotelSheetImportRow> Parse(Stream content)
    {
        IWorkbook wb;
        try { wb = WorkbookFactory.Create(content); }
        catch (Exception ex) { throw new InvalidDataException("This is not an Excel file CEH can read (.xls or .xlsx).", ex); }

        using (wb)
        {
            var ws = wb.GetSheetAt(0);
            var fmt = new DataFormatter(CultureInfo.InvariantCulture);
            int headerRow = -1;
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = ws.FirstRowNum; i <= Math.Min(ws.LastRowNum, ws.FirstRowNum + 15); i++)
            {
                var row = ws.GetRow(i);
                if (row is null) continue;
                var cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int c = row.FirstCellNum; c >= 0 && c < row.LastCellNum; c++)
                {
                    var text = Norm(fmt.FormatCellValue(row.GetCell(c)));
                    if (text.Length > 0) cols.TryAdd(text, c);
                }
                if (cols.ContainsKey("email"))
                {
                    headerRow = i;
                    map = cols;
                    break;
                }
            }
            if (headerRow < 0) throw new InvalidDataException("No header row with an \"Email\" column was found in the first sheet.");

            string? Get(IRow row, string col) =>
                map.TryGetValue(Norm(col), out var idx) ? Blank(fmt.FormatCellValue(row.GetCell(idx))) : null;

            var result = new List<HotelSheetImportRow>();
            for (var i = headerRow + 1; i <= ws.LastRowNum; i++)
            {
                var row = ws.GetRow(i);
                if (row is null) continue;
                var email = Get(row, "Email");
                var first = Get(row, "First Name");
                var last = Get(row, "Last Name");
                if (email is null && first is null && last is null) continue;
                result.Add(new HotelSheetImportRow(
                    i + 1, first ?? "", last ?? "", email ?? "",
                    Get(row, "Confirmation Number"), Get(row, "Cancellation Number"), Get(row, "Reservation State")));
            }
            return result;
        }
    }

    private static string Norm(string? s) =>
        new string((s ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch) && ch != '-' && ch != '_').ToArray()).ToLowerInvariant();

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>"Jan Ketil Skanke" → ("Jan Ketil", "Skanke"); one word → (word, "").</summary>
    public static (string First, string Last) SplitName(string? full)
    {
        var parts = (full ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => ("", ""),
            1 => (parts[0], ""),
            _ => (string.Join(' ', parts[..^1]), parts[^1]),
        };
    }
}
