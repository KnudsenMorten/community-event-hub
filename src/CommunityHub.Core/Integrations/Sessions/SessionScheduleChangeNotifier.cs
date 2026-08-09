using System.Globalization;
using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// §1004 — tells a session's speakers that its room or time changed, from wherever the change was
/// actually made.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why this exists as its own service.</b> The notice used to fire from the Zoho→CEH
/// apply path, because that is where schedule changes arrived. §1000 disabled that direction and
/// made CEH the owner — so the mail had to move to the CEH edit, or no speaker would ever be told
/// about a schedule change again. Extracting it means the CEH page and the (now dormant) queue path
/// render the SAME mail; a second copy is how the two would drift (§660/§719).</para>
///
/// <para>🔒 <b>The §1001 quiet period is enforced HERE</b>, once, rather than at each call site.
/// Before the configured date a change applies everywhere and is simply not announced. A missing
/// settings row means SILENT — a row nobody saved must not mail every speaker on the first agenda
/// edit, which is the noise the operator asked to remove.</para>
///
/// <para>🔑 <b>Only the fields that changed are printed</b> (§997), and times are converted to
/// Danish wall time (§305/§997): the mail once showed 07:30–07:50 for a session Zoho listed at
/// 08:30–08:50, and printed an unchanged room struck through and repeated.</para>
/// </remarks>
public sealed class SessionScheduleChangeNotifier
{
    /// <summary>The template both paths render.</summary>
    public const string TemplateName = "session-time-location-changed";

    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender? _sender;
    private readonly EmailTemplateProvider? _templates;
    private readonly IEmailContextAccessor? _context;
    private readonly TimeProvider _clock;
    private readonly ILogger<SessionScheduleChangeNotifier>? _log;

    public SessionScheduleChangeNotifier(
        CommunityHubDbContext db,
        IEmailSender? sender = null,
        EmailTemplateProvider? templates = null,
        IEmailContextAccessor? context = null,
        TimeProvider? clock = null,
        ILogger<SessionScheduleChangeNotifier>? log = null)
    {
        _db = db;
        _sender = sender;
        _templates = templates;
        _context = context;
        _clock = clock ?? TimeProvider.System;
        _log = log;
    }

    /// <summary>What one notification attempt did — so a caller can say so on screen.</summary>
    /// <param name="Sent">How many speakers were mailed.</param>
    /// <param name="Suppressed">True when a real change was NOT announced (quiet period).</param>
    /// <param name="Reason">Human explanation when nothing was sent.</param>
    public sealed record Result(int Sent, bool Suppressed, string? Reason)
    {
        public static readonly Result NoChange = new(0, false, null);
    }

    /// <summary>
    /// Announce a room/time change on <paramref name="sessionId"/>. Silent when neither actually
    /// changed, and silent (but reported) during the §1001 quiet period.
    /// </summary>
    public async Task<Result> NotifyAsync(
        int eventId, int sessionId,
        DateTimeOffset? oldStart, DateTimeOffset? oldEnd, string? oldRoom,
        DateTimeOffset? newStart, DateTimeOffset? newEnd, string? newRoom,
        CancellationToken ct = default)
    {
        var timeChanged = oldStart != newStart || oldEnd != newEnd;
        var roomChanged = !RoomEquals(oldRoom, newRoom);
        if (!timeChanged && !roomChanged) return Result.NoChange;
        if (_sender is null || _templates is null)
            return new Result(0, false, "no e-mail sender configured");

        // §1001 — the quiet period. Checked here so every call site inherits it.
        var setting = await _db.SessionSourceSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.EventId == eventId, ct);
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        if (setting?.MayNotifySpeakers(today) != true)
        {
            return new Result(0, true,
                setting?.SpeakerScheduleNoticeFrom is { } from
                    ? $"speakers not notified — schedule notices begin {from:d MMM yyyy}"
                    : "speakers not notified — no notice-start date is set");
        }

