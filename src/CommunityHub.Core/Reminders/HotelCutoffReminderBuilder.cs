using System.Globalization;
using System.Net;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §326bs — warns the ORGANIZERS <b>3 days before</b> a hotel release deadline
/// (<see cref="HotelCutoff"/>), so spare rooms get released while that is still free.
///
/// <para><b>Why a reminder at all.</b> A cut-off is the one hotel date with a direct
/// cash consequence: miss it and the event pays for empty rooms. The date sits in a
/// signed PDF nobody re-reads, and the decision needs the live over/under picture —
/// so the mail carries that picture with it rather than only pointing at a page.</para>
///
/// <para><b>Window, not exact-date.</b> Fires while event-local today is inside
/// <c>[cutoff − 3 days, cutoff]</c>. A window (the §326b pattern) lets a missed or
/// ring-dropped run self-heal on a later day instead of losing the only chance to
/// send. The OccasionKey carries no window index, so the
/// <see cref="ReminderEngine"/> ledger delivers it ONCE EVER per organizer per
/// cut-off — and stamps only on real delivery.</para>
///
/// <para><b>Recipients</b> are the edition's ACTIVE organizers. This is an internal
/// operations mail: it must never reach a participant, so the audience is a role
/// filter, never a broadcast.</para>
///
/// <para><b>The hub never releases rooms.</b> It reports the deadline and the numbers;
/// a human decides and tells the hotel. Contract terms differ per hotel and per year
/// (percentage caps, per-day limits), so any automated release would eventually be
/// confidently wrong about somebody's money.</para>
/// </summary>
public sealed class HotelCutoffReminderBuilder
{
    private const string TemplateName = "hotel-cutoff-reminder";

    /// <summary>ReminderType + OccasionKey prefix in the SentReminder ledger.</summary>
    public const string ReminderType = "hotel-cutoff";

    /// <summary>How many days ahead of the deadline the warning goes out.</summary>
    public const int LeadDays = 3;

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly TimeProvider _clock;
    private readonly HotelAllotmentService _allotments;

    public HotelCutoffReminderBuilder(
        CommunityHubDbContext db,
        EmailTemplateProvider templates,
        TimeProvider clock,
        HotelAllotmentService allotments)
    {
        _db = db;
        _templates = templates;
        _clock = clock;
        _allotments = allotments;
    }

    public async Task<IReadOnlyList<ReminderMessage>> BuildDueAsync(
        int eventId, CancellationToken ct = default)
    {
        // Event-local (Danish) date: the deadline is a calendar date in the contract,
        // and an 08:00-UTC run must not fire a day early or late around midnight.
        var todayLocal = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), EventTimezone.Tz).DateTime);

        var due = await _db.HotelCutoffs
            .AsNoTracking()
            .Where(c => c.EventId == eventId)
            .Select(c => new
            {
                c.Id, c.HotelId, c.CutoffDate, c.Label, c.ReleasePercent,
                HotelName = c.Hotel.Name,
            })
            .ToListAsync(ct);

        // Inside [cutoff - LeadDays, cutoff]. A cut-off already past is never chased:
        // the window has closed and a late mail would only cause alarm.
        due = due
            .Where(c => todayLocal >= c.CutoffDate.AddDays(-LeadDays) && todayLocal <= c.CutoffDate)
            .OrderBy(c => c.CutoffDate)
            .ToList();
        if (due.Count == 0) return Array.Empty<ReminderMessage>();

        var ev = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (ev is null) return Array.Empty<ReminderMessage>();

        // §499 — the shared remindable rule (IsActive AND lifecycle Active).
        var organizers = await _db.Participants
            .AsNoTracking()
            .Remindable()
            .Where(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Organizer)
            .Select(p => new { p.Id, p.Email, p.FullName })
            .ToListAsync(ct);
        if (organizers.Count == 0) return Array.Empty<ReminderMessage>();

        // One board build for the whole run — the per-hotel position is sliced out of it.
        var board = await _allotments.BuildAsync(eventId, ct);

        var messages = new List<ReminderMessage>();
        foreach (var c in due)
        {
            var daysLeft = c.CutoffDate.DayNumber - todayLocal.DayNumber;
            var positionHtml = PositionHtml(board, c.HotelId);
            var releaseText = c.ReleasePercent is int pct
                ? $" — up to {pct}% of the original reservation may still be released free"
                : string.Empty;

            foreach (var org in organizers)
            {
                if (string.IsNullOrWhiteSpace(org.Email)) continue;

                var tokens = _templates.NewTokenSet(org.Id);
                tokens["firstName"] = string.IsNullOrWhiteSpace(org.FullName)
                    ? "there"
                    : org.FullName.Split(' ')[0];
                tokens["eventDisplayName"] = ev.DisplayName ?? string.Empty;
                tokens["hotelName"] = c.HotelName ?? string.Empty;
                tokens["cutoffLabel"] = c.Label ?? string.Empty;
                tokens["cutoffDate"] = c.CutoffDate.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
                tokens["daysLeft"] = daysLeft.ToString(CultureInfo.InvariantCulture);
                tokens["releaseText"] = releaseText;
                tokens["positionHtml"] = positionHtml;

                var rendered = _templates.Render(TemplateName, tokens);

                messages.Add(new ReminderMessage(
                    RecipientEmail: org.Email,
                    ReminderType: ReminderType,
                    // No window index — once ever per organizer per cut-off.
                    OccasionKey: $"{ReminderType}:{c.Id}:{org.Id}",
                    Subject: rendered.Subject,
                    HtmlBody: rendered.HtmlBody,
                    ParticipantId: org.Id,
                    RecipientName: org.FullName,
                    FeatureKey: "reminder-jobs", MailKey: TemplateName));
            }
        }

        return messages;
    }

    /// <summary>
    /// The hotel's live position as an HTML list: only the nights that are OFF, newest
    /// problem first. A night that is exactly right needs no line — the mail should be
    /// a to-do list, not a data dump. Nothing off ⇒ say so plainly.
    /// </summary>
    private static string PositionHtml(AllotmentBoard board, int hotelId)
    {
        var row = board.Rows.FirstOrDefault(r => r.HotelId == hotelId);
        if (row is null || row.Cells.Count == 0)
            return "<em>No allotment recorded for this hotel yet.</em>";

        var lines = new List<string>();
        foreach (var cell in row.Cells)
        {
            if (cell.Variance is not int v || v == 0) continue;
            var night = cell.Night.ToString("ddd d MMM", CultureInfo.InvariantCulture);
            lines.Add(v > 0
                ? $"<li style=\"margin:0 0 4px;\"><strong>{WebUtility.HtmlEncode(night)}</strong>: "
                  + $"{v} spare room(s) — {cell.Allotted} held, {cell.Demand} needed "
                  + "<span style=\"color:#15803d;\">(release candidate)</span></li>"
                : $"<li style=\"margin:0 0 4px;\"><strong>{WebUtility.HtmlEncode(night)}</strong>: "
                  + $"short {Math.Abs(v)} room(s) — {cell.Allotted} held, {cell.Demand} needed "
                  + "<span style=\"color:#b42318;\">(need more)</span></li>");
        }

        if (lines.Count == 0)
            return "<em>Every contracted night currently matches demand exactly — nothing to release.</em>";

        return "<ul style=\"margin:0;padding-left:18px;\">" + string.Concat(lines) + "</ul>";
    }
}
