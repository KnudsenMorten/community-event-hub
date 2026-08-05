using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>One hotel's file, with the contact §6.4 mails it to when it changes.</summary>
/// <param name="ContactEmail">
/// The hotel's own contact in CEH — null when nobody has entered one, which the caller must treat as
/// "produce the file, mail nobody" rather than as a reason to skip the file.
/// </param>
public sealed record HotelLogisticsFile(
    int HotelId, string HotelName, string? ContactEmail, GeneratedFile File);

/// <summary>
/// §6.4 / §3.5 — <b>one file per hotel</b>: who is staying there, and on which nights.
/// </summary>
/// <remarks>
/// <para>§3.5: <i>"One file per hotel engagement. From 3 weeks before the event, any change sends
/// the file to that hotel's contact — hotel contacts already exist in CEH, use them."</i></para>
///
/// <para>🔒 <b>One file PER HOTEL is a privacy boundary, not a filing convention.</b> Each of these
/// goes to a different external company. A single combined rooming list would tell every hotel who
/// is staying at their competitors, and it cannot be unsent. The per-hotel content key is what lets
/// the job mail exactly the hotel whose list moved — §6.4's "any change" is per hotel, not per run.</para>
///
/// <para>⚠️ <b>A booking with no hotel assigned yet is NOT in anybody's file.</b> A speaker who has
/// asked for a room but has not been placed belongs to no hotel, and putting them in the first one
/// would book a room somebody has to pay for. They are reported as unplaced instead (see
/// <see cref="UnplacedAsync"/>) so a human closes the gap.</para>
/// </remarks>
public sealed class HotelLogisticsProducer
{
    private readonly CommunityHubDbContext _db;

    public HotelLogisticsProducer(CommunityHubDbContext db) => _db = db;

    private sealed record Guest(
        int Id, string Name, string Email, string Role,
        DateOnly? CheckIn, DateOnly? CheckOut, string? RoomType,
        string ShareWith, string Confirmation, string Notes);

    /// <summary>One file per hotel that has at least one guest.</summary>
    public async Task<IReadOnlyList<HotelLogisticsFile>> BuildAllAsync(
        int eventId, string eventShortName, CancellationToken ct = default)
    {
        var hotels = await _db.Hotels
            .Where(h => h.EventId == eventId)
            .Select(h => new { h.Id, h.Name, h.ContactEmail })
            .ToListAsync(ct);

        var guests = await LoadGuestsAsync(eventId, ct);

        var files = new List<HotelLogisticsFile>();
        foreach (var hotel in hotels.OrderBy(h => h.Name, StringComparer.Ordinal))
        {
            if (!guests.TryGetValue(hotel.Id, out var list) || list.Count == 0) continue;

            files.Add(new HotelLogisticsFile(
                hotel.Id, hotel.Name, hotel.ContactEmail,
                Build(LogisticsFileNames.Hotel(eventShortName, hotel.Name), hotel.Name, list)));
        }

        return files;
    }

    /// <summary>
    /// People who need a room but are not placed in a hotel yet — nobody's file, and somebody's job.
    /// </summary>
    public async Task<IReadOnlyList<string>> UnplacedAsync(int eventId, CancellationToken ct = default)
    {
        var countable = LogisticsAudience.Countable(_db.Participants, eventId);

        return await _db.HotelBookings
            .Where(b => b.EventId == eventId && b.NeedsRoom)
            .Join(countable.Where(p => p.HotelId == null), b => b.ParticipantId, p => p.Id,
                (b, p) => p.FullName)
            .OrderBy(n => n)
            .ToListAsync(ct);
    }

