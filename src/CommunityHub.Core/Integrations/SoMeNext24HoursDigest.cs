using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1060(j) — ONE DAILY MAIL: what publishes on the company page in the next 24 hours.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"new req. daily email with approved some post during next 24 to
/// info@expertslive.dk"</i> · <i>"next 24 hr"</i>.</para>
///
/// <para>🔑 <b>This is a FORECAST, and §1060(i) is an EVENT.</b> They overlap — a post approved today
/// that publishes tomorrow appears in both — and that is correct: one answers <i>"this was approved
/// without me"</i>, the other <i>"here is what goes out today"</i>. Neither substitutes for the other,
/// and neither should be optimised away.</para>
///
/// <para>🔴 <b>"Approved" is <c>IsActive</c>, NOT a status value.</b> <see cref="SoMePostStatus"/> has
/// only Queued/Published/Failed — there is no <c>Approved</c> member. Reading it as a status yields an
/// empty digest every single day, which looks exactly like "nothing is scheduled" rather than like a
/// bug. This is the trap §1060(j) was written down to prevent.</para>
/// </remarks>
public sealed class SoMeNext24HoursDigest
{
    /// <summary>Same mailbox as the auto-approval notice, and a constant for the same reason.</summary>
    public const string NoticeEmail = SoMeAutoApproveService.NoticeEmail;

    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _email;
    private readonly IEmailContextAccessor? _ctx;
    private readonly TimeProvider _clock;
    private readonly ILogger<SoMeNext24HoursDigest>? _log;

    private readonly SoMeSubjectLabeller? _labeller;
    private readonly string _hubUrl;

    public SoMeNext24HoursDigest(
        CommunityHubDbContext db, IEmailSender email, IEmailContextAccessor? ctx = null,
        TimeProvider? clock = null, ILogger<SoMeNext24HoursDigest>? log = null,
        // §1170 — a human subject and a clickable link. Optional so existing constructions and
        // tests are unchanged; both are registered in the hosts that actually send this.
        SoMeSubjectLabeller? labeller = null,
        Microsoft.Extensions.Options.IOptions<Email.EmailTemplateOptions>? branding = null)
    {
        _db = db;
        _email = email;
        _ctx = ctx;
        _clock = clock ?? TimeProvider.System;
        _log = log;
        _labeller = labeller;
        _hubUrl = (branding?.Value.HubUrl ?? string.Empty).TrimEnd('/');
    }

