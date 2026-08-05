using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §720 — tell the operator the moment someone finishes their Get Started, so he can reach out
/// while it is still fresh.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-31: <i>"i would like to get email to mok@expertslive.dk when someone
/// completes the get started wizard in full. - make a notification on/off feature in settings for
/// this. then i know it and can reach out to ask them for their experience"</i>, and on what
/// "in full" means: <i>"Fire at 100%"</i>.</para>
///
/// <para>🔑 <b>100% means what the progress bar means.</b> The same <c>AllDone</c> the wizard shows
/// and <c>getstarted-digest</c> stops on — deliberately not a second definition. §732 is what makes
/// that reachable at all: before optional steps counted as done (Master Class waitlist, booth
/// materials), a person could finish everything they were able to and still never hit 100%, so this
/// mail would never have fired for them.</para>
///
/// <para>🔒 <b>ONCE EVER, on the TRANSITION.</b> The wizard is re-saved every time someone revisits
/// a finished step, so "is it complete?" is true on every later post too. The <c>SentReminders</c>
/// ledger row (<c>getstarted-complete:{participantId}</c>) is what turns that into a single mail —
/// the same idempotency shape §326bf uses for the welcome.</para>
///
/// <para>Rides the ring-exempt <see cref="EngineAlertSender"/>: the recipient is a FIXED operator
/// mailbox, not a ring-gated participant, so there is no audience to resolve. Never throws — a
/// notification failure must not turn someone's completed wizard into an error.</para>
/// </remarks>
public sealed class GetStartedCompletionNotifier
{
    /// <summary>The Settings on/off he asked for. No ring: see <c>FeatureCatalog</c>.</summary>
    public const string FeatureKey = "getstarted-complete-notice";

    /// <summary>Ledger key for the once-ever guard.</summary>
    public const string ReminderType = "getstarted-complete";

    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly EngineAlertSender _alerts;
    private readonly TimeProvider _clock;
    // §737 — for the edition's hub origin, so the link in this mail is ABSOLUTE.
    private readonly EmailTemplateProvider? _templates;

    public GetStartedCompletionNotifier(
        CommunityHubDbContext db, FeatureGateService gate, EngineAlertSender alerts,
        TimeProvider clock, EmailTemplateProvider? templates = null)
    {
        _db = db;
        _gate = gate;
        _alerts = alerts;
        _clock = clock;
        _templates = templates;
    }

    public static string OccasionKeyFor(int participantId) => $"getstarted-complete:{participantId}";

    /// <summary>§737 — an ABSOLUTE deep link into the people list, or plain text when no hub
    /// origin is configured. Never a relative href (see the call site).</summary>
    private string PeopleLinkHtml(string? email)
    {
        string? origin = null;
        try
        {
            if (_templates?.NewTokenSet().TryGetValue("hubUrl", out var h) == true
                && !string.IsNullOrWhiteSpace(h))
            {
                origin = h.TrimEnd('/');
            }
        }
        catch { /* never break the notice over a link */ }

        var q = Uri.EscapeDataString(email ?? string.Empty);
        return origin is null
            ? "<p>Find them under <strong>Organizer &rarr; Participants</strong>.</p>"
            : $"<p><a href=\"{origin}/Organizer/Participants?Search={q}\">Find them in the people list</a></p>";
    }

    /// <summary>
    /// Send the notice if this participant has JUST reached 100% and has not been reported before.
    /// Returns true when a mail was sent. Never throws.
    /// </summary>
    public async Task<bool> NotifyIfNewlyCompleteAsync(
        int eventId, int participantId, bool allDone, CancellationToken ct = default)
    {
        if (!allDone) return false;

        try
        {
            if (!await _gate.IsFeatureEnabledAsync(FeatureKey, eventId, ct)) return false;

            var occasion = OccasionKeyFor(participantId);
            var already = await _db.SentReminders.AnyAsync(
                s => s.EventId == eventId
                     && s.ReminderType == ReminderType
                     && s.OccasionKey == occasion, ct);
            if (already) return false;

            var who = await _db.Participants.AsNoTracking()
                .Where(p => p.Id == participantId && p.EventId == eventId)
                .Select(p => new { p.FullName, p.Email, p.Role })
                .FirstOrDefaultAsync(ct);
            if (who is null) return false;

            var now = _clock.GetUtcNow();
            var name = System.Net.WebUtility.HtmlEncode(
                string.IsNullOrWhiteSpace(who.FullName) ? who.Email : who.FullName);
            var mail = System.Net.WebUtility.HtmlEncode(who.Email ?? string.Empty);

            var html =
                $"<p><strong>{name}</strong> ({who.Role}) has just completed their Get Started "
                + "wizard — every step done.</p>"
                + "<ul>"
                + $"<li>E-mail: {mail}</li>"
                + $"<li>Finished: {now:yyyy-MM-dd HH:mm} UTC</li>"
                + "</ul>"
                + "<p>A good moment to ask them how the onboarding felt, while it is fresh.</p>"
                // 🔴 §737 — ABSOLUTE, never relative. A relative href in an e-mail has no base:
                // Brevo's click tracker resolves it against its OWN domain and the reader lands on
                // Brevo's "Page not found". That is the exact bug he reported on the sync-approval
                // mail the same day — and I had just written it again here. With no configured
                // origin, emit plain text: a dead link is worse than none.
                + PeopleLinkHtml(who.Email);

            // §742 — WHERE it goes is now the operator's, per edition, set on the Settings page
            // beside the on/off. Unset ⇒ the built-in mailbox, so this is unchanged until he
            // changes it. Operator: *"it must go to mok@expertslive.dk and i need to be able to
            // control where it goes and state in settings page"*.
            var recipient = await _gate.GetNotificationRecipientAsync(
                FeatureKey, eventId, EngineAlertSender.Recipient, ct);

            // throttleKey: null — each PERSON completing is its own news; the once-ever ledger row
            // below is the anti-repeat guard, not a time window.
            await _alerts.AlertAsync(
                $"Get Started completed: {who.FullName} [ELDK27]", html, ct,
                throttleKey: null, recipient: recipient,
                // §752.9 — DEV-silent: on DEV the person completing is a seeded test participant, so
                // it is one mail per fixture. 🔒 The once-ever ledger row is written regardless of
                // whether the mail goes, so suppression here cannot cause a PROD duplicate later.
                devSilent: true);

            _db.SentReminders.Add(new SentReminder
            {
                EventId = eventId,
                // 🔒 The ADDRESS THAT WAS USED, not the default — otherwise the ledger would claim
                // a mail went somewhere it did not, and the once-ever guard would be unauditable.
                RecipientEmail = recipient,
                ReminderType = ReminderType,
                OccasionKey = occasion,
                SentAt = now.UtcDateTime,
            });
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch
        {
            // Never let a notification break the save that triggered it (the EngineAlertSender's
            // own guarantee, extended to the ledger write).
            return false;
        }
    }
}