    private async Task<Dictionary<int, List<Guest>>> LoadGuestsAsync(int eventId, CancellationToken ct)
    {
        var countable = LogisticsAudience.Countable(_db.Participants, eventId);

        // 🔒 The BOOKING says they need a room; the PARTICIPANT says which hotel they were placed
        // in. Both are required — one without the other is either an unplaced request or a stale
        // placement for somebody who no longer needs a bed.
        var rows = await _db.HotelBookings
            .Where(b => b.EventId == eventId && b.NeedsRoom)
            .Join(countable.Where(p => p.HotelId != null), b => b.ParticipantId, p => p.Id,
                (b, p) => new
                {
                    HotelId = p.HotelId!.Value,
                    p.Id, p.FullName, p.Email, p.Role,
                    b.CheckInDate, b.CheckOutDate, b.RoomType, b.RoomShareWith,
                    b.ConfirmationState, b.ConfirmationNumber, b.Notes,
                })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.HotelId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .Select(r => new Guest(
                        r.Id, r.FullName, r.Email, r.Role.ToString(),
                        r.CheckInDate, r.CheckOutDate, r.RoomType,
                        (r.RoomShareWith ?? string.Empty).Trim(),
                        r.ConfirmationState == HotelConfirmationState.Confirmed
                            ? (string.IsNullOrWhiteSpace(r.ConfirmationNumber)
                                ? "Confirmed"
                                : $"Confirmed ({r.ConfirmationNumber!.Trim()})")
                            : "Not confirmed",
                        (r.Notes ?? string.Empty).Trim()))
                    .OrderBy(x => x.Name, StringComparer.Ordinal)
                    .ThenBy(x => x.Id)
                    .ToList());
    }

    private static GeneratedFile Build(string fileName, string hotelName, IReadOnlyList<Guest> guests)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Rooming list");

        Header(ws, "Name", "Email", "Role", "Check-in", "Check-out", "Nights",
            "Room type", "Sharing with", "Confirmation", "Notes");

        var row = 2;
        foreach (var g in guests)
        {
            ws.Cell(row, 1).Value = g.Name;
            ws.Cell(row, 2).Value = g.Email;
            ws.Cell(row, 3).Value = g.Role;
            ws.Cell(row, 4).Value = g.CheckIn?.ToString("yyyy-MM-dd") ?? string.Empty;
            ws.Cell(row, 5).Value = g.CheckOut?.ToString("yyyy-MM-dd") ?? string.Empty;
            ws.Cell(row, 6).Value = Nights(g.CheckIn, g.CheckOut);
            ws.Cell(row, 7).Value = g.RoomType ?? string.Empty;
            ws.Cell(row, 8).Value = g.ShareWith;
            ws.Cell(row, 9).Value = g.Confirmation;
            ws.Cell(row, 10).Value = g.Notes;
            row++;
        }

        if (guests.Count > 0)
        {
            ws.Cell(row, 1).Value = $"{hotelName} — total guests";
            ws.Cell(row, 2).Value = guests.Count;
            ws.Cell(row, 6).Value = guests.Sum(g => Nights(g.CheckIn, g.CheckOut));
            ws.Range(row, 1, row, 6).Style.Font.Bold = true;
        }

        Finish(ws);

        var key = GeneratedFile.KeyOf(guests.Select(g =>
            $"{g.Id}|{g.Name}|{g.Email}|{g.Role}|{g.CheckIn}|{g.CheckOut}|{g.RoomType}|"
            + $"{g.ShareWith}|{g.Confirmation}|{g.Notes}"));

        return new GeneratedFile(fileName, Save(wb), GeneratedFile.XlsxContentType, key,
            LogisticsHeadline.Of(guests.Count, "guest", "guests"));
    }

    /// <summary>
    /// Nights between the dates. 0 when either is missing — an unknown stay is not a guess.
    /// </summary>
    /// <remarks>
    /// ⚠️ The hotel bills per night, so this is the number that becomes money. A missing date shows
    /// as 0 and stands out on the sheet, which is exactly what should happen: somebody has to go and
    /// ask, rather than the file quietly inventing a two-night stay.
    /// </remarks>
    private static int Nights(DateOnly? checkIn, DateOnly? checkOut) =>
        checkIn is null || checkOut is null || checkOut <= checkIn
            ? 0
            : checkOut.Value.DayNumber - checkIn.Value.DayNumber;

    private static void Header(IXLWorksheet ws, params string[] headers)
    {
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Range(1, 1, 1, headers.Length).Style.Font.Bold = true;
    }

    private static void Finish(IXLWorksheet ws)
    {
        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
    }

    private static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
