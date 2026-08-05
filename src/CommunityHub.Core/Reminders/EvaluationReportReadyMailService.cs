using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §750 C7/C0 — the "report ready" notification: one per session, when the analysis engine has
/// published its PDF.
/// </summary>
/// <remarks>
/// <para>🔒 <b>A LINK, never the PDF as an attachment.</b> The brief is explicit and the reason is
/// not squeamishness: the report reproduces attendee comments VERBATIM, and mailing that around
/// creates uncontrolled copies outside the access model and outside the 12-month retention schedule.
/// A copy in an inbox cannot be revoked and does not expire.</para>
///
/// <para>🔑 <b>It must SAY whether it supersedes.</b> Also the brief's requirement, and the failure it
/// prevents is concrete: a second mail about the same session, with no explanation, reads as a
/// duplicate and gets deleted — taking the corrected figures with it.</para>
///
/// <para>🔒 <b>Speakers are found through the §749 link, but ADDRESSED the way every other speaker
/// mail addresses them</b> — <c>SpeakerProfile.ContactEmailOverride</c> when set, else the
/// participant's own address. These are two different questions: <c>EvaluationSessionSpeaker</c>
/// answers <i>who spoke</i> (grouping identity, primary e-mail, §743.2), the override answers
/// <i>which inbox they read</i>. Using the grouping key for delivery would quietly ignore an override
/// that every other mail honours.</para>
/// </remarks>
public sealed class EvaluationReportReadyMailService
{
    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly IEmailContextAccessor? _context;
    private readonly EmailTemplateProvider? _templates;
    private readonly ILogger<EvaluationReportReadyMailService> _log;
    private readonly Evaluation.EvaluationReportBuilder? _reportBuilder;
    private readonly Evaluation.EvaluationReportService? _reportRenderer;

    public EvaluationReportReadyMailService(
        CommunityHubDbContext db,
        IEmailSender emailSender,
        ILogger<EvaluationReportReadyMailService> log,
        IEmailContextAccessor? context = null,
        EmailTemplateProvider? templates = null,
        Evaluation.EvaluationReportBuilder? reportBuilder = null,
        Evaluation.EvaluationReportService? reportRenderer = null)
    {
        _db = db;
        _emailSender = emailSender;
        _log = log;
        _context = context;
        _templates = templates;
        // §752.1 — optional so an existing test harness that only cares about the wording keeps
        // constructing this without a render chain; the mail then simply carries no attachment.
        _reportBuilder = reportBuilder;
        _reportRenderer = reportRenderer;
    }

    /// <summary>The mail's own identity — its (mail × role) ring, per the §707 model.</summary>
    public const string TemplateName = "session-evaluation-report-ready";

    /// <summary>The feature switch it hangs off. Shared with the existing speaker results mail.</summary>
    public const string FeatureKey = "session-eval-email";

    /// <summary>The ledger CATEGORY (not the ring key — that conflation is what §707 was about).</summary>
    public const string Category = "session-eval";

    public sealed record Result(int Sent, IReadOnlyList<string> Recipients, string Message);

