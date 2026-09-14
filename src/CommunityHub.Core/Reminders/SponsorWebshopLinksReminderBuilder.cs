using System.Globalization;
using System.Net;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §1127 — chases a sponsor whose <b>webshop</b> website or LinkedIn is blank, and tells the
/// operator which companies those are.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-25: <i>"if weburl + linkedin are blank in webshop, sponsor contact +
/// mok@expertslive.dk must get an alert. twitter is optional"</i>.</para>
///
/// <para>🔑 <b>Why this mail has to exist at all, and only now.</b> Before §1125/§1126 a blank
/// webshop field was recoverable — the sponsor could type the URL straight into CEH. Those sections
/// made the webshop AUTHORITATIVE for all three URLs and the CEH inputs read-only, so a blank in the
/// webshop is now a blank on the public event site with <b>no way to fix it from inside the hub</b>.
/// Making a field unfixable in one place obliges you to chase it in the other; shipping §1126 without
/// this would have quietly converted a self-service fix into a dead end.</para>
///
/// <para>🔒 <b>X/Twitter is NEVER chased</b> — <i>"twitter is optional"</i>, in his words. It is
/// deliberately absent from <see cref="MissingFor"/> rather than filtered later, so there is no
/// switch anyone can flip by accident.</para>
///
/// <para>⚠️ <b>Cadence is WEEKLY UNTIL FIXED, and that was my call, not his.</b> He specified the
/// trigger and the audience and not the frequency. Once-ever is a mail that gets skimmed and lost
/// over a thirty-second fix; per-run is the §302 "70-mail night". The OccasionKey therefore carries
/// an ISO <b>week index</b>, so the <see cref="ReminderEngine"/> ledger allows one per company
/// contact per week — and it <b>stops by itself</b> the moment both fields are filled, because a
/// company with nothing missing produces no message.</para>
///
/// <para>🔒 <b>Reads the WEBSHOP mirror in CEH, not the webshop API.</b> Since §1126 the sync
/// overwrites <c>SponsorInfo</c> from the webshop on every pass, so those columns ARE the webshop's
/// values. Calling Company Manager once per sponsor per run to learn the same thing would add a
/// per-company HTTP round trip to a reminder pass, and would disagree with what the sponsor sees in
/// the hub whenever the API was briefly unreachable.</para>
/// </remarks>
public sealed class SponsorWebshopLinksReminderBuilder
{
    private const string TemplateName = "sponsor-webshop-links-missing";

    /// <summary>ReminderType + OccasionKey prefix in the SentReminder ledger.</summary>
    public const string ReminderType = "sponsor-webshop-links";

    /// <summary>
    /// §1127 — the operator is told as well (<i>"sponsor contact + mok@expertslive.dk"</i>).
    /// </summary>
    /// <remarks>
    /// 🔒 <b>ONE SUMMARY, not a copy of every sponsor's chaser.</b> Both audiences "get an alert",
    /// but N companies × M coordinators worth of duplicates is the noise §493 exists to keep out of
    /// an inbox — and a list of every company still missing something is more useful to him than the
    /// individual nags anyway, because it is the thing he would otherwise have to assemble by hand.
    /// </remarks>
    public const string OperatorRecipient = "mok@expertslive.dk";

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly TimeProvider _clock;
    private readonly SponsorRecipientResolver _sponsorRecipients;
    private readonly EngineAlertSender? _alerts;
    private readonly string _webshopUrl;

    public SponsorWebshopLinksReminderBuilder(
        CommunityHubDbContext db,
        EmailTemplateProvider templates,
        TimeProvider clock,
        SponsorRecipientResolver sponsorRecipients,
        EngineAlertSender? alerts = null,
        string? webshopUrl = null)
    {
        _db = db;
        _templates = templates;
        _clock = clock;
        _sponsorRecipients = sponsorRecipients;
        _alerts = alerts;
        _webshopUrl = string.IsNullOrWhiteSpace(webshopUrl)
            ? "https://expertslive.dk/sponsor"
            : webshopUrl!;
    }

