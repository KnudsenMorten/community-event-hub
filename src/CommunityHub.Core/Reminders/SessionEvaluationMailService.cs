using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>The outcome of emailing one session's evaluation results.</summary>
/// <param name="Sent">True if at least one speaker was emailed.</param>
/// <param name="Recipients">The speaker addresses the results were sent to.</param>
/// <param name="Message">Human-readable status / reason when nothing was sent.</param>
public sealed record SessionEvaluationMailResult(
    bool Sent,
    IReadOnlyList<string> Recipients,
    string Message);

/// <summary>
/// The session-evaluation MAIL HOOK (REQUIREMENTS § evaluation, option a: HappyOrNot).
///
/// HappyOrNot is a physical smiley box at the room: an external device with NO API, so
/// the per-session results arrive <b>manually</b> (the organizer reads them off the
/// HappyOrNot portal / report). This service is the hook that, given those results as
/// text, emails them to the session's speaker(s) after the session. The data flows in
/// manually; only the delivery is automated here.
///
/// A future "own devices via API" ingestion path (◻) would populate the same results
/// text automatically and then reuse this exact send — the seam is the
/// <paramref name="resultsText"/> argument, not a HappyOrNot-specific shape.
///
/// Routed through <see cref="IEmailSender"/> (the same Brevo seam + DEV redirect /
/// PROD allowlist as every other send), so test sends never reach real people.
/// </summary>
public sealed class SessionEvaluationMailService
{
    private const string DefaultSupportEmail = "info@expertslive.dk";

    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly TimeProvider _clock;
    private readonly IEmailContextAccessor? _context;

    // Optional edition-config source for the SPEAKER contact in the footer line.
    // Null in older test constructions → blank name / support-email fallback.
    private readonly EventEditionConfigLoader? _eventConfigLoader;
    private readonly EventConfigOptions? _eventConfigOptions;

    public SessionEvaluationMailService(
        CommunityHubDbContext db,
        IEmailSender emailSender,
        TimeProvider clock,
        IEmailContextAccessor? context = null,
        EventEditionConfigLoader? eventConfigLoader = null,
        EventConfigOptions? eventConfigOptions = null)
    {
        _db = db;
        _emailSender = emailSender;
        _clock = clock;
        _context = context;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
    }

    /// <summary>
    /// Email the supplied (manually-collected) HappyOrNot evaluation results to every
    /// speaker linked to the session. <paramref name="resultsText"/> is the results
    /// summary the organizer pasted from the HappyOrNot report. Stamps
    /// <c>Session.EvaluationEmailedAt</c> when a send occurs. Returns who was emailed,
    /// or why nothing was sent (no session, no linked speaker with an address, blank
    /// results). Never throws for "nothing to send"; a hard email failure propagates so
    /// the organizer can retry.
    /// </summary>
    public async Task<SessionEvaluationMailResult> EmailResultsToSpeakersAsync(
        int sessionId,
        string resultsText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resultsText))
        {
            return new SessionEvaluationMailResult(
                false, Array.Empty<string>(),
                "No results text supplied — nothing was emailed.");
        }

        var session = await _db.Sessions
            .Include(s => s.SessionSpeakers)
                .ThenInclude(ss => ss.Participant)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return new SessionEvaluationMailResult(
                false, Array.Empty<string>(), "Session not found.");
        }

        // Resolve each speaker's effective address: the SpeakerProfile override (when
        // set) else the participant's own address — the same routing the welcome mail
        // uses, so evaluation results reach the speaker's preferred inbox.
        var participantIds = session.SessionSpeakers
            .Select(ss => ss.ParticipantId)
            .ToList();
        var overrides = await _db.SpeakerProfiles
            .Where(sp => participantIds.Contains(sp.ParticipantId)
                         && sp.ContactEmailOverride != null
                         && sp.ContactEmailOverride != "")
            .ToDictionaryAsync(sp => sp.ParticipantId, sp => sp.ContactEmailOverride!, ct);

        var recipients = new List<string>();
        foreach (var link in session.SessionSpeakers)
        {
            var p = link.Participant;
            if (p is null || !p.IsActive) continue;
            var addr = overrides.TryGetValue(link.ParticipantId, out var ov)
                ? ov
                : p.Email;
            if (!string.IsNullOrWhiteSpace(addr) && !recipients.Contains(addr, StringComparer.OrdinalIgnoreCase))
            {
                recipients.Add(addr);
            }
        }

        if (recipients.Count == 0)
        {
            return new SessionEvaluationMailResult(
                false, Array.Empty<string>(),
                "No linked speaker with an email address — nothing was emailed.");
        }

        var subject = $"Your session evaluation — {session.Title}";
        var htmlBody = BuildHtmlBody(session.Title, resultsText);

        // Ring-governed by the session-eval-email feature (operator 2026-06-22).
        using (_context?.Set(new EmailContext(
            "session-eval", FeatureKey: "session-eval-email")))
        {
            foreach (var addr in recipients)
            {
                await _emailSender.SendAsync(addr, subject, htmlBody, ct);
            }
        }

        session.EvaluationEmailedAt = _clock.GetUtcNow();
        session.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        return new SessionEvaluationMailResult(
            true, recipients,
            $"Evaluation results emailed to {recipients.Count} speaker(s).");
    }

    private string BuildHtmlBody(string title, string resultsText)
    {
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);
        var safeResults = System.Net.WebUtility.HtmlEncode(resultsText)
            .Replace("\r\n", "\n")
            .Replace("\n", "<br>");
        return $"""
            <p>Hi,</p>
            <p>Thank you for speaking! Here is the audience evaluation for your session
               <strong>{safeTitle}</strong>:</p>
            <blockquote style="border-left:3px solid #ccc;padding-left:12px;color:#333;">
            {safeResults}
            </blockquote>
            <p>These results were collected from the on-site feedback box.</p>
            {SpeakerContactLine()}
            <p>— The organizer team</p>
            """;
    }

    /// <summary>
    /// The speaker-facing contact line. Recipients of an evaluation mail are always
    /// SPEAKERS, so the SPEAKER organizer-lead (from edition config) is used; name
    /// is blank-safe and the email falls back to the support address.
    /// </summary>
    private string SpeakerContactLine()
    {
        var placeholders = (IReadOnlyDictionary<string, string>)
            new Dictionary<string, string>();
        var supportEmail = DefaultSupportEmail;

        if (_eventConfigLoader is not null)
        {
            try
            {
                var path = _eventConfigOptions?.EventConfigPath
                           ?? new EventConfigOptions().EventConfigPath;
                var cfg = _eventConfigLoader.Load(path);
                placeholders = cfg.Placeholders ?? placeholders;
                if (cfg.Placeholders is not null
                    && cfg.Placeholders.TryGetValue("supportEmail", out var se)
                    && !string.IsNullOrWhiteSpace(se))
                {
                    supportEmail = se;
                }
            }
            catch { /* fail-safe to default */ }
        }

        var (name, email) = RoleContact.For(
            ParticipantRole.Speaker, placeholders, supportEmail);
        var encName = System.Net.WebUtility.HtmlEncode(name);
        var encEmail = System.Net.WebUtility.HtmlEncode(email);
        var encSupport = System.Net.WebUtility.HtmlEncode(supportEmail);
        return $"<p>Questions about your evaluation? Contact {encName} ({encEmail}) — or {encSupport}.</p>";
    }
}
