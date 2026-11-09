using System.Globalization;
using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.DocLibrary;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>§1386 — what one notifier run did.</summary>
public sealed record HotelOpsRunResult(int ChangeMails, int EntriesMailed, int DeadlineWarnings, int NightsFrozen, bool Waiting);

/// <summary>
/// §1386 — the hotel-ops mail: CHANGE mails with the updated spreadsheet, and EARLY deadline warnings.
/// </summary>
/// <remarks>
/// <para><b>Change mails</b> (operator 2026-11-08: <i>"Organizers must get mails when changes happens
/// and we need ceh to send an updated excel file if changes happens. then one organizer sends it to
/// hotel manually"</i>). Every unmailed <see cref="HotelChangeLogEntry"/> is grouped per hotel and
/// mailed to that hotel's <see cref="Hotel.NotifyEmails"/>; when any entry changes the guest list, the
/// hotel's current .xls is attached. A burst of edits waits <see cref="QuietMinutes"/> so it becomes ONE
/// mail.</para>
/// <para><b>Deadline warnings</b> (<i>"we have to downgrade the estimate of rooms (otherwise we have
/// to pay for all blocked). This requires warnings/mails … in advance with detailed information of
/// reserved vs. allocated"</i>): at <see cref="HotelCutoff.WarnDaysBefore"/>, 7, 3 and 1 day(s) before
/// each cut-off, the per-night original / allocated / reserved / confirmed / forecast / result, and —
/// where the contract states a percentage — how many rooms may still be released per night.</para>
/// <para><b>Original reservation.</b> Once a hotel's FIRST cut-off has passed, any night not yet frozen
/// is frozen (<see cref="HotelAllotment.OriginalRooms"/>): the contract measures every later release
/// against what was held on that date.</para>
/// <para>🔒 <b>Recipients are internal only.</b> Blank <see cref="Hotel.NotifyEmails"/> ⇒ the logistics
/// review mailbox. The hotel's own <see cref="Hotel.ContactEmail"/> is NEVER used — an organizer
/// forwards the file.</para>
/// </remarks>
public sealed class HotelOpsNotifier
{
    /// <summary>A change waits this long for further changes before it is mailed.</summary>
    public const int QuietMinutes = 10;

    /// <summary>Fixed warning points besides the cut-off's own lead.</summary>
    public static readonly int[] FixedLeads = { 7, 3, 1 };

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IEmailSender _email;
    private readonly HotelAgreementSheet _sheet;
    private readonly HotelOverviewService _overview;
    private readonly HotelAllotmentService _allotments;
    private readonly HotelChangeLogService _log;
    private readonly LogisticsRecipients _recipients;
    private readonly IEmailContextAccessor? _ctx;

    public const string ChangeMailKey = "hotel-ops-change";
    public const string DeadlineMailKey = "hotel-ops-deadline";

    public HotelOpsNotifier(
        CommunityHubDbContext db, TimeProvider clock, IEmailSender email,
        HotelAgreementSheet sheet, HotelOverviewService overview, HotelAllotmentService allotments,
        HotelChangeLogService log, LogisticsRecipients recipients, IEmailContextAccessor? ctx = null)
    {
        _db = db;
        _clock = clock;
        _email = email;
        _sheet = sheet;
        _overview = overview;
        _allotments = allotments;
        _log = log;
        _recipients = recipients;
        _ctx = ctx;
    }