    /// <summary>What a company is missing. X/Twitter is deliberately not represented.</summary>
    /// <remarks>
    /// 🔒 Blank means blank: null, empty or whitespace. A value of <c>"-"</c> or <c>"n/a"</c> is a
    /// human saying "none", and guessing at those would start silently exempting companies that
    /// really are missing a link.
    /// </remarks>
    public static IReadOnlyList<string> MissingFor(string? websiteUrl, string? linkedInUrl)
    {
        var missing = new List<string>(2);
        if (string.IsNullOrWhiteSpace(websiteUrl)) missing.Add("website address");
        if (string.IsNullOrWhiteSpace(linkedInUrl)) missing.Add("LinkedIn address");
        return missing;
    }

    /// <summary>"website address" / "website address and LinkedIn address".</summary>
    public static string MissingLabel(IReadOnlyList<string> missing) =>
        missing.Count switch
        {
            0 => string.Empty,
            1 => missing[0],
            _ => string.Join(" and ", missing),
        };

    /// <summary>
    /// Build this run's chasers. Returns empty when every sponsor has both fields.
    /// </summary>
    public async Task<IReadOnlyList<ReminderMessage>> BuildAsync(
        int eventId, CancellationToken ct = default)
    {
        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (ev is null) return Array.Empty<ReminderMessage>();

        var companies = await _db.SponsorInfos.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => new
            {
                s.Id,
                s.SponsorCompanyId,
                s.CompanyName,
                s.WebsiteUrl,
                s.LinkedInUrl,
            })
            .ToListAsync(ct);

        // ISO week index — the cadence control. See the class remarks.
        var now = _clock.GetUtcNow();
        var week = ISOWeek.GetWeekOfYear(now.UtcDateTime);
        var weekKey = $"{ISOWeek.GetYear(now.UtcDateTime)}w{week:00}";

        var messages = new List<ReminderMessage>();
        var operatorRows = new List<string>();

        foreach (var co in companies)
        {
            var missing = MissingFor(co.WebsiteUrl, co.LinkedInUrl);
            if (missing.Count == 0) continue;   // ⇐ this is what makes the chaser stop by itself

            var companyName = string.IsNullOrWhiteSpace(co.CompanyName)
                ? "your company"
                : co.CompanyName!;

            operatorRows.Add(
                $"<li><strong>{WebUtility.HtmlEncode(companyName)}</strong> — missing "
                + $"{WebUtility.HtmlEncode(MissingLabel(missing))}</li>");

            if (string.IsNullOrWhiteSpace(co.SponsorCompanyId)) continue;

            // 🔒 The single authority for "who at a sponsor company receives this email" (§7c):
            // every EVENT-COORDINATOR contact, signer-only contacts never. Reusing it is what keeps
            // this mail's audience identical to every other sponsor mail.
            var contacts = await _sponsorRecipients.ResolveAsync(eventId, co.SponsorCompanyId!, ct);

            foreach (var c in contacts)
            {
                if (string.IsNullOrWhiteSpace(c.Email)) continue;

                var tokens = _templates.NewTokenSet(c.ParticipantId);
                tokens["firstName"] = c.FirstName;
                tokens["eventDisplayName"] = ev.DisplayName ?? string.Empty;
                tokens["companyName"] = companyName;
                tokens["missingLabel"] = MissingLabel(missing);
                tokens["missingListHtml"] =
                    "<ul style=\"margin:0;padding-left:18px;\">"
                    + string.Join(string.Empty, missing.Select(m =>
                        $"<li>{WebUtility.HtmlEncode(m)}</li>"))
                    + "</ul>";
                tokens["webshopUrl"] = _webshopUrl;

                var rendered = _templates.Render(TemplateName, tokens);

                messages.Add(new ReminderMessage(
                    RecipientEmail: c.Email,
                    ReminderType: ReminderType,
                    // 🔑 The WEEK is in the key, so the ledger allows one per contact per week.
                    // ⚠️ The MISSING SET is deliberately NOT in the key: if it were, a company that
                    // fills the website but not LinkedIn would look like a brand-new occasion and be
                    // chased again immediately, which reads as punishment for a partial fix.
                    OccasionKey: $"{ReminderType}:{co.Id}:{c.ParticipantId}:{weekKey}",
                    Subject: rendered.Subject,
                    HtmlBody: rendered.HtmlBody,
                    ParticipantId: c.ParticipantId,
                    RecipientName: c.FullName,
                    Cc: c.CcEmail is null ? null : new[] { c.CcEmail },
                    FeatureKey: "sponsor-reminders", MailKey: TemplateName));
            }
        }

