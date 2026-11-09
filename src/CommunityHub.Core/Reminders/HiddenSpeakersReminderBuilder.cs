using System.Globalization;
using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Participants;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §1385 — the WEEKLY organizer notice "Hidden speakers": speakers on no session, speakers with no
/// speaker profile, and real sessions with no speakers. Sent ONLY when the check finds a problem.
/// </summary>
/// <remarks>
/// <para>Operator 2026-11-07: <i>"we cannot have hidden speakers we forgot"</i>. The check itself is
/// <see cref="HiddenSpeakerCheckService"/> — the Speakers page reads the same answer, so the mail can
/// never name a list the organizer opens and finds different.</para>
///
/// <para><b>Cadence: weekly, from Monday 08:00 Danish time.</b> The OccasionKey carries the ISO WEEK
/// (event-local), so the <see cref="ReminderEngine"/> ledger delivers one per organizer per week, and
/// a missed Monday run self-heals later in the same week (the hotel-cutoff window pattern). Before
/// Monday 08:00 local nothing is built, so a new week's notice never lands at night.</para>
///
/// <para><b>Stops by itself:</b> a check with no problem builds no message. Known placeholders /
/// sponsor slots ("expected empty") are listed in the mail when it goes out, but on their own never
/// trigger it.</para>
///
/// <para><b>Recipients</b> are the edition's remindable ORGANIZERS (the hotel-cutoff audience). The
/// mail has its own ring (<c>hidden-speakers-weekly</c>); like every new mail it has no ring row
/// until the operator sets one, so it reaches the inner ring only until he widens it.</para>
/// </remarks>
public sealed class HiddenSpeakersReminderBuilder
{
    /// <summary>The template file / mail identity.</summary>
    public const string TemplateName = "hidden-speakers-weekly";

    /// <summary>ReminderType + OccasionKey prefix in the SentReminder ledger.</summary>
    public const string ReminderType = "hidden-speakers";

    /// <summary>Event-local hour on Monday from which the week's notice may go out.</summary>
    public const int SendFromHourLocal = 8;

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly TimeProvider _clock;
    private readonly HiddenSpeakerCheckService _check;

    public HiddenSpeakersReminderBuilder(
        CommunityHubDbContext db,
        EmailTemplateProvider templates,
        TimeProvider clock,
        HiddenSpeakerCheckService check)
    {
        _db = db;
        _templates = templates;
        _clock = clock;
        _check = check;
    }

    /// <summary>
    /// The ISO-week key for an event-local time, or null before Monday 08:00 local
    /// (the week's notice is not due yet).
    /// </summary>
    public static string? WeekKeyIfDue(DateTimeOffset utcNow)
    {
        var local = TimeZoneInfo.ConvertTime(utcNow, EventTimezone.Tz).DateTime;
        if (local.DayOfWeek == DayOfWeek.Monday && local.Hour < SendFromHourLocal) return null;
        return $"{ISOWeek.GetYear(local)}w{ISOWeek.GetWeekOfYear(local):00}";
    }

    public async Task<IReadOnlyList<ReminderMessage>> BuildDueAsync(
        int eventId, CancellationToken ct = default)
    {
        var weekKey = WeekKeyIfDue(_clock.GetUtcNow());
        if (weekKey is null) return Array.Empty<ReminderMessage>();

        var result = await _check.CheckAsync(eventId, ct);
        if (!result.AnyProblem) return Array.Empty<ReminderMessage>();

        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (ev is null) return Array.Empty<ReminderMessage>();

        // §499 — the shared remindable rule (IsActive AND lifecycle Active); organizers only.
        var organizers = await _db.Participants.AsNoTracking()
            .Remindable()
            .Where(p => p.EventId == eventId && p.Role == ParticipantRole.Organizer)
            .Select(p => new { p.Id, p.Email, p.FullName })
            .ToListAsync(ct);
        if (organizers.Count == 0) return Array.Empty<ReminderMessage>();

        var findingsHtml = FindingsHtml(result);
        var messages = new List<ReminderMessage>();
        foreach (var org in organizers)
        {
            if (string.IsNullOrWhiteSpace(org.Email)) continue;

            var tokens = _templates.NewTokenSet(org.Id);
            tokens["firstName"] = string.IsNullOrWhiteSpace(org.FullName)
                ? "there"
                : org.FullName.Split(' ')[0];
            tokens["eventDisplayName"] = ev.DisplayName ?? string.Empty;
            tokens["summary"] = result.Summary;
            tokens["findingsHtml"] = findingsHtml;

            var rendered = _templates.Render(TemplateName, tokens);

            messages.Add(new ReminderMessage(
                RecipientEmail: org.Email,
                ReminderType: ReminderType,
                // 🔑 The WEEK is in the key: one per organizer per week. The finding set is NOT, so a
                // partial fix mid-week does not look like a new occasion and re-send.
                OccasionKey: $"{ReminderType}:{org.Id}:{weekKey}",
                Subject: rendered.Subject,
                HtmlBody: rendered.HtmlBody,
                ParticipantId: org.Id,
                RecipientName: org.FullName,
                FeatureKey: "reminder-jobs", MailKey: TemplateName));
        }

        return messages;
    }

    /// <summary>The three lists as HTML (empty lists are left out; expected-empty sessions last).</summary>
    public static string FindingsHtml(HiddenSpeakerCheckService.Result r)
    {
        var sb = new StringBuilder();

        void Section(string heading, IEnumerable<string> items, string? note = null)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            sb.Append("<p style=\"margin:0 0 6px;\"><strong>")
              .Append(WebUtility.HtmlEncode(heading)).Append(" (").Append(list.Count).Append(")</strong></p>");
            if (note is not null)
                sb.Append("<p style=\"margin:0 0 6px;color:#6b7280;font-size:13px;\">")
                  .Append(WebUtility.HtmlEncode(note)).Append("</p>");
            sb.Append("<ul style=\"margin:0 0 14px;padding-left:18px;\">");
            foreach (var i in list) sb.Append("<li style=\"margin:0 0 4px;\">").Append(i).Append("</li>");
            sb.Append("</ul>");
        }

        static string Speaker(HiddenSpeakerCheckService.SpeakerItem s) =>
            WebUtility.HtmlEncode(s.Name)
            + " <span style=\"color:#6b7280;\">(added "
            + TimeZoneInfo.ConvertTime(s.CreatedAt, EventTimezone.Tz)
                .ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            + ")</span>";

        Section("Speakers on no session", r.SpeakersWithoutSession.Select(Speaker),
            "Counted as speakers, but not on any real session — invisible on the agenda.");
        Section("Speakers without a speaker profile", r.SpeakersWithoutProfile.Select(Speaker));
        Section("Sessions with no speakers", r.SessionsNeedingSpeakers.Select(s => WebUtility.HtmlEncode(s.Title)));
        Section("Expected empty (placeholders and sponsor slots)",
            r.ExpectedEmptySessions.Select(s => WebUtility.HtmlEncode(s.Title)),
            "Listed so they are not forgotten — not counted as a problem.");

        return sb.ToString();
    }
}
