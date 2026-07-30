using System.Text;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Email;

/// <summary>
/// RULE (operator 2026-07-23): EVERY CEH-made WRITE to Zoho Backstage — agenda/sessions,
/// halls, speakers, sponsors/exhibitors (incl. booth members and exhibitor profile
/// updates) — must notify the event ops mailbox (<c>info@expertslive.dk</c>), because the
/// operator must manually DELETE redundant items and/or PUBLISH the change in Backstage
/// (API writes are not auto-published).
///
/// Design:
///  - BATCH-PER-RUN: each engine pass / save sends ONE mail listing all its changes,
///    never one mail per item.
///  - NO throttle key: every real change batch must arrive (the operator acts on each),
///    so this deliberately bypasses the <see cref="EngineAlertSender"/> 6h suppression.
///  - Rides the ring-exempt <see cref="EngineAlertSender"/> (an ops address is not a
///    ring-gated participant and would otherwise be dropped). INTERNAL ops mail only —
///    participant mail stays ring-governed.
///  - NEVER throws (mirrors the alert sender's guarantee: a mail failure must not break
///    the engine that just pushed), and skips silently when the change list is empty
///    (no write ⇒ no mail).
/// </summary>
public sealed class ZohoChangeNotifier
{
    /// <summary>
    /// §493 (operator 2026-07-27: <i>"alerts to mok@expertslive.dk only"</i>) — these are SYSTEM
    /// alerts (CEH→Zoho writes needing a manual publish/delete, and the §470 "this speaker still
    /// exists in Zoho" notice), not participant correspondence. They now go to the single operator
    /// mailbox rather than the shared <c>info@</c> inbox, which is read by several people who
    /// cannot act on them and for whom they are pure noise.
    ///
    /// <para>Matches <see cref="EngineAlertSender.Recipient"/>, so EVERY system alert in the
    /// product lands in one place.</para>
    /// </summary>
    public const string Recipient = "mok@expertslive.dk";

    /// <summary>
    /// §556 — where an alert goes when a HUMAN MUST DO SOMETHING to fix it.
    ///
    /// <para>Operator 2026-07-28: <i>"alerts that require actions from organizers like this goes to
    /// info@expertslive.dk as we should be more that can fix this"</i>. This REFINES §493 rather
    /// than reversing it: yesterday's rule ("alerts to mok@ only") exists because info@ is read by
    /// several people for whom pure system noise is useless. The distinction is therefore not
    /// "system vs participant" but <b>actionable vs informational</b> — if a person has to open
    /// Backstage and fix something, more than one person should be able to.</para>
    /// </summary>
    public const string ActionableRecipient = "info@expertslive.dk";

    private readonly EngineAlertSender _alerts;
    private readonly ILogger<ZohoChangeNotifier>? _log;

    public ZohoChangeNotifier(EngineAlertSender alerts, ILogger<ZohoChangeNotifier>? log = null)
    {
        _alerts = alerts;
        _log = log;
    }

    /// <summary>
    /// Send ONE ops mail describing a batch of CEH-made Zoho Backstage writes.
    /// <paramref name="area"/> names the surface (e.g. "Agenda / sessions");
    /// <paramref name="changes"/> are human-readable lines (e.g. "Created session
    /// 'Azure Master Class' (Backstage id 123)"). Empty batch ⇒ no mail. Never throws.
    /// </summary>
    public async Task NotifyAsync(
        string area, IReadOnlyList<string> changes, CancellationToken ct,
        bool actionable = false, string? actionUrl = null, string? actionText = null)
    {
        try
        {
            if (changes is null || changes.Count == 0) return; // nothing written ⇒ no mail

            var (subject, html) = Build(area, changes, actionUrl, actionText);
            // §556 — an alert someone must ACT on goes to the shared ops inbox so more than one
            // person can fix it; a pure record of what the hub wrote stays with the single operator
            // (§493), because for the shared inbox that is noise nobody can act on.
            var to = actionable ? ActionableRecipient : Recipient;
            // throttleKey: null — every real change batch must reach the operator.
            await _alerts.AlertAsync(subject, html, ct, throttleKey: null, recipient: to);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Zoho change notification failed for area {Area}.", area);
        }
    }

    /// <summary>
    /// Build the subject + HTML body for a change batch. Public + pure so a test can
    /// assert the format without sending.
    /// </summary>
    public static (string Subject, string Html) Build(string area, IReadOnlyList<string> changes, string? actionUrl = null, string? actionText = null)
    {
        var subject =
            $"[CEH→Zoho] {area}: {changes.Count} change(s) — publish/delete may be needed";

        var sb = new StringBuilder();
        sb.Append("<p>The hub just wrote the following change(s) to Zoho Backstage. ")
          .Append("API writes are <strong>not auto-published</strong> — please open Backstage and ")
          .Append("<strong>publish</strong> the change and/or <strong>delete</strong> any ")
          .Append("now-redundant item so the public event site stays correct.</p>");
        sb.Append("<ul>");
        // §322m (operator: "make the email more easy to cut/paste from"): a change entry
        // may be MULTI-LINE ("\n") — e.g. a label line followed by the full description
        // text or the comma-separated tags line to paste. Encode first, then turn the
        // newlines into real <br/> breaks so the paste blocks aren't cramped into one line.
        foreach (var change in changes)
            sb.Append("<li style=\"margin-bottom:10px;\">")
              .Append(RendersAsHtml(change)
                  // §558 — an ACTIONABLE alert composes its own emphasis (bold headings, ids in
                  // <code>). Encoding it turned that into literal "<b>" text in the operator's
                  // inbox — the same defect as §518, where encoding a finished HTML fragment made
                  // the ERP orphan mail unreadable. Callers that pass PLAIN text still get encoded.
                  ? change.Replace("\n", "<br/>")
                  : System.Net.WebUtility.HtmlEncode(change).Replace("\n", "<br/>"))
              .Append("</li>");
        sb.Append("</ul>");

        // §558 — link to the page the reader must actually OPEN. This footer always sent them to
        // the Zoho Backstage admin, which is right for "we wrote these changes, go publish them"
        // and wrong for anything else (operator 2026-07-28: "but why does it take me to zoho admin
        // and not the page in scope").
        sb.Append(string.IsNullOrWhiteSpace(actionUrl)
            ? "<p><a href=\"https://backstage.zoho.eu/\">Open the Zoho Backstage admin &rarr;</a></p>"
            : $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(actionUrl)}\">"
              + $"{System.Net.WebUtility.HtmlEncode(actionText ?? "Open the page")} &rarr;</a></p>");

        return (subject, sb.ToString());
    }

    /// <summary>
    /// §558 — true when a caller has composed an HTML fragment (bold, code, breaks) rather than
    /// plain prose. Deliberately narrow: only the tags these alerts actually use count, so a change
    /// line that merely mentions "&lt;" in text is still encoded.
    /// </summary>
    private static bool RendersAsHtml(string s) =>
        s.Contains("<b>", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<code>", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<br>", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<i>", StringComparison.OrdinalIgnoreCase);
}