    public async Task<HotelOpsRunResult> RunAsync(int eventId, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), EventTimezone.Tz).DateTime);
        var frozen = await FreezeAfterFirstCutoffAsync(eventId, today, ct);
        var warnings = await SendDeadlineWarningsAsync(eventId, today, ct);
        var (mails, entries, waiting) = await SendChangeMailsAsync(eventId, ct);
        return new HotelOpsRunResult(mails, entries, warnings, frozen, waiting);
    }

    // ---- recipients ------------------------------------------------------------------------

    /// <summary>The addresses a hotel's ops mail goes to. Never the hotel itself.</summary>
    /// <remarks>
    /// 🔒 Only addresses in the review mailbox's own domain are used, and never the hotel's contact
    /// address: these sends are ring-exempt, so the recipient rule IS the safety. A hotel or guest
    /// address typed into the field by mistake is ignored rather than mailed.
    /// </remarks>
    public IReadOnlyList<string> RecipientsFor(Hotel? hotel)
    {
        var domain = DomainOf(_recipients.ReviewMailbox);
        var list = ParseEmails(hotel?.NotifyEmails)
            .Where(e => domain.Length > 0 && string.Equals(DomainOf(e), domain, StringComparison.OrdinalIgnoreCase))
            .Where(e => !string.Equals(e, hotel?.ContactEmail?.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return list.Count > 0 ? list : new[] { _recipients.ReviewMailbox };
    }

    private static string DomainOf(string? email)
    {
        var at = (email ?? string.Empty).LastIndexOf('@');
        return at < 0 ? string.Empty : email![(at + 1)..].Trim();
    }

    public static IReadOnlyList<string> ParseEmails(string? raw) =>
        (raw ?? string.Empty)
            .Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ---- original reservation ----------------------------------------------------------------

    private async Task<int> FreezeAfterFirstCutoffAsync(int eventId, DateOnly today, CancellationToken ct)
    {
        var firstCutoffs = await _db.HotelCutoffs.AsNoTracking()
            .Where(c => c.EventId == eventId)
            .GroupBy(c => c.HotelId)
            .Select(g => new { HotelId = g.Key, First = g.Min(c => c.CutoffDate) })
            .ToListAsync(ct);

        var total = 0;
        foreach (var c in firstCutoffs.Where(c => today > c.First))
        {
            var needs = await _db.HotelAllotments.AnyAsync(
                a => a.EventId == eventId && a.HotelId == c.HotelId && a.OriginalRooms == null, ct);
            if (needs)
                total += await _allotments.FreezeOriginalAsync(eventId, c.HotelId, "system",
                    overwrite: false, source: HotelChangeSource.System, ct: ct);
        }
        return total;
    }

    // ---- change mails ------------------------------------------------------------------------

    private async Task<(int Mails, int Entries, bool Waiting)> SendChangeMailsAsync(int eventId, CancellationToken ct)
    {
        var pending = await _db.HotelChangeLog
            .Where(e => e.EventId == eventId && e.NotifiedAt == null)
            .OrderBy(e => e.At).ThenBy(e => e.Id)
            .ToListAsync(ct);
        if (pending.Count == 0) return (0, 0, false);

        var now = _clock.GetUtcNow();
        if (pending.Max(e => e.At) > now.AddMinutes(-QuietMinutes)) return (0, 0, true);   // still settling

        var hotels = await _db.Hotels.AsNoTracking().Where(h => h.EventId == eventId).ToDictionaryAsync(h => h.Id, ct);
        var eventName = await _db.Events.Where(e => e.Id == eventId).Select(e => e.DisplayName).FirstOrDefaultAsync(ct) ?? "the event";

        var mails = 0;
        foreach (var group in pending.GroupBy(e => e.HotelId))
        {
            hotels.TryGetValue(group.Key ?? -1, out var hotel);
            var entries = group.ToList();
            var attach = hotel is not null && entries.Any(e => e.AffectsGuestList);

            var sb = new StringBuilder();
            sb.Append($"<p>{entries.Count} hotel change(s) for <strong>{Enc(hotel?.Name ?? "no hotel yet")}</strong> ({Enc(eventName)}).</p>");
            if (attach)
            {
                sb.Append("<p><strong>The guest list changed.</strong> The attached spreadsheet is the hotel's current list — "
                          + "please forward it to the hotel"
                          + (string.IsNullOrWhiteSpace(hotel!.ContactEmail) ? "" : $" ({Enc(hotel.ContactEmail!)})")
                          + ". CEH does not send it to the hotel itself.</p>");
            }
            sb.Append(EntriesTable(entries));

            var subject = $"Hotel changes — {hotel?.Name ?? "unplaced"} ({entries.Count})";
            var attachments = new List<EmailAttachment>();
            if (attach)
            {
                var file = await _sheet.BuildAsync(eventId, hotel!.Id, ct);
                if (file is not null) attachments.Add(new EmailAttachment(file.FileName, file.Content, HotelSheetFile.ContentType));
            }

            using var mailCtx = _ctx?.Set(new EmailContext(ChangeMailKey, eventId, TemplateName: ChangeMailKey, RingExempt: true));
            foreach (var to in RecipientsFor(hotel))
            {
                if (attachments.Count > 0) await _email.SendWithAttachmentsAsync(to, subject, sb.ToString(), attachments, ct);
                else await _email.SendAsync(to, subject, sb.ToString(), ct);
            }
            foreach (var e in entries) e.NotifiedAt = now;
            await _db.SaveChangesAsync(ct);   // per group: a later failure never re-mails a sent group
            mails++;
        }
        return (mails, pending.Count, false);
    }

    private static string EntriesTable(IEnumerable<HotelChangeLogEntry> entries)
    {
        var sb = new StringBuilder("<table cellpadding=\"4\" style=\"border-collapse:collapse;font-size:13px;\">"
            + "<tr style=\"background:#f3f4f6;\"><th align=\"left\">When (UTC)</th><th align=\"left\">Who</th>"
            + "<th align=\"left\">Guest</th><th align=\"left\">Change</th></tr>");
        foreach (var e in entries)
        {
            sb.Append("<tr style=\"border-top:1px solid #e5e7eb;\">")
              .Append($"<td>{e.At.UtcDateTime.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)}</td>")
              .Append($"<td>{Enc($"{e.Source}: {e.Actor}")}</td>")
              .Append($"<td>{Enc(e.ParticipantName ?? "")}</td>")
              .Append($"<td>{Enc(e.Summary)}</td></tr>");
        }
        return sb.Append("</table>").ToString();
    }

    // ---- deadline warnings -------------------------------------------------------------------

    /// <summary>The warning points for a cut-off, largest first.</summary>
    public static IReadOnlyList<int> LeadsFor(int warnDaysBefore) =>
        FixedLeads.Append(warnDaysBefore).Where(l => l >= 0 && l <= Math.Max(warnDaysBefore, 0))
            .Distinct().OrderByDescending(l => l).ToList();

    public static string WarningKey(int cutoffId, int lead) => $"[cutoff {cutoffId} · {lead}d]";

    private async Task<int> SendDeadlineWarningsAsync(int eventId, DateOnly today, CancellationToken ct)
    {
        var cutoffs = await _db.HotelCutoffs.AsNoTracking()
            .Where(c => c.EventId == eventId && c.CutoffDate >= today && c.WarnDaysBefore > 0)
            .OrderBy(c => c.CutoffDate)
            .ToListAsync(ct);
        if (cutoffs.Count == 0) return 0;

        var sentKeys = await _db.HotelChangeLog.AsNoTracking()
            .Where(e => e.EventId == eventId && e.Kind == HotelChangeKinds.DeadlineWarning)
            .Select(e => e.Summary)
            .ToListAsync(ct);
        bool Sent(int id, int lead) => sentKeys.Any(s => s.StartsWith(WarningKey(id, lead), StringComparison.Ordinal));

        var sent = 0;
        foreach (var c in cutoffs)
        {
            var daysLeft = c.CutoffDate.DayNumber - today.DayNumber;
            // The smallest lead we are already inside; larger ones that were missed are skipped (and
            // marked) so a late first run sends ONE warning, not a stack of them.
            var due = LeadsFor(c.WarnDaysBefore).Where(l => daysLeft <= l).ToList();
            if (due.Count == 0) continue;
            var current = due.Min();
            if (Sent(c.Id, current)) continue;

            var hotel = await _db.Hotels.AsNoTracking().FirstOrDefaultAsync(h => h.Id == c.HotelId, ct);
            if (hotel is null) continue;
            var html = await DeadlineHtmlAsync(eventId, hotel, c, daysLeft, ct);
            var subject = $"Hotel deadline in {daysLeft} day(s): {c.Label} — {hotel.Name} ({c.CutoffDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture)})";
            using (_ctx?.Set(new EmailContext(DeadlineMailKey, eventId, TemplateName: DeadlineMailKey, RingExempt: true)))
            {
                foreach (var to in RecipientsFor(hotel)) await _email.SendAsync(to, subject, html, ct);
            }

            foreach (var lead in due)
            {
                if (Sent(c.Id, lead)) continue;
                _log.Add(eventId, HotelChangeSource.System, "system", HotelChangeKinds.DeadlineWarning,
                    $"{WarningKey(c.Id, lead)} " + (lead == current
                        ? $"Deadline warning sent: {c.Label} on {c.CutoffDate:yyyy-MM-dd}, {daysLeft} day(s) left"
                        : "Skipped — a later warning point was already due"),
                    hotel.Id, notify: false);
                sentKeys.Add(WarningKey(c.Id, lead));
            }
            await _db.SaveChangesAsync(ct);
            sent++;
        }
        return sent;
    }

    /// <summary>The per-night detail for one hotel's deadline. Public so the page can preview it.</summary>
    public async Task<string> DeadlineHtmlAsync(int eventId, Hotel hotel, HotelCutoff cutoff, int daysLeft, CancellationToken ct = default)
    {
        var rows = await _db.HotelAllotments.AsNoTracking()
            .Where(a => a.EventId == eventId && a.HotelId == hotel.Id)
            .OrderBy(a => a.Night)
            .ToListAsync(ct);
        var bookings = await _db.HotelBookings.AsNoTracking()
            .Where(b => b.EventId == eventId && b.NeedsRoom && b.Participant.HotelId == hotel.Id
                        && b.Participant.IsActive && !b.Participant.IsTestUser
                        && b.ConfirmationState != HotelConfirmationState.Cancelled
                        && b.CheckInDate != null && b.CheckOutDate != null)
            .Select(b => new { b.ParticipantId, b.RoommateParticipantId, b.CheckInDate, b.CheckOutDate, b.ConfirmationState })
            .ToListAsync(ct);
        var isPrimary = !await _db.Hotels.AnyAsync(h => h.EventId == eventId && h.Priority < hotel.Priority, ct);
        var forecast = isPrimary
            ? await _db.HotelForecastExtras.AsNoTracking().Where(f => f.EventId == eventId).ToDictionaryAsync(f => f.Night, f => f.ExtraRooms, ct)
            : new Dictionary<DateOnly, int>();

        var sb = new StringBuilder();
        sb.Append($"<p><strong>{Enc(cutoff.Label)}</strong> — {Enc(hotel.Name)}, on "
                  + $"<strong>{cutoff.CutoffDate.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)}</strong> "
                  + $"({daysLeft} day(s) from today). Changes must reach the hotel BEFORE that date.</p>");
        // §1386 decision (operator 2026-11-08): the limits are CUMULATIVE against the original reservation per
        // night — 20 % up to the period-1 deadline, 30 % in total (20 + the additional 10) up to period 2.
        int? cumPct = cutoff.ReleasePercent is null
            ? null
            : (await _db.HotelCutoffs.AsNoTracking()
                .Where(c => c.EventId == eventId && c.HotelId == hotel.Id && c.CutoffDate <= cutoff.CutoffDate && c.ReleasePercent != null)
                .Select(c => c.ReleasePercent!.Value)
                .ToListAsync(ct)).Sum();
        if (cumPct is int pct)
            sb.Append($"<p>Up to this date, at most <strong>{pct}%</strong> of the ORIGINAL reservation per night may be released "
                      + "free of charge in total (earlier releases count). \"Free release left\" is what may still be given back per night.</p>");
        if (!string.IsNullOrWhiteSpace(cutoff.Notes)) sb.Append($"<p style=\"color:#4b5563;\">{Enc(cutoff.Notes!)}</p>");

        // §1386 — "before we reduce the allotment we need to verify everyone has filled out".
        var rd = await _overview.ReadinessAsync(eventId, ct);
        sb.Append(rd.Ready
            ? "<p style=\"background:#dcfce7;color:#166534;padding:8px 10px;border-radius:6px;\"><strong>✓ Ready to reduce rooms:</strong> "
              + "everyone has answered the hotel form, everyone who reserved has a hotel, and no problem is open.</p>"
            : "<p style=\"background:#fee2e2;color:#991b1b;padding:8px 10px;border-radius:6px;\"><strong>Not ready to reduce rooms yet:</strong> "
              + $"{rd.NotAnswered} have not answered the hotel form"
              + (rd.NotAnsweredByRole.Count == 0 ? "" : " (" + Enc(string.Join(", ", rd.NotAnsweredByRole.Select(kv => $"{kv.Value} {kv.Key}"))) + ")")
              + $", {rd.Unplaced} reserved without a hotel, {rd.Problems} problem(s) open. Chase them, or add them as a forecast, before releasing rooms.</p>");

        sb.Append("<table cellpadding=\"4\" style=\"border-collapse:collapse;font-size:13px;\">"
                  + "<tr style=\"background:#f3f4f6;\"><th align=\"left\">Night</th><th>Original</th><th>Allocated now</th>"
                  + "<th>Reserved</th><th>Confirmed</th><th>Forecast</th><th>Needed</th><th>Result</th>"
                  + (cumPct is not null ? "<th>Free release left</th>" : "") + "</tr>");
        int tAlloc = 0, tNeed = 0;
        foreach (var a in rows)
        {
            var sleeping = bookings.Where(b => b.CheckInDate!.Value <= a.Night && a.Night < b.CheckOutDate!.Value).ToList();
            // §1386 — rooms, not people (HotelRoomMath), exactly as the hotel page counts.
            var roomCount = HotelRoomMath.Count(sleeping.Select(b => new HotelSleeper(
                b.ParticipantId, b.RoommateParticipantId, hotel.Id, b.ConfirmationState == HotelConfirmationState.Confirmed)));
            var reserved = roomCount.Rooms - roomCount.ConfirmedRooms;
            var confirmed = roomCount.ConfirmedRooms;
            forecast.TryGetValue(a.Night, out var fc);
            var needed = reserved + confirmed + fc;
            var result = a.RoomsAllotted - needed;
            tAlloc += a.RoomsAllotted; tNeed += needed;
            // §1386 colour rule: covered = green, surplus = a separate yellow "release N", gap = red.
            var colour = result < 0 ? "#b42318" : "#15803d";
            var label = result < 0 ? $"{-result} short"
                : result > 0 ? $"OK <span style=\"background:#fef3c7;color:#92400e;padding:0 6px;border-radius:8px;\">release {result}</span>"
                : "OK";
            sb.Append("<tr style=\"border-top:1px solid #e5e7eb;\">")
              .Append($"<td>{a.Night.ToString("ddd d MMM", CultureInfo.InvariantCulture)}</td>")
              .Append($"<td align=\"center\">{(a.OriginalRooms?.ToString(CultureInfo.InvariantCulture) ?? "–")}</td>")
              .Append($"<td align=\"center\">{a.RoomsAllotted}</td><td align=\"center\">{reserved}</td>")
              .Append($"<td align=\"center\">{confirmed}</td><td align=\"center\">{(fc == 0 ? "" : "+" + fc)}</td>")
              .Append($"<td align=\"center\">{needed}</td>")
              .Append($"<td align=\"center\" style=\"color:{colour};font-weight:bold;\">{label}</td>");
            if (cumPct is int p)
            {
                var original = a.OriginalRooms ?? a.RoomsAllotted;
                var alreadyReleased = Math.Max(0, original - a.RoomsAllotted);
                var left = Math.Max(0, (int)Math.Floor(original * p / 100.0) - alreadyReleased);
                sb.Append($"<td align=\"center\">{left}</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append($"<tr style=\"border-top:2px solid #9ca3af;font-weight:bold;\"><td>Total room-nights</td><td></td><td align=\"center\">{tAlloc}</td>"
                  + $"<td colspan=\"4\"></td><td align=\"center\">{tAlloc - tNeed}</td>{(cumPct is not null ? "<td></td>" : "")}</tr></table>");
        sb.Append("<p style=\"color:#6b7280;font-size:12px;\">Needed = reserved + confirmed + organizer forecast. "
                  + "CEH never releases rooms — decide, update the allocation in the hub, and tell the hotel.</p>");
        return sb.ToString();
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