        await AlertOperatorAsync(eventId, operatorRows, weekKey, ct);
        return messages;
    }

    /// <summary>
    /// §1127 — one weekly summary to <see cref="OperatorRecipient"/> listing every company still
    /// missing something. Sends nothing when the list is empty.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Guarded by the DURABLE ledger, not by <see cref="EngineAlertSender"/>'s throttle key.</b>
    /// That throttle is a 6-hour IN-MEMORY window — it would allow four summaries a day and reset to
    /// zero on every restart, which on a Functions host is often. "Weekly" has to be written down
    /// somewhere that survives the process, and <see cref="SentReminder"/> is where this codebase
    /// already writes exactly that fact.
    ///
    /// <para>🔒 Stamped only AFTER the send, so a failed pass retries next run rather than recording
    /// a summary nobody received.</para>
    ///
    /// <para>⚠️ Ring-exempt by construction (it rides <see cref="EngineAlertSender"/>): the operator
    /// mailbox is not a participant row, and the ring gate fails closed on those.</para>
    /// </remarks>
    private async Task AlertOperatorAsync(
        int eventId, IReadOnlyList<string> rows, string weekKey, CancellationToken ct)
    {
        if (_alerts is null || rows.Count == 0) return;

        var occasion = $"{ReminderType}:operator:{weekKey}";
        var already = await _db.SentReminders.AsNoTracking().AnyAsync(
            s => s.EventId == eventId
                 && s.ReminderType == ReminderType
                 && s.OccasionKey == occasion
                 && s.RecipientEmail == OperatorRecipient, ct);
        if (already) return;

        var html =
            $"<p><strong>{rows.Count}</strong> sponsor "
            + (rows.Count == 1 ? "company is" : "companies are")
            + " missing a website or LinkedIn address <strong>in the webshop</strong>. Since the "
            + "webshop owns those fields, they cannot be filled in from the hub — the companies "
            + "below have been asked to add them at the source.</p>"
            + "<ul>" + string.Join(string.Empty, rows) + "</ul>"
            + "<p style=\"color:#6b7280;font-size:13px;\">X/Twitter is optional and is never "
            + "chased. This list is sent once a week and stops when nothing is missing.</p>";

        await _alerts.AlertAsync(
            $"{rows.Count} sponsor(s) missing a webshop website or LinkedIn [ELDK27]",
            html, ct,
            // 🔒 No throttle key — the weekly guard above is the dedup, and a second invisible
            // in-memory window on top would be the §765 two-switch trap: two things deciding one
            // cadence, with only one of them written down.
            throttleKey: null,
            recipient: OperatorRecipient,
            // §752.9 — DEV-silent: on DEV these are imported test companies, so the mail asks for
            // work on data nobody maintains.
            devSilent: true);

        _db.SentReminders.Add(new SentReminder
        {
            EventId = eventId,
            RecipientEmail = OperatorRecipient,
            ReminderType = ReminderType,
            OccasionKey = occasion,
            SentAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
    }
}