    /// <summary>
    /// Notify a session's speakers that their report is available.
    /// </summary>
    /// <param name="superseded">
    /// True when an earlier report for this session already went out, so the mail must say the
    /// figures were recomputed rather than read as a duplicate.
    /// </param>
    public async Task<Result> NotifyAsync(
        int eventId, int evaluationSessionId, bool superseded, CancellationToken ct = default)
    {
        var session = await _db.EvaluationSessions
            .Where(s => s.Id == evaluationSessionId && s.EventId == eventId)
            .Select(s => new { s.Id, s.Title, s.CehSessionId })
            .FirstOrDefaultAsync(ct);
        if (session is null) return new Result(0, Array.Empty<string>(), "No such session.");

        // §749 — who spoke. Primary e-mail is the grouping key; delivery is resolved below.
        var speakerEmails = await _db.EvaluationSessionSpeakers
            .Where(x => x.EvaluationSessionId == evaluationSessionId)
            .Select(x => new { x.SpeakerEmail, x.DisplayName })
            .ToListAsync(ct);

        if (speakerEmails.Count == 0)
        {
            // 🔑 Reported, not silent. "The report went out" and "the report went to nobody" must not
            // look the same in a log — an unlinked session is an organiser problem with a fix.
            _log.LogWarning(
                "Report ready for session {SessionId} '{Title}' but NO speakers are linked — nobody was notified.",
                session.Id, session.Title);
            return new Result(0, Array.Empty<string>(),
                "No speakers linked to this session — nobody was notified.");
        }

        var keys = speakerEmails.Select(s => s.SpeakerEmail).ToList();

        // Resolve each linked speaker back to their CEH participant, then to the inbox they read.
        var participants = await _db.Participants
            .Where(p => p.EventId == eventId && p.IsActive && keys.Contains(p.Email))
            .Select(p => new { p.Id, p.Email, p.FullName })
            .ToListAsync(ct);

        var overrides = await _db.SpeakerProfiles
            .Where(sp => sp.ContactEmailOverride != null && sp.ContactEmailOverride != "")
            .Select(sp => new { sp.ParticipantId, sp.ContactEmailOverride })
            .ToDictionaryAsync(x => x.ParticipantId, x => x.ContactEmailOverride!, ct);

        var eventName = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => e.DisplayName)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        // §818 — the FULL name rides along too: it is what the log row is filed under.
        var recipients = new List<(string Addr, string FirstName, int ParticipantId, string FullName)>();
        foreach (var p in participants)
        {
            var addr = overrides.TryGetValue(p.Id, out var ov) ? ov : p.Email;
            if (string.IsNullOrWhiteSpace(addr)) continue;
            if (recipients.Any(r => string.Equals(r.Addr, addr, StringComparison.OrdinalIgnoreCase)))
                continue;
            var first = string.IsNullOrWhiteSpace(p.FullName) ? "there" : p.FullName.Split(' ')[0];
            recipients.Add((addr, first, p.Id, p.FullName ?? string.Empty));
        }

        if (recipients.Count == 0)
        {
            _log.LogWarning(
                "Report ready for session {SessionId} but no linked speaker resolved to an active "
                + "participant with an address — nobody was notified.", session.Id);
            return new Result(0, Array.Empty<string>(),
                "No linked speaker resolved to a deliverable address — nobody was notified.");
        }

        // 🔑 The subject carries the supersede state too, not just the body — a recipient triaging a
        // full inbox decides from the subject line alone whether this is news.
        var subject = superseded
            ? $"Updated evaluation results — {session.Title}"
            : $"Your evaluation results are ready — {session.Title}";

        // 🔑 §752.1 — DECIDED by the operator 2026-08-01: *"can we attack pdf to the session eval is
        // now ready to speakers. so they can choose to open pdf or open ceh button"*.
        //
        // ⚠️ This deliberately OVERRIDES the brief's "a link, never an attachment" rule. The brief's
        // reason still stands and is worth restating rather than burying: the report reproduces
        // attendee comments verbatim, so an attachment is an uncontrolled copy that cannot be revoked
        // and does not expire with the 12-month retention schedule. He has accepted that trade for
        // speaker convenience — a speaker on a phone should not have to sign in to read their own
        // result. The in-hub button remains, so the link path is unchanged for anyone who prefers it.
        //
        // 🔒 Rendered ONCE per session, not per recipient: same bytes to every speaker, and a
        // four-speaker session does not render the same PDF four times.
        byte[]? pdf = null;
        if (_reportBuilder is not null && _reportRenderer is not null)
        {
            try
            {
                var report = await _reportBuilder.BuildAsync(eventId, evaluationSessionId, ct);
                if (report is not null) pdf = _reportRenderer.Render(report.Data);
            }
            catch (Exception ex)
            {
                // 🔒 The NOTIFICATION matters more than the attachment. If rendering fails the mail
                // still goes with its link, and the failure is logged rather than swallowed — a
                // silent downgrade here is what §751.1 was about.
                _log.LogError(ex,
                    "Could not render the PDF to attach for session {SessionId}; sending the "
                    + "notification with its link only.", session.Id);
            }
        }

