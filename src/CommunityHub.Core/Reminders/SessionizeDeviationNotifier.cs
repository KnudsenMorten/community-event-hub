using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Sessions;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §999 — mails the actionable mailbox when Sessionize disagrees with a CEH-owned session field.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-09: *"date/time, location/room and tags are at this point owned by CEH
/// and should not be overwritten automatically, but the comparison must state exactly which fields
/// differ between Sessionize and ceh. Mail must be sent to info@ … and then the organizer will
/// evaluate and maybe change, but ceh could have more correct info here."*</para>
///
/// <para>🔑 <b>This mail exists because the import stopped overwriting.</b> Before §999 these
/// fields were silently replaced hourly; the disagreement was resolved by force and nobody saw it.
/// Now CEH keeps its value and the difference becomes a decision — which is only an improvement if
/// somebody is actually told, so this is the other half of that change, not an optional extra.</para>
///
/// <para>🔒 <b>Nothing here is an instruction.</b> Every other ACTION NEEDED mail in the hub names a
/// thing to go and do. This one deliberately does not: he said *"ceh could have more correct
/// info"*, so the mail presents both values and stops. Wording it as "fix this" would push him
/// toward Sessionize's value, which is exactly the assumption the change was made to remove.</para>
///
/// <para>⚠️ <b>No throttle key.</b> Each mail names the specific fields that differ on this pass;
/// suppressing a later one because an earlier went out would drop a difference silently. The volume
/// control is upstream — <c>AddDeviation</c> ignores formatting-only differences and blank
/// Sessionize values, so a session only appears when a human really changed something.</para>
/// </remarks>
public sealed class SessionizeDeviationNotifier
{
    private readonly EngineAlertSender _alerts;
    private readonly ILogger<SessionizeDeviationNotifier>? _log;

    // §1124 — the shared speaker/session audience. Optional so existing test constructions keep
    // compiling; null falls back to the shared inbox alone, i.e. the pre-§1124 behaviour.
    private readonly EmailOptions? _emailOptions;

    public SessionizeDeviationNotifier(
        EngineAlertSender alerts, ILogger<SessionizeDeviationNotifier>? log = null,
        Microsoft.Extensions.Options.IOptions<EmailOptions>? emailOptions = null)
    {
        _alerts = alerts;
        _log = log;
        _emailOptions = emailOptions?.Value;
    }

    /// <summary>Announce this import pass's deviations. Silent when there are none (§302).</summary>
    public async Task<bool> NotifyAsync(
        IReadOnlyList<SessionizeFieldDeviation> deviations, CancellationToken ct = default)
    {
        if (deviations is null || deviations.Count == 0) return false;

        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        static string Show(string? s) => string.IsNullOrWhiteSpace(s) ? "(empty)" : s!;

        var sessions = deviations
            .GroupBy(d => d.SessionTitle, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = string.Join("", sessions.Select(g =>
            $"<tr><td colspan=\"3\" style=\"padding:10px 10px 4px;font-weight:bold;\">{Enc(g.Key)}</td></tr>"
            + string.Join("", g.Select(d =>
                "<tr>"
                + $"<td style=\"padding:4px 10px;border-bottom:1px solid #e5e7eb;color:#6b7280;\">{Enc(d.Field)}</td>"
                + $"<td style=\"padding:4px 10px;border-bottom:1px solid #e5e7eb;\"><strong>{Enc(Show(d.InCeh))}</strong></td>"
                + $"<td style=\"padding:4px 10px;border-bottom:1px solid #e5e7eb;color:#6b7280;\">{Enc(Show(d.InSessionize))}</td>"
                + "</tr>"))));

        var body =
            $"<p><strong>Sessionize disagrees with the hub on {deviations.Count} field(s) "
            + $"across {sessions.Count} session(s).</strong></p>"
            + "<p>The hub owns the schedule, room, track and tags, so <strong>nothing was "
            + "changed</strong> — these are for you to judge. The hub's value may well be the "
            + "correct one.</p>"
            + "<table style=\"border-collapse:collapse;font-size:14px;\">"
            + "<tr>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">Field</th>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">In the hub (kept)</th>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">In Sessionize</th>"
            + "</tr>"
            + rows
            + "</table>"
            + "<p style=\"color:#6b7280;font-size:13px;\">Title, description and speakers are still "
            + "taken from Sessionize automatically and are not listed here. If the hub's value is "
            + "the one that is wrong, change it in the hub — Sessionize will not overwrite it.</p>";

        // §1124 — a per-session decision for an organizer, so it goes to the shared speaker/session
        // audience rather than the shared inbox alone.
        await _alerts.AlertToAsync(
            _emailOptions?.SpeakerSessionRecipients()
                ?? new[] { ZohoChangeNotifier.ActionableRecipient },
            $"Sessionize differs from the hub on {sessions.Count} session(s)",
            body, ct, throttleKey: null);

        _log?.LogInformation(
            "§999: reported {Count} Sessionize deviation(s) across {Sessions} session(s).",
            deviations.Count, sessions.Count);
        return true;
    }
}
