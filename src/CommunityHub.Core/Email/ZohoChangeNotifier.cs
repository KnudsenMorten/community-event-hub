using System.Text;
using Microsoft.Extensions.Options;
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
    ///
    /// <para>⚠️ §736 (operator 2026-07-31) — <b>this is no longer where a CEH→Zoho change notice
    /// goes.</b> He restated the line as *"only alert mails goes to mok@expertslive.dk"*, and a
    /// publish/delete notice is not an alert, it is a job for whoever is free. `NotifyAsync` now
    /// defaults to <see cref="ActionableRecipient"/>; this constant remains for a genuinely
    /// informational notice, of which there are none today.</para>
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

    /// <summary>
    /// §1121 — the <paramref name="area"/> values whose notices are SPEAKER / SESSION organizer
    /// to-dos, and therefore also go to <see cref="EmailOptions.SpeakerSessionAlsoTo"/>.
    /// </summary>
    /// <remarks>
    /// <para>These are the exact literals the six call sites pass today. The other four —
    /// <i>Exhibitor profiles</i>, <i>Sponsors / exhibitors</i>, <i>Coupon invoicing</i>,
    /// <i>Webshop orders</i> — are deliberately absent: he named speakers and sessions.</para>
    ///
    /// <para>🔒 <b>Matched by PREFIX, not equality.</b> <c>"Speakers — details missing in
    /// Backstage"</c> already exists alongside plain <c>"Speakers"</c>, so the area strings visibly
    /// grow qualifiers over time. Exact matching would mean the next such variant silently loses its
    /// extra recipients with nothing failing anywhere — the quiet kind of wrong. A prefix rule errs
    /// the other way: a new <c>"Speakers — …"</c> area is included by default, which is what someone
    /// adding it would expect.</para>
    /// </remarks>
    public static readonly IReadOnlyList<string> SpeakerSessionAreaPrefixes = new[]
    {
        "Speakers",          // "Speakers", "Speakers — details missing in Backstage"
        "Agenda / sessions", // the session push (the "ACTION NEEDED: session '…'" mail)
    };

    /// <summary>
    /// §1121 — is this area a speaker/session organizer to-do? Case-insensitive prefix match against
    /// <see cref="SpeakerSessionAreaPrefixes"/>.
    /// </summary>
    public static bool IsSpeakerOrSessionArea(string? area) =>
        !string.IsNullOrWhiteSpace(area)
        && SpeakerSessionAreaPrefixes.Any(p =>
            area.Trim().StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// §1123 — is this area the one whose Backstage API is genuinely CREATE-ONLY (speakers)?
    /// </summary>
    /// <remarks>
    /// 🔒 Deliberately NARROWER than <see cref="IsSpeakerOrSessionArea"/>: sessions are created AND
    /// updated, so "Agenda / sessions" must not claim create-only either. Only the speakers areas do.
    /// </remarks>
    public static bool IsSpeakerArea(string? area) =>
        !string.IsNullOrWhiteSpace(area)
        && area.Trim().StartsWith("Speakers", StringComparison.OrdinalIgnoreCase);

    private readonly EngineAlertSender _alerts;
    private readonly ILogger<ZohoChangeNotifier>? _log;

    // §1121 — optional so every existing test construction keeps compiling. Null ⇒ no extra
    // recipients, i.e. exactly the pre-§1121 behaviour, never a crash.
    private readonly EmailOptions? _emailOptions;

    public ZohoChangeNotifier(
        EngineAlertSender alerts,
        ILogger<ZohoChangeNotifier>? log = null,
        IOptions<EmailOptions>? emailOptions = null)
    {
        _alerts = alerts;
        _log = log;
        _emailOptions = emailOptions?.Value;
    }

    /// <summary>
    /// Send ONE ops mail describing a batch of CEH-made Zoho Backstage writes.
    /// <paramref name="area"/> names the surface (e.g. "Agenda / sessions");
    /// <paramref name="changes"/> are human-readable lines (e.g. "Created session
    /// 'Azure Master Class' (Backstage id 123)"). Empty batch ⇒ no mail. Never throws.
    /// </summary>
    /// <param name="actionable">
    /// 🔒 §736 — DEFAULTS TO TRUE (operator 2026-07-31: *"this mail should go to info@expertslive.dk
    /// so any organizer can do it … only alert mails goes to mok@expertslive.dk"*).
    ///
    /// <para>Every mail this notifier sends says, in its own body, *"please open Backstage and
    /// publish the change and/or delete any now-redundant item"*. There is no informational variant
    /// — the whole reason it exists is that a HUMAN must go and do something. So "actionable" is not
    /// a property of the call site; it is a property of this notifier.</para>
    ///
    /// <para>The default was <c>false</c> and NO caller ever passed <c>true</c>, so all six send
    /// sites routed to the single operator mailbox. §556 had already drawn the right line
    /// (*"alerts that require actions from organizers like this goes to info@ as we should be more
    /// that can fix this"*) and built <see cref="ActionableRecipient"/> for it — the default simply
    /// never followed. Flipping it here fixes every site at once and makes the wrong thing the one
    /// you have to ask for.</para>
    /// </param>
    /// <param name="intro">
    /// §745 — an optional lead-in rendered ABOVE the change list and deliberately NOT counted.
    /// Operator 2026-07-31: *"it says 3 changes in subject but mention 2, why. is skill conuted as
    /// 2"*. It was neither — the speaker-edit mail passed its "ACTION NEEDED …" heading as the FIRST
    /// ENTRY of the change list, so the subject counted the heading as a change. Anything that is
    /// not a field change belongs here, where it cannot inflate the number.
    /// </param>
    /// <param name="manualOnly">
    /// 🔒 §763 — TRUE when CEH wrote NOTHING and every line is hand-work for the operator.
    /// </param>
    /// <remarks>
    /// Operator 2026-08-01: <i>"this is wrong as hub cannot update an existing speaker via api.
    /// there is no update endpoint in zoho. therefore it is a manual task - wording is wrong"</i>.
    /// The Backstage <b>speakers</b> API is CREATE-ONLY (per-id POST/PUT/PATCH and DELETE all answer
    /// 404 "Please provide valid method", live-verified 2026-06-25), so a speaker mail must never
    /// say the hub "just wrote" anything or ask him to "publish" it — there is nothing there to
    /// publish. Sessions are different: those really are created, and keep the original wording.
    /// </remarks>
    public async Task NotifyAsync(
        string area, IReadOnlyList<string> changes, CancellationToken ct,
        bool actionable = true, string? actionUrl = null, string? actionText = null,
        string? intro = null, bool manualOnly = false)
    {
        try
        {
            if (changes is null || changes.Count == 0) return; // nothing written ⇒ no mail

            var (subject, html) = Build(area, changes, actionUrl, actionText, intro, manualOnly);
            // §556 — an alert someone must ACT on goes to the shared ops inbox so more than one
            // person can fix it; a pure record of what the hub wrote stays with the single operator
            // (§493), because for the shared inbox that is noise nobody can act on.
            var to = actionable ? ActionableRecipient : Recipient;

            // §1121/§1124 — a speaker/session to-do goes to the SHARED audience
            // (EmailOptions.SpeakerSessionRecipients), not to a locally-assembled list.
            //
            // 🔒 Gated on `actionable` as well as the area. A non-actionable notice goes to the
            // single operator mailbox by construction (§736), and putting two more people on the
            // To: of a mail that asks nobody to do anything is the noise §493 removed from info@ in
            // the first place.
            var speakerSession = actionable && IsSpeakerOrSessionArea(area)
                ? _emailOptions?.SpeakerSessionRecipients()
                : null;

            if (speakerSession is { Count: > 0 })
            {
                await _alerts.AlertToAsync(
                    speakerSession, subject, html, ct, throttleKey: null, devSilent: true);
                return;
            }
            // throttleKey: null — every real change batch must reach the operator.
            // §752.9 — DEV-silent: these announce CHANGES to test data, and say "publish/delete may
            // be needed" about a sandbox nobody publishes. throttleKey stays null for PROD, where
            // every real change batch must reach the operator.
            await _alerts.AlertAsync(subject, html, ct, throttleKey: null, recipient: to,
                devSilent: true);
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
    public static (string Subject, string Html) Build(
        string area, IReadOnlyList<string> changes, string? actionUrl = null,
        string? actionText = null, string? intro = null, bool manualOnly = false)
    {
        // 🔒 §745 — the count is the number of CHANGES, and `changes` must therefore contain only
        // changes. A caller with a heading passes it as `intro`; putting it in this list made the
        // subject say 3 while the body listed 2.
        //
        // 🔒 §763 — the SUBJECT follows the same truth as the body. "publish/delete may be needed"
        // describes an outcome that never happened for a create-only area, and he read the subject
        // before the body.
        var subject = manualOnly
            ? $"[CEH→Zoho] {area}: {changes.Count} item(s) to add by hand in Backstage"
            : $"[CEH→Zoho] {area}: {changes.Count} change(s) — publish/delete may be needed";

        var sb = new StringBuilder();
        if (manualOnly)
        {
            // 🔴 §763 — CEH WROTE NOTHING. The old preamble ("the hub just wrote … please publish")
            // was inherited from the session push, where it is true, and it flatly contradicted the
            // body two lines below ("the speakers API is create-only"). He went looking for a change
            // that was not there.
            sb.Append("<p><strong>CEH could not write these to Zoho Backstage</strong> — ");

            // 🔴 §1123 — THE REASON MUST MATCH THE AREA. Operator 2026-08-25, on a
            // "Sponsors / exhibitors" mail: *"it also refers to speakes api"*.
            //
            // 🔑 §763 wrote this preamble for the create-only SPEAKERS area and hard-coded its
            // reason into the shared builder. Every later area that set `manualOnly` — sponsors and
            // exhibitors (§792), coupon invoicing — then inherited a sentence about an API it does
            // not use. And it is not merely off-topic: it states, in bold, that no update endpoint
            // exists, which for exhibitors is FALSE (ZohoClient.UpdateExhibitorAsync PUTs website,
            // overview, short description and social pages). A wrong explanation is worse than none
            // — it teaches him the system cannot do something it does every sync.
            //
            // ⇒ The create-only claim is made ONLY for the area it is true of. Everywhere else the
            // preamble says what is certain (these values did not reach Backstage; enter them by
            // hand) and does not invent a cause. The per-area REASON belongs to the caller, which
            // knows why its own push did not land; the shared builder must not guess.
            if (IsSpeakerArea(area))
            {
                sb.Append("the Backstage speakers API is <strong>create-only</strong>, with no ")
                  .Append("update or delete endpoint. Nothing has changed over there, so there is ")
                  .Append("nothing to publish: please open Backstage and ");
            }
            else
            {
                sb.Append("these values did not reach Backstage, so there is nothing to publish ")
                  .Append("over there. Please open Backstage and ");
            }

            sb.Append("<strong>make the changes below by hand</strong>.</p>");
        }
        else
        {
            sb.Append("<p>The hub just wrote the following change(s) to Zoho Backstage. ")
              .Append("API writes are <strong>not auto-published</strong> — please open Backstage and ")
              .Append("<strong>publish</strong> the change and/or <strong>delete</strong> any ")
              .Append("now-redundant item so the public event site stays correct.</p>");
        }

        if (!string.IsNullOrWhiteSpace(intro))
        {
            // Same encode-or-trust rule as the list items below (§558): a caller that composes its
            // own emphasis keeps it; plain text is encoded.
            sb.Append("<p>")
              .Append(RendersAsHtml(intro)
                  ? intro.Replace("\n", "<br/>")
                  : System.Net.WebUtility.HtmlEncode(intro).Replace("\n", "<br/>"))
              .Append("</p>");
        }

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
        || s.Contains("<code", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<br", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<i>", StringComparison.OrdinalIgnoreCase)
        // 🔴 §898 — <strong> and <em> were MISSING, and the callers use them. An intro written as
        // "Zoho Backstage <strong>ignores API updates</strong>…" failed this test, was encoded, and
        // printed the raw tags in his inbox. Same defect as §558 and §518, a third time — which is
        // the argument for matching the OPENING bracket of the tags we actually emit rather than
        // an exact spelling that keeps drifting out of date.
        || s.Contains("<strong", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<em", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<span", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<a ", StringComparison.OrdinalIgnoreCase);
}