        var safeTitle = string.Join("_", session.Title.Split(Path.GetInvalidFileNameChars()));
        var attachments = pdf is null
            ? null
            : new[] { new EmailAttachment($"evaluation-{safeTitle}.pdf", pdf, "application/pdf") };

        // 🔒 §818 — PER RECIPIENT, not per loop: the mail identity is loop-invariant but the recipient
        // is not, and a context without `eventId`/`participantId` logs the row against no edition and
        // no speaker — invisible to both organizer views that answer "did she get it". Same defect and
        // same fix as SessionEvaluationMailService; they were written from each other.
        foreach (var (addr, firstName, participantId, fullName) in recipients)
        {
            using (_context?.Set(new EmailContext(
                Category, eventId, participantId, fullName,
                TemplateName: TemplateName, FeatureKey: FeatureKey)))
            {
                var html = Render(firstName, session.Title, eventName, superseded, participantId);
                if (attachments is not null)
                {
                    await _emailSender.SendWithAttachmentsAsync(addr, subject, html, attachments, ct);
                }
                else
                {
                    await _emailSender.SendAsync(addr, subject, html, ct);
                }
            }
        }

        _log.LogInformation(
            "Report-ready notification sent for session {SessionId} to {Count} speaker(s) (superseded={Superseded}).",
            session.Id, recipients.Count, superseded);

        return new Result(recipients.Count, recipients.Select(r => r.Addr).ToList(),
            $"Report-ready notification sent to {recipients.Count} speaker(s).");
    }

    private string Render(
        string firstName, string title, string eventName, bool superseded, int participantId)
    {
        if (_templates is null)
        {
            // Fail-safe plain body, so a missing renderer degrades to a deliverable mail rather
            // than an exception on the send path.
            var lead = superseded
                ? "Your session's evaluation figures have been recomputed, and the report has been updated."
                : "Your session's evaluation report is ready.";
            return $"<p>Hi {System.Net.WebUtility.HtmlEncode(firstName)},</p>"
                 + $"<p>{lead}</p>"
                 + $"<p><strong>{System.Net.WebUtility.HtmlEncode(title)}</strong></p>"
                 + "<p>Sign in to the hub to read it.</p>";
        }

        // §169: pass the recipient's participant id so the CTA is their personal magic link.
        //
        // 🔴 §752.10 — THE DEEP LINK LIVES IN THE TEMPLATE (`{{hubUrl}}/Speaker/Results`), not here.
        //
        // This method used to append `?r=/Speaker/Results` to the hubUrl token. The code read
        // correctly and did not work: the operator clicked the button and landed on the hub home
        // page, and App Insights showed his actual request as a bare `/go/{token}` with NO query
        // string — while a probe fired seconds later against the same deployed build showed `?r=`
        // arriving and being honoured. The redirect mechanism was never the problem; the parameter
        // simply was not in the mail.
        //
        // 🔑 The fix is to stop being the odd one out. Nine other CTAs deep-link as
        // `{{hubUrl}}/Forms/Wizard`, `{{hubUrl}}/Speaker/Graphics` and so on — the PATH form that
        // `/go/{token}/{**target}` has handled since §169 and that §365 taught to carry a query
        // string too. It is exercised by every masterclass and get-started mail we send. Building
        // the same deep link a second way, in code, bought nothing and failed quietly.
        //
        // 🔒 It failed QUIETLY because nothing asserted the CTA URL. The suite proved the mail
        // rendered and carried its attachment; the one thing the mail exists to do — land a speaker
        // on their results — was untested. That test now exists.
        var tokens = _templates.NewTokenSet(participantId);
        tokens["firstName"] = firstName;
        tokens["sessionTitle"] = title;
        tokens["eventDisplayName"] = eventName;
        // 🔑 One template, two states — rather than two templates that drift. The token says which.
        tokens["reportLead"] = superseded
            ? "More feedback arrived after your first report, so the figures have been recomputed "
              + "and your report has been updated. This replaces the earlier one."
            : "The feedback for your session has been collected and your evaluation report is ready.";
        tokens["reportStateLabel"] = superseded ? "Updated report" : "Report ready";
        return _templates.Render(TemplateName, tokens).HtmlBody;
    }
}


