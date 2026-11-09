using System.Globalization;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>§1386 — what one row of the hotel's returned spreadsheet would do.</summary>
public sealed record HotelImportLine(
    int RowNumber, string SheetName, string SheetEmail,
    int? ParticipantId, string? MatchedName, string MatchedBy,
    string? CurrentConfirmation, string? NewConfirmation,
    string? CurrentCancellation, string? NewCancellation,
    HotelConfirmationState? CurrentState, HotelConfirmationState? NewState)
{
    public bool Matched => ParticipantId is not null;

    public bool HasChange => Matched && (
        (NewConfirmation is not null && NewConfirmation != CurrentConfirmation)
        || (NewCancellation is not null && NewCancellation != CurrentCancellation)
        || (NewState is not null && NewState != CurrentState));
}

/// <summary>§1386 — one change the organizer approved on the preview, posted back to apply.</summary>
public sealed record HotelImportApproval(int ParticipantId, string? ConfirmationNumber, string? CancellationNumber);

/// <summary>
/// §1386 — imports the confirmation and cancellation numbers from the spreadsheet the hotel sends back
/// (operator 2026-11-08: <i>"we need a way to upload the excel file from the hotel and import the
/// confirmation number from them"</i>).
/// </summary>
/// <remarks>
/// <para><b>Two steps:</b> <see cref="PreviewAsync"/> reads the file and shows, row by row, who it
/// matched and what would change; nothing is written. <see cref="ApplyAsync"/> writes only the rows
/// the organizer approved.</para>
/// <para><b>Matching</b>, among this hotel's guests (placed here, or cancelled from here): e-mail
/// first, then first + last name (accents and case ignored). A row that matches nobody is shown,
/// never guessed.</para>
/// <para><b>State:</b> a cancellation number ⇒ Cancelled; otherwise a confirmation number ⇒
/// Confirmed. A blank cell never clears a number CEH already holds.</para>
/// <para>🔒 <b>No mail.</b> The participant sees their number on their hotel page; the CONFIRMED
/// calendar re-issue that the hotel-level number triggers (§46) is deliberately not used here.</para>
/// </remarks>
public sealed class HotelSheetImportService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly HotelChangeLogService _log;

    public HotelSheetImportService(CommunityHubDbContext db, TimeProvider clock, HotelChangeLogService log)
    {
        _db = db;
        _clock = clock;
        _log = log;
    }

    private sealed record Guest(
        int ParticipantId, string FullName, string Email, string? First, string? Last,
        string? Confirmation, string? Cancellation, HotelConfirmationState State);

    public async Task<IReadOnlyList<HotelImportLine>> PreviewAsync(
        int eventId, int hotelId, IReadOnlyList<HotelSheetImportRow> rows, CancellationToken ct = default)
    {
        var guests = await GuestsAsync(eventId, hotelId, ct);
        var lines = new List<HotelImportLine>();

        foreach (var r in rows)
        {
            var sheetName = $"{r.FirstName} {r.LastName}".Trim();
            Guest? g = null;
            var by = "";
            if (!string.IsNullOrWhiteSpace(r.Email))
            {
                g = guests.FirstOrDefault(x => string.Equals(x.Email.Trim(), r.Email.Trim(), StringComparison.OrdinalIgnoreCase));
                if (g is not null) by = "email";
            }
            if (g is null && sheetName.Length > 0)
            {
                var key = Fold(sheetName);
                var byName = guests.Where(x => Fold(x.FullName) == key
                                               || (x.First is not null && x.Last is not null && Fold($"{x.First} {x.Last}") == key))
                    .ToList();
                if (byName.Count == 1) { g = byName[0]; by = "name"; }
                else if (byName.Count > 1) by = "name matches several guests";
            }

            if (g is null)
            {
                lines.Add(new HotelImportLine(r.RowNumber, sheetName, r.Email, null, null,
                    by.Length > 0 ? by : "no match", null, r.ConfirmationNumber, null, r.CancellationNumber, null, null));
                continue;
            }

            var newConf = r.ConfirmationNumber ?? g.Confirmation;
            var newCancel = r.CancellationNumber ?? g.Cancellation;
            var stateFromText = HotelReservationStateText.Parse(r.StateText);
            HotelConfirmationState newState =
                !string.IsNullOrWhiteSpace(r.CancellationNumber) || stateFromText == HotelConfirmationState.Cancelled
                    ? HotelConfirmationState.Cancelled
                    : !string.IsNullOrWhiteSpace(r.ConfirmationNumber) && g.State != HotelConfirmationState.Cancelled
                        ? HotelConfirmationState.Confirmed
                        : g.State;

            lines.Add(new HotelImportLine(r.RowNumber, sheetName, r.Email, g.ParticipantId, g.FullName, by,
                g.Confirmation, newConf, g.Cancellation, newCancel, g.State, newState));
        }
        return lines;
    }

    /// <summary>Write the approved rows. Returns how many reservations changed.</summary>
    public async Task<int> ApplyAsync(
        int eventId, int hotelId, IReadOnlyList<HotelImportApproval> approvals, string? actor, CancellationToken ct = default)
    {
        if (approvals.Count == 0) return 0;
        var guestIds = (await GuestsAsync(eventId, hotelId, ct)).Select(g => g.ParticipantId).ToHashSet();
        var ids = approvals.Select(a => a.ParticipantId).Where(guestIds.Contains).Distinct().ToList();

        var bookings = await _db.HotelBookings.Where(b => b.EventId == eventId && ids.Contains(b.ParticipantId)).ToListAsync(ct);
        var people = await _db.Participants.Where(p => p.EventId == eventId && ids.Contains(p.Id)).ToListAsync(ct);
        var now = _clock.GetUtcNow();
        var changed = 0;

        foreach (var a in approvals.Where(a => ids.Contains(a.ParticipantId)))
        {
            var b = bookings.FirstOrDefault(x => x.ParticipantId == a.ParticipantId);
            var p = people.FirstOrDefault(x => x.Id == a.ParticipantId);
            if (b is null || p is null) continue;

            var conf = Clean(a.ConfirmationNumber);
            var cancel = Clean(a.CancellationNumber);
            var notes = new List<string>();

            if (conf is not null && conf != b.ConfirmationNumber)
            {
                notes.Add($"confirmation {b.ConfirmationNumber ?? "(none)"} → {conf}");
                b.ConfirmationNumber = conf;
                p.HotelConfirmationNumber = conf;   // what the participant's hotel page shows
                if (b.ConfirmationState == HotelConfirmationState.NotConfirmed && cancel is null)
                {
                    b.ConfirmationState = HotelConfirmationState.Confirmed;
                    b.ConfirmedAt = now;
                }
            }
            if (cancel is not null && cancel != b.CancellationNumber)
            {
                notes.Add($"cancellation {b.CancellationNumber ?? "(none)"} → {cancel}");
                b.CancellationNumber = cancel;
                if (b.ConfirmationState != HotelConfirmationState.Cancelled)
                {
                    b.ConfirmationState = HotelConfirmationState.Cancelled;
                    b.CancelledAt = now;
                }
                b.CancelledHotelId ??= hotelId;
            }
            if (notes.Count == 0) continue;

            b.UpdatedAt = now;
            changed++;
            _log.Add(eventId, HotelChangeSource.Import, actor,
                cancel is not null ? HotelChangeKinds.CancellationImported : HotelChangeKinds.ConfirmationImported,
                $"Imported from the hotel's spreadsheet: {string.Join("; ", notes)} — now {HotelReservationStateText.Of(b.ConfirmationState)}",
                hotelId, p.Id, p.FullName, affectsGuestList: false, notify: false);
        }

        if (changed > 0) await _db.SaveChangesAsync(ct);
        return changed;
    }

    private async Task<List<Guest>> GuestsAsync(int eventId, int hotelId, CancellationToken ct)
    {
        var rows = await _db.HotelBookings.AsNoTracking()
            .Where(b => b.EventId == eventId
                        && (b.Participant.HotelId == hotelId || b.CancelledHotelId == hotelId))
            .Select(b => new
            {
                b.ParticipantId, b.Participant.FullName, b.Participant.Email,
                b.ConfirmationNumber, b.CancellationNumber, b.ConfirmationState,
                Person = b.Participant.HotelConfirmationNumber,
            })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.ParticipantId).ToList();
        var names = await _db.SpeakerProfiles.AsNoTracking()
            .Where(s => s.EventId == eventId && ids.Contains(s.ParticipantId))
            .Select(s => new { s.ParticipantId, s.FirstName, s.LastName })
            .ToListAsync(ct);
        return rows.Select(r =>
        {
            var n = names.FirstOrDefault(x => x.ParticipantId == r.ParticipantId);
            return new Guest(r.ParticipantId, r.FullName, r.Email ?? "", n?.FirstName, n?.LastName,
                r.ConfirmationNumber ?? r.Person, r.CancellationNumber, r.ConfirmationState);
        }).ToList();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Case-, accent- and whitespace-insensitive name key ("Søren  Ørsted" == "soren orsted").</summary>
    public static string Fold(string? s)
    {
        var d = (s ?? string.Empty).Trim().ToLowerInvariant()
            .Replace("ø", "o").Replace("æ", "ae").Replace("å", "a").Replace("ß", "ss")
            .Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        var space = false;
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(ch)) { if (!space && sb.Length > 0) sb.Append(' '); space = true; continue; }
            space = false;
            sb.Append(ch);
        }
        return sb.ToString().TrimEnd();
    }
}