    /// <summary>Returns how many posts the digest listed; 0 means nothing was sent.</summary>
    public async Task<int> RunAsync(int eventId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var until = now.AddHours(24);

        // 🔴 §1190 — CLEAR THE STAMP ON ANYTHING THAT HAS MOVED BACK OUT OF THE WINDOW, first.
        // A post alerted at 23 hours out and then pushed to next week must be alerted AGAIN when it
        // returns — that is precisely the post whose date somebody has been fiddling with, and the
        // one most likely to need the warning. Without this the stamp would silently suppress it.
        var movedOut = await _db.SoMePosts
            .Where(p => p.EventId == eventId
                        && p.Next24NoticeSentAt != null
                        && p.Status == SoMePostStatus.Queued
                        && p.ScheduledAtUtc > until)
            .ToListAsync(ct);

        if (movedOut.Count > 0)
        {
            foreach (var p in movedOut) p.Next24NoticeSentAt = null;
            await _db.SaveChangesAsync(ct);
        }

        // 🔑 §1190 — ONLY POSTS THAT HAVE JUST CROSSED THE LINE. Operator 2026-09-12: *"notification
        // must go out when a planned post have less than 24 hr, so check must run every 10 min"*.
        //
        // This used to be a daily DIGEST of everything inside the window, which is why a post
        // scheduled shortly after the run got about an hour's notice. Running THAT query every ten
        // minutes would mail the same list 144 times a day (§1041b), so the stamp makes each post
        // announced exactly once — and the ten-minute tick means the announcement lands within ten
        // minutes of the post entering the window, giving him close to the full 24 hours.
        var due = await _db.SoMePosts
            .Where(p => p.EventId == eventId
                        && !p.IsDeleted
                        && p.IsActive                             // ← approved (see class remarks)
                        && p.Status == SoMePostStatus.Queued      // not yet published
                        && p.Next24NoticeSentAt == null           // §1190 — not already announced
                        && p.ScheduledAtUtc > now
                        && p.ScheduledAtUtc <= until)
            .OrderBy(p => p.ScheduledAtUtc)
            .ToListAsync(ct);

        // 🔒 NOTHING TO SAY ⇒ SAY NOTHING. A daily "no posts today" trains him to skim past the one
        // that matters, and this mailbox already carries the auto-approval notices (§1041b's lesson:
        // a correct mechanism reported too loudly becomes the problem).
        if (due.Count == 0)
        {
            _log?.LogInformation("SoMe 24h digest: nothing scheduled in the next 24 hours — no mail sent.");
            return 0;
        }

        // 🔴 §1170 — A HUMAN SUBJECT, NOT A ROW KEY. Operator 2026-09-03: *"no real context"*.
        // "event:eldk27-plan-your-day" and "session:1" are how the database joins a post to its
        // subject; they tell him nothing about which session or which post it is, which is the only
        // question this mail exists to answer.
        IReadOnlyDictionary<int, string>? labels = null;
        if (_labeller is not null)
        {
            try { labels = await _labeller.LabelsForAsync(eventId, due, ct); }
            catch (Exception ex)
            {
                // A label is a nicety; the digest is the requirement.
                _log?.LogWarning(ex, "§1170: subject labels unavailable; the digest falls back to "
                    + "the raw subject keys.");
            }
        }

        string Label(SoMePost p) =>
            labels is not null && labels.TryGetValue(p.Id, out var l) && !string.IsNullOrWhiteSpace(l)
                ? l
                : p.SubjectKey ?? "—";

        // 🔴 §1170 — A LINK, NOT A PATH. The column printed "/Organizer/SoMePostEditor?id=10804" as
        // TEXT: no mail client can open that, so the one action the mail asks for meant retyping a
        // path against the right host. §1122 had already made exactly this fix in the auto-approval
        // notice — and this mail, sent by the same feature to the same mailbox, kept the bare path.
        string EditHref(SoMePost p)
        {
            var path = $"/Organizer/SoMePostEditor?id={p.Id}";
            return string.IsNullOrEmpty(_hubUrl) ? path : _hubUrl + path;
        }

        var rows = string.Join("", due.Select(p =>
            "<tr>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #eee;white-space:nowrap;\"><strong>{SoMeDisplayTime.ToDanish(p.ScheduledAtUtc):ddd dd MMM HH:mm}</strong></td>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #eee;\">{p.Type}</td>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #eee;\">{WebUtility.HtmlEncode(Label(p))}</td>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #eee;white-space:nowrap;\">"
            + $"<a href=\"{WebUtility.HtmlEncode(EditHref(p))}\">Open it &rarr;</a></td>"
            + "</tr>"));

        // §1190 — the hours left on the SOONEST one, because that is the number he is deciding
        // against. "Within 24 hours" is the rule; "in 3 hours" is the urgency.
        var soonest = due[0];
        var hoursLeft = Math.Max(0, (int)Math.Floor((soonest.ScheduledAtUtc - now).TotalHours));

        var body =
            $"<p><strong>{due.Count}</strong> social-media post(s) have just entered the last 24 hours "
            + "before they publish on the LinkedIn company page."
            + (hoursLeft <= 1
                ? " <strong>The first one goes out within the hour.</strong>"
                : $" The first goes out in about <strong>{hoursLeft} hours</strong>.")
            + "</p>"
            + "<table style=\"border-collapse:collapse;font-size:14px;\">"
            + "<tr style=\"text-align:left;\">"
            + "<th style=\"padding:6px 10px;border-bottom:2px solid #ddd;\">Publishes (Danish time)</th>"
            + "<th style=\"padding:6px 10px;border-bottom:2px solid #ddd;\">Type</th>"
            + "<th style=\"padding:6px 10px;border-bottom:2px solid #ddd;\">Subject</th>"
            + "<th style=\"padding:6px 10px;border-bottom:2px solid #ddd;\">Change it</th>"
            + "</tr>"
            + rows
            + "</table>"
            + "<p style=\"color:#6b7280;font-size:13px;\">Anything you do not want published: open it "
            + "and move the date, or switch it off.</p>";

        try
        {
            // 🔴 RING-EXEMPT — the §1061 lesson, applied at the moment of writing rather than after
            // PROD proved it. info@ is an organizer MAILBOX, not a participant row, so without this
            // the mail is dropped as an "unknown recipient" and the only symptom is an empty inbox.
            using var _ = _ctx?.Set(new EmailContext("some-24h-digest", RingExempt: true));
            await _email.SendAsync(
                NoticeEmail,
                due.Count == 1
                    ? $"SoMe: \"{Label(soonest)}\" publishes in {hoursLeft}h"
                    : $"SoMe: {due.Count} post(s) publish within 24 hours",
                body,
                ct);

            // 🔴 §1190 — STAMPED ONLY AFTER THE MAIL WENT. Inside the try, after the send: stamping
            // before it would mean a Brevo outage silently consumed the one warning he gets, and the
            // post would publish with no notice and no trace of why. Unstamped, the next tick (ten
            // minutes) simply tries again.
            var sentAt = _clock.GetUtcNow();
            foreach (var p in due) p.Next24NoticeSentAt = sentAt;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // A digest is a report; failing to send one must never take a job down with it.
            _log?.LogWarning(ex, "SoMe 24h digest: {Count} post(s) found but the mail failed.", due.Count);
        }

        _log?.LogInformation(
            "§1190 SoMe 24h alert: {Count} post(s) entered the last 24 hours; soonest in {Hours}h.",
            due.Count, hoursLeft);
        return due.Count;
    }
}