        var session = await _db.Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId && s.EventId == eventId)
            .Select(s => new { s.Title })
            .FirstOrDefaultAsync(ct);
        if (session is null) return new Result(0, false, "session not found");

        var speakers = await _db.SessionSpeakers
            .Where(ss => ss.SessionId == sessionId)
            .Select(ss => new { ss.ParticipantId, ss.Participant.Email, ss.Participant.FullName })
            .ToListAsync(ct);

        var oldTime = FormatRange(oldStart, oldEnd);
        var newTime = FormatRange(newStart, newEnd);
        var oldRoomText = string.IsNullOrWhiteSpace(oldRoom) ? "TBD" : oldRoom!;
        var newRoomText = string.IsNullOrWhiteSpace(newRoom) ? "TBD" : newRoom!;

        var sent = 0;
        foreach (var sp in speakers)
        {
            if (string.IsNullOrWhiteSpace(sp.Email)) continue;

            var scope = _context?.Set(new EmailContext(
                "session-change", eventId, null, sp.FullName,
                TemplateName: TemplateName,
                FeatureKey: SessionChangeDetectionService.FeatureKey));
            try
            {
                var tokens = _templates.NewTokenSet(sp.ParticipantId);
                tokens["firstName"] = FirstName(sp.FullName);
                tokens["sessionTitle"] = session.Title;
                tokens["oldTime"] = oldTime;
                tokens["newTime"] = newTime;
                tokens["oldRoom"] = oldRoomText;
                tokens["newRoom"] = newRoomText;
                tokens["changeRowsHtml"] = BuildChangeRowsHtml(
                    timeChanged, oldTime, newTime, roomChanged, oldRoomText, newRoomText,
                    tokens.TryGetValue("brandColor", out var bc) && !string.IsNullOrWhiteSpace(bc)
                        ? bc! : "#1565c0");

                var rendered = _templates.Render(TemplateName, tokens);
                await _sender.SendAsync(sp.Email, rendered.Subject, rendered.HtmlBody, ct);
                sent++;
            }
            finally
            {
                scope?.Dispose();
            }
        }

        _log?.LogInformation(
            "§1004: schedule change on session {Session} announced to {Count} speaker(s).",
            sessionId, sent);
        return new Result(sent, false, null);
    }

    /// <summary>§997 — the When cell, in EVENT-LOCAL (Danish) time, never the raw UTC instant.</summary>
    private static string FormatRange(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null) return "TBD";
        var tz = EventTimezone.Tz;
        var text = TimeZoneInfo.ConvertTime(start.Value, tz)
            .ToString("ddd dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        return end is { } e
            ? text + "–" + TimeZoneInfo.ConvertTime(e, tz).ToString("HH:mm", CultureInfo.InvariantCulture)
            : text;
    }

    /// <summary>§997 — ONLY the rows that changed; the template renders this verbatim.</summary>
    private static string BuildChangeRowsHtml(
        bool timeChanged, string oldTime, string newTime,
        bool roomChanged, string oldRoom, string newRoom, string brandColor)
    {
        string Row(string label, string oldValue, string newValue) =>
            "<tr>"
            + "<td style=\"padding:10px 12px;border:1px solid #e5e7eb;background:#f9fafb;"
            + "font-weight:bold;width:34%;\">" + Enc(label) + "</td>"
            + "<td style=\"padding:10px 12px;border:1px solid #e5e7eb;\">"
            + "<span style=\"color:#9ca3af;text-decoration:line-through;\">" + Enc(oldValue) + "</span><br>"
            + "<strong style=\"color:" + Enc(brandColor) + ";\">" + Enc(newValue) + "</strong>"
            + "</td></tr>";

        var sb = new System.Text.StringBuilder();
        if (timeChanged) sb.Append(Row("When", oldTime, newTime));
        if (roomChanged) sb.Append(Row("Where", oldRoom, newRoom));
        return sb.ToString();
    }

    private static bool RoomEquals(string? a, string? b) =>
        string.Equals(
            string.IsNullOrWhiteSpace(a) ? null : a.Trim(),
            string.IsNullOrWhiteSpace(b) ? null : b.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static string FirstName(string? full) =>
        string.IsNullOrWhiteSpace(full) ? "there" : full.Trim().Split(' ')[0];

    private static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
}
